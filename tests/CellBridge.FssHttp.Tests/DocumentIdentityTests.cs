using CellBridge.Storage;

namespace CellBridge.FssHttp.Tests;

public sealed class DocumentIdentityTests
{
    [Fact]
    public void EtagUsesStableFullResourceIdentifierAndChangesOnlyVersion()
    {
        var store = new DocumentStore();
        var document = store.Put("/shared/test.docx", [1, 2, 3]);
        string first = document.Etag;

        Assert.Matches("^\"\\{[0-9A-F]{8}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{12}\\},1\"$", first);
        Assert.Contains(document.TransitionId.ToString("D").ToUpperInvariant(), first);

        document.SetContent([4, 5, 6]);

        Assert.Equal(first[..^2] + "2\"", document.Etag);
    }
}
