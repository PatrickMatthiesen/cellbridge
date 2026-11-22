using System.Globalization;
using System.Xml.Linq;
using static CellBridge.Client.Excel.ExcelPackage;

namespace CellBridge.Client.Excel;

internal sealed class ExcelWorksheet
{
    private sealed record Row(XElement Element, SortedList<int, XElement> Cells);
    private readonly ExcelPackage _package;
    private readonly XElement _root;
    private readonly XElement _data;
    private readonly SortedList<int, Row> _rows = [];
    private readonly (int Min, int Max, uint Style)[] _columns;
    private readonly CellRange[] _merged;
    private readonly (CellRange Range, string Type)[] _formulaRanges;
    private readonly bool _protected;

    internal ExcelWorksheet(ExcelPackage package, XElement root)
    {
        _package = package;
        _root = root;
        _data = root.Element(S + "sheetData") ?? throw new InvalidDataException("The worksheet has no sheetData.");
        _columns = root.Elements(S + "cols").SelectMany(cols => cols.Elements(S + "col")).Select(col =>
            ((int?)col.Attribute("min") ?? 0, (int?)col.Attribute("max") ?? 0, (uint?)col.Attribute("style") ?? 0)).ToArray();
        _merged = root.Element(S + "mergeCells")?.Elements(S + "mergeCell")
            .Select(merge => CellRange.Parse(Required(merge, "ref"))).ToArray() ?? [];
        _protected = IsTrue(root.Element(S + "sheetProtection")?.Attribute("sheet"));
        var ranges = new List<(CellRange Range, string Type)>();
        var rowIndex = 0;
        foreach (var row in _data.Elements(S + "row"))
        {
            var index = (int?)row.Attribute("r") ?? rowIndex + 1;
            if (index <= rowIndex || index > CellAddress.LastRow)
                throw new InvalidDataException("Worksheet rows are out of order or out of range.");
            rowIndex = index;
            _package.Charge(128);
            var cells = new SortedList<int, XElement>();
            var column = 0;
            foreach (var cell in row.Elements(S + "c"))
            {
                var address = (string?)cell.Attribute("r") is { } reference
                    ? CellAddress.Parse(reference) : new CellAddress(index, column + 1);
                if (address.Row != index || address.Column <= column || address.Column > CellAddress.LastColumn)
                    throw new InvalidDataException("Worksheet cells are out of order or out of range.");
                column = address.Column;
                _package.Charge(40);
                cells.Add(column, cell);
                var formula = cell.Element(S + "f");
                if ((string?)formula?.Attribute("ref") is { } range && (string?)formula.Attribute("t") is { } type)
                {
                    ranges.Add((CellRange.Parse(range), type));
                    if (type == "shared" && (string?)formula.Attribute("si") is { } group)
                        _sharedFormulaMasters.Add(group, (formula, address));
                }
            }
            _rows.Add(index, new Row(row, cells));
        }
        _formulaRanges = ranges.ToArray();
    }

    internal ExcelCell Read(CellAddress address)
    {
        var cell = Find(address);
        var style = Style(address, cell);
        var value = Value(cell, address);
        var cached = value.Type == ExcelCellType.Formula && cell!.Element(S + "v") is { } result &&
            (result.Value.Length > 0 || (string?)cell.Attribute("t") == "str") ? Scalar(cell) : null;
        return new ExcelCell(address.ToString(), value, cached, cached is not null,
            style, AsDate(cached ?? value, style));
    }

    internal ExcelCellValue Value(CellAddress address) => Value(Find(address), address);

    internal string EffectiveStyle(CellAddress address) => _package.Styles.Signature(Style(address, Find(address)));

    private XElement? Find(CellAddress address) =>
        _rows.TryGetValue(address.Row, out var row) && row.Cells.TryGetValue(address.Column, out var cell) ? cell : null;

    private uint Style(CellAddress address, XElement? cell)
    {
        if ((uint?)cell?.Attribute("s") is { } explicitStyle)
            return explicitStyle;
        if (_rows.TryGetValue(address.Row, out var row) && IsTrue(row.Element.Attribute("customFormat")) &&
            (uint?)row.Element.Attribute("s") is { } rowStyle)
            return rowStyle;
        return _columns.LastOrDefault(column => address.Column >= column.Min && address.Column <= column.Max).Style;
    }

    private ExcelCellValue Value(XElement? cell, CellAddress address)
    {
        if (cell is null)
            return ExcelCellValue.Blank;
        if (cell.Element(S + "f") is { } formula)
        {
            // Dependent shared formulas store a group ID rather than repeating the master's text.
            var text = formula.Value;
            if (text.Length == 0 && (string?)formula.Attribute("t") == "shared")
            {
                if (!_sharedFormulaMasters.TryGetValue((string?)formula.Attribute("si") ?? "", out var master))
                    throw new InvalidDataException("A shared formula has no master cell.");
                text = ExcelFormula.Translate(master.Formula.Value, master.Address, address);
            }
            return new ExcelCellValue(ExcelCellType.Formula, text);
        }
        return Scalar(cell);
    }

    private readonly Dictionary<string, (XElement Formula, CellAddress Address)> _sharedFormulaMasters = new(StringComparer.Ordinal);

    private ExcelCellValue Scalar(XElement cell)
    {
        var raw = cell.Element(S + "v")?.Value;
        var type = (string?)cell.Attribute("t");
        if (type == "inlineStr" && cell.Element(S + "is") is { } inline)
            return ExcelCellValue.String(ReadString(inline));
        if (raw is null)
            return ExcelCellValue.Blank;
        return type switch
        {
            "s" => ExcelCellValue.String(_package.SharedString(raw)),
            "str" => ExcelCellValue.String(DecodeString(raw)),
            "b" => new ExcelCellValue(ExcelCellType.Boolean, raw),
            "e" => new ExcelCellValue(ExcelCellType.Error, raw),
            "d" => new ExcelCellValue(ExcelCellType.Date, raw),
            null or "n" => raw.Length == 0 ? ExcelCellValue.Blank : new ExcelCellValue(ExcelCellType.Number, raw),
            _ => throw new InvalidDataException($"The worksheet contains an unsupported cell type '{type}'.")
        };
    }

    private DateTime? AsDate(ExcelCellValue value, uint style)
    {
        if (value.Type == ExcelCellType.Date &&
            DateTime.TryParse(value.RawValue, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var iso))
            return iso;
        if (value.Type != ExcelCellType.Number || !_package.Styles.IsDate(style) ||
            !double.TryParse(value.RawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial) ||
            !double.IsFinite(serial))
            return null;
        if (_package.Date1904)
            return FromDays(new DateTime(1904, 1, 1), serial);
        if (serial is >= 60 and < 61)
            return null; // Excel reserves serial 60 for the fictitious date 1900-02-29.
        return FromDays(new DateTime(1899, 12, 31), serial >= 61 ? serial - 1 : serial);
    }

    private static DateTime? FromDays(DateTime epoch, double serial) =>
        serial >= (DateTime.MinValue - epoch).TotalDays && serial < (DateTime.MaxValue - epoch).TotalDays
            ? epoch.AddDays(serial) : null;

    internal static bool Equivalent(ExcelCellValue first, ExcelCellValue second)
    {
        if (first == second)
            return true;
        if (first.Type != second.Type)
            return false;
        if (first.Type == ExcelCellType.Boolean)
            return BooleanValue(first.RawValue) == BooleanValue(second.RawValue);
        return first.Type == ExcelCellType.Number &&
            double.TryParse(first.RawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var left) &&
            double.TryParse(second.RawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var right) && left == right;
    }

    private static bool BooleanValue(string? value) => value is "1" or "true";

    internal void CheckEditable(CellAddress address)
    {
        var cell = Find(address);
        if (_protected && _package.Styles.IsLocked(Style(address, cell)))
            throw new InvalidOperationException($"Cell {address} is locked on a protected worksheet.");
        if (_merged.Any(range => range.Contains(address) && range.Start != address))
            throw new InvalidOperationException($"Cell {address} is a non-origin cell in a merged range.");
        var formulaType = (string?)cell?.Element(S + "f")?.Attribute("t");
        if (formulaType is "shared" or "array" or "dataTable" || cell?.Attribute("cm") is not null ||
            cell?.Attribute("vm") is not null || _formulaRanges.Any(range => range.Range.Contains(address)))
            throw new InvalidOperationException($"Cell {address} belongs to a shared formula, array, data table, spill range, or cell metadata type.");
    }

    internal HashSet<CellAddress> Apply(IReadOnlyDictionary<CellAddress, ExcelCellValue> edits)
    {
        var changed = new HashSet<CellAddress>();
        foreach (var (address, value) in edits.OrderBy(pair => pair.Key))
        {
            if (Equivalent(Value(address), value))
                continue;
            Write(address, value);
            changed.Add(address);
        }
        foreach (var rowIndex in changed.Select(address => address.Row).Distinct())
        {
            var row = _rows[rowIndex];
            if (row.Cells.Count == 0 && row.Element.Attributes().All(attr => attr.Name.LocalName is "r" or "spans") &&
                !row.Element.Elements().Any())
            {
                row.Element.Remove();
                _rows.Remove(rowIndex);
                continue;
            }
            row.Element.SetAttributeValue("spans", row.Cells.Count == 0 ? null : $"{row.Cells.Keys[0]}:{row.Cells.Keys[^1]}");
        }
        if (changed.Count > 0)
            UpdateDimension();
        return changed;
    }

    private void Write(CellAddress address, ExcelCellValue value)
    {
        if (!_rows.TryGetValue(address.Row, out var row))
        {
            var element = new XElement(S + "row", new XAttribute("r", address.Row));
            _rows.Add(address.Row, row = new Row(element, []));
            var index = _rows.IndexOfKey(address.Row);
            if (index + 1 < _rows.Count)
                _rows.Values[index + 1].Element.AddBeforeSelf(element);
            else
                _data.Add(element);
        }
        if (!row.Cells.TryGetValue(address.Column, out var cell))
        {
            cell = new XElement(S + "c", new XAttribute("r", address.ToString()));
            var style = Style(address, cell);
            if (style != 0)
                cell.SetAttributeValue("s", style);
            row.Cells.Add(address.Column, cell);
            var index = row.Cells.IndexOfKey(address.Column);
            if (index + 1 < row.Cells.Count)
                row.Cells.Values[index + 1].AddBeforeSelf(cell);
            else if (row.Element.Element(S + "extLst") is { } extension)
                extension.AddBeforeSelf(cell);
            else
                row.Element.Add(cell);
        }
        cell.Elements().Where(element => element.Name == S + "v" || element.Name == S + "f" || element.Name == S + "is").Remove();
        cell.SetAttributeValue("t", null);
        switch (value.Type)
        {
            case ExcelCellType.Blank:
                if (!cell.HasElements && cell.Attributes().All(attr => attr.Name == "r"))
                {
                    cell.Remove();
                    row.Cells.Remove(address.Column);
                }
                break;
            case ExcelCellType.String:
                cell.SetAttributeValue("t", "inlineStr");
                cell.AddFirst(new XElement(S + "is", new XElement(S + "t",
                    new XAttribute(XNamespace.Xml + "space", "preserve"), EncodeString(value.RawValue!))));
                break;
            case ExcelCellType.Formula:
                cell.AddFirst(new XElement(S + "f", value.RawValue));
                break;
            case ExcelCellType.Boolean:
                cell.SetAttributeValue("t", "b");
                cell.AddFirst(new XElement(S + "v", BooleanValue(value.RawValue) ? "1" : "0"));
                break;
            case ExcelCellType.Number:
                cell.AddFirst(new XElement(S + "v", value.RawValue));
                break;
        }
    }

    private string ActualDimension()
    {
        var populated = _rows.Where(row => row.Value.Cells.Count > 0).ToArray();
        if (populated.Length == 0)
            return "A1";
        var first = new CellAddress(populated[0].Key, populated.Min(row => row.Value.Cells.Keys[0]));
        var last = new CellAddress(populated[^1].Key, populated.Max(row => row.Value.Cells.Keys[^1]));
        return new CellRange(first, last).ToString();
    }

    private void UpdateDimension()
    {
        // Make implicit coordinates explicit before a row or column insertion changes their interpretation.
        foreach (var (rowIndex, row) in _rows)
        {
            if (row.Element.Attribute("r") is null)
                row.Element.SetAttributeValue("r", rowIndex);
            foreach (var (column, cell) in row.Cells)
                if (cell.Attribute("r") is null)
                    cell.SetAttributeValue("r", new CellAddress(rowIndex, column).ToString());
        }
        var dimension = _root.Element(S + "dimension");
        if (dimension is null)
        {
            dimension = new XElement(S + "dimension");
            if (_root.Element(S + "sheetPr") is { } properties)
                properties.AddAfterSelf(dimension);
            else
                _root.AddFirst(dimension);
        }
        dimension.SetAttributeValue("ref", ActualDimension());
    }

    internal bool StructureDiffers(ExcelWorksheet remote)
    {
        if (!StructuralMarkup().SequenceEqual(remote.StructuralMarkup()) ||
            _package.Date1904 != remote._package.Date1904 || _package.DefinedNames != remote._package.DefinedNames)
            return true;

        // OOXML does not record the operation that produced a cell grid. Detect
        // identifiable values that moved as evidence of insertion, deletion or sorting.
        var removed = new Dictionary<ExcelCellValue, Movement>();
        var added = new Dictionary<ExcelCellValue, Movement>();
        foreach (var (row, data) in _rows)
        foreach (var (column, cell) in data.Cells)
        {
            var address = new CellAddress(row, column);
            var before = Value(cell, address);
            var after = remote.Value(address);
            if (!Equivalent(before, after))
            {
                TrackMovement(removed, before, address);
                TrackMovement(added, after, address);
            }
        }
        foreach (var (row, data) in remote._rows)
        foreach (var (column, cell) in data.Cells)
        {
            var address = new CellAddress(row, column);
            if (Find(address) is null)
                TrackMovement(added, remote.Value(cell, address), address);
        }

        var offsets = new HashSet<(int Row, int Column)>();
        foreach (var (value, source) in removed)
        {
            if (!added.TryGetValue(value, out var destination))
                continue;
            var from = source.First;
            var to = destination.First;
            var fromAfter = remote.Value(from);
            var toBefore = Value(to);
            if (Equivalent(fromAfter, toBefore))
                return true; // A swap, or a move between a formerly empty and now empty cell.

            var offset = (to.Row - from.Row, to.Column - from.Column);
            if (!offsets.Add(offset) || (source.Count > 1 && destination.Count > 1 &&
                offset == (destination.Last.Row - source.Last.Row, destination.Last.Column - source.Last.Column)))
                return true;
        }
        return false;
    }

    private readonly record struct Movement(CellAddress First, CellAddress Last, int Count);

    private void TrackMovement(Dictionary<ExcelCellValue, Movement> values, ExcelCellValue value, CellAddress address)
    {
        if (value.Type == ExcelCellType.Blank)
            return;
        if (value.Type == ExcelCellType.Number &&
            double.TryParse(value.RawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            value = ExcelCellValue.Number(number);
        else if (value.Type == ExcelCellType.Boolean)
            value = ExcelCellValue.Boolean(BooleanValue(value.RawValue));
        if (values.TryGetValue(value, out var movement))
        {
            values[value] = new Movement(address.CompareTo(movement.First) < 0 ? address : movement.First,
                address.CompareTo(movement.Last) > 0 ? address : movement.Last, movement.Count + 1);
        }
        else
        {
            _package.Charge(120L + (value.RawValue?.Length ?? 0) * 2L);
            values.Add(value, new Movement(address, address, 1));
        }
    }

    private IEnumerable<string> StructuralMarkup()
    {
        foreach (var element in _root.Elements())
            if (element.Name.Namespace == S && element.Name.LocalName is
                "sheetProtection" or "protectedRanges" or "mergeCells" or "autoFilter" or "sortState" or
                "rowBreaks" or "colBreaks" or "dataValidations" or "conditionalFormatting" or "hyperlinks")
                yield return ExcelStyles.Signature(element);

        foreach (var range in _formulaRanges)
            yield return $"{range.Type}:{range.Range}";
        foreach (var table in _package.Tables(_root))
            yield return ExcelStyles.Signature(table);
        if ((string?)_root.Element(S + "dimension")?.Attribute("ref") is { } declared &&
            CellRange.Parse(declared).ToString() != ActualDimension())
            yield return "dimension:" + CellRange.Parse(declared);
    }

}
