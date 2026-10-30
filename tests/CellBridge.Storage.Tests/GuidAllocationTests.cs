using CellBridge.AspNetCore;
using CellBridge.FssHttpB;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.TestFixtures;

namespace CellBridge.Storage.Tests;

[Collection("PostgreSQL")]
public sealed class GuidAllocationTests
{
    [Theory]
    [InlineData(false, 1UL)]
    [InlineData(true, 1UL)]
    [InlineData(false, 999UL)]
    [InlineData(true, 999UL)]
    [InlineData(false, 1000UL)]
    [InlineData(true, 1000UL)]
    [InlineData(false, 99_999UL)]
    [InlineData(true, 99_999UL)]
    [InlineData(false, 100_000UL)]
    [InlineData(true, 100_000UL)]
    public async Task AllocatorReturnsExactCountWithoutMutatingDocument(bool postgreSql, ulong count)
    {
        await using var testProvider = ProtocolExecutionTestProvider.Create(postgreSql);
        var provider = testProvider.Provider;
        var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/allocation-" + Guid.NewGuid().ToString("N") + ".docx", MinimalDocx.Create(), TestActor.Value))!;
        var results = new List<CellExecution>();
        for (int i = 0; i < 10; i++)
            results.Add(await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents,
                Request(count), new Dictionary<string, string>(), TestActor.Value));
        var ranges = results.Select(result => AssertRange(result.Response)).ToArray();
        Assert.All(ranges, r => Assert.Equal(count, r.IntegerRangeMax - r.IntegerRangeMin));
        Assert.Equal(ranges.Length, ranges.Select(r => r.GuidComponent).Distinct().Count());
        Assert.Equal(state,await provider.Documents.FindAsync(new DocumentIdentity.Resource(state.ResourceId)));
        Assert.All(results, result => Assert.Equal(state.StateVersion, result.State.StateVersion));
    }

    [Theory]
    [InlineData(false, 0UL)]
    [InlineData(true, 0UL)]
    [InlineData(false, 100_001UL)]
    [InlineData(true, 100_001UL)]
    [InlineData(false, ulong.MaxValue)]
    [InlineData(true, ulong.MaxValue)]
    public async Task UnsupportedCountReturnsMatchingError(bool postgreSql, ulong count)
    {
        await using var testProvider = ProtocolExecutionTestProvider.Create(postgreSql);
        var provider = testProvider.Provider;
        var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/allocation-" + Guid.NewGuid().ToString("N") + ".docx", MinimalDocx.Create(), TestActor.Value))!;
        var execution = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents,
            Request(count), new Dictionary<string, string>(), TestActor.Value);
        var result = Assert.Single(FsshttpbResponse.Deserialize(new(execution.Response.ToByteArray())).SubResponses);
        Assert.True(result.Status);
        Assert.Equal(23UL, result.RequestId);
        Assert.Equal(RequestTypes.AllocateExtendedGuidRange, result.RequestType);
        Assert.Equal((ulong)ProtocolErrorCode.RequestNotSupported, result.Error!.ErrorCode);
        Assert.Equal(state,await provider.Documents.FindAsync(new DocumentIdentity.Resource(state.ResourceId)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentServiceInstancesAndRecreatedServiceUseIndependentNamespacesWithoutStateChanges(bool postgreSql)
    {
        await using var testProvider = ProtocolExecutionTestProvider.Create(postgreSql);
        var provider = testProvider.Provider;
        var state = (await new CellBridgeDocumentService(provider).CreateAsync("/allocation-" + Guid.NewGuid().ToString("N") + ".docx", MinimalDocx.Create(), TestActor.Value))!;
        var executions = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => new CellBridgeDocumentService(provider)
            .ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents, Request(100_000),
                new Dictionary<string, string>(), TestActor.Value).AsTask()));
        var ranges = executions.Select(x => AssertRange(x.Response)).ToArray();
        Assert.Equal(32, ranges.Select(x => x.GuidComponent).Distinct().Count());
        Assert.All(ranges, x => Assert.Equal(100_000UL, x.IntegerRangeMax - x.IntegerRangeMin));
        Assert.Equal(state,await provider.Documents.FindAsync(new DocumentIdentity.Resource(state.ResourceId)));
        Assert.All(executions, x => Assert.Equal(state.StateVersion, x.State.StateVersion));
    }

    [Theory]
    [InlineData(false, DocumentAccess.None)]
    [InlineData(true, DocumentAccess.None)]
    [InlineData(false, DocumentAccess.Read)]
    [InlineData(true, DocumentAccess.Read)]
    public async Task ReadOnlyAndUnauthorizedCallersCannotAllocate(bool postgreSql, DocumentAccess access)
    {
        await using var testProvider = ProtocolExecutionTestProvider.Create(postgreSql);
        var provider = testProvider.Provider;
        var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/allocation-" + Guid.NewGuid().ToString("N") + ".docx", MinimalDocx.Create(), TestActor.Value))!;
        var other = new CellBridgeActor(new("tests:allocation-reader", "reader", "Reader"));
        Assert.True((await provider.Documents.ExecuteControlAsync(new DocumentControlCommand.SetPermissions(state.ResourceId,
            StorageTestDocuments.Admission(state),state.Security with { Grants = state.Security.Grants.SetItem(other.Identity.Subject,access) },state.AuthorityGeneration))).Changed);
        var denied = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents,
            Request(1), new Dictionary<string, string>(), other);
        Assert.Equal(CellBridgeAuthorization.AccessDeniedHResult, Assert.Single(denied.Response.SubResponses).Error!.ErrorCode);
        Assert.Equal(state.ContentVersion, denied.State.ContentVersion);
        Assert.Equal(state.Package,denied.State.Package);
        Assert.Equal(0,denied.State.Counters.SaveReceipts);
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
