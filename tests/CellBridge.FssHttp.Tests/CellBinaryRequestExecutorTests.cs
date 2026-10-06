using CellBridge.AspNetCore;
using CellBridge.FssHttpB;
using CellBridge.Storage;
using CellBridge.Web;

namespace CellBridge.FssHttp.Tests;

public sealed class CellBinaryRequestExecutorTests
{
    [Fact]
    public void ExecutesEveryBinaryOperationAndPreservesEachRequestId()
    {
        var store = new DocumentStore();
        var document = store.Put("/test.docx", [1, 2, 3]);
        var request = new FsshttpbCellRequest();
        request.SubRequests.Add(new FsshttpbCellSubRequest(RequestTypes.QueryAccess) { RequestId = 41 });
        request.SubRequests.Add(new FsshttpbCellSubRequest(RequestTypes.QueryChanges)
            { RequestId = 73, Data = new QueryChangesSubRequestData() });

        var response = CellBinaryRequestExecutor.Execute(document, document.FilePartition, request, CellBridge.Storage.Abstractions.DocumentAccess.Read | CellBridge.Storage.Abstractions.DocumentAccess.Write);

        Assert.Equal(new ulong[] { 41, 73 }, response.SubResponses.Select(x => x.RequestId));
        Assert.Equal(new[] { RequestTypes.QueryAccess, RequestTypes.QueryChanges },
            response.SubResponses.Select(x => x.RequestType));
        Assert.NotNull(response.DataElementPackage);
    }

    [Fact]
    public void UnsupportedPutChangesReturnsProtocolErrorWithoutMutatingDocumentOrPartition()
    {
        var store = new DocumentStore();
        byte[] original = [9, 8, 7, 6];
        var document = store.Put("/test.docx", original);
        byte[] contentBefore = document.Content.ToArray();
        uint contentVersionBefore = document.ContentVersion;
        ulong fileKnowledgeBefore = document.FilePartition.KnowledgeSequence;
        ulong metadataKnowledgeBefore = document.MetadataPartition.KnowledgeSequence;

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

        var response = CellBinaryRequestExecutor.Execute(document, document.FilePartition, request, CellBridge.Storage.Abstractions.DocumentAccess.Read | CellBridge.Storage.Abstractions.DocumentAccess.Write);
        var result = Assert.Single(response.SubResponses);

        Assert.Equal(982UL, result.RequestId);
        Assert.Equal(RequestTypes.PutChanges, result.RequestType);
        Assert.True(result.Status);
        Assert.Equal(ErrorType.Cell, result.Error?.Type);
        Assert.Equal((ulong)CellErrorCode.RequestNotSupported, result.Error?.ErrorCode);
        Assert.Null(result.Data);
        Assert.Equal(contentBefore, document.Content);
        Assert.Equal(contentVersionBefore, document.ContentVersion);
        Assert.Equal(fileKnowledgeBefore, document.FilePartition.KnowledgeSequence);
        Assert.Equal(metadataKnowledgeBefore, document.MetadataPartition.KnowledgeSequence);

        var wireResponse = FsshttpbResponse.Deserialize(new BinaryReaderEx(response.ToByteArray()));
        var wireResult = Assert.Single(wireResponse.SubResponses);
        Assert.Equal(982UL, wireResult.RequestId);
        Assert.Equal(RequestTypes.PutChanges, wireResult.RequestType);
        Assert.True(wireResult.Status);
        Assert.Equal((ulong)CellErrorCode.RequestNotSupported, wireResult.Error?.ErrorCode);
    }

    [Fact]
    public void EmptyRequestFailsAndRepeatedFileQueriesShareOnePackage()
    {
        var store = new DocumentStore();
        var document = store.Put("/test.docx", [1]);

        var emptyResponse = CellBinaryRequestExecutor.Execute(
            document, document.FilePartition, new FsshttpbCellRequest(), CellBridge.Storage.Abstractions.DocumentAccess.Read | CellBridge.Storage.Abstractions.DocumentAccess.Write);
        Assert.True(emptyResponse.Status);
        Assert.Equal((ulong)ProtocolErrorCode.RequestStreamSchemaError, emptyResponse.Error?.ErrorCode);

        var request = new FsshttpbCellRequest();
        request.SubRequests.Add(new FsshttpbCellSubRequest(RequestTypes.QueryChanges)
            { RequestId = 12, Data = new QueryChangesSubRequestData() });
        request.SubRequests.Add(new FsshttpbCellSubRequest(RequestTypes.QueryChanges)
            { RequestId = 34, Data = new QueryChangesSubRequestData() });
        var response = CellBinaryRequestExecutor.Execute(document, document.FilePartition, request, CellBridge.Storage.Abstractions.DocumentAccess.Read | CellBridge.Storage.Abstractions.DocumentAccess.Write);

        Assert.Equal(new ulong[] { 12, 34 }, response.SubResponses.Select(x => x.RequestId));
        Assert.False(response.SubResponses[0].Status);
        Assert.False(response.SubResponses[1].Status);
        Assert.Equal(response.DataElementPackage!.DataElements.Count,
            response.DataElementPackage.DataElements.Select(e => e.DataElementExtendedGuid).Distinct().Count());
    }

    [Fact]
    public void UnknownOperationReturnsMatchingUnsupportedError()
    {
        var store = new DocumentStore();
        var document = store.Put("/test.docx", [1]);
        var request = new FsshttpbCellRequest();
        request.SubRequests.Add(new FsshttpbCellSubRequest((RequestTypes)99) { RequestId = 321 });

        var response = CellBinaryRequestExecutor.Execute(document, document.FilePartition, request, CellBridge.Storage.Abstractions.DocumentAccess.Read | CellBridge.Storage.Abstractions.DocumentAccess.Write);
        var result = Assert.Single(response.SubResponses);

        Assert.Equal(321UL, result.RequestId);
        Assert.Equal((RequestTypes)99, result.RequestType);
        Assert.True(result.Status);
        Assert.Equal(ErrorType.Cell, result.Error?.Type);
        Assert.Equal((ulong)CellErrorCode.UnknownRequest, result.Error?.ErrorCode);
    }

    [Fact]
    public void DuplicateAndMaximumRequestIdsRejectBeforeExecutionWhileZeroIsValid()
    {
        var document = new DocumentStore().Put("/test.docx", [1]);
        var duplicate = new FsshttpbCellRequest
        {
            SubRequests =
            {
                new(RequestTypes.QueryAccess) { RequestId = 0 },
                new(RequestTypes.QueryAccess) { RequestId = 0 },
            },
        };

        var rejected = CellBinaryRequestExecutor.Execute(document, document.FilePartition, duplicate,
            CellBridge.Storage.Abstractions.DocumentAccess.Read);
        Assert.True(rejected.Status);
        Assert.Equal(ErrorType.Cell, rejected.Error?.Type);
        Assert.Equal((ulong)CellErrorCode.RequestStreamSchemaError, rejected.Error?.ErrorCode);
        Assert.Empty(rejected.SubResponses);

        var zero = new FsshttpbCellRequest
        {
            SubRequests = { new(RequestTypes.QueryAccess) { RequestId = 0 } },
        };
        Assert.Equal(0UL, Assert.Single(CellBinaryRequestExecutor.Execute(document, document.FilePartition, zero,
            CellBridge.Storage.Abstractions.DocumentAccess.Read).SubResponses).RequestId);

        zero.SubRequests[0].RequestId = uint.MaxValue;
        Assert.Equal((ulong)CellErrorCode.RequestStreamSchemaError,
            CellBinaryRequestExecutor.Execute(document, document.FilePartition, zero,
                CellBridge.Storage.Abstractions.DocumentAccess.Read).Error?.ErrorCode);
    }

    [Theory]
    [InlineData(10, 11)]
    [InlineData(13, 10)]
    public void WireAndDirectIncompatibleVersionsReturnTypedCellError(ushort version, ushort minimum)
    {
        var request = new FsshttpbCellRequest
        {
            ProtocolVersion = version,
            MinimumVersion = minimum,
            SubRequests = { new(RequestTypes.QueryAccess) { RequestId = 1 } },
        };
        var parsed = FsshttpbCellRequest.Deserialize(new(request.ToByteArray()));
        var document = new DocumentStore().Put("/version.docx", [1]);

        var response = CellBinaryRequestExecutor.Execute(document, document.FilePartition, parsed,
            CellBridge.Storage.Abstractions.DocumentAccess.Read);

        Assert.True(response.Status);
        Assert.Equal(ErrorType.Cell, response.Error?.Type);
        Assert.Equal((ulong)CellErrorCode.IncompatibleProtocolVersion, response.Error?.ErrorCode);
        Assert.Empty(response.SubResponses);
    }
}
