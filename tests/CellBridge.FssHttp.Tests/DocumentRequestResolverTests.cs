using CellBridge.FssHttp;
using CellBridge.Storage;
using CellBridge.Web;

namespace CellBridge.FssHttp.Tests;

public sealed class DocumentRequestResolverTests
{
    [Fact]
    public void CanonicalUrlEscapesSpacesAndLiteralPercentOnce()
    {
        var store = new DocumentStore();
        var spaced = store.Put("/shared/Excel test.xlsx", Array.Empty<byte>());
        var literalPercent = store.Put("/shared/Excel%2520test.xlsx", Array.Empty<byte>());

        Assert.Equal("https://localhost:7292/shared/Excel%20test.xlsx",
            DocumentRequestResolver.CanonicalUrl(spaced, "https://localhost:7292/"));
        Assert.Equal("https://localhost:7292/shared/Excel%2520test.xlsx",
            DocumentRequestResolver.CanonicalUrl(literalPercent, "https://localhost:7292"));
    }

    [Fact]
    public void UrlMissDoesNotCreateDocument()
    {
        var store = new DocumentStore();

        var resolved = DocumentRequestResolver.Resolve(store, new FssHttpRequest
        {
            Url = "/shared/missing.docx",
        });

        Assert.Null(resolved);
        Assert.Null(DocumentRequestResolver.Resolve(store, new FssHttpRequest()));
        Assert.Empty(store.List());
    }

    [Fact]
    public void ResourceIdWinsOverDriftedUrl()
    {
        var store = new DocumentStore();
        var document = store.Put("/shared/original.docx", [1]);

        var resolved = DocumentRequestResolver.Resolve(store, new FssHttpRequest
        {
            Url = "/shared/drifted%20name.docx",
            UseResourceId = true,
            ResourceId = document.TransitionId.ToString("D"),
        });

        Assert.Same(document, resolved);
    }

    [Fact]
    public void InvalidResourceIdDoesNotFallbackButAbsentResourceIdUsesUrl()
    {
        var store = new DocumentStore();
        var document = store.Put("/shared/existing.docx", [1]);

        Assert.Null(DocumentRequestResolver.Resolve(store, new FssHttpRequest
        {
            Url = document.Url,
            UseResourceId = true,
            ResourceId = "not-a-guid",
        }));
        Assert.Same(document, DocumentRequestResolver.Resolve(store, new FssHttpRequest
        {
            Url = document.Url,
            UseResourceId = true,
        }));
    }

    [Fact]
    public void ResourceIdResolutionPreservesLockOwnership()
    {
        var store = new DocumentStore();
        var document = store.Put("/shared/original.docx", [1]);
        var clientId = Guid.NewGuid();
        var lockId = Guid.NewGuid().ToString("D");
        var lockRequest = new FssHttpSubRequest
        {
            Type = SubRequestType.SchemaLock,
        };
        lockRequest.SubRequestDataAttributes["SchemaLockRequestType"] = "GetLock";
        lockRequest.SubRequestDataAttributes["SchemaLockID"] = lockId;
        lockRequest.SubRequestDataAttributes["ClientID"] = clientId.ToString("D");
        var lockResponse = new FssHttpSubResponse();
        Assert.Equal(LockOperationResult.Granted,
            FssHttpLockCoordinator.For(document).ApplySchemaLock(lockRequest, lockResponse));

        var resolved = DocumentRequestResolver.Resolve(store, new FssHttpRequest
        {
            Url = "/shared/old-encoded-name.docx",
            UseResourceId = true,
            ResourceId = document.TransitionId.ToString("D"),
        });
        Assert.Same(document, resolved);

        var statusResponse = new FssHttpSubResponse();
        Assert.Equal(LockOperationResult.Observed,
            FssHttpLockCoordinator.For(resolved!).ApplyLockStatus(
                new FssHttpSubRequest { Type = SubRequestType.LockStatus }, statusResponse));
        Assert.Equal("1", statusResponse.SubResponseDataAttributes["LockType"]);
        Assert.Equal(lockId, statusResponse.SubResponseDataAttributes["LockID"]);
    }
}
