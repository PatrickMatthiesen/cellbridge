using System.Globalization;
using CellBridge.Client.Excel;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace CellBridge.Client.Tests;

public sealed class ExcelWorkbookPropertyTests
{
    private const uint SheetId = 1;
    private const int RepeatedValue = 7;
    private static readonly byte[] EmptyWorkbook = OfficeDocumentTemplates.CreateExcelWorkbook();
    private static readonly string[] FormulaColumns = ["X", "Y", "Z", "AA", "AB", "AC", "AD", "AE", "AF", "AG", "AH", "AI", "AJ"];

    public enum Movement { Insert, Delete, Permute }

    public static TheoryData<int> Seeds => new(Enumerable.Range(8100, 16));

    public static TheoryData<int, Movement, bool> StructuralCases
    {
        get
        {
            var cases = new TheoryData<int, Movement, bool>();
            foreach (var seed in Enumerable.Range(8100, 8))
            foreach (var movement in Enum.GetValues<Movement>())
            {
                cases.Add(seed, movement, false);
                cases.Add(seed, movement, true);
            }
            return cases;
        }
    }

    [Theory]
    [MemberData(nameof(StructuralCases))]
    public void StaleWritesConflictWhenTheAddressNowBelongsToADifferentLogicalRecord(int seed, Movement movement, bool columns)
    {
        var random = new Random(seed);
        var originalIds = RecordIds(random);
        var currentIds = originalIds.ToList();
        var span = random.Next(1, 4);
        var pivot = random.Next(1, currentIds.Count - 2 * span);
        int staleIndex;
        switch (movement)
        {
            case Movement.Insert:
                currentIds.InsertRange(pivot, Enumerable.Range(1000, span));
                staleIndex = random.Next(pivot + span, originalIds.Length);
                break;
            case Movement.Delete:
                currentIds.RemoveRange(pivot, span);
                staleIndex = random.Next(pivot + span, currentIds.Count);
                break;
            default:
                // Rotate a contiguous block. Every record in the block changes position.
                var length = random.Next(2, currentIds.Count - pivot + 1);
                var rotation = random.Next(1, length);
                var block = currentIds.GetRange(pivot, length);
                currentIds.RemoveRange(pivot, length);
                currentIds.InsertRange(pivot, block.Skip(rotation).Concat(block.Take(rotation)));
                staleIndex = random.Next(pivot, pivot + length);
                break;
        }

        var before = RecordWorkbook(originalIds, columns);
        var current = RecordWorkbook(currentIds, columns);
        var idAddress = RecordAddress(staleIndex, field: 0, columns);
        var previousId = ReadValue(before, idAddress);
        Assert.Equal(ExcelCellValue.Number(originalIds[staleIndex]), previousId);
        Assert.Equal(ExcelCellValue.Number(currentIds[staleIndex]), ReadValue(current, idAddress));
        Assert.NotEqual(originalIds[staleIndex], currentIds[staleIndex]);
        var movedIndex = currentIds.IndexOf(originalIds[staleIndex]);
        Assert.InRange(movedIndex, 0, currentIds.Count - 1);
        Assert.Equal(previousId, ReadValue(current, RecordAddress(movedIndex, field: 0, columns)));

        // Try each target separately so a conflict on either edit cannot mask a missed conflict on the other.
        foreach (var field in new[] { 1, 2 })
        {
            var address = RecordAddress(staleIndex, field, columns);
            Assert.Equal(ReadValue(before, address), ReadValue(current, address));
            var result = ExcelWorkbook.MergeCells(before, current, SheetId, [new(address, ExcelCellValue.Number(-1))]);

            Assert.True(result.Status == ExcelWriteStatus.StructuralConflict,
                $"Seed {seed}, {movement}, columns {columns}, before [{string.Join(',', originalIds)}], " +
                $"after [{string.Join(',', currentIds)}], target {address}. Received {result.Status}.");
            Assert.Null(result.Package);
        }
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void DisjointEditsRetainBothWritersValuesAndUntouchedRecords(int seed)
    {
        var random = new Random(seed);
        var ids = RecordIds(random);
        var columns = seed % 2 == 0;
        var before = RecordWorkbook(ids, columns);
        var records = Enumerable.Range(0, ids.Length).ToArray();
        random.Shuffle(records);
        var remote = new[]
        {
            new ExcelCellEdit(RecordAddress(records[0], field: 1, columns), ExcelCellValue.Number(-seed)),
            new ExcelCellEdit(RecordAddress(records[1], field: 2, columns), ExcelCellValue.Boolean(seed % 3 == 0))
        };
        var local = new[]
        {
            new ExcelCellEdit(RecordAddress(records[2], field: 1, columns), ExcelCellValue.Blank),
            new ExcelCellEdit(RecordAddress(records[3], field: 2, columns), ExcelCellValue.String($"note {seed}"))
        };
        var current = RequirePackage(ExcelWorkbook.MergeCells(before, before, SheetId, remote), seed);
        var final = RequirePackage(ExcelWorkbook.MergeCells(before, current, SheetId, local), seed);

        var expected = new Dictionary<string, ExcelCellValue>();
        for (var index = 0; index < ids.Length; index++)
        {
            expected.Add(RecordAddress(index, field: 0, columns), ExcelCellValue.Number(ids[index]));
            expected.Add(RecordAddress(index, field: 1, columns), ExcelCellValue.Number(RepeatedValue));
            expected.Add(RecordAddress(index, field: 2, columns), ExcelCellValue.Blank);
        }
        foreach (var edit in remote.Concat(local))
            expected[edit.Address] = edit.Value;

        var range = $"A1:{RecordAddress(ids.Length - 1, field: 2, columns)}";
        var finalCells = ExcelWorkbook.ReadRange(final, SheetId, range);
        Assert.Equal(expected.Count, finalCells.Count);
        foreach (var cell in finalCells)
            Assert.True(expected[cell.Address] == cell.Value,
                $"Seed {seed}, {cell.Address}. Expected {expected[cell.Address]}, received {cell.Value}.");
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void GeneratedSupportedFormulaRoundTripsAndRejectsAnIncompleteExpression(int seed)
    {
        var formula = new FormulaCase(new Random(seed)).Render();
        var value = ExcelCellValue.Formula(formula);
        var edited = RequirePackage(ExcelWorkbook.MergeCells(EmptyWorkbook, EmptyWorkbook, SheetId, [new("A1", value)]), seed);

        Assert.Equal(value, ReadValue(edited, "A1"));

        var incomplete = seed % 2 == 0 ? formula + "+" : formula[..^1];
        // The formula always ends with a string literal. Removing its last quote leaves it unterminated.
        var error = Record.Exception(() => ExcelWorkbook.MergeCells(EmptyWorkbook, EmptyWorkbook, SheetId,
            [new("A1", ExcelCellValue.Formula(incomplete))]));
        Assert.True(error is ArgumentException,
            $"Seed {seed}, incomplete formula {incomplete}. Received {error?.GetType().Name ?? "no exception"}.");
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void SharedFormulaFollowerOnlyMovesRelativeAxes(int seed)
    {
        var random = new Random(seed);
        var formula = new FormulaCase(random);
        var rowOffset = seed % 4 == 0 ? 0 : random.Next(1, 5);
        var columnOffset = seed % 4 == 1 ? 0 : random.Next(1, 5);
        var masterRow = random.Next(1, 10);
        var masterColumn = random.Next(0, 6);
        var masterAddress = SmallAddress(masterRow, masterColumn);
        var followerAddress = SmallAddress(masterRow + rowOffset, masterColumn + columnOffset);
        var master = new Cell(new CellFormula(formula.Render())
        {
            FormulaType = CellFormulaValues.Shared,
            SharedIndex = 0,
            Reference = $"{masterAddress}:{followerAddress}"
        }) { CellReference = masterAddress };
        var follower = new Cell(new CellFormula { FormulaType = CellFormulaValues.Shared, SharedIndex = 0 })
        {
            CellReference = followerAddress
        };
        var first = new Row(master) { RowIndex = (uint)masterRow };
        var rows = new List<Row> { first };
        if (rowOffset == 0)
            first.Append(follower);
        else
            rows.Add(new Row(follower) { RowIndex = (uint)(masterRow + rowOffset) });

        var workbook = WithRows(rows);
        var expected = ExcelCellValue.Formula(formula.Render(rowOffset, columnOffset));
        var actual = ReadValue(workbook, followerAddress);

        Assert.True(expected == actual,
            $"Seed {seed}, {masterAddress} to {followerAddress}, master {formula.Render()}. " +
            $"Expected {expected.RawValue}, received {actual.RawValue}.");
    }

    private static int[] RecordIds(Random random)
    {
        var ids = Enumerable.Range(101, random.Next(9, 15)).ToArray();
        random.Shuffle(ids);
        return ids;
    }

    private static byte[] RecordWorkbook(IReadOnlyList<int> ids, bool columns)
    {
        const int populatedFields = 2;
        var rows = new List<Row>();
        if (columns)
        {
            for (var field = 0; field < populatedFields; field++)
                rows.Add(new Row(ids.Select((id, index) =>
                    NumberCell(RecordAddress(index, field, columns), field == 0 ? id : RepeatedValue)))
                { RowIndex = (uint)(field + 1) });
        }
        else
        {
            for (var index = 0; index < ids.Count; index++)
                rows.Add(new Row(NumberCell(RecordAddress(index, field: 0, columns), ids[index]),
                    NumberCell(RecordAddress(index, field: 1, columns), RepeatedValue)) { RowIndex = (uint)(index + 1) });
        }
        return WithRows(rows);
    }

    private static Cell NumberCell(string address, int value) =>
        new(new CellValue(value.ToString(CultureInfo.InvariantCulture))) { CellReference = address };

    private static string RecordAddress(int index, int field, bool columns) =>
        columns ? SmallAddress(field + 1, index) : SmallAddress(index + 1, field);

    private static string SmallAddress(int row, int column) => FormattableString.Invariant($"{(char)('A' + column)}{row}");

    private static byte[] WithRows(IEnumerable<Row> rows)
    {
        using var stream = new MemoryStream();
        stream.Write(EmptyWorkbook);
        stream.Position = 0;
        using (var document = SpreadsheetDocument.Open(stream, true))
        {
            var worksheet = Assert.IsType<Worksheet>(Assert.Single(document.WorkbookPart!.WorksheetParts).Worksheet);
            Assert.IsType<SheetData>(worksheet.GetFirstChild<SheetData>()).Append(rows);
            worksheet.Save();
        }
        return stream.ToArray();
    }

    private static ExcelCellValue ReadValue(byte[] workbook, string address) =>
        Assert.Single(ExcelWorkbook.ReadRange(workbook, SheetId, address)).Value;

    private static byte[] RequirePackage(ExcelMergeResult result, int seed)
    {
        Assert.True(result.Status == ExcelWriteStatus.Success, $"Seed {seed}. Received {result.Status}. {result.Detail}");
        return Assert.IsType<byte[]>(result.Package);
    }

    private sealed class FormulaCase(Random random)
    {
        private readonly int _column = random.Next(0, 5);
        private readonly int _row = random.Next(1, 100);
        private readonly int _width = random.Next(1, 4);
        private readonly int _height = random.Next(1, 4);
        private readonly int _constant = random.Next(1, 20);

        internal string Render(int rowOffset = 0, int columnOffset = 0)
        {
            var fixedColumn = FormulaColumns[_column];
            var shiftedColumn = FormulaColumns[_column + columnOffset];
            var shiftedEndColumn = FormulaColumns[_column + _width + columnOffset];
            var shiftedRow = _row + rowOffset;
            var shiftedEndRow = _row + _height + rowOffset;
            var columnBoundary = columnOffset == 0 ? "XFD$1048576" : "#REF!";
            var rowBoundary = rowOffset == 0 ? "$XFD1048576" : "#REF!";
            return FormattableString.Invariant(
                $"SUM({shiftedColumn}{shiftedRow},${fixedColumn}{shiftedRow},{shiftedColumn}${_row},${fixedColumn}${_row},'Sheet1'!${fixedColumn}{shiftedRow} : {shiftedEndColumn}{shiftedEndRow},${fixedColumn} : {shiftedEndColumn},${_row} : {shiftedEndRow},{columnBoundary},{rowBoundary},$XFD$1048576,{{{_constant},2;3,4}})&\"{fixedColumn}{_row}\"\" and $A$1\"");
        }
    }
}
