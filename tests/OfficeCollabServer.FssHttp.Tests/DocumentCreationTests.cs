using System.IO.Compression;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using OfficeCollabServer.Storage;
using OfficeCollabServer.Web;

namespace OfficeCollabServer.FssHttp.Tests;

public sealed class DocumentCreationTests
{
    [Fact]
    public void PowerPointMasterRegistersTheLayoutUsedByTheSlide()
    {
        using var presentation = PresentationDocument.Open(new MemoryStream(MinimalPptx.Create()), false);
        var part = presentation.PresentationPart!;
        var slide = Assert.Single(part.SlideParts);
        var layout = slide.SlideLayoutPart!;
        var master = layout.SlideMasterPart!;
        Assert.Same(Assert.Single(part.SlideMasterParts), master);
        var registration = Assert.Single(master.SlideMaster.SlideLayoutIdList!
            .Elements<DocumentFormat.OpenXml.Presentation.SlideLayoutId>());
        Assert.True(registration.Id!.Value >= 2147483648u);
        Assert.Same(layout, master.GetPartById(registration.RelationshipId!.Value!));
    }

    [Theory]
    [InlineData("word", "docx")]
    [InlineData("budget.xlsx", "xlsx")]
    [InlineData("team slides", ".pptx")]
    public void CreatesAnOfficePackageWithTheRequestedExtension(string name, string type)
    {
        var store = new DocumentStore();

        var result = DocumentCreation.TryCreate(store, name, type);

        Assert.True(result.Created, result.Error);
        Assert.NotNull(result.Document);
        var document = result.Document!;
        Assert.EndsWith("." + type.TrimStart('.'), document.Name, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(document.Path, store.List().Single().Url);
        using var archive = new ZipArchive(new MemoryStream(store.Get(document.Path)!.Content));
        Assert.NotEmpty(archive.Entries);
        Assert.NotNull(archive.GetEntry("[Content_Types].xml"));
    }

    [Fact]
    public void GeneratedPackagesPassTheOpenXmlValidator()
    {
        var store = new DocumentStore();
        foreach (var type in new[] { "docx", "xlsx", "pptx" })
        {
            var result = DocumentCreation.TryCreate(store, "validator-" + type, type);
            Assert.NotNull(result.Document);
            var document = result.Document!;
            var errors = type switch
            {
                "docx" => Validate(WordprocessingDocument.Open(new MemoryStream(store.Get(document.Path)!.Content), false)),
                "xlsx" => Validate(SpreadsheetDocument.Open(new MemoryStream(store.Get(document.Path)!.Content), false)),
                "pptx" => Validate(PresentationDocument.Open(new MemoryStream(store.Get(document.Path)!.Content), false)),
                _ => throw new InvalidOperationException(),
            };
            Assert.True(errors.Count == 0,
                type + " validation errors:\n" + string.Join("\n", errors.Select(static error => error.Description)));
        }
    }

    [Fact]
    public void RejectsInvalidNamesAndMismatchedExtensions()
    {
        var store = new DocumentStore();

        Assert.False(DocumentCreation.TryCreate(store, "../escape", "docx").Created);
        Assert.False(DocumentCreation.TryCreate(store, "name.xlsx", "docx").Created);
        Assert.False(DocumentCreation.TryCreate(store, "CON.docx", "docx").Created);
        Assert.False(DocumentCreation.TryCreate(store, "", "docx").Created);
        Assert.False(DocumentCreation.TryCreate(store, "name", "pdf").Created);
    }

    [Fact]
    public void EscapesUrlSpecialCharactersWhileKeepingTheDisplayName()
    {
        var store = new DocumentStore();
        var result = DocumentCreation.TryCreate(store, "Budget 100% #1 &2.xlsx", "xlsx");

        Assert.True(result.Created, result.Error);
        var document = result.Document!;
        Assert.Equal("Budget 100% #1 &2.xlsx", document.Name);
        Assert.Equal("/shared/Budget 100% #1 &2.xlsx", document.Path);
        Assert.Same(store.Get(document.Path), store.List().Single());
    }

    [Fact]
    public void ConflictingCreatesDoNotOverwriteTheFirstDocument()
    {
        var store = new DocumentStore();
        var first = DocumentCreation.TryCreate(store, "same", "docx");
        var second = DocumentCreation.TryCreate(store, "same.docx", "docx");

        Assert.True(first.Created);
        Assert.False(second.Created);
        Assert.True(second.Conflict);
        Assert.Single(store.List());
    }

    [Fact]
    public async Task ConcurrentCreatesOfOneNameHaveOneWinner()
    {
        var store = new DocumentStore();
        var results = await Task.WhenAll(Enumerable.Range(0, 16)
            .Select(_ => Task.Run(() => DocumentCreation.TryCreate(store, "race", "xlsx"))));

        Assert.Single(results, x => x.Created);
        Assert.Equal(15, results.Count(x => x.Conflict));
        Assert.Single(store.List());
    }

    private static List<ValidationErrorInfo> Validate(OpenXmlPackage package)
    {
        using (package)
        {
            return new OpenXmlValidator().Validate(package).ToList();
        }
    }
}
