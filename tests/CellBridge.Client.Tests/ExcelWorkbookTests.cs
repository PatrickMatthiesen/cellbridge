using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using CellBridge.Client.Excel;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;

namespace CellBridge.Client.Tests;

public sealed class ExcelWorkbookTests
{
    private const string Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string OfficeRels = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string PackageRels = "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly XNamespace S = Main;

    [Fact]
    public void EditsKeepOriginalZipRecordsAndWorkbookMarkup()
    {
        var original = Workbook("<row r='4'><c r='C4'><v>3</v></c></row>", nonSeekableZip: true);
        var edited = Edit(original, new ExcelCellEdit("B4", ExcelCellValue.Number(2)),
            new ExcelCellEdit("A3", ExcelCellValue.String("  <hello>  ")),
            new ExcelCellEdit("A1", ExcelCellValue.Boolean(true)),
            new ExcelCellEdit("XFD4", ExcelCellValue.Formula("=SUM(B4:C4)")));
        Validate(edited);

        var sheet = Part(edited, "xl/worksheets/sheet1.xml");
        Assert.Equal("A1:XFD4", (string?)sheet.Root!.Element(S + "dimension")?.Attribute("ref"));
        var rows = sheet.Descendants(S + "row").ToArray();
        Assert.Equal(["1", "3", "4"], rows.Select(row => (string?)row.Attribute("r")));
        Assert.Equal("2:16384", (string?)rows[^1].Attribute("spans"));
        Assert.Equal(["B4", "C4", "XFD4"], rows[^1].Elements(S + "c").Select(cell => (string?)cell.Attribute("r")));
        Assert.Equal("dyDescent", sheet.Root.Attributes().Single(attribute => attribute.Name.LocalName == "dyDescent").Name.LocalName);

        foreach (var name in new[] { "[Content_Types].xml", "_rels/.rels", "xl/workbook.xml",
                     "xl/_rels/workbook.xml.rels", "xl/styles.xml", "xl/worksheets/sheet2.xml" })
        {
            var before = ZipRecord(original, name);
            var after = ZipRecord(edited, name);
            Assert.Equal(before.HeaderAndPayload, after.HeaderAndPayload);
            Assert.Equal(before.CentralDirectoryExceptOffset, after.CentralDirectoryExceptOffset);
        }

        var text = Assert.Single(ExcelWorkbook.ReadRange(edited, 4, "a3"));
        Assert.Equal("  <hello>  ", text.Value.RawValue);
        var formula = Assert.Single(ExcelWorkbook.ReadRange(edited, 4, "XFD4"));
        Assert.Equal(ExcelCellValue.Formula("SUM(B4:C4)"), formula.Value);
        Assert.Null(formula.CachedValue);
        Assert.False(formula.Calculated);
    }

    [Fact]
    public void ReadsDate1904AndInheritsRowAndColumnStyles()
    {
        const string column = "<cols><col min='2' max='2' width='12' style='1' customWidth='1'/></cols>";
        var original = Workbook(
            "<row r='1' customFormat='1' s='1'><c r='A1' s='1'><v>0</v></c><c r='C1' s='1'><f>1+1</f><v>2</v></c></row>",
            beforeData: column, date1904: true);
        var edited = Edit(original, new ExcelCellEdit("A2", ExcelCellValue.Number(4)),
            new ExcelCellEdit("B1", ExcelCellValue.Number(1)), new ExcelCellEdit("B2", ExcelCellValue.Number(3)));
        Validate(edited);

        var cells = ExcelWorkbook.ReadRange(edited, 4, "A1:C2");
        Assert.Equal(new DateTime(1904, 1, 1), cells[0].DateValue);
        Assert.Equal("0", cells[0].Value.RawValue);
        Assert.Equal((uint)1, cells[1].StyleIndex);
        Assert.Equal(new DateTime(1904, 1, 2), cells[1].DateValue);
        Assert.True(cells[2].Calculated);
        Assert.Equal("2", cells[2].CachedValue?.RawValue);
        Assert.Equal(new DateTime(1904, 1, 4), cells[4].DateValue);
        Assert.Equal((uint)1, cells[4].StyleIndex);
        Assert.Equal(ExcelCellValue.Blank, cells[5].Value);
    }

    [Fact]
    public void AddSheetUpdatesRelationshipsAndContentTypesWithStableIds()
    {
        var original = Workbook("");
        var addition = ExcelWorkbook.AddSheet(original, "New data");
        Assert.Equal((uint)10, addition.SheetId);
        Assert.Equal(new[] { new ExcelSheet(4, "First"), new ExcelSheet(9, "Second"), new ExcelSheet(10, "New data") },
            ExcelWorkbook.ListSheets(addition.Package));
        Assert.Equal(ExcelCellValue.Blank, Assert.Single(ExcelWorkbook.ReadRange(addition.Package, 10, "A1")).Value);
        Assert.Equal(ZipRecord(original, "xl/worksheets/sheet1.xml").HeaderAndPayload,
            ZipRecord(addition.Package, "xl/worksheets/sheet1.xml").HeaderAndPayload);
        Validate(addition.Package);
        Assert.Throws<ArgumentException>(() => ExcelWorkbook.AddSheet(original, "fIrSt"));
        Assert.Throws<ArgumentException>(() => ExcelWorkbook.AddSheet(original, "Not/allowed"));
        Assert.Throws<ArgumentException>(() => ExcelWorkbook.AddSheet(original, new string('a', 32)));
    }

    [Fact]
    public void ClearingCellsPrunesOnlyMatchingCalculationEntriesAndPreservesInheritedSheetIds()
    {
        var original = Workbook(
            "<row r='1'><c r='A1'><f>1+1</f><v>2</v></c><c r='B1'><f>2+2</f><v>4</v></c></row>",
            calculationChain: "<c r='A1' i='4'/><c r='B1'/><c r='A1' i='9'/>");
        var edited = Edit(original, new ExcelCellEdit("A1", ExcelCellValue.Blank));
        Validate(edited);
        var chain = Part(edited, "xl/calcChain.xml").Root!.Elements(S + "c").ToArray();
        Assert.Equal(["B1", "A1"], chain.Select(cell => (string?)cell.Attribute("r")));
        Assert.Equal(["4", "9"], chain.Select(cell => (string?)cell.Attribute("i")));
        Assert.Equal("B1", (string?)Part(edited, "xl/worksheets/sheet1.xml").Root!.Element(S + "dimension")?.Attribute("ref"));

        var only = Workbook("<row r='1'><c r='A1'><f>1+1</f><v>2</v></c></row>", calculationChain: "<c r='A1' i='4'/>");
        Validate(Edit(only, new ExcelCellEdit("A1", ExcelCellValue.Blank)));
    }

    [Fact]
    public void MergesDisjointCellsAndReportsAnOverlapWithOriginalValues()
    {
        var original = Workbook("<row r='1'><c r='A1'><v>1</v></c><c r='B1'><v>2</v></c></row>");
        var latest = Edit(original, new ExcelCellEdit("B1", ExcelCellValue.Number(3)));
        var merge = ExcelWorkbook.MergeCells(original, latest, 4, [new("A1", ExcelCellValue.Number(5))]);
        Assert.Equal(ExcelWriteStatus.Success, merge.Status);
        Validate(merge.Package!);
        Assert.Equal(["5", "3"], ExcelWorkbook.ReadRange(merge.Package!, 4, "A1:B1").Select(cell => cell.Value.RawValue));

        var stale = ExcelWorkbook.MergeCells(original, latest, 4, [new("B1", ExcelCellValue.Number(9))]);
        Assert.Equal(ExcelWriteStatus.CellConflict, stale.Status);
        var conflict = Assert.Single(stale.Conflicts);
        Assert.Equal(new ExcelCellConflict("B1", ExcelCellValue.Number(2), ExcelCellValue.Number(3), ExcelCellValue.Number(9)), conflict);
        Assert.Null(stale.Package);
        Assert.Null(ExcelWorkbook.MergeCells(original, latest, 4, [new("B1", ExcelCellValue.Number(3))]).Package);

        var precondition = ExcelWorkbook.MergeCells(original, latest, 4, [new("B1", ExcelCellValue.Number(9))],
            new Dictionary<string, ExcelCellValue> { ["b1"] = ExcelCellValue.Number(2) }, ExcelConflictPolicy.LocalWins);
        Assert.Equal(ExcelWriteStatus.CellConflict, precondition.Status);
    }

    [Fact]
    public void SharedStringReindexingAndSheetReorderingDoNotConflict()
    {
        var original = Workbook("<row r='1'><c r='A1' t='s'><v>0</v></c></row>", sharedStrings: ["<t>same</t>", "<t>other</t>"]);
        var newOrder = Workbook("<row r='1'><c r='A1' t='s'><v>1</v></c></row>",
            sharedStrings: ["<t>other</t>", "<t>same</t>"], reverseSheets: true);
        var result = ExcelWorkbook.MergeCells(original, newOrder, 4, [new("A1", ExcelCellValue.String("changed"))]);
        Assert.Equal(ExcelWriteStatus.Success, result.Status);
        Assert.Equal((uint)9, ExcelWorkbook.ListSheets(result.Package!)[0].SheetId);
        Assert.Equal("changed", Assert.Single(ExcelWorkbook.ReadRange(result.Package!, 4, "A1")).Value.RawValue);
        Validate(result.Package!);
    }

    [Fact]
    public void EffectiveStyleChangeConflictsEvenWhenTheCellValueIsUnchanged()
    {
        var original = Workbook("<row r='1'><c r='A1' s='1'><v>1</v></c></row>");
        var remote = Workbook("<row r='1' s='1' customFormat='1'><c r='A1'><v>1</v></c></row>");
        Assert.Equal(ExcelWriteStatus.Success,
            ExcelWorkbook.MergeCells(original, remote, 4, [new("A1", ExcelCellValue.Number(4))]).Status);
        remote = Workbook("<row r='1'><c r='A1'><v>1</v></c></row>");
        Assert.Equal(ExcelWriteStatus.CellConflict,
            ExcelWorkbook.MergeCells(original, remote, 4, [new("A1", ExcelCellValue.Number(4))]).Status);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void RichTextFormattingChangeConflictsEvenWhenTheCellValueIsUnchanged(bool sharedBefore, bool sharedAfter)
    {
        var original = StringWorkbook(sharedBefore, "<r><t>sa</t></r><r><t>me</t></r>");
        var remote = StringWorkbook(sharedAfter, "<r><t>sa</t></r><r><rPr><b/></rPr><t>me</t></r>");

        var result = ExcelWorkbook.MergeCells(original, remote, 4, [new("A1", ExcelCellValue.String("changed"))]);

        Assert.Equal(ExcelWriteStatus.CellConflict, result.Status);
        var conflict = Assert.Single(result.Conflicts);
        Assert.Equal(ExcelCellValue.String("same"), conflict.Base);
        Assert.Equal(conflict.Base, conflict.Remote);
        Assert.Null(result.Package);
    }

    [Theory]
    [InlineData("<t>same</t>", "<r><t>sa</t></r><r><t>me</t></r>")]
    [InlineData("<r><rPr><b/></rPr><t>same</t></r>",
        "<r><rPr><b/></rPr><t>sa</t></r><r><rPr><b/></rPr><t>me</t></r>")]
    public void EquivalentStringFormattingSurvivesStorageAndRunBoundaryChanges(string before, string after)
    {
        var original = StringWorkbook(false, before);
        var remote = StringWorkbook(true, after);

        var result = ExcelWorkbook.MergeCells(original, remote, 4, [new("A1", ExcelCellValue.String("changed"))]);

        Assert.Equal(ExcelWriteStatus.Success, result.Status);
        Assert.Equal(ExcelCellValue.String("changed"), Assert.Single(ExcelWorkbook.ReadRange(result.Package!, 4, "A1")).Value);
    }

    [Fact]
    public void RowMovesAndSortsConflictEvenWithRepeatedValues()
    {
        const string before =
            "<row r='1'><c r='A1'><v>2</v></c></row><row r='2'><c r='A2'><v>1</v></c></row>" +
            "<row r='3'><c r='A3'><v>2</v></c></row><row r='4'><c r='A4'><v>1</v></c></row>";
        const string sorted =
            "<row r='1'><c r='A1'><v>1</v></c></row><row r='2'><c r='A2'><v>1</v></c></row>" +
            "<row r='3'><c r='A3'><v>2</v></c></row><row r='4'><c r='A4'><v>2</v></c></row>";
        var result = ExcelWorkbook.MergeCells(Workbook(before), Workbook(sorted), 4, [new("B1", ExcelCellValue.Number(5))]);
        Assert.Equal(ExcelWriteStatus.StructuralConflict, result.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ShiftingRowsWithDuplicateValuesConflictsAtAnUnchangedAddress(bool insert)
    {
        var shorter = Workbook(
            "<row r='1'><c r='A1'><v>7</v></c></row><row r='2'><c r='A2'><v>9</v></c></row>");
        var longer = Workbook(
            "<row r='1'><c r='A1'><v>7</v></c></row><row r='2'><c r='A2'><v>7</v></c></row>" +
            "<row r='3'><c r='A3'><v>9</v></c></row>");

        var result = ExcelWorkbook.MergeCells(insert ? shorter : longer, insert ? longer : shorter, 4,
            [new("A1", ExcelCellValue.Number(5))]);

        Assert.Equal(ExcelWriteStatus.StructuralConflict, result.Status);
        Assert.Null(result.Package);
    }

    [Fact]
    public void ShiftConflictsWhenOnlyOneRemovedOccurrenceWasCleared()
    {
        var original = Workbook("<row r='1'><c r='A1' t='str'><v>Y</v></c></row>" +
            "<row r='2'><c r='A2' t='str'><v>X</v></c></row><row r='3'><c r='A3' t='str'><v>Y</v></c></row>");
        var remote = Workbook("<row r='1'><c r='A1' t='str'><v>Z</v></c></row>" +
            "<row r='2'><c r='A2' t='str'><v>Y</v></c></row>");

        var result = ExcelWorkbook.MergeCells(original, remote, 4, [new("B1", ExcelCellValue.Number(5))]);

        Assert.Equal(ExcelWriteStatus.StructuralConflict, result.Status);
        Assert.Null(result.Package);
    }

    [Theory]
    [InlineData("B1", "B2", "B3")]
    [InlineData("SUM($B1,C$1,$D$1,1:3)&amp;&quot;B1&quot;", "SUM($B2,C$1,$D$1,2:4)&amp;&quot;B1&quot;",
        "SUM($B3,C$1,$D$1,3:5)&amp;&quot;B1&quot;")]
    public void FormulaReferencesAdjustedByAnInsertedRowConflictAtAnUnchangedAddress(string first, string second, string third)
    {
        var original = Workbook($"<row r='1'><c r='A1'><f>{first}</f></c></row>" +
            $"<row r='2'><c r='A2'><f>{second}</f></c></row>");
        var remote = Workbook($"<row r='2'><c r='A2'><f>{second}</f></c></row>" +
            $"<row r='3'><c r='A3'><f>{third}</f></c></row>");

        var result = ExcelWorkbook.MergeCells(original, remote, 4, [new("A2", ExcelCellValue.Formula("42"))]);

        Assert.Equal(ExcelWriteStatus.StructuralConflict, result.Status);
        Assert.Null(result.Package);
    }

    [Theory]
    [InlineData("<row r='2'><c r='B2'><f>A1+A3</f></c></row>",
        "<row r='3'><c r='B3'><f>A1+A4</f></c></row>", "C2")]
    [InlineData("<row r='2'><c r='A2'><f>$B$2</f></c></row>",
        "<row r='3'><c r='A3'><f>$B$3</f></c></row>", "C2")]
    [InlineData("<row r='2'><c r='B2'><f>A1+C1</f></c></row>",
        "<row r='2'><c r='C2'><f>A1+D1</f></c></row>", "B3")]
    [InlineData("<row r='2'><c r='B2'><f>99+1</f></c></row><row r='3'><c r='B3'><f>A1+A4</f></c></row>",
        "<row r='2'><c r='B2'><f>A1+A3</f></c></row>", "C3")]
    [InlineData("<row r='2' ht='30' customHeight='1'/>", "<row r='3' ht='30' customHeight='1'/>", "C2")]
    public void RowOrColumnShiftsConflictAtABlankAddress(
        string before, string after, string address)
    {
        var result = ExcelWorkbook.MergeCells(Workbook(before), Workbook(after), 4,
            [new(address, ExcelCellValue.Number(5))]);

        Assert.Equal(ExcelWriteStatus.StructuralConflict, result.Status);
        Assert.Null(result.Package);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MovingAnEmptyCustomWidthColumnConflicts(bool reindexStyles)
    {
        var original = Workbook("", beforeData: "<cols><col min='2' max='2' width='20' customWidth='1' style='1'/></cols>");
        var latestStyle = reindexStyles ? 2 : 1;
        var latest = Workbook("", beforeData:
            $"<cols><col min='3' max='3' width='20' customWidth='1' style='{latestStyle}'/></cols>", swapStyles: reindexStyles);

        var result = ExcelWorkbook.MergeCells(original, latest, 4, [new("B2", ExcelCellValue.Number(5))]);

        Assert.Equal(ExcelWriteStatus.StructuralConflict, result.Status);
    }

    [Theory]
    [InlineData(false, ExcelWriteStatus.StructuralConflict)]
    [InlineData(true, ExcelWriteStatus.Success)]
    public void ShiftingBlankCellsRequiresMatchingNonDefaultStyles(bool useDefaultStyle, ExcelWriteStatus expected)
    {
        var firstStyle = useDefaultStyle ? 0 : 1;
        var secondStyle = useDefaultStyle ? 0 : 2;
        var original = Workbook($"<row r='2'><c r='A2' s='{firstStyle}'/></row>" +
            $"<row r='3'><c r='A3' s='{secondStyle}'/></row>");
        var latest = Workbook($"<row r='3'><c r='A3' s='{secondStyle}'/></row>" +
            $"<row r='4'><c r='A4' s='{firstStyle}'/></row>", swapStyles: true);

        var result = ExcelWorkbook.MergeCells(original, latest, 4, [new("B2", ExcelCellValue.Number(5))]);

        Assert.Equal(expected, result.Status);
    }

    [Theory]
    [InlineData(12, 1)]
    [InlineData(20, 2)]
    public void DisjointEditMergesWithAnInPlaceColumnChange(int originalWidth, int latestStyle)
    {
        var original = Workbook("", beforeData:
            $"<cols><col min='26' max='26' width='{originalWidth}' customWidth='1' style='1'/></cols>");
        var latest = Workbook("", beforeData:
            $"<cols><col min='26' max='26' width='20' customWidth='1' style='{latestStyle}'/></cols>",
            swapStyles: latestStyle == 2);

        var result = ExcelWorkbook.MergeCells(original, latest, 4, [new("A1", ExcelCellValue.Number(5))]);

        Assert.Equal(ExcelWriteStatus.Success, result.Status);
        var package = Assert.IsType<byte[]>(result.Package);
        Validate(package);
        Assert.Equal("20", (string?)Part(package, "xl/worksheets/sheet1.xml")
            .Root!.Element(S + "cols")!.Element(S + "col")!.Attribute("width"));
        Assert.Equal(ExcelCellValue.Number(5), Assert.Single(ExcelWorkbook.ReadRange(package, 4, "A1")).Value);
    }

    [Theory]
    [InlineData("1+2", "7*8")]
    [InlineData("$A$2+2", "$A$3+3")]
    [InlineData("$A$2+2", "$A$3*2")]
    public void DisjointEditMergesWithAnUnrelatedAlignedFormulaAddition(string before, string added)
    {
        var original = Workbook($"<row r='2'><c r='B2'><f>{before}</f></c></row>");
        var latest = Edit(original, new ExcelCellEdit("B2", ExcelCellValue.Number(9)),
            new ExcelCellEdit("B3", ExcelCellValue.Formula(added)));

        var result = ExcelWorkbook.MergeCells(original, latest, 4, [new("A1", ExcelCellValue.Number(5))]);

        Assert.Equal(ExcelWriteStatus.Success, result.Status);
        var package = Assert.IsType<byte[]>(result.Package);
        Validate(package);
        Assert.Equal([ExcelCellValue.Number(9), ExcelCellValue.Formula(added)],
            ExcelWorkbook.ReadRange(package, 4, "B2:B3").Select(cell => cell.Value));
    }

    [Fact]
    public void DisjointEditMergesWithFormulaReplacementAndAddition()
    {
        var original = Workbook("<row r='2'><c r='B2'><f>1+2</f></c></row>");
        var latest = Edit(original, new ExcelCellEdit("B2", ExcelCellValue.Formula("2+3")),
            new ExcelCellEdit("B3", ExcelCellValue.Formula("4+5")));

        var result = ExcelWorkbook.MergeCells(original, latest, 4, [new("A1", ExcelCellValue.Number(5))]);

        Assert.Equal(ExcelWriteStatus.Success, result.Status);
        var package = Assert.IsType<byte[]>(result.Package);
        Validate(package);
        Assert.Equal(["2+3", "4+5"], ExcelWorkbook.ReadRange(package, 4, "B2:B3").Select(cell => cell.Value.RawValue));
    }

    [Fact]
    public void SharedFormulaPreservesEscapedTableColumnName()
    {
        var package = Workbook("<row r='1'><c r='B1'><f t='shared' si='0' ref='B1:C1'>SUM(Table1[Cost'] A1],A1)</f></c>" +
            "<c r='C1'><f t='shared' si='0'/></c></row>");

        var follower = Assert.Single(ExcelWorkbook.ReadRange(package, 4, "C1"));

        Assert.Equal(ExcelCellValue.Formula("SUM(Table1[Cost'] A1],B1)"), follower.Value);
    }

    [Fact]
    public void SortWithAThreeCellCycleConflictsAtABlankAddress()
    {
        var original = Workbook(
            "<row r='1'><c r='A1'><v>2</v></c></row><row r='2'><c r='A2'><v>4</v></c></row>" +
            "<row r='3'><c r='A3'><v>3</v></c></row><row r='4'><c r='A4'><v>1</v></c></row>");
        var sorted = Workbook(
            "<row r='1'><c r='A1'><v>1</v></c></row><row r='2'><c r='A2'><v>2</v></c></row>" +
            "<row r='3'><c r='A3'><v>3</v></c></row><row r='4'><c r='A4'><v>4</v></c></row>" +
            "<row r='5'><c r='C5'><v>8</v></c></row>");

        var result = ExcelWorkbook.MergeCells(original, sorted, 4, [new("B1", ExcelCellValue.String("annotation"))]);

        Assert.Equal(ExcelWriteStatus.StructuralConflict, result.Status);
        Assert.Null(result.Package);
    }

    [Fact]
    public void DisjointReplacementsAndClearsMergeWithoutAMovementCycle()
    {
        var original = Workbook("<row r='1'><c r='A1'><v>1</v></c><c r='B1'><v>2</v></c>" +
            "<c r='C1'><v>4</v></c></row>");
        var latest = Edit(original, new ExcelCellEdit("A1", ExcelCellValue.Number(2)),
            new ExcelCellEdit("B1", ExcelCellValue.Number(3)), new ExcelCellEdit("C1", ExcelCellValue.Blank));

        var result = ExcelWorkbook.MergeCells(original, latest, 4, [new("D1", ExcelCellValue.Number(5))]);

        Assert.Equal(ExcelWriteStatus.Success, result.Status);
        Assert.Equal(["2", "3", null, "5"],
            ExcelWorkbook.ReadRange(result.Package!, 4, "A1:D1").Select(cell => cell.Value.RawValue));
    }

    [Theory]
    [InlineData("B1+1", "B1+2")]
    [InlineData(null, "B1+1")]
    [InlineData("B1+1", null)]
    public void DisjointEditsMergeWithAnIsolatedFormulaChange(string? before, string? after)
    {
        var formula = before is null ? "" : $"<c r='A1'><f>{before}</f></c>";
        var original = Workbook($"<row r='1'>{formula}<c r='B1'><v>2</v></c></row>");
        var remoteValue = after is null ? ExcelCellValue.Blank : ExcelCellValue.Formula(after);
        var latest = Edit(original, new ExcelCellEdit("A1", remoteValue));
        var localValue = ExcelCellValue.Number(5);

        var result = ExcelWorkbook.MergeCells(original, latest, 4, [new("C2", localValue)]);

        Assert.Equal(ExcelWriteStatus.Success, result.Status);
        var package = Assert.IsType<byte[]>(result.Package);
        Validate(package);
        Assert.Equal(remoteValue, Assert.Single(ExcelWorkbook.ReadRange(package, 4, "A1")).Value);
        Assert.Equal(localValue, Assert.Single(ExcelWorkbook.ReadRange(package, 4, "C2")).Value);
    }

    [Fact]
    public void RejectsWritesIntoProtectedMergedSharedOrSpillCells()
    {
        var protectedSheet = Workbook(
            "<row r='1'><c r='A1' s='2'><v>1</v></c></row>", afterData: "<sheetProtection sheet='1'/>");
        Validate(Edit(protectedSheet, new ExcelCellEdit("A1", ExcelCellValue.Number(4))));
        Assert.Throws<InvalidOperationException>(() => Edit(protectedSheet, new ExcelCellEdit("B1", ExcelCellValue.Number(4))));
        var merged = Workbook("", afterData: "<mergeCells count='1'><mergeCell ref='A1:B2'/></mergeCells>");
        Assert.Throws<InvalidOperationException>(() => Edit(merged, new ExcelCellEdit("B2", ExcelCellValue.Number(4))));
        var shared = Workbook("<row r='1'><c r='A1'><f t='shared' ref='A1:A4' si='1'>1+1</f><v>2</v></c></row>");
        Assert.Throws<InvalidOperationException>(() => Edit(shared, new ExcelCellEdit("A1", ExcelCellValue.Number(4))));
        var array = Workbook("<row r='1'><c r='A1'><f t='array' ref='A1:A4'>1+1</f><v>2</v></c></row>");
        Assert.Throws<InvalidOperationException>(() => Edit(array, new ExcelCellEdit("A3", ExcelCellValue.Number(4))));
    }

    [Fact]
    public void ReadsSharedFormulaReferencesAndEmptyCachedText()
    {
        var original = Workbook("<row r='1'><c r='B1'><f t='shared' si='2' ref='B1:C1'>" +
            "SUM(A1,$A1,A$1,$A$1,Sheet1!A1,A:A,1:1)&amp;\"A1\"</f><v>10</v></c>" +
            "<c r='C1'><f t='shared' si='2'/><v>11</v></c>" +
            "<c r='D1' t='str'><f>IF(TRUE,\"\",\"unused\")</f><v/></c></row>");
        var cells = ExcelWorkbook.ReadRange(original, 4, "C1:D1");
        Assert.Equal("SUM(B1,$A1,B$1,$A$1,Sheet1!B1,B:B,1:1)&\"A1\"", cells[0].Value.RawValue);
        Assert.Equal(ExcelCellValue.Number(11), cells[0].CachedValue);
        Assert.True(cells[1].Calculated);
        Assert.Equal(ExcelCellValue.String(""), cells[1].CachedValue);
    }

    [Fact]
    public void SharedFormulaFollowerPreservesDefinedNamesOutsideTheCellGrid()
    {
        var original = Workbook(
            @"<row r='1'><c r='B1'><f t='shared' si='0' ref='B1:C2'>SUM(XFE1,A1048577,\A1,A1,$A$1)</f></c></row>" +
            "<row r='2'><c r='C2'><f t='shared' si='0'/></c></row>",
            definedNames: @"<definedName name='XFE1'>1</definedName><definedName name='A1048577'>2</definedName><definedName name='\A1'>42</definedName>");
        Validate(original);

        var follower = Assert.Single(ExcelWorkbook.ReadRange(original, 4, "C2"));

        Assert.Equal(ExcelCellValue.Formula(@"SUM(XFE1,A1048577,\A1,B2,$A$1)"), follower.Value);
    }

    [Fact]
    public void TableResizesAndOverriddenDimensionsConflict()
    {
        var original = Workbook("", tableRange: "A1:B2");
        var resized = Workbook("", tableRange: "A1:B4");
        var edits = new[] { new ExcelCellEdit("D1", ExcelCellValue.Number(5)) };
        Assert.Equal(ExcelWriteStatus.StructuralConflict, ExcelWorkbook.MergeCells(original, resized, 4, edits).Status);

        var before = Workbook("", beforeData: "<dimension ref='A1:B2'/>");
        var after = Workbook("", beforeData: "<dimension ref='A2:B3'/>");
        Assert.Equal(ExcelWriteStatus.StructuralConflict, ExcelWorkbook.MergeCells(before, after, 4, edits).Status);
    }

    [Fact]
    public void ValidatesLimitsAddressesAndFormulaSubset()
    {
        var workbook = Workbook("");
        Assert.Throws<InvalidDataException>(() => ExcelWorkbook.ListSheets(workbook, new() { MaxExpandedBytes = workbook.Length }));
        Assert.Throws<InvalidDataException>(() => ExcelWorkbook.ListSheets(workbook, new() { MaxSessionBytes = workbook.Length }));
        Assert.Throws<ArgumentOutOfRangeException>(() => ExcelWorkbook.ReadRange(workbook, 4, "A1:XFD1048576"));
        Assert.Throws<ArgumentException>(() => Edit(workbook, new ExcelCellEdit("XFE1", ExcelCellValue.Number(1))));
        Assert.Throws<ArgumentException>(() => Edit(workbook, new ExcelCellEdit("A1", ExcelCellValue.Formula("SUM(1;2)"))));
        Assert.Throws<ArgumentException>(() => Edit(workbook, new ExcelCellEdit("A1", ExcelCellValue.Formula("XLOOKUP(1,A2:A3,B2:B3)"))));
        Assert.Throws<ArgumentException>(() => Edit(workbook, new ExcelCellEdit("A1", ExcelCellValue.Formula("_xlfn.SEQUENCE(2)"))));
        Assert.Throws<ArgumentException>(() => Edit(workbook, new ExcelCellEdit("A1", ExcelCellValue.Formula("_xlfn._xlws.FILTER(A2:A3,B2:B3)"))));
        Validate(Edit(workbook, new ExcelCellEdit("A1", ExcelCellValue.Formula("_xlfn.XLOOKUP(1,A2:A3,B2:B3)"))));
    }

    [Theory]
    [InlineData("_xlfn.NORM.DIST(0,0,1,TRUE)")]
    [InlineData("_xlfn.T.DIST.2T(1,10)")]
    public void AcceptsDottedFutureFunctions(string text)
    {
        var formula = ExcelCellValue.Formula(text);
        var edited = Edit(Workbook(""), new ExcelCellEdit("A1", formula));

        Validate(edited);
        Assert.Equal(formula, Assert.Single(ExcelWorkbook.ReadRange(edited, 4, "A1")).Value);
    }

    private static byte[] Edit(byte[] package, params ExcelCellEdit[] edits)
    {
        var result = ExcelWorkbook.MergeCells(package, package, 4, edits);
        Assert.Equal(ExcelWriteStatus.Success, result.Status);
        return Assert.IsType<byte[]>(result.Package);
    }

    private static void Validate(byte[] package)
    {
        using var document = SpreadsheetDocument.Open(new MemoryStream(package), false);
        var errors = new OpenXmlValidator().Validate(document)
            .Select(error => $"{error.Part?.Uri} {error.Path?.XPath}: {error.Description}").ToArray();
        Assert.True(errors.Length == 0, string.Join(Environment.NewLine, errors));
    }

    private static XDocument Part(byte[] package, string name)
    {
        using var zip = new ZipArchive(new MemoryStream(package));
        using var stream = zip.GetEntry(name)!.Open();
        return XDocument.Load(stream);
    }

    private static (byte[] HeaderAndPayload, byte[] CentralDirectoryExceptOffset) ZipRecord(byte[] package, string name)
    {
        var end = package.Length - 22;
        var start = ReadInt32(package, end + 16);
        var entries = new List<(string Name, int Local, int Central, int Length)>();
        for (var offset = start; offset < end;)
        {
            var nameLength = ReadUInt16(package, offset + 28);
            var length = 46 + nameLength + ReadUInt16(package, offset + 30) + ReadUInt16(package, offset + 32);
            entries.Add((Encoding.UTF8.GetString(package, offset + 46, nameLength),
                ReadInt32(package, offset + 42), offset, length));
            offset += length;
        }
        var entry = entries.Single(entry => entry.Name == name);
        var localEnd = entries.Select(entry => entry.Local).Where(offset => offset > entry.Local).DefaultIfEmpty(start).Min();
        var central = package.AsSpan(entry.Central, entry.Length).ToArray();
        central.AsSpan(42, 4).Clear();
        return (package[entry.Local..localEnd], central);
    }

    private static int ReadUInt16(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset));
    private static int ReadInt32(byte[] bytes, int offset) => BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset));

    private static byte[] StringWorkbook(bool shared, string markup) => shared
        ? Workbook("<row r='1'><c r='A1' t='s'><v>1</v></c></row>", sharedStrings: ["<t>other</t>", markup])
        : Workbook($"<row r='1'><c r='A1' t='inlineStr'><is>{markup}</is></c></row>");

    private static byte[] Workbook(string rows, string beforeData = "", string afterData = "", bool date1904 = false,
        string? calculationChain = null, string[]? sharedStrings = null, bool nonSeekableZip = false,
        bool reverseSheets = false, string? tableRange = null, string? definedNames = null, bool swapStyles = false)
    {
        const string worksheetType = "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml";
        const string workbookType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml";
        static string Override(string name, string type) => $"<Override PartName='/{name}' ContentType='{type}'/>";
        static string Relationship(string id, string type, string target) =>
            $"<Relationship Id='{id}' Type='{OfficeRels}/{type}' Target='{target}'/>";
        var chainType = calculationChain is null ? "" : Override("xl/calcChain.xml",
            "application/vnd.openxmlformats-officedocument.spreadsheetml.calcChain+xml");
        var sharedType = sharedStrings is null ? "" : Override("xl/sharedStrings.xml",
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sharedStrings+xml");
        const string first = "<sheet name='First' sheetId='4' r:id='rId1'/>";
        const string second = "<sheet name='Second' sheetId='9' r:id='rId2'/>";
        using var memory = new MemoryStream();
        using var nonSeekable = new WriteOnlyStream(memory);
        using (var zip = new ZipArchive(nonSeekableZip ? nonSeekable : memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string path, string xml)
            {
                var entry = zip.CreateEntry(path, CompressionLevel.Fastest);
                entry.LastWriteTime = new DateTimeOffset(2024, 2, 3, 4, 5, 6, TimeSpan.Zero);
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                writer.Write(xml);
            }

            Add("[Content_Types].xml",
                "<Types xmlns='http://schemas.openxmlformats.org/package/2006/content-types'>" +
                "<Default Extension='rels' ContentType='application/vnd.openxmlformats-package.relationships+xml'/>" +
                "<Default Extension='xml' ContentType='application/xml'/>" +
                Override("xl/workbook.xml", workbookType) + Override("xl/worksheets/sheet1.xml", worksheetType) +
                Override("xl/worksheets/sheet2.xml", worksheetType) +
                Override("xl/styles.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml") +
                chainType + sharedType + (tableRange is null ? "" : Override("xl/tables/table1.xml",
                    "application/vnd.openxmlformats-officedocument.spreadsheetml.table+xml")) + "</Types>");
            Add("_rels/.rels", $"<Relationships xmlns='{PackageRels}'>" +
                Relationship("root", "officeDocument", "xl/workbook.xml") + "</Relationships>");
            Add("xl/workbook.xml", $"<workbook xmlns='{Main}' xmlns:r='{OfficeRels}'>" +
                (date1904 ? "<workbookPr date1904='1'/>" : "") +
                "<sheets>" + (reverseSheets ? second + first : first + second) + "</sheets>" +
                (definedNames is null ? "" : $"<definedNames>{definedNames}</definedNames>") + "</workbook>");
            Add("xl/_rels/workbook.xml.rels", $"<Relationships xmlns='{PackageRels}'>" +
                Relationship("rId1", "worksheet", "worksheets/sheet1.xml") +
                Relationship("rId2", "worksheet", "worksheets/sheet2.xml") +
                Relationship("rId3", "styles", "styles.xml") +
                (calculationChain is null ? "" : Relationship("rId4", "calcChain", "calcChain.xml")) +
                (sharedStrings is null ? "" : Relationship("rId5", "sharedStrings", "sharedStrings.xml")) + "</Relationships>");
            Add("xl/worksheets/sheet1.xml", $"<worksheet xmlns='{Main}' " +
                "xmlns:mc='http://schemas.openxmlformats.org/markup-compatibility/2006' " +
                "xmlns:x14ac='http://schemas.microsoft.com/office/spreadsheetml/2009/9/ac' " +
                "mc:Ignorable='x14ac' x14ac:dyDescent='0.25'>" + beforeData +
                "<sheetData>" + rows + "</sheetData>" + afterData +
                (tableRange is null ? "" :
                    $"<tableParts xmlns:r='{OfficeRels}' count='1'><tablePart r:id='rId1'/></tableParts>") + "</worksheet>");
            Add("xl/worksheets/sheet2.xml", $"<worksheet xmlns='{Main}'><sheetData><row r='1'>" +
                "<c r='A1'><f>5+5</f><v>10</v></c></row></sheetData></worksheet>");
            const string dateStyle = "<xf numFmtId='14' applyNumberFormat='1'/>";
            const string unlockedStyle = "<xf applyProtection='1'><protection locked='0'/></xf>";
            Add("xl/styles.xml", $"<styleSheet xmlns='{Main}'><cellXfs count='3'><xf/>" +
                (swapStyles ? unlockedStyle + dateStyle : dateStyle + unlockedStyle) + "</cellXfs></styleSheet>");
            if (tableRange is not null)
            {
                Add("xl/worksheets/_rels/sheet1.xml.rels", $"<Relationships xmlns='{PackageRels}'>" +
                    Relationship("rId1", "table", "../tables/table1.xml") + "</Relationships>");
                Add("xl/tables/table1.xml", $"<table xmlns='{Main}' id='1' name='Table1' displayName='Table1' ref='{tableRange}'>" +
                    "<tableColumns count='2'><tableColumn id='1' name='X'/><tableColumn id='2' name='Y'/></tableColumns></table>");
            }
            if (calculationChain is not null)
                Add("xl/calcChain.xml", $"<calcChain xmlns='{Main}'>" + calculationChain + "</calcChain>");
            if (sharedStrings is not null)
                Add("xl/sharedStrings.xml", $"<sst xmlns='{Main}'>" +
                    string.Concat(sharedStrings.Select(markup => $"<si>{markup}</si>")) + "</sst>");
        }
        return memory.ToArray();
    }

    private sealed class WriteOnlyStream(Stream target) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => target.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => target.Write(buffer, offset, count);
    }
}
