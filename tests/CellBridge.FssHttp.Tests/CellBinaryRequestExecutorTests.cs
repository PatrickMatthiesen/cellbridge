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
        request.SubRequests.Add(new FsshttpbCellSubRequest(RequestTypes.QueryChanges) { RequestId = 73 });

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
        request.SubRequests.Add(new FsshttpbCellSubRequest(RequestTypes.PutChanges) { RequestId = 982 });

        var response = CellBinaryRequestExecutor.Execute(document, document.FilePartition, request, CellBridge.Storage.Abstractions.DocumentAccess.Read | CellBridge.Storage.Abstractions.DocumentAccess.Write);
        var result = Assert.Single(response.SubResponses);

        Assert.Equal(982UL, result.RequestId);
        Assert.Equal(RequestTypes.PutChanges, result.RequestType);
        Assert.True(result.Status);
        Assert.Equal(ErrorType.Cell, result.Error?.Type);
        Assert.Equal((ulong)ProtocolErrorCode.RequestNotSupported, result.Error?.ErrorCode);
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
        Assert.Equal((ulong)ProtocolErrorCode.RequestNotSupported, wireResult.Error?.ErrorCode);
    }

    [Fact]
    public void EmptyRequestFailsAndRepeatedQueryChangesIsExplicitlyRejected()
    {
        var store = new DocumentStore();
        var document = store.Put("/test.docx", [1]);

        var emptyResponse = CellBinaryRequestExecutor.Execute(
            document, document.FilePartition, new FsshttpbCellRequest(), CellBridge.Storage.Abstractions.DocumentAccess.Read | CellBridge.Storage.Abstractions.DocumentAccess.Write);
        Assert.True(emptyResponse.Status);
        Assert.Equal((ulong)ProtocolErrorCode.RequestStreamSchemaError, emptyResponse.Error?.ErrorCode);

        var request = new FsshttpbCellRequest();
        request.SubRequests.Add(new FsshttpbCellSubRequest(RequestTypes.QueryChanges) { RequestId = 12 });
        request.SubRequests.Add(new FsshttpbCellSubRequest(RequestTypes.QueryChanges) { RequestId = 34 });
        var response = CellBinaryRequestExecutor.Execute(document, document.FilePartition, request, CellBridge.Storage.Abstractions.DocumentAccess.Read | CellBridge.Storage.Abstractions.DocumentAccess.Write);

        Assert.Equal(new ulong[] { 12, 34 }, response.SubResponses.Select(x => x.RequestId));
        Assert.False(response.SubResponses[0].Status);
        Assert.True(response.SubResponses[1].Status);
        Assert.Equal((ulong)ProtocolErrorCode.RequestNotSupported, response.SubResponses[1].Error?.ErrorCode);
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
        Assert.Equal((ulong)ProtocolErrorCode.RequestNotSupported, result.Error?.ErrorCode);
    }
}
