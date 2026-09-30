using Microsoft.Extensions.Configuration;
using CellBridge.Storage;
using CellBridge.Web;

namespace CellBridge.FssHttp.Tests;

public sealed class DocumentLibraryTests
{
    [Fact]
    public void CatalogIncludesEveryDocumentAndReflectsIndependentRevisions()
    {
        var store = new DocumentStore();
        var first = store.Put("/shared/a.docx", [1]);
        var second = store.Put("/shared/b.xlsx", [2, 3]);
        first.SetContent([4, 5, 6]);

        var listing = DocumentLibrary.List(store);

        Assert.Equal(2, listing.Length);
        Assert.Equal("a.docx", listing[0].Name);
        Assert.Equal(2u, listing[0].Version);
        Assert.Equal(3, listing[0].Size);
        Assert.Equal(1u, listing[1].Version);
        Assert.Equal(2, listing[1].Size);
        Assert.Equal(second.LastModifiedUtc, listing[1].LastModifiedUtc);
    }

    [Theory]
    [InlineData("file.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document")]
    [InlineData("file.XLSX", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet")]
    [InlineData("file.pptx", "application/vnd.openxmlformats-officedocument.presentationml.presentation")]
    [InlineData("file.unknown", "application/octet-stream")]
    public void UsesFileSpecificContentType(string file, string expected) =>
        Assert.Equal(expected, DocumentLibrary.ContentType(file));

    [Fact]
    public void ConfiguredDirectoryLoadsFlatFilesAndSkipsOfficeLockFiles()
    {
        var directory = Path.Combine(Path.GetTempPath(), "collab-library-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllBytes(Path.Combine(directory, "Budget 100% #1.xlsx"), [7, 8]);
            File.WriteAllBytes(Path.Combine(directory, "~$Budget.xlsx"), [0]);
            Directory.CreateDirectory(Path.Combine(directory, "nested"));
            File.WriteAllBytes(Path.Combine(directory, "nested", "hidden.docx"), [0]);
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Documents:SeedExamples"] = "false",
                ["Documents:Directory"] = directory,
            }).Build();
            var store = new DocumentStore();

            DocumentLibrary.Seed(store, config, directory);

            var item = Assert.Single(DocumentLibrary.List(store));
            Assert.Equal("Budget 100% #1.xlsx", item.Name);
            Assert.Equal(new byte[] { 7, 8 }, store.Get("/shared/Budget%20100%25%20%231.xlsx")!.Content);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void MissingConfiguredDirectoryFailsInsteadOfSilentlyShowingExamples()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Documents:Directory"] = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()),
        }).Build();
        Assert.Throws<DirectoryNotFoundException>(() =>
            DocumentLibrary.Seed(new DocumentStore(), config, Path.GetTempPath()));
    }
}
