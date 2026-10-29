using CellBridge.AspNetCore;
using CellBridge.FssHttpB;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using CellBridge.TestFixtures;
using CellBridge.Tests;

namespace CellBridge.Storage.Tests;

internal static class StorageTestDocuments
{
    internal static StorageProvider Memory(IContentStore? content = null, StorageLimits? limits = null)
    {
        content ??= new InMemoryContentStore();
        limits ??= new();
        return new(new InMemoryDocumentRepository(content, limits), content, limits);
    }

    internal static DocumentActorAdmission Admission(DocumentHead head, CellBridgeActor? actor = null)
    {
        actor ??= TestActor.Value;
        return new(actor.Identity, head.ResourceId, head.LifecycleGeneration, head.Security.AuthorizationPolicy,
            head.Security.AccessFor(actor.Identity.Subject))
        { AuthorityGeneration = head.AuthorityGeneration, StateVersion = head.StateVersion };
    }

    internal static async Task<PartitionGraphSnapshot> GraphAsync(CellBridgeDocumentService service, DocumentHead head,
        CellBridgeActor? actor = null)
    {
        var request = new FsshttpbCellRequest();
        request.SubRequests.Add(new(RequestTypes.QueryChanges) { RequestId = 1,
            Data = new QueryChangesSubRequestData { IncludeStorageManifest = true, IncludeCellChanges = true } });
        var result = await service.ExecuteAsync(head.ResourceId, DocumentPartitionKind.FileContents,
            request, new Dictionary<string, string>(), actor ?? TestActor.Value);
        Assert.False(result.IsCancelled);
        Assert.False(result.Response.Status);
        var operation = Assert.Single(result.Response.SubResponses);
        Assert.False(operation.Status);
        return PartitionGraphSnapshot.Create(result.Response.DataElementPackage!.DataElements,
            Assert.IsType<QueryChangesSubResponseData>(operation.Data).StorageIndexExtendedGuid);
    }

    internal static async Task<FsshttpbCellRequest> SaveRequestAsync(CellBridgeDocumentService service,
        DocumentHead head, byte[] bytes, CellBridgeActor? actor = null) =>
        SyntheticWorkload.Save(await GraphAsync(service, head, actor), bytes, "changed-full", 0, new Random(Random.Shared.Next()));

    internal static FsshttpbCellRequest MetadataRequest(GraphFixture fixture)
    {
        var request = new FsshttpbCellRequest { DataElementPackage = new() };
        request.DataElementPackage.DataElements.AddRange(fixture.Elements);
        request.SubRequests.Add(new(RequestTypes.PutChanges) { RequestId = 1,
            Data = new PutChangesSubRequestData { StorageIndex = fixture.Index, HasAdditionalFlags = true, AdditionalFlagsBits = 1 } });
        return request;
    }

    internal static async Task<byte[]> PackageAsync(StorageProvider provider, DocumentPackage package)
    {
        await using var stream = await provider.Packages.OpenReadAsync(package);
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy);
        return copy.ToArray();
    }
}
