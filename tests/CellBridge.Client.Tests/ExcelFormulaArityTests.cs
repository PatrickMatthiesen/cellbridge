using CellBridge.Client.Excel;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace CellBridge.Client.Tests;

public sealed class ExcelFormulaArityTests
{
    private const uint SheetId = 1;
    private static readonly byte[] EmptyWorkbook = OfficeDocumentTemplates.CreateExcelWorkbook();

    [Theory]
    [InlineData("ABS(1,2)")]
    [InlineData("ABS( )")]
    [InlineData("SUM(1,aBs(2,))")]
    [InlineData("SUM()")]
    [InlineData("NOW(1)")]
    [InlineData("TRUE(,)")]
    [InlineData("IF(TRUE,1,2,)")]
    [InlineData("INDEX(A1:B2)")]
    [InlineData("_xlfn.XLOOKUP(1,A1:A2)")]
    public void MergeRejectsInvalidBuiltInArity(string formula)
    {
        Assert.Throws<ArgumentException>(() => Merge(formula));
    }

    [Theory]
    [InlineData("aBs(-1)")]
    [InlineData("ABS({1,-2;3,4})")]
    [InlineData("LEN(\"a,b\")")]
    [InlineData("SUM(NOW( ),TRUE(),FALSE(),ROW(),COLUMN(A1))")]
    [InlineData("IF(,SUM(1,,3),)")]
    [InlineData("INDEX((A1:A2,C1:C2),2,,2)")]
    [InlineData("_xlfn.XLOOKUP(1,A1:A2,B1:B2,,0,-1)")]
    [InlineData("_xlfn.LAMBDA(x,y,_xlfn.ISOMITTED(y))(1,)")]
    [InlineData("NORMDIST(0,0,1,TRUE)")]
    [InlineData("Vendor.ABS(1,2)+MyFunction()")]
    public void MergePreservesCallsWithValidArgumentLists(string formula)
    {
        AssertSaved(formula);
    }

    [Theory]
    [InlineData("SUM({0})+SUM({0})", false)]
    [InlineData("SUM(MyFunction({0}))", false)]
    [InlineData("SUM({0})", true)]
    public void MergeEnforcesExcelLimitPerCall(string format, bool omitArguments)
    {
        string Formula(int argumentCount)
        {
            var arguments = omitArguments ? "1" + new string(',', argumentCount - 1) :
                string.Join(',', Enumerable.Repeat("1", argumentCount));
            return string.Format(System.Globalization.CultureInfo.InvariantCulture, format, arguments);
        }

        AssertSaved(Formula(255));
        Assert.Throws<ArgumentException>(() => Merge(Formula(256)));
    }

    [Fact]
    public void MergeRejectsTooManyArgumentsWhenInvokingAFunctionResult()
    {
        var arguments = string.Join(',', Enumerable.Repeat("1", 256));

        Assert.Throws<ArgumentException>(() => Merge($"_xlfn.LAMBDA(x,x+1)({arguments})"));
    }

    [Fact]
    public void EditingAnotherCellPreservesAnExistingFormulaOutsideTheArityRules()
    {
        const string formula = "ABS(1,2)";
        using var stream = new MemoryStream();
        stream.Write(EmptyWorkbook);
        stream.Position = 0;
        using (var document = SpreadsheetDocument.Open(stream, true))
        {
            var worksheet = Assert.IsType<Worksheet>(Assert.Single(document.WorkbookPart!.WorksheetParts).Worksheet);
            var sheetData = Assert.IsType<SheetData>(worksheet.GetFirstChild<SheetData>());
            sheetData.Append(new Row(new Cell(new CellFormula(formula)) { CellReference = "B1" }) { RowIndex = 1 });
            worksheet.Save();
        }
        var workbook = stream.ToArray();

        var result = ExcelWorkbook.MergeCells(workbook, workbook, SheetId, [new("C1", ExcelCellValue.Number(1))]);

        Assert.Equal(ExcelWriteStatus.Success, result.Status);
        var package = Assert.IsType<byte[]>(result.Package);
        Assert.Equal(ExcelCellValue.Formula(formula), Assert.Single(ExcelWorkbook.ReadRange(package, SheetId, "B1")).Value);
    }

    private static ExcelMergeResult Merge(string formula) =>
        ExcelWorkbook.MergeCells(EmptyWorkbook, EmptyWorkbook, SheetId, [new("B1", ExcelCellValue.Formula(formula))]);

    private static void AssertSaved(string formula)
    {
        var result = Merge(formula);

        Assert.Equal(ExcelWriteStatus.Success, result.Status);
        var package = Assert.IsType<byte[]>(result.Package);
        Assert.Equal(ExcelCellValue.Formula(formula), Assert.Single(ExcelWorkbook.ReadRange(package, SheetId, "B1")).Value);
    }
}
