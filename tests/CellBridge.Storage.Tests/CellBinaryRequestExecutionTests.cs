using CellBridge.AspNetCore;
using CellBridge.FssHttpB;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;

namespace CellBridge.Storage.Tests;

[Collection("PostgreSQL")]
public sealed class CellBinaryRequestExecutionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecutesEveryBinaryOperationAndPreservesEachRequestId(bool postgreSql)
    {
        await using var scope = ProtocolExecutionTestProvider.Create(postgreSql);
        var provider = scope.Provider;
        var service = new CellBridgeDocumentService(provider);
        var document = (await service.CreateAsync($"/request-{Guid.NewGuid():N}.docx", [1, 2, 3], TestActor.Value))!;
        var request = new FsshttpbCellRequest();
        request.SubRequests.Add(new FsshttpbCellSubRequest(RequestTypes.QueryAccess) { RequestId = 41 });
        request.SubRequests.Add(new FsshttpbCellSubRequest(RequestTypes.QueryChanges)
            { RequestId = 73, Data = new QueryChangesSubRequestData() });

        var execution = await service.ExecuteAsync(document.ResourceId, DocumentPartitionKind.FileContents, request,
            new Dictionary<string, string>(), TestActor.Value);
        var response = execution.Response;

        Assert.Equal(new ulong[] { 41, 73 }, response.SubResponses.Select(x => x.RequestId));
        Assert.Equal(new[] { RequestTypes.QueryAccess, RequestTypes.QueryChanges },
            response.SubResponses.Select(x => x.RequestType));
        Assert.NotNull(response.DataElementPackage);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnsupportedPutChangesReturnsProtocolErrorWithoutMutatingDocumentOrPartition(bool postgreSql)
    {
        await using var scope = ProtocolExecutionTestProvider.Create(postgreSql);
        var provider = scope.Provider;
        var service = new CellBridgeDocumentService(provider);
        byte[] original = [9, 8, 7, 6];
        var document = (await service.CreateAsync($"/request-{Guid.NewGuid():N}.docx", original, TestActor.Value))!;
        byte[] contentBefore = await provider.Content.ReadVerifiedAsync(document.Content);
        uint contentVersionBefore = document.ContentVersion;
        ulong fileKnowledgeBefore = document.Partitions.Single(p => p.Kind == (int)DocumentPartitionKind.FileContents).Knowledge;
        ulong metadataKnowledgeBefore = document.Partitions.Single(p => p.Kind == (int)DocumentPartitionKind.Metadata).Knowledge;

        var request = new FsshttpbCellRequest
        {
            DataElementPackage = new DataElementPackage
            {
                DataElements =
                {
                    new DataElement(
                        DataElementType.ObjectDataBLOBDataElementData,
                        new ExGuid(1, Guid.NewGuid()),
                        new SerialNumber(Guid.NewGuid(), 1))
                    {
                        // This used to be mistaken for a complete document when
                        // it happened to be the largest BLOB in the package.
                        Data = Enumerable.Repeat((byte)0xCC, 128).ToArray(),
                    },
                },
            },
        };
        request.SubRequests.Add(new FsshttpbCellSubRequest(RequestTypes.PutChanges)
        {
            RequestId = 982,
            Data = new PutChangesSubRequestData
            {
                StorageIndex = new ExGuid(1, Guid.NewGuid()),
                ExpectedStorageIndex = new ExGuid(1, Guid.NewGuid()),
                Flags = 0x02,
            },
        });

        var execution = await service.ExecuteAsync(document.ResourceId, DocumentPartitionKind.FileContents, request,
            new Dictionary<string, string>(), TestActor.Value);
        var response = execution.Response;
        var result = Assert.Single(response.SubResponses);

        Assert.Equal(982UL, result.RequestId);
        Assert.Equal(RequestTypes.PutChanges, result.RequestType);
        Assert.True(result.Status);
        Assert.Equal(ErrorType.Cell, result.Error?.Type);
        Assert.Equal((ulong)CellErrorCode.RequestNotSupported, result.Error?.ErrorCode);
        Assert.Null(result.Data);
        var persisted = (await provider.State.FindByResourceIdAsync(document.ResourceId))!;
        Assert.Equal(contentBefore, await provider.Content.ReadVerifiedAsync(persisted.Content));
        Assert.Equal(contentVersionBefore, persisted.ContentVersion);
        Assert.Equal(fileKnowledgeBefore, persisted.Partitions.Single(p => p.Kind == (int)DocumentPartitionKind.FileContents).Knowledge);
        Assert.Equal(metadataKnowledgeBefore, persisted.Partitions.Single(p => p.Kind == (int)DocumentPartitionKind.Metadata).Knowledge);
        Assert.Equal(document.StateVersion, persisted.StateVersion);
        Assert.Empty(execution.AcceptedSaves);

        var wireResponse = FsshttpbResponse.Deserialize(new BinaryReaderEx(response.ToByteArray()));
        var wireResult = Assert.Single(wireResponse.SubResponses);
        Assert.Equal(982UL, wireResult.RequestId);
        Assert.Equal(RequestTypes.PutChanges, wireResult.RequestType);
        Assert.True(wireResult.Status);
        Assert.Equal((ulong)CellErrorCode.RequestNotSupported, wireResult.Error?.ErrorCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmptyRequestFailsAndRepeatedFileQueriesShareOnePackage(bool postgreSql)
    {
        await using var scope = ProtocolExecutionTestProvider.Create(postgreSql);
        var provider = scope.Provider;
        var service = new CellBridgeDocumentService(provider);
        var document = (await service.CreateAsync($"/request-{Guid.NewGuid():N}.docx", [1], TestActor.Value))!;

        var emptyResponse = (await service.ExecuteAsync(document.ResourceId, DocumentPartitionKind.FileContents,
            new FsshttpbCellRequest(), new Dictionary<string, string>(), TestActor.Value)).Response;
        Assert.True(emptyResponse.Status);
        Assert.Equal((ulong)ProtocolErrorCode.RequestStreamSchemaError, emptyResponse.Error?.ErrorCode);

        var request = new FsshttpbCellRequest();
        request.SubRequests.Add(new FsshttpbCellSubRequest(RequestTypes.QueryChanges)
            { RequestId = 12, Data = new QueryChangesSubRequestData() });
        request.SubRequests.Add(new FsshttpbCellSubRequest(RequestTypes.QueryChanges)
            { RequestId = 34, Data = new QueryChangesSubRequestData() });
        var execution = await service.ExecuteAsync(document.ResourceId, DocumentPartitionKind.FileContents, request,
            new Dictionary<string, string>(), TestActor.Value);
        var response = execution.Response;

        Assert.Equal(new ulong[] { 12, 34 }, response.SubResponses.Select(x => x.RequestId));
        Assert.False(response.SubResponses[0].Status);
        Assert.False(response.SubResponses[1].Status);
        Assert.Equal(response.DataElementPackage!.DataElements.Count,
            response.DataElementPackage.DataElements.Select(e => e.DataElementExtendedGuid).Distinct().Count());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnknownOperationReturnsMatchingUnsupportedError(bool postgreSql)
    {
        await using var scope = ProtocolExecutionTestProvider.Create(postgreSql);
        var provider = scope.Provider;
        var service = new CellBridgeDocumentService(provider);
        var document = (await service.CreateAsync($"/request-{Guid.NewGuid():N}.docx", [1], TestActor.Value))!;
        var request = new FsshttpbCellRequest();
        request.SubRequests.Add(new FsshttpbCellSubRequest((RequestTypes)99) { RequestId = 321 });

        var execution = await service.ExecuteAsync(document.ResourceId, DocumentPartitionKind.FileContents, request,
            new Dictionary<string, string>(), TestActor.Value);
        var response = execution.Response;
        var result = Assert.Single(response.SubResponses);

        Assert.Equal(321UL, result.RequestId);
        Assert.Equal((RequestTypes)99, result.RequestType);
        Assert.True(result.Status);
        Assert.Equal(ErrorType.Cell, result.Error?.Type);
        Assert.Equal((ulong)CellErrorCode.UnknownRequest, result.Error?.ErrorCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DuplicateAndMaximumRequestIdsRejectBeforeExecutionWhileZeroIsValid(bool postgreSql)
    {
        await using var scope = ProtocolExecutionTestProvider.Create(postgreSql);
        var provider = scope.Provider;
        var service = new CellBridgeDocumentService(provider);
        var document = (await service.CreateAsync($"/request-{Guid.NewGuid():N}.docx", [1], TestActor.Value))!;
        var duplicate = new FsshttpbCellRequest
        {
            SubRequests =
            {
                new(RequestTypes.QueryAccess) { RequestId = 0 },
                new(RequestTypes.QueryAccess) { RequestId = 0 },
            },
        };

        var reader = TestActor.Value with { AccessLimit = new(document.ResourceId, DocumentAccess.Read) };
        var rejected = (await service.ExecuteAsync(document.ResourceId, DocumentPartitionKind.FileContents, duplicate,
            new Dictionary<string, string>(), reader)).Response;
        Assert.True(rejected.Status);
        Assert.Equal(ErrorType.Cell, rejected.Error?.Type);
        Assert.Equal((ulong)CellErrorCode.RequestStreamSchemaError, rejected.Error?.ErrorCode);
        Assert.Empty(rejected.SubResponses);

        var zero = new FsshttpbCellRequest
        {
            SubRequests = { new(RequestTypes.QueryAccess) { RequestId = 0 } },
        };
        Assert.Equal(0UL, Assert.Single((await service.ExecuteAsync(document.ResourceId, DocumentPartitionKind.FileContents, zero,
            new Dictionary<string, string>(), reader)).Response.SubResponses).RequestId);

        zero.SubRequests[0].RequestId = uint.MaxValue;
        Assert.Equal((ulong)CellErrorCode.RequestStreamSchemaError,
            (await service.ExecuteAsync(document.ResourceId, DocumentPartitionKind.FileContents, zero,
                new Dictionary<string, string>(), reader)).Response.Error?.ErrorCode);
    }

    [Theory]
    [InlineData(false, 10, 11)]
    [InlineData(false, 13, 10)]
    [InlineData(true, 10, 11)]
    [InlineData(true, 13, 10)]
    public async Task WireAndDirectIncompatibleVersionsReturnTypedCellError(bool postgreSql, ushort version, ushort minimum)
    {
        var request = new FsshttpbCellRequest
        {
            ProtocolVersion = version,
            MinimumVersion = minimum,
            SubRequests = { new(RequestTypes.QueryAccess) { RequestId = 1 } },
        };
        var parsed = FsshttpbCellRequest.Deserialize(new(request.ToByteArray()));
        await using var scope = ProtocolExecutionTestProvider.Create(postgreSql);
        var provider = scope.Provider;
        var service = new CellBridgeDocumentService(provider);
        var document = (await service.CreateAsync($"/version-{Guid.NewGuid():N}.docx", [1], TestActor.Value))!;

        foreach (var candidate in new[] { request, parsed })
        {
            var response = (await service.ExecuteAsync(document.ResourceId, DocumentPartitionKind.FileContents, candidate,
                new Dictionary<string, string>(), TestActor.Value)).Response;

            Assert.True(response.Status);
            Assert.Equal(ErrorType.Cell, response.Error?.Type);
            Assert.Equal((ulong)CellErrorCode.IncompatibleProtocolVersion, response.Error?.ErrorCode);
            Assert.Empty(response.SubResponses);
        }
    }
}
