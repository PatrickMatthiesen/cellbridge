using CellBridge.AspNetCore;
using CellBridge.FssHttpB;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using CellBridge.Web;

namespace CellBridge.Storage.Tests;

public sealed class GuidAllocationTests
{
    [Theory]
    [InlineData(1UL)]
    [InlineData(999UL)]
    [InlineData(1000UL)]
    [InlineData(99_999UL)]
    [InlineData(100_000UL)]
    public void BufferedAllocatorReturnsExactCountWithoutMutatingDocument(ulong count)
    {
        var document = new DocumentStore().Put("/allocation.docx", [1, 2]);
        var original = document.FilePartition.FileGraph.StorageIndex;
        var results = Enumerable.Range(0, 10).Select(_ => CellBinaryRequestExecutor.Execute(document,
            document.FilePartition, Request(count), DocumentAccess.Read | DocumentAccess.Write)).ToArray();
        var ranges = results.Select(AssertRange).ToArray();
        Assert.All(ranges, r => Assert.Equal(count, r.IntegerRangeMax - r.IntegerRangeMin));
        Assert.Equal(ranges.Length, ranges.Select(r => r.GuidComponent).Distinct().Count());
        Assert.Equal(original, document.FilePartition.FileGraph.StorageIndex);
        Assert.Equal(1U, document.ContentVersion);
        Assert.Equal(new byte[] { 1, 2 }, document.Content);
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(100_001UL)]
    [InlineData(ulong.MaxValue)]
    public void UnsupportedCountReturnsMatchingError(ulong count)
    {
        var document = new DocumentStore().Put("/allocation.docx", [1]);
        var result = Assert.Single(CellBinaryRequestExecutor.Execute(document, document.FilePartition,
            Request(count), DocumentAccess.Write).SubResponses);
        Assert.True(result.Status);
        Assert.Equal(23UL, result.RequestId);
        Assert.Equal(RequestTypes.AllocateExtendedGuidRange, result.RequestType);
        Assert.Equal((ulong)ProtocolErrorCode.RequestNotSupported, result.Error!.ErrorCode);
    }

    [Fact]
    public async Task ConcurrentServiceInstancesAndRecreatedServiceUseIndependentNamespacesWithoutStateChanges()
    {
        var provider = new StorageProvider(new InMemoryStateStore(), new InMemoryContentStore());
        var state = (await new CellBridgeDocumentService(provider).CreateAsync("/allocation.docx", MinimalDocx.Create(), TestActor.Value))!;
        var executions = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => new CellBridgeDocumentService(provider)
            .ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents, Request(100_000),
                new Dictionary<string, string>(), TestActor.Value).AsTask()));
        var ranges = executions.Select(x => AssertRange(x.Response)).ToArray();
        Assert.Equal(32, ranges.Select(x => x.GuidComponent).Distinct().Count());
        Assert.All(ranges, x => Assert.Equal(100_000UL, x.IntegerRangeMax - x.IntegerRangeMin));
        Assert.Equal(state, await provider.State.FindByResourceIdAsync(state.ResourceId));
        Assert.All(executions, x => Assert.Equal(state.StateVersion, x.State.StateVersion));
    }

    [Theory]
    [InlineData(DocumentAccess.None)]
    [InlineData(DocumentAccess.Read)]
    public async Task ReadOnlyAndUnauthorizedCallersCannotAllocate(DocumentAccess access)
    {
        var provider = new StorageProvider(new InMemoryStateStore(), new InMemoryContentStore());
        var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/allocation.docx", MinimalDocx.Create(), TestActor.Value))!;
        var other = new CellBridgeActor(new("tests:allocation-reader", "reader", "Reader"));
        await provider.State.TransitionAsync(state.ResourceId, (current, now) =>
            new StateTransition<bool>(DocumentPermissionUpdates.Apply(current, now,
                current.Security with { Grants = current.Security.Grants.SetItem(other.Identity.Subject, access) }), true));
        var denied = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents,
            Request(1), new Dictionary<string, string>(), other);
        Assert.Equal(CellBridgeAuthorization.AccessDeniedHResult, Assert.Single(denied.Response.SubResponses).Error!.ErrorCode);
        var document = new DocumentStore().Put("/buffered.docx", [1]);
        var buffered = CellBinaryRequestExecutor.Execute(document, document.FilePartition, Request(1), access);
        Assert.Equal(CellBridgeAuthorization.AccessDeniedHResult, Assert.Single(buffered.SubResponses).Error!.ErrorCode);
    }

    private static FsshttpbCellRequest Request(ulong count)
    {
        var request = new FsshttpbCellRequest();
        request.SubRequests.Add(new(RequestTypes.AllocateExtendedGuidRange) { RequestId = 23,
            Data = new AllocateExtendedGuidRangeSubRequestData { RequestIdCount = count } });
        return FsshttpbCellRequest.Deserialize(new BinaryReaderEx(request.ToByteArray()));
    }

    private static AllocateExtendedGuidRangeSubResponseData AssertRange(FsshttpbResponse response)
    {
        var decoded = FsshttpbResponse.Deserialize(new BinaryReaderEx(response.ToByteArray()));
        var result = Assert.Single(decoded.SubResponses);
        Assert.False(result.Status);
        Assert.Equal(23UL, result.RequestId);
        var data = Assert.IsType<AllocateExtendedGuidRangeSubResponseData>(result.Data);
        Assert.NotEqual(Guid.Empty, data.GuidComponent);
        Assert.InRange(data.IntegerRangeMax, 1000UL, 100_000UL);
        return data;
    }
}
