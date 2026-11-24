using System.Globalization;
using System.IO.Compression;
using System.Xml.Linq;
using CellBridge.Client.Excel;

namespace CellBridge.Client.PowerPoint;

internal sealed partial class PowerPointPackage : IDisposable
{
    private static readonly XNamespace P = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace Rel = "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly XNamespace Ct = "http://schemas.openxmlformats.org/package/2006/content-types";
    private static readonly XNamespace P14 = "http://schemas.microsoft.com/office/powerpoint/2010/main";
    private static readonly XNamespace Mc = "http://schemas.openxmlformats.org/markup-compatibility/2006";
    private const string ContentTypes = "[Content_Types].xml";
    private const string PowerPointContentType = "application/vnd.openxmlformats-officedocument.presentationml.";
    internal const int MaxTextCharacters = 1_000_000;

    private static readonly ExcelWorkbookLimits Limits = new();
    private readonly ExcelMemoryBudget _budget = new(Limits.MaxSessionBytes);
    private readonly RawExcelZip _zip;
    private readonly ZipArchive _entries;
    private readonly Dictionary<string, ZipArchiveEntry> _entriesByName;
    private readonly Dictionary<string, XDocument> _loaded = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, XDocument> _changed = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _removed = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<uint, Slide> _slides = [];
    private readonly XElement _presentation;
    private readonly string _presentationRelationships;

    private sealed record Slide(uint Id, int Position, string Part, XElement Entry);
    private sealed record Shape(uint Id, XElement Element, XElement Properties);
    internal sealed record TextTarget(string SlideIdentity, string? Identity, string? Text, string? Markup);

    internal PowerPointPackage(byte[] bytes)
    {
        CheckSize(bytes);
        _zip = new RawExcelZip(bytes, Limits.MaxExpandedBytes, _budget);
        try
        {
            _entries = new ZipArchive(new MemoryStream(bytes, writable: false));
            _entriesByName = _entries.Entries.ToDictionary(entry => entry.FullName, StringComparer.OrdinalIgnoreCase);
            PresentationPart = FindRelatedPart("", "officeDocument")
                ?? throw new InvalidDataException("The package has no presentation relationship.");
            _presentationRelationships = RelationshipsPath(PresentationPart);
            _presentation = Root(PresentationPart, P + "presentation");
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in _presentation.Elements(P + "sldIdLst").SingleOrDefault()?.Elements(P + "sldId") ?? [])
            {
                var id = ReadId(entry, "id");
                var part = FindReferencedPart(PresentationPart, entry, "slide");
                if (id is < 256 or >= 2147483648 || !paths.Add(part) ||
                    !_slides.TryAdd(id, new Slide(id, _slides.Count, part, entry)))
                    throw new InvalidDataException("The presentation contains a duplicate or invalid slide ID or part.");
            }
        }
        catch
        {
            _entries?.Dispose();
            _zip.Dispose();
            throw;
        }
    }

    internal string PresentationPart { get; }

    internal static void CheckSize(byte[] bytes)
    {
        if (bytes.LongLength > Limits.MaxPackageBytes)
            throw new InvalidDataException("The presentation exceeds the package size limit.");
    }

    internal IReadOnlyList<PowerPointSlide> ListSlides() =>
        Array.AsReadOnly(_slides.Values.Select(ReadSlide).ToArray());

    internal PowerPointSlide ReadSlide(uint id) => ReadSlide(GetSlide(id));

    private PowerPointSlide ReadSlide(Slide slide)
    {
        var shapes = ReadShapes(ShapeTree(slide.Part, "sld"))
            .Select(shape => new PowerPointShape(shape.Id, Required(shape.Properties, "name"),
                ReadText(shape.Element.Element(P + "txBody")))).ToArray();
        return new PowerPointSlide(slide.Id, Array.AsReadOnly(shapes), NotesTarget(slide.Id).Text)
        {
            Position = slide.Position,
        };
    }

    internal TextTarget ShapeTarget(uint slideId, uint shapeId)
    {
        var slide = GetSlide(slideId);
        var shape = EditableShape(slide, shapeId);
        var identity = string.Join("/", shape.Element.AncestorsAndSelf()
            .Where(element => element.Name == P + "grpSp" || element == shape.Element)
            .Select(element => ShapeProperties(element)?.ToString(SaveOptions.DisableFormatting)));
        var body = shape.Element.Element(P + "txBody")!;
        return new(SlideIdentity(slideId), identity, ReadText(body), body.ToString(SaveOptions.DisableFormatting));
    }

    internal string SlideIdentity(uint slideId)
    {
        var slide = GetSlide(slideId);
        var data = Root(slide.Part, P + "sld").Elements(P + "cSld").SingleOrDefault();
        var creationId = data?.Element(P + "extLst")?.Elements(P + "ext")
            .SelectMany(extension => extension.Elements(P14 + "creationId")).SingleOrDefault();
        return slide.Part + ":" + (creationId is null ? null : ReadId(creationId, "val"));
    }

    internal void SetShapeText(uint slideId, uint shapeId, string text)
    {
        var slide = GetSlide(slideId);
        var shape = EditableShape(slide, shapeId);
        var body = shape.Element.Element(P + "txBody")!;
        if (SetText(body, text))
            Change(slide.Part);
    }

    private Shape EditableShape(Slide slide, uint id)
    {
        var tree = ShapeTree(slide.Part, "sld");
        var shape = ReadShapes(tree).SingleOrDefault(shape => shape.Id == id)
            ?? throw new KeyNotFoundException($"Slide {slide.Id} has no shape {id}.");
        if (tree.Descendants(P + "cNvPr").Count(properties => (uint?)properties.Attribute("id") == id) != 1)
            throw new InvalidOperationException($"Slide {slide.Id} has an ambiguous shape ID {id}.");
        if (shape.Element.Name != P + "sp" || shape.Element.Elements(P + "txBody").Count() != 1)
            throw new InvalidOperationException($"Shape {id} on slide {slide.Id} has no editable text body.");
        return shape;
    }

    private static IReadOnlyList<Shape> ReadShapes(XElement tree)
    {
        var shapes = new List<Shape>();
        var ids = new HashSet<uint>();
        foreach (var element in tree.Descendants())
        {
            if (element.Parent != tree && element.Parent!.Name != P + "grpSp")
                continue;
            if (element.Ancestors().TakeWhile(parent => parent != tree).Any(parent => parent.Name != P + "grpSp"))
                continue;
            if (ShapeProperties(element) is not { } properties)
                continue;
            var id = ReadId(properties, "id");
            if (id == 0 || !ids.Add(id))
                throw new InvalidOperationException("The slide contains an invalid or duplicate shape ID.");
            shapes.Add(new(id, element, properties));
        }

        return shapes;
    }

    private static XElement? ShapeProperties(XElement element)
    {
        var properties = element.Name == P + "sp" ? "nvSpPr"
            : element.Name == P + "grpSp" ? "nvGrpSpPr"
            : element.Name == P + "pic" ? "nvPicPr"
            : element.Name == P + "graphicFrame" ? "nvGraphicFramePr"
            : element.Name == P + "cxnSp" ? "nvCxnSpPr"
            : element.Name == P + "contentPart" ? "nvContentPartPr"
            : null;
        return properties is null ? null : element.Element(P + properties)?.Element(P + "cNvPr");
    }

    private XElement ShapeTree(string part, string root) =>
        Root(part, P + root).Elements(P + "cSld").SingleOrDefault()?.Elements(P + "spTree").SingleOrDefault()
        ?? throw new InvalidDataException($"'{part}' has no slide shape tree.");

    private static string? ReadText(XElement? body) => body is null ? null : string.Join("\n",
        body.Elements(A + "p").Select(paragraph => string.Concat(paragraph.Elements().Select(element =>
            element.Name == A + "br" ? "\n"
            : element.Name == A + "r" || element.Name == A + "fld"
                ? (string?)element.Element(A + "t") : null))));

    private static bool SetText(XElement body, string text)
    {
        if (ReadText(body) == text)
            return false;
        var paragraphs = body.Elements(A + "p").ToArray();
        if (paragraphs.Any(paragraph => paragraph.Element(A + "br") is not null))
            throw new InvalidOperationException("The text body contains soft line breaks and cannot be rewritten.");
        if (body.Elements().Any(element => element.Name != A + "p" && element.Name != A + "bodyPr" &&
            element.Name != A + "lstStyle") || paragraphs.Any(paragraph => paragraph.Elements().Any(element =>
            element.Name != A + "pPr" && element.Name != A + "r" && element.Name != A + "endParaRPr")) ||
            paragraphs.SelectMany(paragraph => paragraph.Elements(A + "r"))
                .Any(run => run.Elements(A + "t").Count() != 1 ||
                    run.Element(A + "t")!.HasElements || run.Elements().Any(element =>
                        element.Name != A + "rPr" && element.Name != A + "t")))
            throw new InvalidOperationException("The text body contains fields or unsupported markup.");

        var lines = text.Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var paragraph = index < paragraphs.Length ? paragraphs[index] : new XElement(A + "p",
                paragraphs.LastOrDefault()?.Element(A + "pPr"), paragraphs.LastOrDefault()?.Element(A + "endParaRPr"));
            if (paragraph.Parent is null)
                body.Add(paragraph);
            if (string.Concat(paragraph.Elements(A + "r").Select(run => run.Element(A + "t")!.Value)) == lines[index])
                continue;
            var runs = paragraph.Elements(A + "r").ToArray();
            if (runs.Length == 0)
            {
                var run = new XElement(A + "r", new XElement(A + "t", ""));
                if (paragraph.Element(A + "endParaRPr") is { } end)
                    end.AddBeforeSelf(run);
                else
                    paragraph.Add(run);
                runs = [run];
            }
            var offset = 0;
            for (var runIndex = 0; runIndex < runs.Length; runIndex++)
            {
                var value = runs[runIndex].Element(A + "t")!;
                var remaining = lines[index].Length - offset;
                var count = runIndex == runs.Length - 1 ? remaining : Math.Min(remaining, value.Value.Length);
                if (count > 0 && count < remaining && char.IsHighSurrogate(lines[index][offset + count - 1]))
                    count--;
                value.Value = lines[index].Substring(offset, count);
                offset += count;
            }
        }

        foreach (var paragraph in paragraphs.Skip(lines.Length))
            paragraph.Remove();
        return true;
    }

    private Slide GetSlide(uint id) => _slides.TryGetValue(id, out var slide)
        ? slide : throw new KeyNotFoundException($"The presentation has no slide {id}.");

    private XDocument Read(string part)
    {
        if (!_loaded.TryGetValue(part, out var document))
        {
            document = _zip.ReadXml(part, _budget);
            _loaded.Add(part, document);
        }

        return document;
    }

    private XElement Root(string part, XName name) => Read(part).Root is { } root && root.Name == name ? root
        : throw new InvalidDataException($"'{part}' has an unexpected XML root.");

    private void Change(string part) => _changed[part] = Read(part);

    private void AddPart(string part, XElement root)
    {
        var document = new XDocument(root);
        _loaded.Add(part, document);
        _changed.Add(part, document);
    }

    internal byte[] Save() => _zip.Rewrite(_changed, _removed, Limits, _budget);

    private static string RelationshipsPath(string source)
    {
        var separator = source.LastIndexOf('/');
        return source[..(separator + 1)] + "_rels/" + source[(separator + 1)..] + ".rels";
    }

    private IEnumerable<XElement> Relationships(string part)
    {
        var path = RelationshipsPath(part);
        if (!_zip.Contains(path) && !_loaded.ContainsKey(path))
            return [];
        var relationships = Root(path, Rel + "Relationships").Elements(Rel + "Relationship").ToArray();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var relationship in relationships)
            if (!ids.Add(Required(relationship, "Id")))
                throw new InvalidDataException($"'{path}' contains duplicate relationship IDs.");
        return relationships;
    }

    private string? FindRelatedPart(string source, string type)
    {
        var matches = Relationships(source).Where(relationship => HasType(relationship, type)).ToArray();
        if (matches.Length > 1)
            throw new InvalidOperationException($"'{source}' has more than one '{type}' relationship.");
        return matches.Length == 1 ? ExistingTarget(source, matches[0]) : null;
    }

    private string FindReferencedPart(string source, XElement reference, string type)
    {
        var id = Required(reference, R + "id");
        var relationship = Relationships(source).SingleOrDefault(relationship =>
            Required(relationship, "Id") == id && HasType(relationship, type))
            ?? throw new InvalidDataException($"The '{type}' relationship '{id}' is missing.");
        return ExistingTarget(source, relationship);
    }

    private string ExistingTarget(string source, XElement relationship)
    {
        var target = ResolveTarget(source, relationship);
        if (!_entriesByName.TryGetValue(target, out var entry))
            throw new InvalidDataException($"The presentation is missing '{target}'.");
        return entry.FullName;
    }

    private static string ResolveTarget(string source, XElement relationship)
    {
        var target = Required(relationship, "Target");
        var origin = new Uri("https://package.invalid/" + source);
        if ((string?)relationship.Attribute("TargetMode") is not (null or "Internal") ||
            target.Length == 0 || target.Contains('\\') || target.StartsWith("//", StringComparison.Ordinal) ||
            Uri.TryCreate(target, UriKind.Absolute, out _) && !target.StartsWith('/') ||
            !Uri.TryCreate(origin, target, out var uri) || uri.Host != origin.Host ||
            uri.Scheme != origin.Scheme || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw new InvalidDataException($"'{source}' has an invalid package relationship target.");
        return uri.AbsolutePath.TrimStart('/');
    }

    private static bool HasType(XElement relationship, string type) =>
        (string?)relationship.Attribute("Type") == R.NamespaceName + "/" + type;

    private static bool IsExternal(XElement relationship) => (string?)relationship.Attribute("TargetMode") == "External";

    private static string Required(XElement element, XName name) => (string?)element.Attribute(name)
        ?? throw new InvalidDataException($"The '{element.Name.LocalName}' element has no '{name.LocalName}' attribute.");

    private static uint ReadId(XElement element, XName name) =>
        uint.TryParse(Required(element, name), NumberStyles.None, CultureInfo.InvariantCulture, out var id)
        ? id : throw new InvalidDataException($"The '{element.Name.LocalName}' element has an invalid ID.");

    public void Dispose()
    {
        _entries.Dispose();
        _zip.Dispose();
    }
}
