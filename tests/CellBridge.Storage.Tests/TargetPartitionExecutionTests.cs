using CellBridge.AspNetCore;
using CellBridge.FssHttpB;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using CellBridge.Web;

namespace CellBridge.Storage.Tests;

public sealed class TargetPartitionExecutionTests
{
    [Theory]
    [InlineData(DocumentPartitionKind.FileContents)]
    [InlineData(DocumentPartitionKind.Metadata)]
    [InlineData(DocumentPartitionKind.EditorsTable)]
    public void MatchingSelectorsWorkAndForeignSelectorsFailInBufferedExecutor(DocumentPartitionKind kind)
    {
        var document = new DocumentStore().Put("/selector.docx", [1]);
        foreach (var target in new[] { Selector(kind), Guid.NewGuid() }.Distinct())
        {
            var request = Request(RequestTypes.AllocateExtendedGuidRange, target);
            var operation = Assert.Single(CellBinaryRequestExecutor.Execute(document, document.GetPartition(kind),
                request, DocumentAccess.Read | DocumentAccess.Write).SubResponses);
            Assert.Equal(target != Selector(kind), operation.Status);
            if (!operation.Status) Assert.IsType<AllocateExtendedGuidRangeSubResponseData>(operation.Data);
            else
            {
                Assert.Equal(ErrorType.Protocol, operation.Error!.Type);
                Assert.Equal((ulong)ProtocolErrorCode.RequestNotSupported, operation.Error.ErrorCode);
            }
        }
    }

    [Theory]
    [InlineData(RequestTypes.PutChanges)]
    [InlineData(RequestTypes.AllocateExtendedGuidRange)]
    [InlineData(RequestTypes.QueryChanges)]
    public async Task UnsupportedTargetCannotPublishOrAllocateInDurableExecutor(RequestTypes type)
    {
        var provider = new StorageProvider(new InMemoryStateStore(), new InMemoryContentStore());
        var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/target.docx", MinimalDocx.Create(), TestActor.Value))!;
        foreach (var target in new[] { StoredDocument.MetadataPartitionId, Guid.NewGuid() })
        {
            var result = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents,
                Request(type, target), new Dictionary<string, string>(), TestActor.Value);
            var response = Assert.Single(result.Response.SubResponses);
            Assert.True(response.Status); Assert.Equal(type, response.RequestType);
            Assert.Null(response.Data);
            Assert.Equal(ErrorType.Protocol, response.Error!.Type);
            Assert.Equal((ulong)ProtocolErrorCode.RequestNotSupported, response.Error.ErrorCode);
            Assert.Equal(state, await provider.State.FindByResourceIdAsync(state.ResourceId));
        }
        // Explicit zero means file, and does not inherit a metadata SOAP selector.
        var mismatch = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.Metadata,
            Request(RequestTypes.QueryAccess, Guid.Empty), new Dictionary<string, string>(), TestActor.Value);
        Assert.True(Assert.Single(mismatch.Response.SubResponses).Status);
    }

    private static Guid Selector(DocumentPartitionKind kind) => kind switch
    {
        DocumentPartitionKind.Metadata => StoredDocument.MetadataPartitionId,
        DocumentPartitionKind.EditorsTable => StoredDocument.EditorsTablePartitionId,
        _ => Guid.Empty,
    };
    private static FsshttpbCellRequest Request(RequestTypes type, Guid target)
    {
        var request = new FsshttpbCellRequest();
        request.SubRequests.Add(new(type) { RequestId = 4, TargetPartitionId = target,
            Data = type == RequestTypes.AllocateExtendedGuidRange ? new AllocateExtendedGuidRangeSubRequestData { RequestIdCount = 1 } : null });
        return request;
    }
}
