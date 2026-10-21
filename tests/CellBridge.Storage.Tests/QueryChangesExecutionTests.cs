using CellBridge.AspNetCore;
using CellBridge.FssHttpB;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.TestFixtures;

namespace CellBridge.Storage.Tests;

[Collection("PostgreSQL")]
public sealed class QueryChangesExecutionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FileContentsQueryReturnsTheStableManifestGraph(bool postgreSql)
    {
        await using var scope = ProtocolExecutionTestProvider.Create(postgreSql);
        var provider = scope.Provider;
        var service = new CellBridgeDocumentService(provider);
        var document = (await service.CreateAsync($"/query-{Guid.NewGuid():N}.docx", [1, 2, 3], TestActor.Value))!;
        var request = QueryChanges(includeStorageManifest: true, includeCellChanges: true);

        var response = (await service.ExecuteAsync(document.ResourceId, DocumentPartitionKind.FileContents, request,
            new Dictionary<string, string>(), TestActor.Value)).Response;

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
        var persisted = (await provider.State.FindByResourceIdAsync(document.ResourceId))!;
        Assert.Equal(new byte[] { 1, 2, 3 }, await provider.Content.ReadVerifiedAsync(
            persisted.Partitions.Single(p => p.Kind == (int)DocumentPartitionKind.FileContents).Content));
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryChangesHonorsManifestAndCellChangeFlags(bool postgreSql)
    {
        await using var scope = ProtocolExecutionTestProvider.Create(postgreSql);
        var provider = scope.Provider;
        var service = new CellBridgeDocumentService(provider);
        var document = (await service.CreateAsync($"/query-{Guid.NewGuid():N}.docx", [1], TestActor.Value))!;
        var request = QueryChanges(includeStorageManifest: false, includeCellChanges: false);

        var response = (await service.ExecuteAsync(document.ResourceId, DocumentPartitionKind.FileContents, request,
            new Dictionary<string, string>(), TestActor.Value)).Response;
        var types = response.DataElementPackage!.DataElements.Select(element => element.DataElementType);

        Assert.DoesNotContain(DataElementType.StorageManifestDataElementData, types);
        Assert.DoesNotContain(DataElementType.CellManifestDataElementData, types);
        Assert.Contains(DataElementType.StorageIndexDataElementData, types);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryChangesRejectsAnUnrepresentableByteBudget(bool postgreSql)
    {
        await using var scope = ProtocolExecutionTestProvider.Create(postgreSql);
        var provider = scope.Provider;
        var service = new CellBridgeDocumentService(provider);
        var document = (await service.CreateAsync($"/query-{Guid.NewGuid():N}.docx", [1], TestActor.Value))!;
        var request = QueryChanges(includeStorageManifest: true, includeCellChanges: true);
        request.SubRequests[0].Data = new QueryChangesSubRequestData
        {
            IncludeStorageManifest = true,
            IncludeCellChanges = true,
            // One byte cannot contain even the first serialized data element.
            MaxDataElements = 1,
        };

        var response = (await service.ExecuteAsync(document.ResourceId, DocumentPartitionKind.FileContents, request,
            new Dictionary<string, string>(), TestActor.Value)).Response;
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
