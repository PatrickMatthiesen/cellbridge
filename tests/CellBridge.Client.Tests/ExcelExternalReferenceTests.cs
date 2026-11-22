using System.IO.Compression;
using System.Xml.Linq;
using CellBridge.Client.Excel;

namespace CellBridge.Client.Tests;

public sealed class ExcelExternalReferenceTests
{
    private const string Budget = "https://example.invalid/Budget.xlsx";
    private const string Forecast = "https://example.invalid/Forecast.xlsx";
    private static readonly XNamespace S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace P = "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly XNamespace Ct = "http://schemas.openxmlformats.org/package/2006/content-types";
    private static readonly byte[] EmptyWorkbook = OfficeDocumentTemplates.CreateExcelWorkbook();

    [Theory]
    [InlineData("[1]Budget!A1")]
    [InlineData("'[01]Budget 2026'!$A$1")]
    [InlineData("SUM('[1]Jan:Mar'!$1:$2)")]
    [InlineData("[1]!ExchangeRate")]
    [InlineData("'[01]Budget 2026'!ExchangeRate")]
    [InlineData("SUM([01]!Table1[Amount])")]
    [InlineData("SUM([1]!Table1)")]
    [InlineData("[002]Budget!A1")]
    public void ReorderingExternalReferencesConflictsEvenWhenTheFormulaTextMatches(string formula)
    {
        var workbook = WithExternalWorkbooks(EmptyWorkbook, 1, Budget, Forecast);
        var original = Assert.IsType<byte[]>(Merge(workbook, workbook, formula).Package);
        var current = Rewrite(original, zip =>
        {
            const string path = "xl/workbook.xml";
            var root = Read(zip, path);
            var references = root.Element(S + "externalReferences")!;
            references.ReplaceNodes(references.Elements().Reverse().ToArray());
            Write(zip, path, root);
        });

        var result = Merge(original, current, formula);

        Assert.Equal(ExcelWriteStatus.StructuralConflict, result.Status);
        Assert.Null(result.Package);
    }

    [Fact]
    public void ChangingTheExternalPathForTheSamePartConflicts()
    {
        var original = WithExternalWorkbooks(EmptyWorkbook, 1, Budget);
        var current = WithExternalWorkbooks(EmptyWorkbook, 1, Forecast);

        var result = Merge(original, current, "[1]Budget!A1");

        Assert.Equal(ExcelWriteStatus.StructuralConflict, result.Status);
        Assert.Null(result.Package);
    }

    [Fact]
    public void EqualTargetsSurvivePartRenumberingAndUnreferencedLinkChanges()
    {
        var original = WithExternalWorkbooks(EmptyWorkbook, 1, Budget, Forecast);
        var current = WithExternalWorkbooks(EmptyWorkbook, 7, Budget);
        const string formula = "[01]Budget!A1&\"[2]Forecast!A1\"";

        var result = Merge(original, current, formula);

        Assert.Equal(ExcelWriteStatus.Success, result.Status);
        var package = Assert.IsType<byte[]>(result.Package);
        Assert.Equal(ExcelCellValue.Formula(formula), Assert.Single(ExcelWorkbook.ReadRange(package, 1, "B1")).Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("xl/externalLinks/externalLink1.xml")]
    [InlineData("xl/externalLinks/_rels/externalLink1.xml.rels")]
    public void MissingLinksRejectCurrentEditsAndConflictWithStaleEdits(string? missingPart)
    {
        var complete = WithExternalWorkbooks(EmptyWorkbook, 1, Budget);
        var missing = missingPart is null ? EmptyWorkbook : Rewrite(complete, zip => zip.GetEntry(missingPart)!.Delete());
        const string formula = "[1]Budget!A1";

        var error = Assert.Throws<ArgumentException>(() => Merge(missing, missing, formula));
        Assert.Contains("unknown external workbook '[1]'", error.Message);
        Assert.Equal(ExcelWriteStatus.StructuralConflict, Merge(complete, missing, formula).Status);
        Assert.Equal(ExcelWriteStatus.StructuralConflict, Merge(missing, complete, formula).Status);
    }

    [Theory]
    [InlineData("[0]!A1")]
    [InlineData("[1]!A1")]
    [InlineData("[01]!$A$1:$B$2")]
    [InlineData("[0]!1:2")]
    [InlineData("[1]!$A : $B")]
    public void WorkbookOnlyQualifierCannotQualifyCellOrAxisReferences(string formula)
    {
        var workbook = WithExternalWorkbooks(EmptyWorkbook, 1, Budget);

        Assert.Throws<ArgumentException>(() => Merge(workbook, workbook, formula));
    }

    [Theory]
    [InlineData("[0]Sheet1!A1")]
    [InlineData("'[00]Sheet1'!$A:$B")]
    [InlineData("[0]!ExchangeRate")]
    [InlineData("[1]Budget!A1")]
    [InlineData("[1]Budget!1:2")]
    [InlineData("[1]!ExchangeRate")]
    [InlineData("SUM([1]!Table1[Amount])")]
    public void ValidWorkbookQualifiersPersist(string formula)
    {
        var workbook = WithExternalWorkbooks(EmptyWorkbook, 1, Budget);

        var result = Merge(workbook, workbook, formula);

        Assert.Equal(ExcelWriteStatus.Success, result.Status);
        var package = Assert.IsType<byte[]>(result.Package);
        Assert.Equal(ExcelCellValue.Formula(formula), Assert.Single(ExcelWorkbook.ReadRange(package, 1, "B1")).Value);
    }

    private static ExcelMergeResult Merge(byte[] original, byte[] current, string formula) =>
        ExcelWorkbook.MergeCells(original, current, 1, [new("B1", ExcelCellValue.Formula(formula))],
            policy: ExcelConflictPolicy.LocalWins);

    internal static byte[] WithExternalWorkbooks(byte[] workbook, int firstPartNumber, params string[] targets) =>
        Rewrite(workbook, zip =>
        {
            const string workbookPath = "xl/workbook.xml";
            const string workbookRelsPath = "xl/_rels/workbook.xml.rels";
            const string typesPath = "[Content_Types].xml";
            var root = Read(zip, workbookPath);
            var relationships = Read(zip, workbookRelsPath);
            var types = Read(zip, typesPath);
            var references = new XElement(S + "externalReferences");
            root.Element(S + "sheets")!.AddAfterSelf(references);
            for (var i = 0; i < targets.Length; i++)
            {
                var number = firstPartNumber + i;
                var file = $"externalLink{number}.xml";
                var referenceId = $"external{number}";
                var bookId = $"book{number}";
                references.Add(new XElement(S + "externalReference", new XAttribute(R + "id", referenceId)));
                relationships.Add(Relationship(referenceId, "externalLink", $"externalLinks/{file}"));
                types.Add(new XElement(Ct + "Override", new XAttribute("PartName", $"/xl/externalLinks/{file}"),
                    new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.externalLink+xml")));
                Write(zip, $"xl/externalLinks/{file}", new XElement(S + "externalLink",
                    new XElement(S + "externalBook", new XAttribute(R + "id", bookId))));
                var path = Relationship(bookId, "externalLinkPath", targets[i]);
                path.SetAttributeValue("TargetMode", "External");
                Write(zip, $"xl/externalLinks/_rels/{file}.rels", new XElement(P + "Relationships", path));
            }
            Write(zip, workbookPath, root);
            Write(zip, workbookRelsPath, relationships);
            Write(zip, typesPath, types);
        });

    private static XElement Relationship(string id, string type, string target) =>
        new(P + "Relationship", new XAttribute("Id", id), new XAttribute("Type", $"{R}/{type}"), new XAttribute("Target", target));

    private static byte[] Rewrite(byte[] workbook, Action<ZipArchive> edit)
    {
        using var stream = new MemoryStream();
        stream.Write(workbook);
        stream.Position = 0;
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Update, leaveOpen: true))
            edit(zip);
        return stream.ToArray();
    }

    private static XElement Read(ZipArchive zip, string path)
    {
        using var stream = zip.GetEntry(path)!.Open();
        return XElement.Load(stream);
    }

    private static void Write(ZipArchive zip, string path, XElement root)
    {
        zip.GetEntry(path)?.Delete();
        using var stream = zip.CreateEntry(path).Open();
        root.Save(stream);
    }
}
