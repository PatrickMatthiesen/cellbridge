using CellBridge.Client.Excel;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace CellBridge.Client.Tests;

public sealed class ExcelFormulaRegressionTests
{
    private const uint SheetId = 1;
    private static readonly byte[] EmptyWorkbook = OfficeDocumentTemplates.CreateExcelWorkbook();

    [Theory]
    [InlineData("B2", "SUM(A1:INDEX(C1:C10,2))", "SUM(A2:INDEX(C2:C11,2))")]
    [InlineData("C2", "SUM('Sheet1'!$A1 : INDEX(Sheet1!C1:C10,2))", "SUM('Sheet1'!$A2 : INDEX(Sheet1!D2:D11,2))")]
    [InlineData("C2", "SUM(LOG10:ABC10!A1,XFE1:INDEX(C1:C10,2),LOG10(B1))",
        "SUM(LOG10:ABC10!B2,XFE1:INDEX(D2:D11,2),LOG10(C2))")]
    [InlineData("C2", "SUM($1 : 2,$A : B,1:$1048576,A:$XFD)", "SUM($1 : 3,$A : C,2:$1048576,B:$XFD)")]
    [InlineData("C2", "SUM(#REF!:2,3:#REF!,#REF!:B,C:#REF!)", "SUM(#REF!:3,4:#REF!,#REF!:C,D:#REF!)")]
    public void SharedFormulaMovesRelativeReferences(string follower, string master, string expected)
    {
        var workbook = WithRows(
            FormulaRow(1, "B1", new CellFormula(master)
            {
                FormulaType = CellFormulaValues.Shared, SharedIndex = 0, Reference = $"B1:{follower}"
            }),
            FormulaRow(2, follower, new CellFormula { FormulaType = CellFormulaValues.Shared, SharedIndex = 0 }));

        Assert.Equal(ExcelCellValue.Formula(expected), ReadValue(workbook, follower));
    }

    [Theory]
    [InlineData("SUM(A1:INDEX(C1:C10,2))", "SUM(A2:INDEX(C2:C11,2))", ExcelWriteStatus.StructuralConflict)]
    [InlineData("SUM(A1:INDEX(C1:C10,2))", "SUM(B2:INDEX(C2:C11,2))", ExcelWriteStatus.Success)]
    public void StaleMergeTracksBothEndsOfAFunctionRange(string before, string after, ExcelWriteStatus expected)
    {
        var original = WithRows(FormulaRow(2, "D2", new CellFormula(before)));
        var latest = WithRows(FormulaRow(3, "D3", new CellFormula(after)));
        var localValue = ExcelCellValue.Number(5);

        var result = ExcelWorkbook.MergeCells(original, latest, SheetId, [new("C2", localValue)]);

        Assert.Equal(expected, result.Status);
        if (expected == ExcelWriteStatus.Success)
        {
            var package = Assert.IsType<byte[]>(result.Package);
            Assert.Equal(localValue, ReadValue(package, "C2"));
            Assert.Equal(ExcelCellValue.Formula(after), ReadValue(package, "D3"));
        }
        else
        {
            Assert.Null(result.Package);
        }
    }

    [Theory]
    [InlineData("SUM(A1:$2)")]
    [InlineData("SUM($2:A1)")]
    [InlineData("SUM($2)")]
    [InlineData("SUM(1:1048577)")]
    [InlineData("SUM(Sheet1!$1048577 : 2)")]
    [InlineData("SUM($0:2)")]
    [InlineData("SUM(1:$99999999999999)")]
    [InlineData("SUM(#REF!:$1048577)")]
    [InlineData("SUM(A:$XFE)")]
    [InlineData("SUM($XFE:A)")]
    [InlineData("SUM($XFE1)")]
    [InlineData("SUM(A$1048577)")]
    public void MergeRejectsInvalidWholeAxisReferences(string formula)
    {
        Assert.Throws<ArgumentException>(() =>
            ExcelWorkbook.MergeCells(EmptyWorkbook, EmptyWorkbook, SheetId, [new("B1", ExcelCellValue.Formula(formula))]));
    }

    [Theory]
    [InlineData("SUM(A1:INDEX(C1:C10,2))")]
    [InlineData("SUM(Sheet1!$1 : 1048576,$A : XFD,1:$2,A:$XFD)")]
    [InlineData("SUM(#REF!:$2,$1:#REF!,#REF!:$B,$A:#REF!)")]
    [InlineData("SUM(1,1048577,99999999999999,XFE1,MyName)")]
    public void MergeAcceptsValidReferencesAndNumericArguments(string formula)
    {
        var value = ExcelCellValue.Formula(formula);

        var result = ExcelWorkbook.MergeCells(EmptyWorkbook, EmptyWorkbook, SheetId, [new("B1", value)]);

        Assert.Equal(ExcelWriteStatus.Success, result.Status);
        Assert.Equal(value, ReadValue(Assert.IsType<byte[]>(result.Package), "B1"));
    }

    private static Row FormulaRow(uint row, string address, CellFormula formula) =>
        new(new Cell(formula) { CellReference = address }) { RowIndex = row };

    private static byte[] WithRows(params Row[] rows)
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
}
