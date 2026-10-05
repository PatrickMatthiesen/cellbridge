using CellBridge.AspNetCore;
using CellBridge.FssHttpB;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using CellBridge.Storage.PostgreSql;
using CellBridge.Web;
using Npgsql;

namespace CellBridge.Storage.Tests;

public sealed class AppliedStorageIndexTests
{
    [Fact]
    public Task AppliedIndexPackageSurvivesServiceRecreation() => CheckReplay(new(new InMemoryStateStore(), new InMemoryContentStore()));

    [PostgreSqlFact]
    public async Task AppliedIndexPackageSurvivesPostgreSqlProviderRecreation()
    {
        await using var source = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("ConnectionStrings__cellbridge")!);
        await CheckReplay(new(new PostgreSqlStateStore(source), new PostgreSqlContentStore(source)),
            () => new(new PostgreSqlStateStore(source), new PostgreSqlContentStore(source)));
    }

    private static async Task CheckReplay(StorageProvider provider, Func<StorageProvider>? recreate = null)
    {
        var service = new CellBridgeDocumentService(provider);
        var initial = (await service.CreateAsync("/applied-" + Guid.NewGuid().ToString("N") + ".docx", MinimalDocx.Create(), TestActor.Value))!;
        var request = Save(initial);
        var saved = await service.ExecuteAsync(initial.ResourceId, DocumentPartitionKind.FileContents, request, new Dictionary<string, string>(), TestActor.Value);
        var index = CheckIndex(saved.Response);
        var stored = saved.State.Partitions[0].Elements.Single(e => StorageIds.Restore(e.Id).Equals(index.DataElementExtendedGuid));
        Assert.Equal(new SerialNumber(stored.Serial.Guid, stored.Serial.Value), index.SerialNumber);
        Assert.Equal(await provider.Content.ReadVerifiedAsync(stored.Payload), index.Data);
        var retry = await new CellBridgeDocumentService(recreate?.Invoke() ?? provider).ExecuteAsync(initial.ResourceId,
            DocumentPartitionKind.FileContents, request, new Dictionary<string, string>(), TestActor.Value);
        Assert.Equal(saved.State.StateVersion, retry.State.StateVersion);
        Assert.Equal(saved.Response.ToByteArray(), retry.Response.ToByteArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MixedQueriesAndSaveShareAppliedIndexWithoutDuplicates(bool buffered)
    {
        var provider = new StorageProvider(new InMemoryStateStore(), new InMemoryContentStore());
        var service = new CellBridgeDocumentService(provider);
        var initial = (await service.CreateAsync("/mixed-applied.docx", MinimalDocx.Create(), TestActor.Value))!;
        var request = Save(initial);
        request.SubRequests.Insert(0, Query(10));
        request.SubRequests.Add(Query(20));
        var document = await StoredDocument.RestoreAsync(initial, provider.Content);
        FsshttpbResponse response = buffered
            ? CellBinaryRequestExecutor.Execute(document, document.FilePartition, request, DocumentAccess.Read | DocumentAccess.Write)
            : (await service.ExecuteAsync(initial.ResourceId, DocumentPartitionKind.FileContents, request,
                new Dictionary<string, string>(), TestActor.Value)).Response;
        Assert.All(response.SubResponses, s => Assert.False(s.Status, s.Error?.ErrorMessage));
        var decoded = FsshttpbResponse.Deserialize(new(response.ToByteArray()));
        var index = Assert.IsType<PutChangesSubResponseData>(decoded.SubResponses[1].Data).PutChangesResponse!.AppliedStorageIndexID;
        Assert.Equal(index, Assert.IsType<QueryChangesSubResponseData>(decoded.SubResponses[2].Data).StorageIndexExtendedGuid);
        Assert.Single(decoded.DataElementPackage!.DataElements, e => e.DataElementExtendedGuid.Equals(index));
        Assert.Equal(decoded.DataElementPackage.DataElements.Count,
            decoded.DataElementPackage.DataElements.Select(e => e.DataElementExtendedGuid).Distinct().Count());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    public async Task MandatoryIndexPreflightHasExactBoundaryAndCannotPublishOnFailure(int offset)
    {
        var provider = new StorageProvider(new InMemoryStateStore(), new InMemoryContentStore());
        var initial = (await new CellBridgeDocumentService(provider).CreateAsync("/bounded-applied.docx", MinimalDocx.Create(), TestActor.Value))!;
        var request = Save(initial);
        var previewDocument = await StoredDocument.RestoreAsync(initial, provider.Content);
        var preview = CellBinaryRequestExecutor.Execute(previewDocument, previewDocument.FilePartition, request, DocumentAccess.Read | DocumentAccess.Write);
        var writer = new BinaryWriterEx();
        CheckIndex(preview).Serialize(writer);
        var document = await StoredDocument.RestoreAsync(initial, provider.Content);
        var response = CellBinaryRequestExecutor.Execute(document, document.FilePartition, request,
            DocumentAccess.Read | DocumentAccess.Write, maxResponseBytes: writer.Length + offset);
        Assert.Equal(offset < 0, Assert.Single(response.SubResponses).Status);
        Assert.Equal(initial.ContentVersion + (offset < 0 ? 0u : 1u), document.ContentVersion);
        if (offset < 0) Assert.Null(response.DataElementPackage);
        else CheckIndex(response);
    }

    private static DataElement CheckIndex(FsshttpbResponse response)
    {
        var decoded = FsshttpbResponse.Deserialize(new(response.ToByteArray()));
        var sub = Assert.Single(decoded.SubResponses);
        Assert.False(sub.Status, sub.Error?.ErrorMessage);
        var id = Assert.IsType<PutChangesSubResponseData>(sub.Data).PutChangesResponse!.AppliedStorageIndexID;
        Assert.False(id.IsNull);
        var index = Assert.Single(decoded.DataElementPackage!.DataElements);
        Assert.Equal(id, index.DataElementExtendedGuid);
        Assert.Equal(DataElementType.StorageIndexDataElementData, index.DataElementType);
        Assert.NotEmpty(StorageIndexMappingSerials.ReadMappings(index.Data!));
        return index;
    }

    private static FsshttpbCellRequest Save(DocumentState state)
    {
        var request = StorageTests.Fixture("save-first");
        var put = Assert.IsType<PutChangesSubRequestData>(request.SubRequests[0].Data);
        put.ExpectedStorageIndex = StorageIds.Restore(state.Partitions[0].StorageIndex!);
        put.AdditionalFlagsBits |= 1;
        return request;
    }

    private static FsshttpbCellSubRequest Query(ulong id) => new(RequestTypes.QueryChanges)
    { RequestId = id, Data = new QueryChangesSubRequestData { IncludeStorageManifest = true, IncludeCellChanges = true } };
}
