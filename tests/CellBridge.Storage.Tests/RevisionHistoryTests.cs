using System.Text.Json;
using CellBridge.AspNetCore;
using CellBridge.FssHttpB;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage;
using CellBridge.Storage.InMemory;
using CellBridge.Storage.PostgreSql;
using CellBridge.Tests;
using CellBridge.TestFixtures;
using Npgsql;

namespace CellBridge.Storage.Tests;

[Collection("PostgreSQL")]
public sealed class RevisionHistoryTests
{
    [Fact]
    public async Task OrdinaryMetadataUploadReactivatesRetainedGraphWithoutRewritingPayloads()
    {
        var content = new WriteCountingContent(new InMemoryContentStore());
        var provider = StorageTestDocuments.Memory(content);
        var service = new CellBridgeDocumentService(provider);
        var initial = (await service.CreateAsync("/ordinary-reactivation.docx",MinimalDocx.Create(),TestActor.Value))!;
        var fixture = new GraphFixture([4,5,6],blob:true);
        var opaque = await Metadata(service,initial.ResourceId,InitialMetadata(fixture));
        var query = new FsshttpbCellRequest { SubRequests = { new(RequestTypes.QueryChanges) { RequestId=1,
            Data = new QueryChangesSubRequestData { IncludeStorageManifest=true,IncludeCellChanges=true } } } };
        var original = await service.ExecuteAsync(initial.ResourceId,DocumentPartitionKind.Metadata,query,new Dictionary<string,string>(),TestActor.Value);
        var indexId = Assert.IsType<QueryChangesSubResponseData>(original.Response.SubResponses[0].Data).StorageIndexExtendedGuid;
        var selected = original.Response.DataElementPackage!.DataElements.Single(element => element.DataElementExtendedGuid.Equals(indexId));
        await service.RestoreRevisionAsync(initial.ResourceId,1,opaque.State.LatestRevisionNumber,"inline-before-upload",TestActor.Value);
        var before = (await provider.Documents.ExportDocumentAsync(initial.ResourceId))!;
        var writes = content.Writes;
        var proposed = new ExGuid(1,Guid.NewGuid());
        var request = new FsshttpbCellRequest { DataElementPackage = new() { DataElements =
            [new(DataElementType.StorageIndexDataElementData,proposed,SerialNumber.Null) { Data=selected.Data!.ToArray() }] },
            SubRequests = { new(RequestTypes.PutChanges) { RequestId=1,Data=new PutChangesSubRequestData
                { StorageIndex=proposed,HasAdditionalFlags=true,AdditionalFlagsBits=1 } } } };
        var accepted = await Metadata(service,initial.ResourceId,request);
        Assert.Equal(writes+1,content.Writes); // Only the new acknowledgement is a physical object.
        var after = (await provider.Documents.ExportDocumentAsync(initial.ResourceId))!;
        Assert.Equal(before.Elements.Select(e => (e.Scope,e.Element.Id,e.Element.Payload)).OrderBy(e => e.Id.Guid).ThenBy(e => e.Id.Value),
            after.Elements.Select(e => (e.Scope,e.Element.Id,e.Element.Payload)).OrderBy(e => e.Id.Guid).ThenBy(e => e.Id.Value));
        var result = await service.ExecuteAsync(initial.ResourceId,DocumentPartitionKind.Metadata,query,new Dictionary<string,string>(),TestActor.Value);
        var graph = GenericPartitionGraphSnapshot.Create(result.Response.DataElementPackage!.DataElements,
            Assert.IsType<QueryChangesSubResponseData>(result.Response.SubResponses[0].Data).StorageIndexExtendedGuid);
        Assert.Equal(fixture.Bytes,Assert.Single(graph.GetObjectPartitions(fixture.Cell,fixture.Child)).Content);
        Assert.Equal(before.Current.Head.ContentVersion,accepted.State.ContentVersion);
    }

    [Fact]
    public async Task RestoredOpaqueMetadataReactivatesHistoricalPayloadsForQueryAndArchive()
    {
        var provider = StorageTestDocuments.Memory();
        var service = new CellBridgeDocumentService(provider);
        var initial = (await service.CreateAsync("/metadata-reactivation.docx",MinimalDocx.Create(),TestActor.Value))!;
        var fixture = new GraphFixture([9,8,7],blob:true);
        var opaque = await Metadata(service,initial.ResourceId,InitialMetadata(fixture));
        var inline = await service.RestoreRevisionAsync(initial.ResourceId,1,opaque.State.LatestRevisionNumber,"restore-inline",TestActor.Value);
        var restored = await service.RestoreRevisionAsync(initial.ResourceId,opaque.State.LatestRevisionNumber,inline.Revision.RevisionNumber,"restore-opaque",TestActor.Value);
        var query = new FsshttpbCellRequest { SubRequests = { new(RequestTypes.QueryChanges) { RequestId=1,
            Data = new QueryChangesSubRequestData { IncludeStorageManifest=true,IncludeCellChanges=true } } } };
        var result = await service.ExecuteAsync(initial.ResourceId,DocumentPartitionKind.Metadata,query,new Dictionary<string,string>(),TestActor.Value);
        Assert.False(Assert.Single(result.Response.SubResponses).Status);
        var index = Assert.IsType<QueryChangesSubResponseData>(result.Response.SubResponses[0].Data).StorageIndexExtendedGuid;
        var graph = GenericPartitionGraphSnapshot.Create(result.Response.DataElementPackage!.DataElements,index);
        Assert.Equal(fixture.Bytes,Assert.Single(graph.GetObjectPartitions(fixture.Cell,fixture.Child)).Content);
        var archivePath = Path.Combine(Path.GetTempPath(),"cellbridge-reactivated-metadata-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            var archive = new PortableArchive();
            var context = new RecoveryContext(RecoveryAuthorizationContext.StoredGrants("tests"),[]);
            await archive.ExportAsync(provider,archivePath,context);
            var destination = StorageTestDocuments.Memory();
            await archive.ImportAsync(destination,archivePath,context);
            var reopened = await new CellBridgeDocumentService(destination).ExecuteAsync(initial.ResourceId,DocumentPartitionKind.Metadata,
                query,new Dictionary<string,string>(),TestActor.Value);
            var reopenedGraph = GenericPartitionGraphSnapshot.Create(reopened.Response.DataElementPackage!.DataElements,
                Assert.IsType<QueryChangesSubResponseData>(reopened.Response.SubResponses[0].Data).StorageIndexExtendedGuid);
            Assert.Equal(fixture.Bytes,Assert.Single(reopenedGraph.GetObjectPartitions(fixture.Cell,fixture.Child)).Content);
            Assert.Equal(restored.Revision.Package.Sha256,(await destination.Documents.FindAsync(new DocumentIdentity.Resource(initial.ResourceId)))!.Package.Sha256);
        }
        finally { File.Delete(archivePath); }
    }

    [Fact]
    public async Task RestoreEmptyMetadataIndexRetainsOldGraphAndDoesNotFabricateManifest()
    {
        var provider = StorageTestDocuments.Memory();
        var service = new CellBridgeDocumentService(provider);
        var initial = (await service.CreateAsync("/empty-history.docx", MinimalDocx.Create(), TestActor.Value))!;
        var emptyId = new ExGuid(1, Guid.NewGuid());
        var empty = new DataElement(DataElementType.StorageIndexDataElementData, emptyId, new(Guid.NewGuid(), 1)) { Data = [] };
        var emptyRequest = new FsshttpbCellRequest { DataElementPackage = new() };
        emptyRequest.DataElementPackage.DataElements.Add(empty);
        emptyRequest.SubRequests.Add(new(RequestTypes.PutChanges) { RequestId = 1,
            Data = new PutChangesSubRequestData { StorageIndex = emptyId, HasAdditionalFlags = true, AdditionalFlagsBits = 1 } });
        var withEmpty = await Metadata(service, initial.ResourceId, emptyRequest);
        var fixture = new GraphFixture([1, 2, 3]);
        var populated = InitialMetadata(fixture);
        var withGraph = await Metadata(service, initial.ResourceId, populated);
        var before = (await provider.Documents.ReadPartitionAsync(initial.ResourceId, new(1) { IncludeCatalog = true }))!;
        var saved = await Save(service, withGraph.State, "file change");
        var preserved = (await provider.Documents.ReadPartitionAsync(initial.ResourceId, new(1) { IncludeCatalog = true }))!;
        Assert.Equal(JsonSerializer.Serialize(before.Partition), JsonSerializer.Serialize(preserved.Partition));
        await service.RestoreRevisionAsync(initial.ResourceId, withEmpty.State.LatestRevisionNumber,
            saved.LatestRevisionNumber, "empty-metadata", TestActor.Value);
        var after = (await provider.Documents.ReadPartitionAsync(initial.ResourceId, new(1) { IncludeCatalog = true }))!;
        var mappings = await provider.Documents.ReadMappingsAsync(new(initial.ResourceId, 1, 1), after.Partition.PublicationSequence);
        Assert.DoesNotContain(mappings, mapping => mapping.Key.Kind == IndexedMappingKind.Cell && mapping.Target != new ExtendedId(0, Guid.Empty));
        Assert.DoesNotContain(mappings, mapping => mapping.Key.Kind == IndexedMappingKind.Manifest && mapping.Target != new ExtendedId(0, Guid.Empty));
        Assert.NotEqual(before.Partition.StorageIndex, after.Partition.StorageIndex);
        var historical = (await provider.Documents.ExportDocumentAsync(initial.ResourceId))!;
        Assert.All(before.Elements, element => Assert.Contains(historical.Elements, value => value.Scope.TargetPartition == 1 && value.Element == element));
        Assert.Equal(withEmpty.State.Package.Sha256, after.Current.Package.Sha256);
        var archivePath = Path.Combine(Path.GetTempPath(),"cellbridge-empty-metadata-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            var archive = new PortableArchive();
            var context = new RecoveryContext(RecoveryAuthorizationContext.StoredGrants("tests"),[]);
            await archive.ExportAsync(provider,archivePath,context);
            var recovered = StorageTestDocuments.Memory();
            await archive.ImportAsync(recovered,archivePath,context);
            var recoveredHead = (await recovered.Documents.FindAsync(new DocumentIdentity.Resource(initial.ResourceId)))!;
            Assert.Equal(after.Current,recoveredHead);
            Assert.Equal(await StorageTestDocuments.PackageAsync(provider,after.Current.Package),
                await StorageTestDocuments.PackageAsync(recovered,recoveredHead.Package));
        }
        finally { File.Delete(archivePath); }
    }

    [Fact]
    public Task SaveRestoreRetryAndMetadataHistory() => CheckHistory(StorageTestDocuments.Memory());

    [PostgreSqlFact]
    public async Task DurableHistorySurvivesIndependentProviderRecreation()
    {
        await using var source = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("ConnectionStrings__cellbridge")!);
        await CheckHistory(new(new PostgreSqlStateStore(source), new PostgreSqlContentStore(source)),
            () => new(new PostgreSqlStateStore(source), new PostgreSqlContentStore(source)));
    }

    private static async Task CheckHistory(StorageProvider provider, Func<StorageProvider>? recreate = null)
    {
        var service = new CellBridgeDocumentService(provider);
        var original = (await service.CreateAsync("/shared/" + Guid.NewGuid().ToString("N") + ".docx", MinimalDocx.Create("original"), TestActor.Value))!;
        var first = (await provider.Documents.ReadRevisionAsync(original.ResourceId, 1))!.Revision!;
        var saved = await Save(service, original, "second");
        Assert.Equal(2, saved.Counters.Revisions);
        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize((await provider.Documents.ReadRevisionAsync(original.ResourceId, 1))!.Revision));
        var oldCatalog = (await provider.Documents.ReadPartitionAsync(original.ResourceId, new(0) { IncludeCatalog = true }))!;
        var restarted = new CellBridgeDocumentService(recreate?.Invoke() ?? provider);
        var restored = await restarted.RestoreRevisionAsync(original.ResourceId, first.RevisionNumber,
            saved.LatestRevisionNumber, "restore-first", TestActor.Value);
        Assert.False(restored.IsReplay);
        Assert.Equal(saved.ContentVersion + 1, restored.Revision.ContentVersion);
        Assert.Equal(first.Package.Sha256, restored.Revision.Package.Sha256);
        var after = (await provider.Documents.FindAsync(new DocumentIdentity.Resource(original.ResourceId)))!;
        Assert.Equal(3, after.Counters.Revisions);
        Assert.Equal(await StorageTestDocuments.PackageAsync(provider, first.Package), (await StorageTestDocuments.GraphAsync(service, after)).Materialize());
        Assert.NotEqual(saved.Package.Root.StorageIndex, after.Package.Root.StorageIndex);
        var history = (await provider.Documents.ExportDocumentAsync(original.ResourceId))!;
        Assert.All(oldCatalog.Elements, element => Assert.Contains(history.Elements, value => value.Scope.TargetPartition == 0 && value.Element == element));
        var later = await Save(service, after, "third");
        var replay = await restarted.RestoreRevisionAsync(original.ResourceId, first.RevisionNumber,
            saved.LatestRevisionNumber, "restore-first", TestActor.Value);
        Assert.True(replay.IsReplay);
        Assert.Equal(JsonSerializer.Serialize(restored.Revision), JsonSerializer.Serialize(replay.Revision));
        Assert.Equal(later.ContentVersion, (await provider.Documents.FindAsync(new DocumentIdentity.Resource(original.ResourceId)))!.ContentVersion);
        await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.RestoreRevisionAsync(original.ResourceId,
            first.RevisionNumber, later.LatestRevisionNumber, "restore-first", TestActor.Value).AsTask());
        Assert.Equal(4, (await restarted.ListRevisionsAsync(original.ResourceId, TestActor.Value)).Count);
        var metadata = await Metadata(service, original.ResourceId, InitialMetadata(new GraphFixture([1, 2, 3])));
        Assert.Equal(later.ContentVersion, metadata.State.ContentVersion);
        Assert.Equal(later.LatestRevisionNumber + 1, metadata.State.LatestRevisionNumber);
    }

    [Fact]
    public async Task RevocationAndReaderCeilingsApplyToHistoryAndRetries()
    {
        var provider = StorageTestDocuments.Memory(); var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/denied.docx", MinimalDocx.Create(), TestActor.Value))!;
        var reader = TestActor.Value with { AccessLimit = new(state.ResourceId, DocumentAccess.Read) };
        Assert.Single(await service.ListRevisionsAsync(state.ResourceId, reader));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.RestoreRevisionAsync(state.ResourceId, 1, 1, "key", reader).AsTask());
        await service.RestoreRevisionAsync(state.ResourceId, 1, 1, "key", TestActor.Value);
        var head = (await provider.Documents.FindAsync(new DocumentIdentity.Resource(state.ResourceId)))!;
        var revoked = await provider.Documents.ExecuteControlAsync(new DocumentControlCommand.SetPermissions(head.ResourceId,
            StorageTestDocuments.Admission(head), head.Security with { Owner = "different-owner" }, head.AuthorityGeneration));
        Assert.True(revoked.Changed);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetRevisionAsync(state.ResourceId, 1, TestActor.Value).AsTask());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.RestoreRevisionAsync(state.ResourceId, 1, 1, "key", TestActor.Value).AsTask());
    }

    [Fact]
    public async Task ConcurrentRestorePublishesOnceAndStaleSaveCannotOverwriteIt()
    {
        var provider = StorageTestDocuments.Memory(); var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/concurrent.docx", MinimalDocx.Create(), TestActor.Value))!;
        var staleSave = await StorageTestDocuments.SaveRequestAsync(service, state, MinimalDocx.Create("stale"));
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => service.RestoreRevisionAsync(state.ResourceId,
            1, 1, "same-key", TestActor.Value).AsTask()));
        Assert.Single(results, r => !r.IsReplay);
        Assert.All(results, r => Assert.Equal(2UL, r.Revision.RevisionNumber));
        var rejected = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents, staleSave,
            new Dictionary<string, string>(), TestActor.Value);
        Assert.True(Assert.Single(rejected.Response.SubResponses).Status);
        Assert.Equal(2, rejected.State.Counters.Revisions);
    }

    [Fact]
    public async Task HistoryLimitRejectsWithoutChangingPublishedState()
    {
        var provider = StorageTestDocuments.Memory(limits: new() { MaxHistoryRevisions = 1 });
        var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/quota.docx", MinimalDocx.Create(), TestActor.Value))!;
        await Assert.ThrowsAsync<StorageQuotaExceededException>(() => service.RestoreRevisionAsync(state.ResourceId, 1, 1, "key", TestActor.Value).AsTask());
        Assert.Equal(state, await provider.Documents.FindAsync(new DocumentIdentity.Resource(state.ResourceId)));
    }

    [Fact]
    public async Task OfflineFormatTwoCapturePreservesHistoryAndLegacySeedsOnlyKnownCurrent()
    {
        var content = new InMemoryContentStore();
        var state = await LegacyStorageFixture.CreateAsync(content, "/codec.docx", MinimalDocx.Create());
        state = (await LegacyStorageFixture.SaveAsync(state, content, MinimalDocx.Create("saved"))).State;
        var decoded = JsonSerializer.Deserialize<DocumentState>(JsonSerializer.Serialize(state))!;
        var captured = await (await StoredDocument.RestoreAsync(decoded, content)).CaptureAsync(content);
        Assert.Equal(JsonSerializer.Serialize(decoded.Revisions), JsonSerializer.Serialize(captured.Revisions));
        Assert.Equal(decoded.RestoreReceipts, captured.RestoreReceipts);
        var legacyJson = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(state))!.AsObject();
        legacyJson.Remove("Revisions"); legacyJson.Remove("RestoreReceipts"); legacyJson.Remove("LifecycleGeneration");
        var legacy = JsonSerializer.Deserialize<DocumentState>(legacyJson.ToJsonString())!;
        Assert.Empty(legacy.Revisions);
        Assert.Equal(state.ContentVersion, Assert.Single(RevisionHistory.Initialize(legacy).Revisions).ContentVersion);
        Assert.Equal(1, legacy.LifecycleGeneration);
    }

    internal static async Task<DocumentHead> Save(CellBridgeDocumentService service, DocumentHead state, string text)
    {
        var result = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents,
            await StorageTestDocuments.SaveRequestAsync(service, state, MinimalDocx.Create(text)), new Dictionary<string, string>(), TestActor.Value);
        Assert.False(Assert.Single(result.Response.SubResponses).Status);
        return result.State;
    }

    private static FsshttpbCellRequest InitialMetadata(GraphFixture fixture)
    {
        var request = new FsshttpbCellRequest { DataElementPackage = new() };
        request.DataElementPackage.DataElements.AddRange(fixture.Elements);
        request.SubRequests.Add(new(RequestTypes.PutChanges) { RequestId = 1,
            Data = new PutChangesSubRequestData { StorageIndex = fixture.Index, HasAdditionalFlags = true, AdditionalFlagsBits = 1 } });
        return request;
    }

    private static async Task<CellExecution> Metadata(CellBridgeDocumentService service, Guid id, FsshttpbCellRequest request)
    {
        var result = await service.ExecuteAsync(id, DocumentPartitionKind.Metadata, request, new Dictionary<string, string>(), TestActor.Value);
        Assert.False(Assert.Single(result.Response.SubResponses).Status, JsonSerializer.Serialize(result.Response.SubResponses[0].Error));
        return result;
    }
    private sealed class WriteCountingContent(IContentStore inner) : IContentStore
    {
        public int Writes { get; private set; }
        public bool Durable => inner.Durable;
        public bool Shared => inner.Shared;
        public ValueTask<ContentHandle> WriteAsync(Stream source,CancellationToken cancellationToken=default)
        { Writes++; return inner.WriteAsync(source,cancellationToken); }
        public ValueTask<Stream> OpenReadAsync(ContentHandle handle,CancellationToken cancellationToken=default) => inner.OpenReadAsync(handle,cancellationToken);
    }

}
