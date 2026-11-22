using System.Globalization;
using System.Xml.Linq;
using static CellBridge.Client.Excel.ExcelPackage;

namespace CellBridge.Client.Excel;

internal sealed class ExcelWorksheet
{
    private const long MaxWildcardFormulaCharacters = 16L * 1024 * 1024;
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

    internal IEnumerable<(int Offset, string? Properties)> StringFormatting(CellAddress address)
    {
        var cell = Find(address);
        var markup = (string?)cell?.Attribute("t") switch
        {
            "inlineStr" => cell.Element(S + "is"),
            "s" when cell.Element(S + "v") is { } index => _package.SharedString(index.Value).Markup,
            _ => null
        };
        return ExcelPackage.StringFormatting(markup);
    }

    private XElement? Find(CellAddress address) =>
        _rows.TryGetValue(address.Row, out var row) && row.Cells.TryGetValue(address.Column, out var cell) ? cell : null;

    private uint Style(CellAddress address, XElement? cell)
    {
        if ((uint?)cell?.Attribute("s") is { } explicitStyle)
            return explicitStyle;
        if (_rows.TryGetValue(address.Row, out var row) && CustomRowStyle(row) is { } rowStyle)
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
            "s" => ExcelCellValue.String(_package.SharedString(raw).Text),
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
            _package.Date1904 != remote._package.Date1904 || _package.DefinedNames != remote._package.DefinedNames ||
            ColumnLayoutMoved(remote) || RowStyleMoved(remote))
            return true;

        // OOXML does not record the operation that produced a cell grid. Detect
        // identifiable values that moved as evidence of insertion, deletion or sorting.
        var movements = new Dictionary<ExcelCellValue, ValueMovement>();
        var styledBlankMovements = new Dictionary<string, ValueMovement>(StringComparer.Ordinal);
        // Keep raw formula matches and also look for references Excel adjusted during a shift.
        var formulaMovements = new Dictionary<ExcelCellValue, ValueMovement>();
        // A shift can adjust references that neither raw nor relative signatures match.
        var formulaChanges = new Dictionary<(bool IsRow, int Index, string Pattern, FormulaChange Change), FormulaPatterns>();
        long wildcardCharactersRemaining = MaxWildcardFormulaCharacters;
        foreach (var (row, data) in _rows)
        foreach (var (column, cell) in data.Cells)
        {
            var address = new CellAddress(row, column);
            var before = Value(cell, address);
            var remoteCell = remote.Find(address);
            var after = remote.Value(remoteCell, address);
            var beforeBlankStyle = ExplicitBlankStyle(cell, before);
            var afterBlankStyle = remote.ExplicitBlankStyle(remoteCell, after);
            if (beforeBlankStyle != afterBlankStyle && EffectiveStyle(address) != remote.EffectiveStyle(address))
                RecordStyledBlankMovement(beforeBlankStyle, afterBlankStyle, address);
            if (Equivalent(before, after))
                continue;

            var hadFormula = before.Type == ExcelCellType.Formula;
            var hasFormula = after.Type == ExcelCellType.Formula;
            if ((hadFormula || hasFormula) && HasAlignedFormulaChange(address, hasFormula ? after : before,
                hasFormula ? FormulaChange.Introduced : FormulaChange.Removed))
                return true;

            RecordMovement(movements, before, after, address);
            if (hadFormula || hasFormula)
                RecordMovement(formulaMovements, RelativeFormula(before, address), RelativeFormula(after, address), address,
                    formulasOnly: true);
        }
        foreach (var (row, data) in remote._rows)
        foreach (var (column, cell) in data.Cells)
        {
            var address = new CellAddress(row, column);
            if (Find(address) is not null)
                continue;
            var after = remote.Value(cell, address);
            RecordStyledBlankMovement(null, remote.ExplicitBlankStyle(cell, after), address);
            var hasFormula = after.Type == ExcelCellType.Formula;
            if (hasFormula && HasAlignedFormulaChange(address, after, FormulaChange.Introduced))
                return true;

            RecordMovement(movements, ExcelCellValue.Blank, after, address);
            if (hasFormula)
                RecordMovement(formulaMovements, ExcelCellValue.Blank, RelativeFormula(after, address), address);
        }

        return HasMovement(movements.Values) || HasMovement(formulaMovements.Values) || HasMovement(styledBlankMovements.Values);

        void RecordStyledBlankMovement(string? before, string? after, CellAddress address)
        {
            if (before == after)
                return;
            // Styling or unstyling one blank is common. Require corroborating movements.
            if (before is not null)
            {
                _package.Charge(48);
                FindMovement(styledBlankMovements, before, before.Length).Removed.Add(new Occurrence(address, false));
            }
            if (after is not null)
            {
                _package.Charge(48);
                FindMovement(styledBlankMovements, after, after.Length).Added.Add(new Occurrence(address, false));
            }
        }

        bool HasAlignedFormulaChange(CellAddress address, ExcelCellValue formula, FormulaChange change)
        {
            var pattern = ExcelFormula.ReferencePattern(formula.RawValue!, ignoreAnchors: false);
            var relaxed = ExcelFormula.ReferencePattern(formula.RawValue!, ignoreAnchors: true);
            var broken = ExcelFormula.HasBrokenReference(pattern);
            var opposite = change == FormulaChange.Introduced ? FormulaChange.Removed : FormulaChange.Introduced;
            return OnAxis(true, address.Row) || OnAxis(false, address.Column);

            bool OnAxis(bool isRow, int index)
            {
                if (formulaChanges.TryGetValue((isRow, index, relaxed, opposite), out var previous) &&
                    (previous.Intact.Contains(pattern) ||
                     HasCompatibleAnchors(previous.Broken, pattern) ||
                     broken && HasCompatibleAnchors(previous.Intact, pattern)))
                    return true;

                var key = (isRow, index, relaxed, change);
                if (!formulaChanges.TryGetValue(key, out var patterns))
                {
                    _package.Charge(192L + relaxed.Length * 2L);
                    formulaChanges.Add(key, patterns = new());
                }
                if ((broken ? patterns.Broken : patterns.Intact).Add(pattern))
                    _package.Charge(64L + pattern.Length * 2L);
                return false;
            }
        }

        bool HasCompatibleAnchors(HashSet<string> alternatives, string pattern)
        {
            foreach (var candidate in alternatives)
            {
                // A wildcard check can scan the entire pattern, even on a mismatch.
                wildcardCharactersRemaining -= pattern.Length;
                if (wildcardCharactersRemaining < 0)
                    throw new InvalidDataException("Worksheet merge exceeds the broken-reference comparison limit.");
                if (ExcelFormula.CompatibleReferenceAnchors(pattern, candidate))
                    return true;
            }
            return false;
        }
    }

    private string? ExplicitBlankStyle(XElement? cell, ExcelCellValue value)
    {
        if (value.Type != ExcelCellType.Blank || (uint?)cell?.Attribute("s") is not { } index || index == 0)
            return null;
        var signature = _package.Styles.Signature(index);
        return signature != _package.Styles.Signature(0) ? signature : null;
    }

    private enum FormulaChange { Introduced, Removed }

    private sealed class FormulaPatterns
    {
        internal HashSet<string> Intact { get; } = new(StringComparer.Ordinal);
        internal HashSet<string> Broken { get; } = new(StringComparer.Ordinal);
    }

    private static ExcelCellValue RelativeFormula(ExcelCellValue value, CellAddress address) =>
        value.Type == ExcelCellType.Formula
            ? value with { RawValue = ExcelFormula.RelativeSignature(value.RawValue!, address) } : value;

    private void RecordMovement(Dictionary<ExcelCellValue, ValueMovement> movements, ExcelCellValue before,
        ExcelCellValue after, CellAddress address, bool formulasOnly = false)
    {
        if (Equivalent(before, after))
            return;
        var source = !formulasOnly || before.Type == ExcelCellType.Formula ? FindValueMovement(movements, before) : null;
        var destination = !formulasOnly || after.Type == ExcelCellType.Formula ? FindValueMovement(movements, after) : null;
        if (source is not null)
        {
            _package.Charge(48);
            source.Removed.Add(new Occurrence(address, after.Type == ExcelCellType.Blank));
        }
        if (destination is not null)
        {
            _package.Charge(48);
            destination.Added.Add(new Occurrence(address, before.Type == ExcelCellType.Blank));
        }
        if (source is null || destination is null || source == destination)
            return;
        if (source.Replacements is null)
        {
            _package.Charge(64);
            source.Replacements = [];
        }
        _package.Charge(16);
        source.Replacements.Add(destination);
        destination.IncomingReplacements++;
    }

    private bool HasMovement(ICollection<ValueMovement> movements)
    {
        var offsets = new HashSet<(int Row, int Column)>();
        foreach (var movement in movements)
        {
            var source = movement.Removed;
            var destination = movement.Added;
            if (source.Count == 0 || destination.Count == 0)
                continue;
            // An occurrence at a cleared or newly filled cell can reveal a shift
            // even when another occurrence of the same value was overwritten.
            if ((source.HasBlank && destination.HasBlank) ||
                HasAlignedBlankEndpoint(source, destination) || HasAlignedBlankEndpoint(destination, source))
                return true;

            var from = source.First;
            var to = destination.First;
            var offset = (to.Row - from.Row, to.Column - from.Column);
            if (!offsets.Add(offset) || (source.Count > 1 && destination.Count > 1 &&
                offset == (destination.Last.Row - source.Last.Row, destination.Last.Column - source.Last.Column)))
                return true;
        }
        return HasReplacementCycle(movements);
    }

    private bool HasAlignedBlankEndpoint(Occurrences endpoints, Occurrences other)
    {
        if (!endpoints.HasBlank)
            return false;
        if (endpoints.Count == 1)
            return other.AlignedWith(endpoints.First, requireBlank: false);
        if (other.Count == 1)
            return endpoints.AlignedWith(other.First, requireBlank: true);

        _package.Charge(64L + 64L * endpoints.Count);
        var rows = new HashSet<int>();
        var columns = new HashSet<int>();
        foreach (var endpoint in endpoints.Items)
            if (endpoint.OtherIsBlank)
            {
                rows.Add(endpoint.Address.Row);
                columns.Add(endpoint.Address.Column);
            }
        return other.Items.Any(item => rows.Contains(item.Address.Row) || columns.Contains(item.Address.Column));
    }

    private static bool HasReplacementCycle(ICollection<ValueMovement> movements)
    {
        // Follow replacements by value. A sort can close a cycle even if
        // some cells stayed in place or other changes occurred alongside it.
        var ready = new Queue<ValueMovement>();
        foreach (var movement in movements)
            if (movement.IncomingReplacements == 0)
                ready.Enqueue(movement);

        var remaining = movements.Count;
        while (ready.TryDequeue(out var movement))
        {
            remaining--;
            if (movement.Replacements is not { } replacements)
                continue;
            foreach (var replacement in replacements)
                if (--replacement.IncomingReplacements == 0)
                    ready.Enqueue(replacement);
        }
        return remaining > 0;
    }

    private sealed class ValueMovement
    {
        internal Occurrences Removed;
        internal Occurrences Added;
        internal List<ValueMovement>? Replacements;
        internal int IncomingReplacements;
    }

    private readonly record struct Occurrence(CellAddress Address, bool OtherIsBlank);

    private struct Occurrences
    {
        private Occurrence _first;
        private List<Occurrence>? _additional;
        internal CellAddress First { get; private set; }
        internal CellAddress Last { get; private set; }
        internal int Count { get; private set; }
        internal bool HasBlank { get; private set; }

        internal void Add(Occurrence occurrence)
        {
            var address = occurrence.Address;
            if (Count == 0)
                _first = occurrence;
            else
                (_additional ??= []).Add(occurrence);
            if (Count == 0 || address.CompareTo(First) < 0)
                First = address;
            if (Count == 0 || address.CompareTo(Last) > 0)
                Last = address;
            Count++;
            HasBlank |= occurrence.OtherIsBlank;
        }

        internal IEnumerable<Occurrence> Items
        {
            get
            {
                yield return _first;
                if (_additional is not null)
                    foreach (var occurrence in _additional)
                        yield return occurrence;
            }
        }

        internal bool AlignedWith(CellAddress address, bool requireBlank) =>
            Items.Any(item => (!requireBlank || item.OtherIsBlank) &&
                (item.Address.Row == address.Row || item.Address.Column == address.Column));
    }

    private ValueMovement? FindValueMovement(Dictionary<ExcelCellValue, ValueMovement> values, ExcelCellValue value)
    {
        if (value.Type == ExcelCellType.Blank)
            return null;
        if (value.Type == ExcelCellType.Number &&
            double.TryParse(value.RawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            value = ExcelCellValue.Number(number == 0 ? 0 : number);
        else if (value.Type == ExcelCellType.Boolean)
            value = ExcelCellValue.Boolean(BooleanValue(value.RawValue));
        return FindMovement(values, value, value.RawValue?.Length ?? 0);
    }

    private ValueMovement FindMovement<TKey>(Dictionary<TKey, ValueMovement> values, TKey value, int textLength)
        where TKey : notnull
    {
        if (values.TryGetValue(value, out var movement))
            return movement;

        _package.Charge(144L + textLength * 2L);
        movement = new ValueMovement();
        values.Add(value, movement);
        return movement;
    }

    private bool RowStyleMoved(ExcelWorksheet remote)
    {
        var before = RowStyles();
        var after = remote.RowStyles();
        if (before.Keys.ToHashSet().SetEquals(after.Keys))
            return false;
        var removed = before.Where(row => after.GetValueOrDefault(row.Key) != row.Value).Select(row => row.Value)
            .ToHashSet(StringComparer.Ordinal);
        var added = after.Where(row => before.GetValueOrDefault(row.Key) != row.Value).Select(row => row.Value)
            .ToHashSet(StringComparer.Ordinal);
        return before.Any(row => !remote._rows.ContainsKey(row.Key) && added.Contains(row.Value)) ||
            after.Any(row => !_rows.ContainsKey(row.Key) && removed.Contains(row.Value));
    }

    private Dictionary<int, string> RowStyles()
    {
        var styles = new Dictionary<int, string>();
        var defaultStyle = _package.Styles.Signature(0);
        bool hasStyledColumn = _columns.Any(column => _package.Styles.Signature(column.Style) != defaultStyle);
        foreach (var (index, row) in _rows)
        {
            if (CustomRowStyle(row) is not { } style)
                continue;
            var signature = _package.Styles.Signature(style);
            if (signature == defaultStyle && !hasStyledColumn)
                continue;
            _package.Charge(64);
            styles.Add(index, signature);
        }
        return styles;
    }

    private static uint? CustomRowStyle(Row row) => IsTrue(row.Element.Attribute("customFormat"))
        ? (uint?)row.Element.Attribute("s") : null;

    private bool ColumnLayoutMoved(ExcelWorksheet remote)
    {
        var before = ColumnLayouts().ToHashSet();
        var after = remote.ColumnLayouts().ToHashSet();
        var beforeCoverage = ColumnCoverage(before).ToArray();
        var afterCoverage = ColumnCoverage(after).ToArray();
        if (beforeCoverage.SequenceEqual(afterCoverage))
            return false;

        // A matching old and new layout is evidence of movement only when at
        // least one candidate range intersects changed column coverage.
        _package.Charge((CellAddress.LastColumn + 2L) * sizeof(int));
        var coverageChanges = new int[CellAddress.LastColumn + 2];
        foreach (var (min, max) in beforeCoverage)
        {
            coverageChanges[min]++;
            coverageChanges[max + 1]--;
        }
        foreach (var (min, max) in afterCoverage)
        {
            coverageChanges[min]--;
            coverageChanges[max + 1]++;
        }
        int coverage = 0, changedColumns = 0;
        for (int column = 1; column <= CellAddress.LastColumn; column++)
        {
            coverage += coverageChanges[column];
            if (coverage != 0)
                changedColumns++;
            coverageChanges[column] = changedColumns;
        }

        var removed = before.Except(after).ToArray();
        var added = after.Except(before).ToArray();
        return MatchesChangedCoverage(removed, added) || MatchesChangedCoverage(added, removed);

        bool MatchesChangedCoverage(IEnumerable<(int Min, int Max, string Signature)> endpoints,
            IEnumerable<(int Min, int Max, string Signature)> other)
        {
            var signatures = endpoints.Where(range => coverageChanges[range.Max] != coverageChanges[range.Min - 1])
                .Select(range => range.Signature).ToHashSet(StringComparer.Ordinal);
            return other.Any(range => signatures.Contains(range.Signature));
        }
    }

    private static IEnumerable<(int Min, int Max)> ColumnCoverage(IEnumerable<(int Min, int Max, string Signature)> ranges)
    {
        int min = 0, max = 0;
        foreach (var range in ranges.OrderBy(range => range.Min))
        {
            if (range.Min > max + 1 || min == 0)
            {
                if (min != 0)
                    yield return (min, max);
                min = range.Min;
            }
            max = Math.Max(max, range.Max);
        }
        if (min != 0)
            yield return (min, max);
    }

    private IEnumerable<(int Min, int Max, string Signature)> ColumnLayouts()
    {
        foreach (var column in _root.Elements(S + "cols").SelectMany(cols => cols.Elements(S + "col")))
        {
            var min = (int?)column.Attribute("min") ?? 0;
            var max = (int?)column.Attribute("max") ?? 0;
            if (min < 1 || max < min || max > CellAddress.LastColumn)
                continue;
            var properties = new XElement(column);
            properties.Attribute("min")?.Remove();
            properties.Attribute("max")?.Remove();
            var style = _package.Styles.Signature((uint?)column.Attribute("style") ?? 0);
            properties.SetAttributeValue("style", style == _package.Styles.Signature(0) ? null : style);
            if (!properties.HasElements && !properties.Attributes().Any(attribute => !attribute.IsNamespaceDeclaration))
                continue;
            _package.Charge(256);
            yield return (min, max, ExcelStyles.Signature(properties));
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
        foreach (var (index, row) in _rows)
            foreach (var attribute in row.Element.Attributes()
                .Where(attribute => attribute.Name.Namespace == XNamespace.None &&
                    attribute.Name.LocalName is "ht" or "customHeight" or "hidden" or "outlineLevel" or "collapsed")
                .OrderBy(attribute => attribute.Name.LocalName, StringComparer.Ordinal))
                yield return $"row:{index}:{attribute.Name}={attribute.Value}";
        if ((string?)_root.Element(S + "dimension")?.Attribute("ref") is { } declared &&
            CellRange.Parse(declared).ToString() != ActualDimension())
            yield return "dimension:" + CellRange.Parse(declared);
    }

}
