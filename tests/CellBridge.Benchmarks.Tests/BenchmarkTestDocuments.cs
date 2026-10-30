using CellBridge.AspNetCore;
using CellBridge.FssHttpB;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;

namespace CellBridge.Benchmarks.Tests;

internal static class BenchmarkTestDocuments
{
    internal static StorageProvider Memory()
    {
        var content = new InMemoryContentStore();
        return new(new InMemoryDocumentRepository(content), content);
    }

    internal static async Task<PartitionGraphSnapshot> GraphAsync(CellBridgeDocumentService service, DocumentHead head, CellBridgeActor actor)
    {
        var request = new FsshttpbCellRequest();
        request.SubRequests.Add(new(RequestTypes.QueryChanges) { RequestId = 1,
            Data = new QueryChangesSubRequestData { IncludeStorageManifest = true, IncludeCellChanges = true } });
        var result = await service.ExecuteAsync(head.ResourceId, DocumentPartitionKind.FileContents, request, new Dictionary<string, string>(), actor);
        Assert.False(result.IsCancelled);
        Assert.False(result.Response.Status);
        var operation = Assert.Single(result.Response.SubResponses);
        Assert.False(operation.Status);
        return PartitionGraphSnapshot.Create(result.Response.DataElementPackage!.DataElements,
            Assert.IsType<QueryChangesSubResponseData>(operation.Data).StorageIndexExtendedGuid);
    }

    internal static async Task<byte[]> PackageAsync(StorageProvider provider, DocumentPackage package)
    {
        await using var stream = await provider.Packages.OpenReadAsync(package);
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy);
        return copy.ToArray();
    }
}
