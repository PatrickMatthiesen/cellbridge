using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace CellBridge.Client.Excel;

internal sealed class ExcelMemoryBudget(long limit)
{
    private long _remaining = limit;

    internal void Charge(long bytes)
    {
        if (bytes > _remaining)
            throw new InvalidDataException("The workbook exceeds the Excel session memory budget.");
        _remaining -= bytes;
    }
}

internal sealed class ExcelPackage : IDisposable
{
    internal static readonly XNamespace S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XName RevisionUid = XName.Get("uid", "http://schemas.microsoft.com/office/spreadsheetml/2014/revision");
    private static readonly XName TableColumnUid = XName.Get("uid", "http://schemas.microsoft.com/office/spreadsheetml/2016/revision3");
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace P = "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly XNamespace Ct = "http://schemas.openxmlformats.org/package/2006/content-types";
    private const string WorkbookPart = "xl/workbook.xml";
    private const string WorkbookRelationships = "xl/_rels/workbook.xml.rels";
    private const string ContentTypes = "[Content_Types].xml";
    private readonly RawExcelZip _zip;
    private readonly ExcelMemoryBudget _budget;
    private readonly ExcelWorkbookLimits _limits;
    private readonly Dictionary<string, XDocument> _loaded = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, XDocument> _changes = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _removed = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<uint, ExcelWorksheet> _worksheets = [];
    private readonly Dictionary<uint, string?> _sheetUids = [];
    private readonly Dictionary<uint, (ExcelSheet Sheet, string Path, bool IsWorksheet)> _sheets = [];
    private readonly XElement _sheetList;
    private readonly XElement _relationships;
    private Dictionary<string, (uint SheetId, XElement Table)>? _tablesByName;
    private ExcelStyles? _styles;
    private string? _themeSignature;
    private (XElement Markup, string Text)[]? _strings;

    internal ExcelPackage(byte[] bytes, ExcelWorkbookLimits limits, ExcelMemoryBudget budget)
    {
        _limits = limits;
        _budget = budget;
        if (bytes.LongLength > limits.MaxPackageBytes)
            throw new InvalidDataException("The workbook exceeds the package size limit.");

        _zip = new RawExcelZip(bytes, limits.MaxExpandedBytes, budget);
        try
        {
            var workbook = Root(WorkbookPart, S + "workbook");
            _relationships = Root(WorkbookRelationships, P + "Relationships");
            _sheetList = workbook.Element(S + "sheets")
                ?? throw new InvalidDataException("The workbook has no sheet list.");
            Date1904 = IsTrue(workbook.Element(S + "workbookPr")?.Attribute("date1904"));
            foreach (var sheet in _sheetList.Elements(S + "sheet"))
            {
                var name = Required(sheet, "name");
                var id = (uint?)sheet.Attribute("sheetId") ?? 0;
                var relationship = Relationship(_relationships, Required(sheet, R + "id"));
                var path = ResolvePart(WorkbookPart, relationship);
                var isWorksheet = (string?)relationship.Attribute("Type") == R.NamespaceName + "/worksheet";
                if (id == 0 || !_sheets.TryAdd(id, (new ExcelSheet(id, name), path, isWorksheet)))
                    throw new InvalidDataException("The workbook contains an invalid or duplicate sheetId.");
            }
        }
        catch
        {
            _zip.Dispose();
            throw;
        }
    }

    internal bool Date1904 { get; }
    internal string DefinedNames => _sheetList.Parent?.Element(S + "definedNames") is { } names
        ? ExcelStyles.Signature(names) : "";
    internal IReadOnlyList<ExcelSheet> ListSheets() => _sheets.Values.Select(entry => entry.Sheet).ToArray();
    internal bool HasSheet(uint id) => _sheets.ContainsKey(id);
    internal string SheetPath(uint id) => _sheets[id].Path;
    internal bool IsWorksheet(uint id) => _sheets[id].IsWorksheet;
    internal string? SheetUid(uint id)
    {
        var path = SheetPath(id);
        if (_loaded.TryGetValue(path, out var document))
            return (string?)document.Root?.Attribute(RevisionUid);
        if (!_sheetUids.TryGetValue(id, out var uid))
        {
            uid = _zip.ReadRootAttribute(path, RevisionUid);
            Charge(64L + (uid?.Length ?? 0) * 2L);
            _sheetUids.Add(id, uid);
        }
        return uid;
    }

    internal ExcelStyles Styles => _styles ??= new ExcelStyles(OptionalWorkbookPart("styles") is { } part
        ? Root(part, S + "styleSheet") : null, ThemeSignature);

    private string ThemeSignature() => _themeSignature ??= OptionalWorkbookPart("theme") is { } part
        ? ExcelStyles.Signature(Root(part, XName.Get("theme", "http://schemas.openxmlformats.org/drawingml/2006/main")))
        : "";

    internal ExcelWorksheet Sheet(uint id)
    {
        if (!_sheets.TryGetValue(id, out var sheet))
            throw new ArgumentException($"Sheet ID {id} is absent from the workbook.", nameof(id));
        if (!_worksheets.TryGetValue(id, out var worksheet))
        {
            var root = Root(sheet.Path, S + "worksheet");
            _worksheets.Add(id, worksheet = new ExcelWorksheet(this, root, sheet.Path));
        }
        return worksheet;
    }

    internal (XElement Markup, string Text) SharedString(string index)
    {
        if (_strings is null)
        {
            var part = OptionalWorkbookPart("sharedStrings")
                ?? throw new InvalidDataException("A cell references a missing shared string table.");
            _strings = Root(part, S + "sst").Elements(S + "si").Select(element =>
            {
                var text = ReadString(element);
                Charge(16 + text.Length * 2L);
                return (element, text);
            }).ToArray();
        }
        if (!int.TryParse(index, NumberStyles.None, CultureInfo.InvariantCulture, out var offset) ||
            offset >= _strings.Length)
            throw new InvalidDataException("A cell references an invalid shared string index.");
        return _strings[offset];
    }

    internal static string ReadString(XElement value) => string.Concat(value.Elements()
        .Where(element => element.Name == S + "t" || element.Name == S + "r")
        .Select(element => DecodeString(element.Name == S + "t" ? element.Value : element.Element(S + "t")?.Value ?? "")));

    internal static IEnumerable<(int Offset, string? Properties)> StringFormatting(XElement? value)
    {
        if (value is null)
            yield break;

        var offset = value.Element(S + "t") is { } plain ? DecodeString(plain.Value).Length : 0;
        string? previous = null;
        // Compare where the formatting changes so equivalent runs can be split or joined.
        foreach (var run in value.Elements(S + "r"))
        {
            var text = run.Element(S + "t");
            if (text is null || text.Value.Length == 0)
                continue;
            var properties = run.Element(S + "rPr") is { HasElements: true } format
                ? ExcelStyles.Signature(format) : null;
            if (properties != previous)
                yield return (offset, properties);
            previous = properties;
            offset += DecodeString(text.Value).Length;
        }
    }

    internal void MarkSheetChanged(uint sheetId) => MarkChanged(_sheets[sheetId].Path);

    internal uint AddSheet(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Length > 31 || name.IndexOfAny(['[', ']', ':', '*', '?', '/', '\\']) >= 0 ||
            name.Any(char.IsControl) || name.StartsWith('\'') || name.EndsWith('\'') ||
            name.Equals("History", StringComparison.OrdinalIgnoreCase) ||
            _sheets.Values.Any(entry => entry.Sheet.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("The sheet name is invalid or already in use.", nameof(name));
        XmlConvert.VerifyXmlChars(name);
        if (IsTrue(_sheetList.Parent?.Element(S + "workbookProtection")?.Attribute("lockStructure")))
            throw new InvalidOperationException("The workbook structure is protected.");

        uint id = _sheets.Count == 0 ? 1 : checked(_sheets.Keys.Max() + 1);
        var relationshipId = $"rIdSheet{id}";
        while (_relationships.Elements().Any(r => (string?)r.Attribute("Id") == relationshipId))
            relationshipId += "_";
        var path = $"xl/worksheets/sheet{id}.xml";
        while (_zip.Contains(path) || _changes.ContainsKey(path))
            path = path.Insert(path.Length - 4, "_");
        _sheetList.Add(new XElement(S + "sheet", new XAttribute("name", name), new XAttribute("sheetId", id),
            new XAttribute(R + "id", relationshipId)));
        _relationships.Add(new XElement(P + "Relationship", new XAttribute("Id", relationshipId),
            new XAttribute("Type", R.NamespaceName + "/worksheet"), new XAttribute("Target", path[3..])));
        var types = Root(ContentTypes, Ct + "Types");
        types.Add(new XElement(Ct + "Override", new XAttribute("PartName", "/" + path),
            new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml")));
        _changes.Add(path, new XDocument(new XElement(S + "worksheet",
            new XElement(S + "dimension", new XAttribute("ref", "A1")), new XElement(S + "sheetData"))));
        _sheets.Add(id, (new ExcelSheet(id, name), path, true));
        MarkChanged(WorkbookPart);
        MarkChanged(WorkbookRelationships);
        MarkChanged(ContentTypes);
        return id;
    }

    internal void PruneCalculationChain(uint sheetId, HashSet<CellAddress> changed)
    {
        if (changed.Count == 0 || FindWorkbookRelationship("calcChain") is not { } relationship)
            return;
        var path = ResolvePart(WorkbookPart, relationship);
        var chain = Root(path, S + "calcChain");
        uint? originalSheet = null;
        uint? writtenSheet = null;
        var modified = false;
        foreach (var cell in chain.Elements(S + "c").ToArray())
        {
            var explicitSheet = (uint?)cell.Attribute("i");
            originalSheet = explicitSheet ?? originalSheet
                ?? throw new InvalidDataException("The calculation chain is missing its first sheet ID.");
            if (originalSheet == sheetId && changed.Contains(CellAddress.Parse(Required(cell, "r"))))
            {
                cell.Remove();
                modified = true;
                continue;
            }
            if (explicitSheet is null && writtenSheet != originalSheet)
                cell.SetAttributeValue("i", originalSheet);
            writtenSheet = originalSheet;
        }
        if (!modified)
            return;
        if (chain.Elements(S + "c").Any())
        {
            MarkChanged(path);
            return;
        }

        // The chain schema requires at least one cell. Remove its package registrations
        // only when all its original entries have been pruned.
        _removed.Add(path);
        relationship.Remove();
        MarkChanged(WorkbookRelationships);
        var types = Root(ContentTypes, Ct + "Types");
        types.Elements(Ct + "Override").Where(entry =>
            string.Equals((string?)entry.Attribute("PartName"), "/" + path, StringComparison.OrdinalIgnoreCase)).Remove();
        MarkChanged(ContentTypes);
    }

    internal IEnumerable<XElement> Tables(XElement worksheet, string part)
    {
        var tableIds = worksheet.Element(S + "tableParts")?.Elements(S + "tablePart");
        if (tableIds is null)
            yield break;
        var rels = Root(RelationshipsPath(part), P + "Relationships");
        foreach (var table in tableIds)
            yield return Root(ResolvePart(part, Relationship(rels, Required(table, R + "id"))), S + "table");
    }

    internal bool TableBindingDiffers(ExcelPackage current, string name)
    {
        var before = TablesByName().GetValueOrDefault(name);
        var after = current.TablesByName().GetValueOrDefault(name);
        if (before.Table is null || after.Table is null)
            return before.Table != after.Table;

        // Relationship IDs and part paths can be reassigned when a workbook is repacked.
        return before.SheetId != after.SheetId || !SameText(SheetUid(before.SheetId), current.SheetUid(after.SheetId)) ||
            !SameIdentity(before.Table, after.Table, RevisionUid) ||
            CellRange.Parse(Required(before.Table, "ref")) != CellRange.Parse(Required(after.Table, "ref")) ||
            ((uint?)before.Table.Attribute("headerRowCount") ?? 1) != ((uint?)after.Table.Attribute("headerRowCount") ?? 1) ||
            ((uint?)before.Table.Attribute("totalsRowCount") ?? 0) != ((uint?)after.Table.Attribute("totalsRowCount") ?? 0) ||
            !Columns(before.Table).SequenceEqual(Columns(after.Table), EqualityComparer<XElement>.Create((left, right) =>
                left is not null && right is not null && SameIdentity(left, right, TableColumnUid) &&
                SameText((string?)left.Attribute("name"), (string?)right.Attribute("name"))));

        static IEnumerable<XElement> Columns(XElement table) => table.Element(S + "tableColumns")?.Elements(S + "tableColumn") ?? [];
        static bool SameIdentity(XElement left, XElement right, XName uid) =>
            (uint?)left.Attribute("id") == (uint?)right.Attribute("id") && SameText((string?)left.Attribute(uid), (string?)right.Attribute(uid));
        static bool SameText(string? left, string? right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private Dictionary<string, (uint SheetId, XElement Table)> TablesByName()
    {
        if (_tablesByName is not null)
            return _tablesByName;

        var tables = new Dictionary<string, (uint, XElement)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, (_, part, isWorksheet)) in _sheets)
        {
            if (!isWorksheet)
                continue;
            var relationshipsPart = RelationshipsPath(part);
            if (!_zip.Contains(relationshipsPart))
                continue;
            var relationships = Root(relationshipsPart, P + "Relationships");
            if (!relationships.Elements(P + "Relationship").Any(rel => (string?)rel.Attribute("Type") == R.NamespaceName + "/table"))
                continue;
            foreach (var table in Tables(Root(part, S + "worksheet"), part))
            {
                var name = Required(table, "displayName");
                Charge(96L + name.Length * 2L);
                if (!tables.TryAdd(name, (id, table)))
                    throw new InvalidDataException("The workbook contains duplicate table names.");
            }
        }
        return _tablesByName = tables;
    }

    private static string RelationshipsPath(string part)
    {
        var separator = part.LastIndexOf('/');
        return part[..(separator + 1)] + "_rels/" + part[(separator + 1)..] + ".rels";
    }

    internal byte[] Save()
    {
        // Account for replacement ZIP, output stream growth and returned buffer.
        _budget.Charge(_zip.PackageBytes * 3L);
        foreach (var (path, document) in _changes)
        {
            var growthBound = document.Descendants().Sum(element =>
                24L + element.Name.LocalName.Length * 4 +
                element.Attributes().Sum(attribute => 12L + (attribute.Name.LocalName.Length + attribute.Value.Length) * 6L) +
                element.Nodes().OfType<XText>().Sum(text => text.Value.Length * 6L));
            _budget.Charge(growthBound * 3);
        }
        return _zip.Rewrite(_changes, _removed, _limits, _budget);
    }

    internal void Charge(long bytes) => _budget.Charge(bytes);

    private void MarkChanged(string path) => _changes.TryAdd(path, _loaded[path]);

    private XElement Root(string part, XName name)
    {
        if (!_loaded.TryGetValue(part, out var document))
        {
            document = _zip.ReadXml(part, _budget);
            _loaded.Add(part, document);
        }
        if (document.Root?.Name != name)
            throw new InvalidDataException($"'{part}' is not an OOXML {name.LocalName} part.");
        return document.Root;
    }

    private string? OptionalWorkbookPart(string type) =>
        FindWorkbookRelationship(type) is { } relationship ? ResolvePart(WorkbookPart, relationship) : null;

    private XElement? FindWorkbookRelationship(string type) =>
        _relationships.Elements(P + "Relationship").SingleOrDefault(r =>
            (string?)r.Attribute("Type") == R.NamespaceName + "/" + type);

    private static XElement Relationship(XElement relationships, string id) =>
        relationships.Elements(P + "Relationship").SingleOrDefault(r => (string?)r.Attribute("Id") == id)
        ?? throw new InvalidDataException($"The workbook relationship '{id}' is missing.");

    private static string ResolvePart(string source, XElement relationship)
    {
        var target = Required(relationship, "Target");
        var origin = new Uri("https://package.invalid/" + source);
        if ((string?)relationship.Attribute("TargetMode") == "External" ||
            !Uri.TryCreate(origin, target, out var uri) || uri.Host != origin.Host ||
            uri.Scheme != origin.Scheme || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw new InvalidDataException("An Excel part relationship has an invalid package target.");
        return uri.AbsolutePath.TrimStart('/');
    }

    internal static string Required(XElement element, XName name) => (string?)element.Attribute(name)
        ?? throw new InvalidDataException($"The {element.Name.LocalName} element has no {name.LocalName} attribute.");

    internal static bool IsTrue(XAttribute? value) => value?.Value is "1" or "true";

    internal static string DecodeString(string value) => DecodeEscapes.Replace(value,
        match => ((char)ushort.Parse(match.ValueSpan[2..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToString());

    internal static string EncodeString(string value)
    {
        value = DecodeEscapes.Replace(value, match => "_x005F_" + match.Value[1..]);
        return InvalidStringXml.Replace(value, match => $"_x{(int)match.Value[0]:X4}_");
    }

    private static readonly System.Text.RegularExpressions.Regex DecodeEscapes = new(@"_x[0-9A-Fa-f]{4}_");
    private static readonly System.Text.RegularExpressions.Regex InvalidStringXml = new(@"[\u0000-\u0008\u000B\u000C\u000D-\u001F\uFFFE\uFFFF]");

    public void Dispose() => _zip.Dispose();
}
