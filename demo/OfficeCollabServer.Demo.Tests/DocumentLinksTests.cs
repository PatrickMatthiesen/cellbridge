using OfficeCollabServer.Demo;

namespace OfficeCollabServer.Demo.Tests;

public sealed class DocumentLinksTests
{
    private static readonly Uri BaseUri = new("https://collab.example.test/");

    [Theory]
    [InlineData("brief.DOCX", OfficeApplication.Word)]
    [InlineData("budget.xlsx", OfficeApplication.Excel)]
    [InlineData("deck.pptm", OfficeApplication.PowerPoint)]
    [InlineData("notes.txt", OfficeApplication.None)]
    public void DetectsOfficeApplicationFromExtension(string name, OfficeApplication expected)
    {
        Assert.Equal(expected, DocumentLinks.GetApplication(name, "/shared/" + name));
    }

    [Fact]
    public void EscapesPathSegmentsWithoutChangingTheServer()
    {
        Assert.True(DocumentLinks.TryBuildDocumentUri(BaseUri, "/shared/quarter one.docx", out var uri));
        Assert.Equal("https://collab.example.test/shared/quarter%20one.docx", uri.AbsoluteUri);
    }

    [Theory]
    [InlineData("https://attacker.test/file.docx")]
    [InlineData("//attacker.test/file.docx")]
    [InlineData("/shared/../secret.docx")]
    [InlineData("/shared\\secret.docx")]
    public void RejectsUnsafePaths(string path)
    {
        Assert.False(DocumentLinks.TryBuildDocumentUri(BaseUri, path, out _));
    }

    [Fact]
    public void BuildsDocumentedOfficeLaunchScheme()
    {
        Assert.True(DocumentLinks.TryBuildDocumentUri(BaseUri, "/shared/test.docx", out var uri));
        Assert.Equal("ms-word:ofe|u|https://collab.example.test/shared/test.docx",
            DocumentLinks.BuildOfficeLaunchUrl(OfficeApplication.Word, uri));
    }

    [Fact]
    public void AddsDownloadFlagToTheDocumentUri()
    {
        Assert.True(DocumentLinks.TryBuildDocumentUri(BaseUri, "/shared/test.docx", out var uri));
        Assert.Equal("https://collab.example.test/shared/test.docx?download=true",
            DocumentLinks.BuildDownloadUri(uri).AbsoluteUri);
    }
}
