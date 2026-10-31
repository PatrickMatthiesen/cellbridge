using System.IO.Compression;
using CellBridge.Storage;
using CellBridge.WebDemo;

namespace CellBridge.WebDemo.Tests;

public sealed class DocumentCreationTests
{
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
        var expectedBytes = type.TrimStart('.') switch
        {
            "docx" => OfficeDocumentTemplates.CreateWordDocument(),
            "xlsx" => OfficeDocumentTemplates.CreateExcelWorkbook(),
            "pptx" => OfficeDocumentTemplates.CreatePowerPointPresentation(),
            _ => throw new InvalidOperationException(),
        };
        using var expected = new ZipArchive(new MemoryStream(expectedBytes));
        Assert.Equal(expected.Entries.Select(x => x.FullName), archive.Entries.Select(x => x.FullName));
        foreach (var entry in expected.Entries)
        {
            using var expectedReader = new StreamReader(entry.Open());
            using var actualReader = new StreamReader(archive.GetEntry(entry.FullName)!.Open());
            Assert.Equal(expectedReader.ReadToEnd(), actualReader.ReadToEnd());
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
}
