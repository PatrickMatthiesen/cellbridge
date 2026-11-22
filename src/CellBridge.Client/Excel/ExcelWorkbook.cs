using System.Globalization;

namespace CellBridge.Client.Excel;

public sealed record ExcelMergeResult(
    ExcelWriteStatus Status, byte[]? Package, IReadOnlyList<ExcelCellConflict> Conflicts, string? Detail = null)
{
    internal uint? AddedSheetId { get; init; }
}

public sealed record ExcelSheetAddition(uint SheetId, byte[] Package);

/// <summary>Reads and edits workbook packages without a CellBridge connection.</summary>
public static class ExcelWorkbook
{
    public static IReadOnlyList<ExcelSheet> ListSheets(byte[] package, ExcelWorkbookLimits? limits = null) =>
        ReadProperties(package, limits).Sheets;

    internal static (IReadOnlyList<ExcelSheet> Sheets, bool Date1904) ReadProperties(byte[] package, ExcelWorkbookLimits? limits)
    {
        var (settings, budget) = Prepare(limits, package.LongLength);
        using var workbook = new ExcelPackage(package, settings, budget);
        return (workbook.ListSheets(), workbook.Date1904);
    }

    public static IReadOnlyList<ExcelCell> ReadRange(byte[] package, uint sheetId, string a1Range,
        int maxCells = 10_000, ExcelWorkbookLimits? limits = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCells);
        var (settings, budget) = Prepare(limits, package.LongLength);
        using var workbook = new ExcelPackage(package, settings, budget);
        var sheet = workbook.Sheet(sheetId);
        var result = new List<ExcelCell>();
        foreach (var address in CellRange.Parse(a1Range).Enumerate(Math.Min(maxCells, settings.MaxCellsPerOperation)))
        {
            var cell = sheet.Read(address);
            budget.Charge(200L + (cell.Value.RawValue?.Length ?? 0) * 2L + (cell.CachedValue?.RawValue?.Length ?? 0) * 2L);
            result.Add(cell);
        }
        return result;
    }

    /// <summary>Merges edit intent against the specified base and current packages.</summary>
    /// <remarks>A successful result has a null package when the current package already has the requested values.</remarks>
    public static ExcelMergeResult MergeCells(byte[] basePackage, byte[] currentPackage, uint sheetId,
        IEnumerable<ExcelCellEdit> edits, IReadOnlyDictionary<string, ExcelCellValue>? expectedValues = null,
        ExcelConflictPolicy policy = ExcelConflictPolicy.FailWithConflict, ExcelWorkbookLimits? limits = null)
    {
        var sameRevision = ReferenceEquals(basePackage, currentPackage) || basePackage.AsSpan().SequenceEqual(currentPackage);
        var (settings, budget) = Prepare(limits, currentPackage.LongLength + (sameRevision ? 0 : basePackage.LongLength));
        var changes = ParseEdits(edits, settings.MaxCellsPerOperation, budget);
        var expected = expectedValues is null ? null : ParseEdits(
            expectedValues.Select(pair => new ExcelCellEdit(pair.Key, pair.Value)), settings.MaxCellsPerOperation, budget,
            validateValues: false);
        return MergeCells(basePackage, currentPackage, sameRevision, sheetId, changes, expected, policy, settings, budget);
    }

    public static ExcelSheetAddition AddSheet(byte[] package, string sheetName, ExcelWorkbookLimits? limits = null)
    {
        var (settings, budget) = Prepare(limits, package.LongLength);
        return AddSheet(package, sheetName, settings, budget);
    }

    internal static ExcelSheetAddition AddSheet(byte[] bytes, string name, ExcelWorkbookLimits limits,
        ExcelMemoryBudget budget)
    {
        using var workbook = new ExcelPackage(bytes, limits, budget);
        var sheetId = workbook.AddSheet(name);
        return new ExcelSheetAddition(sheetId, workbook.Save());
    }

    internal static ExcelMergeResult MergeCells(byte[] baseBytes, byte[] currentBytes, bool sameRevision, uint sheetId,
        Dictionary<CellAddress, ExcelCellValue> changes, Dictionary<CellAddress, ExcelCellValue>? expected,
        ExcelConflictPolicy policy, ExcelWorkbookLimits limits, ExcelMemoryBudget budget)
    {
        if (!Enum.IsDefined(policy))
            throw new ArgumentOutOfRangeException(nameof(policy));
        if (expected is not null && expected.Keys.Any(address => !changes.ContainsKey(address)))
            throw new ArgumentException("Expected values must correspond to edited cells.", nameof(expected));

        using var current = new ExcelPackage(currentBytes, limits, budget);
        using var original = sameRevision ? null : new ExcelPackage(baseBytes, limits, budget);
        if (!(original ?? current).HasSheet(sheetId))
            throw new ArgumentException($"Sheet ID {sheetId} is absent from the base workbook.", nameof(sheetId));
        if (!current.HasSheet(sheetId))
            return new ExcelMergeResult(ExcelWriteStatus.StructuralConflict, null, [], "The sheet was deleted.");
        if (original is not null && !string.Equals(original.SheetPath(sheetId), current.SheetPath(sheetId),
            StringComparison.OrdinalIgnoreCase))
            return new ExcelMergeResult(ExcelWriteStatus.StructuralConflict, null, [], "The worksheet identity changed.");

        var remote = current.Sheet(sheetId);
        var before = original?.Sheet(sheetId) ?? remote;
        if (original is not null && !string.Equals(original.SheetUid(sheetId), current.SheetUid(sheetId),
            StringComparison.OrdinalIgnoreCase))
            return new ExcelMergeResult(ExcelWriteStatus.StructuralConflict, null, [], "The worksheet identity changed.");
        if (!sameRevision && before.StructureDiffers(remote))
            return new ExcelMergeResult(ExcelWriteStatus.StructuralConflict, null, [], "The worksheet structure changed.");

        var conflicts = new List<ExcelCellConflict>();
        var pending = new Dictionary<CellAddress, ExcelCellValue>();
        foreach (var (address, local) in changes)
        {
            var baseValue = before.Value(address);
            var remoteValue = remote.Value(address);
            if (expected is not null && expected.TryGetValue(address, out var condition) &&
                (!ExcelWorksheet.Equivalent(condition, baseValue) || !ExcelWorksheet.Equivalent(condition, remoteValue)))
            {
                conflicts.Add(new ExcelCellConflict(address.ToString(), baseValue, remoteValue, local));
                continue;
            }
            remote.CheckEditable(address);
            if (ExcelWorksheet.Equivalent(local, remoteValue))
                continue;

            var overlap = !sameRevision && (!ExcelWorksheet.Equivalent(baseValue, remoteValue) ||
                before.EffectiveStyle(address) != remote.EffectiveStyle(address) ||
                (baseValue.Type == ExcelCellType.String &&
                 !before.StringFormatting(address).SequenceEqual(remote.StringFormatting(address))));
            if (overlap && policy != ExcelConflictPolicy.LocalWins)
            {
                if (policy == ExcelConflictPolicy.FailWithConflict)
                    conflicts.Add(new ExcelCellConflict(address.ToString(), baseValue, remoteValue, local));
                continue;
            }
            pending.Add(address, local);
        }
        if (conflicts.Count > 0)
            return new ExcelMergeResult(ExcelWriteStatus.CellConflict, null, conflicts);
        if (original is not null && FormulaSheetConflict(original, current, sheetId, pending.Values) is { } detail)
            return new ExcelMergeResult(ExcelWriteStatus.StructuralConflict, null, [], detail);

        var changed = remote.Apply(pending);
        if (changed.Count == 0)
            return new ExcelMergeResult(ExcelWriteStatus.Success, null, []);
        current.MarkSheetChanged(sheetId);
        current.PruneCalculationChain(sheetId, changed);
        return new ExcelMergeResult(ExcelWriteStatus.Success, current.Save(), []);
    }

    private static string? FormulaSheetConflict(ExcelPackage original, ExcelPackage current, uint editedSheetId,
        IEnumerable<ExcelCellValue> pending)
    {
        IReadOnlyList<ExcelSheet>? originalSheets = null;
        IReadOnlyList<ExcelSheet>? currentSheets = null;
        Dictionary<string, int>? originalIndices = null;
        Dictionary<string, int>? currentIndices = null;
        var verifiedRanges = new HashSet<(int First, int Last)>();
        var verifiedSheets = new HashSet<uint> { editedSheetId };
        foreach (var value in pending)
        {
            if (value.Type != ExcelCellType.Formula)
                continue;
            foreach (var (first, last) in ExcelFormula.LocalSheetReferences(value.RawValue!))
            {
                var reference = last is null ? first : $"{first}:{last}";
                originalSheets ??= original.ListSheets();
                currentSheets ??= current.ListSheets();
                originalIndices ??= SheetIndices(originalSheets);
                currentIndices ??= SheetIndices(currentSheets);
                if (!originalIndices.TryGetValue(first, out var beforeFirst) ||
                    !currentIndices.TryGetValue(first, out var afterFirst) ||
                    !originalIndices.TryGetValue(last ?? first, out var beforeLast) ||
                    !currentIndices.TryGetValue(last ?? first, out var afterLast) ||
                    beforeLast - beforeFirst != afterLast - afterFirst)
                    return $"The worksheet binding for formula reference '{reference}' changed.";
                if (!verifiedRanges.Add((beforeFirst, beforeLast)))
                    continue;

                var length = Math.Abs(beforeLast - beforeFirst) + 1;
                for (var offset = 0; offset < length; offset++)
                {
                    var step = beforeLast < beforeFirst ? -offset : offset;
                    var beforeSheet = originalSheets[beforeFirst + step];
                    var afterSheet = currentSheets[afterFirst + step];
                    var targetId = beforeSheet.SheetId;
                    if (targetId != afterSheet.SheetId)
                        return $"The worksheet binding for formula reference '{reference}' changed.";
                    if (!verifiedSheets.Add(targetId))
                        continue;

                    var isWorksheet = original.IsWorksheet(targetId);
                    if (isWorksheet != current.IsWorksheet(targetId) ||
                        !string.Equals(original.SheetPath(targetId), current.SheetPath(targetId),
                            StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(original.SheetUid(targetId), current.SheetUid(targetId),
                            StringComparison.OrdinalIgnoreCase))
                        return $"The worksheet binding for formula reference '{reference}' changed.";
                    if (isWorksheet && original.Sheet(targetId).StructureDiffers(current.Sheet(targetId)))
                        return $"The structure of worksheet '{beforeSheet.Name}' changed for formula reference '{reference}'.";
                }
            }
        }
        return null;

        static Dictionary<string, int> SheetIndices(IReadOnlyList<ExcelSheet> sheets)
        {
            var indices = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < sheets.Count; i++)
            {
                if (!indices.TryAdd(sheets[i].Name, i))
                    throw new InvalidDataException("The workbook contains duplicate worksheet names.");
            }
            return indices;
        }
    }

    internal static Dictionary<CellAddress, ExcelCellValue> ParseEdits(IEnumerable<ExcelCellEdit> edits, int maxCells,
        ExcelMemoryBudget budget, bool validateValues = true)
    {
        ArgumentNullException.ThrowIfNull(edits);
        var result = new Dictionary<CellAddress, ExcelCellValue>();
        foreach (var edit in edits)
        {
            if (result.Count == maxCells)
                throw new ArgumentException($"The operation exceeds the {maxCells} cell limit.", nameof(edits));
            ArgumentNullException.ThrowIfNull(edit.Value);
            var address = CellAddress.Parse(edit.Address);
            budget.Charge(200L + (edit.Value.RawValue?.Length ?? 0) * 8L);
            if (validateValues)
                ValidateValue(edit.Value);
            if (!result.TryAdd(address, edit.Value))
                throw new ArgumentException($"Duplicate cell address '{address}'.", nameof(edits));
        }
        return result;
    }

    private static void ValidateValue(ExcelCellValue value)
    {
        var raw = value.RawValue;
        switch (value.Type)
        {
            case ExcelCellType.Blank when raw is null:
            case ExcelCellType.Boolean when raw is "0" or "1" or "true" or "false":
                return;
            case ExcelCellType.String when raw is { Length: <= 32767 }:
                System.Xml.XmlConvert.VerifyXmlChars(ExcelPackage.EncodeString(raw));
                return;
            case ExcelCellType.Number when raw is { Length: <= 64 } &&
                double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number):
                return;
            case ExcelCellType.Formula when raw is not null:
                ExcelFormula.Validate(raw);
                return;
            default:
                throw new ArgumentException($"Invalid or unsupported Excel {value.Type} value.", nameof(value));
        }
    }

    private static (ExcelWorkbookLimits Limits, ExcelMemoryBudget Budget) Prepare(ExcelWorkbookLimits? limits, long bytes)
    {
        limits ??= new ExcelWorkbookLimits();
        limits.Validate();
        var budget = new ExcelMemoryBudget(limits.MaxSessionBytes);
        budget.Charge(bytes);
        return (limits, budget);
    }
}
