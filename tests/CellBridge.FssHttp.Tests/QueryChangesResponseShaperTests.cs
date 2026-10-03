using CellBridge.FssHttpB;
using CellBridge.Storage;
using CellBridge.Web;

namespace CellBridge.FssHttp.Tests;

public sealed class QueryChangesResponseShaperTests
{
    [Fact]
    public void FileContentsQueryReturnsTheStableManifestGraph()
    {
        var store = new DocumentStore();
        var document = store.Put("/test.docx", [1, 2, 3]);
        var request = QueryChanges(includeStorageManifest: true, includeCellChanges: true);

        var response = CellBinaryRequestExecutor.Execute(document, document.FilePartition, request, CellBridge.Storage.Abstractions.DocumentAccess.Read | CellBridge.Storage.Abstractions.DocumentAccess.Write);

        Assert.Equal(
            new[]
            {
                DataElementType.StorageManifestDataElementData,
                DataElementType.CellManifestDataElementData,
                DataElementType.RevisionManifestDataElementData,
                DataElementType.ObjectGroupDataElementData,
                DataElementType.StorageIndexDataElementData,
            },
            response.DataElementPackage!.DataElements.Select(element => element.DataElementType));
        Assert.Equal(new byte[] { 1, 2, 3 }, document.FilePartition.Content);
        Assert.False(Assert.IsType<QueryChangesSubResponseData>(
            Assert.Single(response.SubResponses).Data).PartialResult);

        var inspection = FsshttpbResponseInspector.Inspect(response.ToByteArray());
        Assert.Empty(inspection.Issues);
        Assert.DoesNotContain(
            inspection.DataElements,
            element => element.DataElementType == DataElementType.ObjectDataBLOBDataElementData);
        Assert.Contains(
            inspection.Relationships,
            relationship => relationship.Kind == "storage-index");
    }

    [Fact]
    public void QueryChangesHonorsManifestAndCellChangeFlags()
    {
        var store = new DocumentStore();
        var document = store.Put("/test.docx", [1]);
        var request = QueryChanges(includeStorageManifest: false, includeCellChanges: false);

        var response = CellBinaryRequestExecutor.Execute(document, document.FilePartition, request, CellBridge.Storage.Abstractions.DocumentAccess.Read | CellBridge.Storage.Abstractions.DocumentAccess.Write);
        var types = response.DataElementPackage!.DataElements.Select(element => element.DataElementType);

        Assert.DoesNotContain(DataElementType.StorageManifestDataElementData, types);
        Assert.DoesNotContain(DataElementType.CellManifestDataElementData, types);
        Assert.Contains(DataElementType.StorageIndexDataElementData, types);
    }

    [Fact]
    public void QueryChangesRejectsAnUnrepresentableByteBudget()
    {
        var store = new DocumentStore();
        var document = store.Put("/test.docx", [1]);
        var request = QueryChanges(includeStorageManifest: true, includeCellChanges: true);
        request.SubRequests[0].Data = new QueryChangesSubRequestData
        {
            IncludeStorageManifest = true,
            IncludeCellChanges = true,
            // One byte cannot contain even the first serialized data element.
            MaxDataElements = 1,
        };

        var response = CellBinaryRequestExecutor.Execute(document, document.FilePartition, request, CellBridge.Storage.Abstractions.DocumentAccess.Read | CellBridge.Storage.Abstractions.DocumentAccess.Write);
        var subResponse = Assert.Single(response.SubResponses);

        Assert.Null(response.DataElementPackage);
        Assert.True(subResponse.Status);
        Assert.Equal((ulong)ProtocolErrorCode.RequestNotSupported, subResponse.Error!.ErrorCode);
        Assert.Null(subResponse.Data);
    }

    private static FsshttpbCellRequest QueryChanges(bool includeStorageManifest, bool includeCellChanges)
    {
        var request = new FsshttpbCellRequest();
        request.SubRequests.Add(new FsshttpbCellSubRequest(RequestTypes.QueryChanges)
        {
            RequestId = 7,
            Data = new QueryChangesSubRequestData
            {
                IncludeStorageManifest = includeStorageManifest,
                IncludeCellChanges = includeCellChanges,
            },
        });
        return request;
    }
}
