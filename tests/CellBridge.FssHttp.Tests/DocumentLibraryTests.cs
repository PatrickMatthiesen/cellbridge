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

}
