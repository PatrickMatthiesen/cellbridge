using CellBridge.AspNetCore;
using CellBridge.FssHttpB;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.TestFixtures;

namespace CellBridge.Storage.Tests;

[Collection("PostgreSQL")]
public sealed class TargetPartitionExecutionTests
{
    [Theory]
    [InlineData(false, DocumentPartitionKind.FileContents)]
    [InlineData(true, DocumentPartitionKind.FileContents)]
    [InlineData(false, DocumentPartitionKind.Metadata)]
    [InlineData(true, DocumentPartitionKind.Metadata)]
    [InlineData(false, DocumentPartitionKind.EditorsTable)]
    [InlineData(true, DocumentPartitionKind.EditorsTable)]
    public async Task KnownSelectorsWorkAcrossSoapPartitionsAndUnknownSelectorsFail(bool postgreSql, DocumentPartitionKind kind)
    {
        await using var testProvider = ProtocolExecutionTestProvider.Create(postgreSql);
        var provider = testProvider.Provider;
        var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/selector-" + Guid.NewGuid().ToString("N") + ".docx", MinimalDocx.Create(), TestActor.Value))!;
        var unknown = Guid.NewGuid();
        foreach (var target in new[] { Guid.Empty, StoredDocument.MetadataPartitionId, StoredDocument.EditorsTablePartitionId, unknown })
        {
            var request = Request(RequestTypes.AllocateExtendedGuidRange, target);
            var execution = await service.ExecuteAsync(state.ResourceId, kind, request,
                new Dictionary<string, string>(), TestActor.Value);
            var operation = Assert.Single(execution.Response.SubResponses);
            Assert.Equal(target == unknown, operation.Status);
            if (!operation.Status) Assert.IsType<AllocateExtendedGuidRangeSubResponseData>(operation.Data);
            else
            {
                Assert.Equal(ErrorType.Cell, operation.Error!.Type);
                Assert.Equal((ulong)CellErrorCode.RequestNotSupported, operation.Error.ErrorCode);
            }
        }
    }

    [Theory]
    [InlineData(false, RequestTypes.PutChanges)]
    [InlineData(true, RequestTypes.PutChanges)]
    [InlineData(false, RequestTypes.AllocateExtendedGuidRange)]
    [InlineData(true, RequestTypes.AllocateExtendedGuidRange)]
    [InlineData(false, RequestTypes.QueryChanges)]
    [InlineData(true, RequestTypes.QueryChanges)]
    public async Task UnsupportedTargetCannotPublishOrAllocate(bool postgreSql, RequestTypes type)
    {
        await using var testProvider = ProtocolExecutionTestProvider.Create(postgreSql);
        var provider = testProvider.Provider;
        var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/target-" + Guid.NewGuid().ToString("N") + ".docx", MinimalDocx.Create(), TestActor.Value))!;
        var result = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents,
            Request(type, Guid.NewGuid()), new Dictionary<string, string>(), TestActor.Value);
        var response = Assert.Single(result.Response.SubResponses);
        Assert.True(response.Status); Assert.Equal(type, response.RequestType);
        Assert.Null(response.Data);
        Assert.Equal(ErrorType.Cell, response.Error!.Type);
        Assert.Equal((ulong)CellErrorCode.RequestNotSupported, response.Error.ErrorCode);
        Assert.Empty(result.AcceptedSaves);
        Assert.Equal(state,await provider.Documents.FindAsync(new DocumentIdentity.Resource(state.ResourceId)));
        // Explicit zero means file, and does not inherit a metadata SOAP selector.
        var explicitFile = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.Metadata,
            Request(RequestTypes.QueryAccess, Guid.Empty), new Dictionary<string, string>(), TestActor.Value);
        Assert.False(Assert.Single(explicitFile.Response.SubResponses).Status);
    }

    private static FsshttpbCellRequest Request(RequestTypes type, Guid target)
    {
        var request = new FsshttpbCellRequest();
        request.SubRequests.Add(new(type) { RequestId = 4, TargetPartitionId = target,
            Data = type switch
            {
                RequestTypes.AllocateExtendedGuidRange => new AllocateExtendedGuidRangeSubRequestData { RequestIdCount = 1 },
                RequestTypes.QueryChanges => new QueryChangesSubRequestData(),
                RequestTypes.PutChanges => new PutChangesSubRequestData(),
                _ => null,
            } });
        return request;
    }
}
