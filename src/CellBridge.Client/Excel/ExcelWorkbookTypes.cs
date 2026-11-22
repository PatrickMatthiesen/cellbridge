using System.Globalization;

namespace CellBridge.Client.Excel;

public sealed record ExcelWorkbookLimits
{
    // The server applies StorageLimits.MaxDocumentBytes to both package sizes.
    public long MaxPackageBytes { get; init; } = 128L * 1024 * 1024;
    public long MaxExpandedBytes { get; init; } = 128L * 1024 * 1024;
    public long MaxSessionBytes { get; init; } = 512L * 1024 * 1024;
    public int MaxCellsPerOperation { get; init; } = 10_000;
    public int MaxRetainedRevisions { get; init; } = 8;
    public int MaxCommitAttempts { get; init; } = 4;

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxPackageBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxExpandedBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxSessionBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxCellsPerOperation);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxRetainedRevisions);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxCommitAttempts);
    }
}

public sealed record ExcelSheet(uint SheetId, string Name);
public enum ExcelCellType { Blank, String, Number, Boolean, Formula, Error, Date }

/// <summary>Invariant OOXML value, or formula text without the leading equals sign.</summary>
public sealed record ExcelCellValue(ExcelCellType Type, string? RawValue)
{
    public static ExcelCellValue Blank { get; } = new(ExcelCellType.Blank, null);
    public static ExcelCellValue String(string value) => new(ExcelCellType.String, value);
    public static ExcelCellValue Number(double value) => new(ExcelCellType.Number, value.ToString("R", CultureInfo.InvariantCulture));
    public static ExcelCellValue Boolean(bool value) => new(ExcelCellType.Boolean, value ? "1" : "0");
    public static ExcelCellValue Formula(string formula) => new(ExcelCellType.Formula, formula.StartsWith('=') ? formula[1..] : formula);
}

public sealed record ExcelCell(
    string Address,
    ExcelCellValue Value,
    ExcelCellValue? CachedValue,
    bool Calculated,
    uint StyleIndex,
    DateTime? DateValue);

public sealed record ExcelCellEdit(string Address, ExcelCellValue Value);
public enum ExcelConflictPolicy { FailWithConflict, LocalWins, RemoteWins }
public enum ExcelWriteStatus { Success, CellConflict, StructuralConflict, BaseRevisionUnavailable, RetryExhausted }
public sealed record ExcelCellConflict(string Address, ExcelCellValue Base, ExcelCellValue Remote, ExcelCellValue Local);
public sealed record ExcelWriteResult(
    ExcelWriteStatus Status, string Revision, IReadOnlyList<ExcelCellConflict> Conflicts, string? Detail = null)
{
    public uint? AddedSheetId { get; init; }
}

internal readonly record struct CellAddress(int Row, int Column) : IComparable<CellAddress>
{
    internal const int LastRow = 1_048_576;
    internal const int LastColumn = 16_384;

    public int CompareTo(CellAddress other)
    {
        var rowOrder = Row.CompareTo(other.Row);
        return rowOrder != 0 ? rowOrder : Column.CompareTo(other.Column);
    }

    public override string ToString()
    {
        Span<char> letters = stackalloc char[3];
        var offset = letters.Length;
        var column = Column;
        while (column > 0)
        {
            column--;
            letters[--offset] = (char)('A' + column % 26);
            column /= 26;
        }
        return string.Concat(letters[offset..], Row.ToString(CultureInfo.InvariantCulture));
    }

    internal static CellAddress Parse(string text)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        var i = text[0] == '$' ? 1 : 0;
        var column = 0;
        while (i < text.Length && char.IsAsciiLetter(text[i]))
        {
            column = column * 26 + char.ToUpperInvariant(text[i++]) - 'A' + 1;
            if (column > LastColumn)
                throw new ArgumentException($"'{text}' is not a valid Excel cell address.");
        }
        if (i < text.Length && text[i] == '$')
            i++;
        if (column == 0 ||
            !int.TryParse(text.AsSpan(i), NumberStyles.None, CultureInfo.InvariantCulture, out var row) ||
            row is < 1 or > LastRow)
            throw new ArgumentException($"'{text}' is not a valid Excel cell address.");
        return new CellAddress(row, column);
    }
}

internal readonly record struct CellRange(CellAddress Start, CellAddress End)
{
    internal static CellRange Parse(string text)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        var separator = text.IndexOf(':');
        var start = CellAddress.Parse(separator < 0 ? text : text[..separator]);
        var end = separator < 0 ? start : CellAddress.Parse(text[(separator + 1)..]);
        if (start.Row > end.Row || start.Column > end.Column)
            throw new ArgumentException($"'{text}' is not an ascending Excel range.");
        return new CellRange(start, end);
    }

    internal bool Contains(CellAddress address) =>
        address.Row >= Start.Row && address.Row <= End.Row && address.Column >= Start.Column && address.Column <= End.Column;

    internal IEnumerable<CellAddress> Enumerate(int limit)
    {
        if ((long)(End.Row - Start.Row + 1) * (End.Column - Start.Column + 1) > limit)
            throw new ArgumentOutOfRangeException(nameof(limit), $"The range exceeds the {limit} cell limit.");
        for (var row = Start.Row; row <= End.Row; row++)
            for (var column = Start.Column; column <= End.Column; column++)
                yield return new CellAddress(row, column);
    }

    public override string ToString() => Start == End ? Start.ToString() : $"{Start}:{End}";
}
