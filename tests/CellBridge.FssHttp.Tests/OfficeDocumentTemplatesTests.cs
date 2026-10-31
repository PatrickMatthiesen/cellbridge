using CellBridge;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using W = DocumentFormat.OpenXml.Wordprocessing;
using S = DocumentFormat.OpenXml.Spreadsheet;
using P = DocumentFormat.OpenXml.Presentation;
using A = DocumentFormat.OpenXml.Drawing;

namespace CellBridge.FssHttp.Tests;

public sealed class OfficeDocumentTemplatesTests
{
    [Fact]
    public void WordIsBlankWithLinkedMode15Settings()
    {
        using var stream = new MemoryStream(OfficeDocumentTemplates.CreateWordDocument());
        using var package = WordprocessingDocument.Open(stream, false);
        AssertValid(package);
        var main = package.MainDocumentPart!;
        var body = main.Document.Body!;
        Assert.Single(body.Elements<W.Paragraph>());
        Assert.Empty(body.Descendants<W.Text>());
        Assert.Empty(body.Descendants<W.Run>());
        var settings = main.DocumentSettingsPart!;
        Assert.Equal("/word/settings.xml", settings.Uri.OriginalString);
        Assert.Equal("application/vnd.openxmlformats-officedocument.wordprocessingml.settings+xml", settings.ContentType);
        Assert.Equal("http://schemas.openxmlformats.org/officeDocument/2006/relationships/settings", settings.RelationshipType);
        Assert.Same(settings, main.GetPartById(main.GetIdOfPart(settings)));
        var mode = Assert.Single(settings.Settings.Descendants<W.CompatibilitySetting>());
        Assert.Equal(W.CompatSettingNameValues.CompatibilityMode, mode.Name!.Value);
        Assert.Equal("http://schemas.microsoft.com/office/word", mode.Uri!.Value);
        Assert.Equal("15", mode.Val!.Value);
    }

    [Fact]
    public void ExcelHasOneEmptySheet1()
    {
        using var stream = new MemoryStream(OfficeDocumentTemplates.CreateExcelWorkbook());
        using var package = SpreadsheetDocument.Open(stream, false);
        AssertValid(package);
        var workbook = package.WorkbookPart!;
        var sheet = Assert.Single(workbook.Workbook.Sheets!.Elements<S.Sheet>());
        Assert.Equal("Sheet1", sheet.Name!.Value);
        var worksheet = Assert.Single(workbook.WorksheetParts);
        Assert.Same(worksheet, workbook.GetPartById(sheet.Id!.Value!));
        Assert.Empty(Assert.Single(worksheet.Worksheet.Elements<S.SheetData>()).ChildElements);
        Assert.Empty(worksheet.Worksheet.Descendants<S.Cell>());
    }

    [Fact]
    public void PowerPointHasOneBlankSlideWithRegisteredLayoutMasterAndTheme()
    {
        using var stream = new MemoryStream(OfficeDocumentTemplates.CreatePowerPointPresentation());
        using var package = PresentationDocument.Open(stream, false);
        AssertValid(package);
        var presentation = package.PresentationPart!;
        var slideId = Assert.Single(presentation.Presentation.SlideIdList!.Elements<P.SlideId>());
        var slide = Assert.Single(presentation.SlideParts);
        Assert.Same(slide, presentation.GetPartById(slideId.RelationshipId!.Value!));
        Assert.Empty(slide.Slide.Descendants<A.Text>());
        Assert.Empty(slide.Slide.CommonSlideData!.ShapeTree!.Elements<P.Shape>());
        var layout = slide.SlideLayoutPart!;
        Assert.Equal(P.SlideLayoutValues.Blank, layout.SlideLayout.Type!.Value);
        var master = layout.SlideMasterPart!;
        Assert.Same(Assert.Single(presentation.SlideMasterParts), master);
        var masterId = Assert.Single(presentation.Presentation.SlideMasterIdList!.Elements<P.SlideMasterId>());
        Assert.Same(master, presentation.GetPartById(masterId.RelationshipId!.Value!));
        var registration = Assert.Single(master.SlideMaster.SlideLayoutIdList!.Elements<P.SlideLayoutId>());
        Assert.True(registration.Id!.Value >= 2147483648u);
        Assert.Same(layout, master.GetPartById(registration.RelationshipId!.Value!));
        Assert.NotNull(master.ThemePart!.Theme.ThemeElements);
    }

    [Theory]
    [InlineData("docx")]
    [InlineData("xlsx")]
    [InlineData("pptx")]
    public void EachCallReturnsAnIndependentlyOwnedBuffer(string format)
    {
        Func<byte[]> create = format switch
        {
            "docx" => OfficeDocumentTemplates.CreateWordDocument,
            "xlsx" => OfficeDocumentTemplates.CreateExcelWorkbook,
            "pptx" => OfficeDocumentTemplates.CreatePowerPointPresentation,
            _ => throw new InvalidOperationException(),
        };
        var first = create();
        var second = create();
        Assert.NotSame(first, second);
        var original = second[0];
        first[0] ^= 0xff;
        Assert.Equal(original, second[0]);
        Assert.Equal(original, create()[0]);
    }

    private static void AssertValid(OpenXmlPackage package)
    {
        var errors = new OpenXmlValidator(FileFormatVersions.Office2013).Validate(package).ToList();
        Assert.True(errors.Count == 0, string.Join("\n", errors.Select(error => error.Description)));
    }
}
