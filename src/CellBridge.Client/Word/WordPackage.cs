using System.Globalization;
using System.Xml.Linq;
using CellBridge.Client.Excel;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;

namespace CellBridge.Client.Word;

internal sealed class WordPackage : IDisposable
{
    internal static readonly XName ParagraphId = XName.Get("paraId", "http://schemas.microsoft.com/office/word/2010/wordml");
    internal static readonly XNamespace Mc = "http://schemas.openxmlformats.org/markup-compatibility/2006";
    private static readonly XNamespace Relationships = "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly XNamespace ContentTypes = "http://schemas.openxmlformats.org/package/2006/content-types";
    private const string ContentTypesPart = "[Content_Types].xml";
    private const string Transitional = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private const string Strict = "http://purl.oclc.org/ooxml/wordprocessingml/main";
    private static readonly ExcelWorkbookLimits ZipLimits = new();
    private readonly ExcelMemoryBudget _budget = new(ZipLimits.MaxSessionBytes);
    private readonly RawExcelZip _zip;
    private readonly string _documentPart;
    private readonly string? _stylesPart;
    private string? _commentsPart;
    private readonly XDocument _document;
    private readonly Dictionary<string, XDocument> _changes = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _paragraphIds;
    private XElement? _styles;
    private XDocument? _comments;
    private HashSet<int>? _commentIds;
    private int _nextCommentId;

    internal WordPackage(byte[] bytes)
    {
        if (bytes.LongLength > ZipLimits.MaxPackageBytes)
            throw new InvalidDataException("The Word document exceeds the package size limit.");
        _budget.Charge(bytes.LongLength);
        _zip = new RawExcelZip(bytes, ZipLimits.MaxExpandedBytes, _budget);
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using var package = WordprocessingDocument.Open(stream, false, new OpenSettings
            {
                MarkupCompatibilityProcessSettings = new(MarkupCompatibilityProcessMode.NoProcess, FileFormatVersions.Office2013),
            });
            if (package.DocumentType != WordprocessingDocumentType.Document || package.MainDocumentPart is not { } main)
                throw new InvalidDataException("Expected a .docx document with a main Word part.");
            _documentPart = PartName(main);
            _stylesPart = main.StyleDefinitionsPart is { } styles ? PartName(styles) : null;
            _commentsPart = main.WordprocessingCommentsPart is { } comments ? PartName(comments) : null;
            _document = _zip.ReadXml(_documentPart, _budget);
            W = _document.Root?.Name.Namespace ?? XNamespace.None;
            if ((W.NamespaceName is not Transitional and not Strict) || _document.Root?.Name != W + "document")
                throw new InvalidDataException("The main Word part has an invalid root element.");
            Body = _document.Root.Element(W + "body") ?? throw new InvalidDataException("The Word document has no body.");
            _paragraphIds = _document.Descendants(W + "p").Select(StableId).OfType<string>()
                .ToHashSet(StringComparer.Ordinal);
        }
        catch (OpenXmlPackageException exception)
        {
            _zip.Dispose();
            throw new InvalidDataException("The Word package is invalid.", exception);
        }
        catch
        {
            _zip.Dispose();
            throw;
        }
    }

    internal XElement Body { get; }
    internal XNamespace W { get; }

    internal XElement? ReadStyles() => _stylesPart is null ? null : _styles ??= ReadRoot(_stylesPart, W + "styles");

    internal byte[] Save() => _zip.Rewrite(_changes, new HashSet<string>(), ZipLimits, _budget);

    internal void MarkDocumentChanged() => _changes[_documentPart] = _document;

    internal XElement NewParagraph(string text)
    {
        var root = _document.Root!;
        var prefix = root.GetPrefixOfNamespace(ParagraphId.Namespace);
        if (string.IsNullOrEmpty(prefix))
        {
            prefix = "w14";
            for (var suffix = 1; root.GetNamespaceOfPrefix(prefix) is not null; suffix++)
                prefix = "w14_" + suffix.ToString(CultureInfo.InvariantCulture);
            root.SetAttributeValue(XNamespace.Xmlns + prefix, ParagraphId.NamespaceName);
        }
        var ignorable = ((string?)root.Attribute(Mc + "Ignorable"))?.Split((char[]?)null,
            StringSplitOptions.RemoveEmptyEntries) ?? [];
        if (!ignorable.Contains(prefix, StringComparer.Ordinal))
            root.SetAttributeValue(Mc + "Ignorable", string.Join(' ', ignorable.Append(prefix)));

        string id;
        do { id = Random.Shared.Next(1, int.MaxValue).ToString("X8", CultureInfo.InvariantCulture); }
        while (!_paragraphIds.Add(id));
        return new XElement(W + "p", new XAttribute(ParagraphId, id), new XElement(W + "r", TextElements(text)));
    }

    internal static string? StableId(XElement element)
    {
        var id = (string?)element.Attribute(ParagraphId);
        return id?.Length == 8 && uint.TryParse(id, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture,
            out var value) && value is > 0 and < 0x80000000 ? id.ToUpperInvariant() : null;
    }

    internal IEnumerable<XElement> TextElements(string text)
    {
        var offset = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] is not ('\t' or '\n'))
                continue;
            if (i > offset)
                yield return TextElement(text[offset..i]);
            yield return new XElement(W + (text[i] == '\t' ? "tab" : "br"));
            offset = i + 1;
        }
        if (offset < text.Length)
            yield return TextElement(text[offset..]);
    }

    private XElement TextElement(string text) => new(W + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), text);

    internal void AddComment(XElement target, WordAddComment edit, string text)
    {
        InitializeComments();
        while (!_commentIds!.Add(_nextCommentId))
            _nextCommentId = checked(_nextCommentId + 1);
        var id = _nextCommentId.ToString(CultureInfo.InvariantCulture);
        var comment = new XElement(W + "comment", new XAttribute(W + "id", id), new XAttribute(W + "author", edit.Author));
        if (edit.Initials is not null)
            comment.SetAttributeValue(W + "initials", edit.Initials);
        comment.Add(new XElement(W + "p", new XElement(W + "r", TextElements(text))));
        _comments!.Root!.Add(comment);
        _changes[_commentsPart!] = _comments;

        var start = new XElement(W + "commentRangeStart", new XAttribute(W + "id", id));
        if (target.Element(W + "pPr") is { } properties)
            properties.AddAfterSelf(start);
        else
            target.AddFirst(start);
        target.Add(new XElement(W + "commentRangeEnd", new XAttribute(W + "id", id)),
            new XElement(W + "r", new XElement(W + "commentReference", new XAttribute(W + "id", id))));
    }

    private void InitializeComments()
    {
        if (_comments is not null)
            return;
        if (_commentsPart is { } path)
        {
            _comments = _zip.ReadXml(path, _budget);
            if (_comments.Root?.Name != W + "comments")
                throw new InvalidDataException("The Word comments part has an invalid root element.");
        }
        else
        {
            _comments = new XDocument(new XElement(W + "comments", new XAttribute(XNamespace.Xmlns + "w", W.NamespaceName)));
            CreateCommentsPart();
        }
        _commentIds = _comments.Root!.Elements(W + "comment").Concat(Body.Descendants().Where(node =>
                node.Name == W + "commentRangeStart" || node.Name == W + "commentRangeEnd" || node.Name == W + "commentReference"))
            .Select(node => int.TryParse((string?)node.Attribute(W + "id"), CultureInfo.InvariantCulture, out var id) ? id : -1)
            .ToHashSet();
    }

    private void CreateCommentsPart()
    {
        var slash = _documentPart.LastIndexOf('/');
        var directory = _documentPart[..(slash + 1)];
        var file = _documentPart[(slash + 1)..];
        var relationshipsPart = directory + "_rels/" + file + ".rels";
        var relationships = _zip.Contains(relationshipsPart) ? _zip.ReadXml(relationshipsPart, _budget)
            : new XDocument(new XElement(Relationships + "Relationships"));
        var root = relationships.Root;
        if (root?.Name != Relationships + "Relationships")
            throw new InvalidDataException("The main Word relationships part has an invalid root element.");
        var types = _zip.ReadXml(ContentTypesPart, _budget);
        if (types.Root?.Name != ContentTypes + "Types")
            throw new InvalidDataException("The package content types part has an invalid root element.");

        var relationshipIds = root.Elements(Relationships + "Relationship").Select(item => (string?)item.Attribute("Id"))
            .ToHashSet(StringComparer.Ordinal);
        var partNames = types.Root.Elements(ContentTypes + "Override").Select(item => (string?)item.Attribute("PartName"))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var relationshipId = "rIdComments";
        for (var suffix = 1; relationshipIds.Contains(relationshipId); suffix++)
            relationshipId = "rIdComments" + suffix.ToString(CultureInfo.InvariantCulture);
        var target = "comments.xml";
        for (var suffix = 1; _zip.Contains(directory + target) || partNames.Contains("/" + directory + target); suffix++)
            target = "comments" + suffix.ToString(CultureInfo.InvariantCulture) + ".xml";
        _commentsPart = directory + target;

        var relationshipType = W.NamespaceName == Strict ? "http://purl.oclc.org/ooxml/officeDocument/relationships/comments"
            : "http://schemas.openxmlformats.org/officeDocument/2006/relationships/comments";
        root.Add(new XElement(Relationships + "Relationship", new XAttribute("Id", relationshipId),
            new XAttribute("Type", relationshipType), new XAttribute("Target", target)));
        types.Root.Add(new XElement(ContentTypes + "Override", new XAttribute("PartName", "/" + _commentsPart),
            new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.wordprocessingml.comments+xml")));
        _changes[relationshipsPart] = relationships;
        _changes[ContentTypesPart] = types;
    }

    private XElement ReadRoot(string path, XName name)
    {
        var root = _zip.ReadXml(path, _budget).Root;
        return root?.Name == name ? root : throw new InvalidDataException($"The Word part '{path}' has an invalid root element.");
    }

    private static string PartName(OpenXmlPart part) => part.Uri.OriginalString.TrimStart('/');

    public void Dispose() => _zip.Dispose();
}
