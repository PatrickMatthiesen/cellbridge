using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using CellBridge.AspNetCore;
using CellBridge.FssHttp;
using CellBridge.FssHttpB;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using CellBridge.Storage.PostgreSql;
using CellBridge.Tests;
using CellBridge.TestFixtures;
using Npgsql;

namespace CellBridge.Storage.Tests;

[Collection("PostgreSQL")]
public sealed class MetadataPublicationTests
{
    private static readonly Dictionary<string, string> Attributes = new();

    [Fact]
    public Task CompleteMetadataGraphReadsAndRetriesAfterRecreation() => CheckRecreation(StorageTestDocuments.Memory());

    [PostgreSqlFact]
    public async Task PostgreSqlMetadataGraphReadsAndRetriesAfterProviderRecreation()
    {
        await using var data = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("ConnectionStrings__cellbridge")!);
        await CheckRecreation(new(new PostgreSqlStateStore(data), new PostgreSqlContentStore(data)),
            () => new(new PostgreSqlStateStore(data), new PostgreSqlContentStore(data)));
    }

    private static async Task CheckRecreation(StorageProvider provider, Func<StorageProvider>? recreate = null)
    {
        var service = new CellBridgeDocumentService(provider);
        var before = await Create(service);
        var f = new GraphFixture("opaque application bytes"u8.ToArray(), blob: true);
        var request = Initial(f);
        request.DataElementPackage!.DataElements.Add(f.Elements[0]);
        request = FsshttpbCellRequest.Deserialize(new(request.ToByteArray()));
        var saved = await service.ExecuteAsync(before.ResourceId, DocumentPartitionKind.Metadata, request, Attributes, TestActor.Value);
        Success(saved);
        Assert.Empty(saved.AcceptedSaves);
        Assert.Equal(before.ContentVersion, saved.State.ContentVersion);
        Assert.Equal(before.Package.Sha256, saved.State.Package.Sha256);
        Assert.Equal(JsonSerializer.Serialize((await PartitionAsync(before, provider, 2))), JsonSerializer.Serialize((await PartitionAsync(saved.State, provider, 2))));
        var reopenedProvider = recreate?.Invoke() ?? provider;
        var reopened = new CellBridgeDocumentService(reopenedProvider);
        var replay = await reopened.ExecuteAsync(before.ResourceId, DocumentPartitionKind.Metadata, request, Attributes, TestActor.Value);
        Assert.Equal(saved.Response.ToByteArray(), replay.Response.ToByteArray());
        Assert.Equal(saved.State.StateVersion, replay.State.StateVersion);
        var query = Query(); query.SubRequests.Add(Query().SubRequests[0]); query.SubRequests[1].RequestId = 2;
        var read = await reopened.ExecuteAsync(before.ResourceId, DocumentPartitionKind.Metadata, query, Attributes, TestActor.Value);
        Assert.All(read.Response.SubResponses, s => Assert.False(s.Status));
        var graph = GenericPartitionGraphSnapshot.Create(read.Response.DataElementPackage!.DataElements,
            Assert.IsType<QueryChangesSubResponseData>(read.Response.SubResponses[0].Data).StorageIndexExtendedGuid);
        Assert.Equal(f.Bytes, Assert.Single(graph.GetObjectPartitions(f.Cell, f.Child)).Content);
        Assert.Contains(f.Blob, graph.RequiredElements);
        var knowledge = ClientKnowledge.Deserialize(new(Assert.IsType<QueryChangesSubResponseData>(read.Response.SubResponses[0].Data).KnowledgeBytes!));
        var known = Query(); Assert.IsType<QueryChangesSubRequestData>(known.SubRequests[0].Data).Knowledge = knowledge;
        var unchanged = await reopened.ExecuteAsync(before.ResourceId, DocumentPartitionKind.Metadata, known, Attributes, TestActor.Value);
        Assert.Empty(unchanged.Response.DataElementPackage!.DataElements);
        var scoped = Query(); Assert.IsType<QueryChangesSubRequestData>(scoped.SubRequests[0].Data).CellId = f.OtherCell;
        var scopedRead = await reopened.ExecuteAsync(before.ResourceId, DocumentPartitionKind.Metadata, scoped, Attributes, TestActor.Value);
        Assert.Contains(scopedRead.Response.DataElementPackage!.DataElements, e => e.DataElementExtendedGuid.Equals(f.OtherGroup));
        Assert.DoesNotContain(scopedRead.Response.DataElementPackage.DataElements, e => e.DataElementExtendedGuid.Equals(f.Blob));
        var persisted = (await reopenedProvider.Documents.ExportDocumentAsync(saved.State.ResourceId))!;
        var detached = JsonSerializer.Deserialize<NormalizedDocumentSnapshot>(JsonSerializer.Serialize(persisted))!;
        NormalizedDocumentRecovery.Validate(detached, reopenedProvider.Limits);
        Assert.Equal(JsonSerializer.Serialize(persisted.Elements), JsonSerializer.Serialize(detached.Elements));

    }

    [Fact]
    public async Task MappingPatchPreservesUntouchedCellsAndReturnsTheSelectedLogicalIndex()
    {
        var provider = StorageTestDocuments.Memory();
        var service = new CellBridgeDocumentService(provider); var before = await Create(service);
        var f = new GraphFixture([1, 2, 3], blob: true);
        var first = await Save(service, before, Initial(f)); Success(first);
        var expected = await Index(first.State, provider);
        var patch = f.CreatePatch(expected, false);
        var saved = await Save(service, first.State, patch); Success(saved);
        var applied = Assert.IsType<PutChangesSubResponseData>(saved.Response.SubResponses[0].Data).PutChangesResponse!.AppliedStorageIndexID;
        var selected = StorageIds.Restore((await PartitionAsync(saved.State, provider, 1)).StorageIndex!);
        Assert.Equal(applied, selected);
        Assert.Equal(2, StorageIndexPatch.Read(Assert.Single(saved.Response.DataElementPackage!.DataElements)).Count);
        var graph = await GraphAsync(saved.State, provider);
        Assert.Equal(f.OtherRevision, graph.GetCurrentRevision(f.OtherCell));
        Assert.NotEqual(f.Revision, graph.GetCurrentRevision(f.Cell));
        Assert.Contains(f.Blob, graph.RequiredElements);
        var staleSameKey = await Save(service, saved.State, f.CreatePatch(expected, false));
        Assert.Equal((ulong)CellErrorCode.CoherencyFailure, Assert.Single(staleSameKey.Response.SubResponses).Error!.ErrorCode);
        var unaffected = await Save(service, saved.State, f.CreatePatch(expected, true)); Success(unaffected);
        var merged = await GraphAsync(unaffected.State, provider);
        Assert.Equal(graph.GetCurrentRevision(f.Cell), merged.GetCurrentRevision(f.Cell));
        Assert.NotEqual(f.OtherRevision, merged.GetCurrentRevision(f.OtherCell));
    }

    [Fact]
    public async Task TwoServicesMergeCompetingDistinctMappingPatchesWithoutLostUpdates()
    {
        var inner = new InMemoryContentStore();
        var content = new HookContent(inner);
        var provider = StorageTestDocuments.Memory(content);
        var firstService = new CellBridgeDocumentService(provider); var before = await Create(firstService);
        var f = new GraphFixture([1], blob: true);
        var initial = await Save(firstService, before, Initial(f)); Success(initial);
        var expected = await Index(initial.State, provider);
        CellExecution? competitor = null;
        content.Hook = async () => { competitor = await Save(new(provider), initial.State, f.CreatePatch(expected, true)); Success(competitor); };
        var saved = await Save(firstService, initial.State, f.CreatePatch(expected, false)); Success(saved);
        Assert.NotNull(competitor);
        var graph = await GraphAsync(saved.State, provider);
        Assert.NotEqual(f.Revision, graph.GetCurrentRevision(f.Cell));
        Assert.NotEqual(f.OtherRevision, graph.GetCurrentRevision(f.OtherCell));
        Assert.Equal(3, (await provider.Documents.ExportDocumentAsync(saved.State.ResourceId))!.Receipts.Count(r => r.Operation.PartitionKind == 1));
    }

    [Theory]
    [InlineData("permission")]
    [InlineData("lease")]
    public async Task AuthorityChangesDuringPreparationCannotPublish(string change)
    {
        var content = new HookContent(new InMemoryContentStore());
        var provider = StorageTestDocuments.Memory(content);
        var service = new CellBridgeDocumentService(provider); var before = await Create(service);
        content.Hook = async () =>
        {
            if (change == "permission") await RevokeAsync(provider, before.ResourceId);
            else Assert.True((await new SharedDocumentLocks(service).ApplyAsync(before.ResourceId,
                HostLockOperation.Acquire, "new-authority", TestActor.Value)).Success);
        };
        var saved = await Save(service, before, Initial(new GraphFixture([1], blob: true)));
        Assert.True(saved.LockError is not null || Assert.Single(saved.Response.SubResponses).Status);
        Assert.Equal((await PartitionAsync(before, provider, 1)), (await PartitionAsync(saved.State, provider, 1)));
        Assert.Equal(0, saved.State.Counters.SaveReceipts);
        Assert.Equal(before.Package.Sha256, saved.State.Package.Sha256);
    }

    [Fact]
    public async Task DeletedAndRecreatedResourceCannotReceivePreparedMetadata()
    {
        var content = new HookContent(new InMemoryContentStore());
        var provider = StorageTestDocuments.Memory(content);
        var service = new CellBridgeDocumentService(provider); var before = await Create(service);
        var lifecycle = provider.Documents;
        PreparedDocumentCreate? replacement = null;
        var originalMetadata = await PartitionAsync(before,provider,1);
        content.Hook = async () =>
        {
            Assert.True((await lifecycle.ChangeLifecycleAsync(new DocumentLifecycleCommand.Delete(before.ResourceId,
                before.LifecycleGeneration, before.StateVersion))).Applied);
            var tombstone = (await lifecycle.FindAsync(new DocumentIdentity.Resource(before.ResourceId, true)))!;
            replacement = await IndexedDocumentPublicationBuilder.CreateAsync(Guid.NewGuid(), before.Path,
                MinimalDocx.Create("replacement"), before.Security, provider.Content, provider.Limits,
                lifecycleGeneration: before.LifecycleGeneration + 1);
            Assert.True((await lifecycle.ChangeLifecycleAsync(new DocumentLifecycleCommand.Recreate(before.ResourceId,
                before.LifecycleGeneration, tombstone.StateVersion, replacement))).Applied);
        };
        var rejected = await Save(service, before, Initial(new GraphFixture([1], blob: true)));
        Assert.Empty(rejected.AcceptedSaves);
        Assert.True(rejected.LockError is not null || Assert.Single(rejected.Response.SubResponses).Status);
        var retired = (await lifecycle.FindAsync(new DocumentIdentity.Resource(before.ResourceId, true)))!;
        Assert.Equal(originalMetadata,(await lifecycle.ExportDocumentAsync(retired.ResourceId))!.Current.Partitions.Single(p => p.Kind == 1)); Assert.Equal(0, retired.Counters.SaveReceipts);
        var current = (await provider.Documents.FindAsync(new DocumentIdentity.Resource(replacement!.Head.ResourceId)))!;
        Assert.Null((await PartitionAsync(current, provider, 1)).StorageIndex); Assert.Equal(0, current.Counters.SaveReceipts);
        Assert.Equal(replacement.Head.Package, current.Package);
    }

    [Fact]
    public async Task HostLockRejectsMetadataBeforeStaging()
    {
        var content = new HookContent(new InMemoryContentStore());
        var provider = StorageTestDocuments.Memory(content);
        var service = new CellBridgeDocumentService(provider); var before = await Create(service);
        Assert.True((await new SharedDocumentLocks(service).ApplyAsync(before.ResourceId,
            HostLockOperation.Acquire, "host-token", TestActor.Value)).Success);
        var staged = false; content.Hook = () => { staged = true; return Task.CompletedTask; };
        var result = await Save(service, before, Initial(new GraphFixture([1], blob: true)));
        Assert.Equal("FileAlreadyLockedOnServer", result.LockError); Assert.False(staged);
        Assert.Equal("host-token", (await provider.Documents.ReadControlAsync(before.ResourceId))!.Coordination.HostLock!.Token);
        Assert.Equal(0, result.State.Counters.SaveReceipts); Assert.Equal((await PartitionAsync(before, provider, 1)), (await PartitionAsync(result.State, provider, 1)));
    }

    [Fact]
    public Task MetadataAndAppliedFileSavesPreservePublicationAndHistory() =>
        CheckPublicationIntegration(StorageTestDocuments.Memory(limits: new StorageLimits { MaxPendingExternalRevisions = 1 }));

    [PostgreSqlFact]
    public async Task PostgreSqlMetadataAndAppliedFileSavesPreservePublicationAndHistory()
    {
        await using var source = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("ConnectionStrings__cellbridge")!);
        var limits = new StorageLimits { MaxPendingExternalRevisions = 1 };
        await CheckPublicationIntegration(new(new PostgreSqlStateStore(source, limits), new PostgreSqlContentStore(source), limits));
    }

    private static async Task CheckPublicationIntegration(StorageProvider provider)
    {
        var service = new CellBridgeDocumentService(provider); var before = await Create(service);
        Assert.True((await provider.Documents.UpdateDeliveryAsync(new DocumentDeliveryCommand.Bind(before.ResourceId,
            before.LifecycleGeneration, before.StateVersion, new(Guid.NewGuid(), "unchanged-destination", "remote-r0", 1)))).Applied);
        var metadata = await Save(service, before, Initial(new GraphFixture([1], blob: true))); Success(metadata);
        Assert.Equal(0, metadata.State.Counters.PendingDeliveries); Assert.Empty(metadata.AcceptedSaves);
        var request = await Upload(service, metadata.State, file: true);
        var put = Assert.IsType<PutChangesSubRequestData>(request.SubRequests[0].Data);
        put.HasAdditionalFlags = true; put.AdditionalFlagsBits |= 1;
        var file = await service.ExecuteAsync(before.ResourceId, DocumentPartitionKind.FileContents, request, Attributes, TestActor.Value); Success(file);
        var queued = (await provider.Documents.ReadNextDeliveryAsync(before.ResourceId))!.Revision;
        Assert.Equal(file.State.Package, queued.Package); Assert.Equal(file.State.ContentVersion, queued.ContentVersion);
        Assert.Equal(file.State.LifecycleGeneration, queued.LifecycleGeneration);
        Assert.Equal(JsonSerializer.Serialize((await PartitionAsync(metadata.State, provider, 1))), JsonSerializer.Serialize((await PartitionAsync(file.State, provider, 1))));
        Assert.Equal(file.State.ContentVersion, (await provider.Documents.ReadRevisionAsync(before.ResourceId, file.State.LatestRevisionNumber))!.Revision!.ContentVersion);
        Assert.Equal(queued.Package.Length, (await StorageTestDocuments.PackageAsync(provider, queued.Package)).LongLength);
        var delivery = (await provider.Documents.ReadNextDeliveryAsync(before.ResourceId))!;
        Assert.True((await provider.Documents.UpdateDeliveryAsync(new DocumentDeliveryCommand.Block(before.ResourceId, delivery, "conflict"))).Applied);
        var full = (await provider.Documents.FindAsync(new DocumentIdentity.Resource(before.ResourceId)))!;
        var patched = await Save(service, full, new GraphFixture([1], blob: true).CreatePatch(await Index(full, provider), false)); Success(patched);
        Assert.Equal(JsonSerializer.Serialize(full.Delivery), JsonSerializer.Serialize(patched.State.Delivery));
        Assert.Equal(full.ContentVersion, patched.State.ContentVersion);
        Assert.Equal(full.Counters.Revisions + 1, patched.State.Counters.Revisions);
        // A replay acknowledges the committed revision even after a lease epoch changes.
        Assert.True((await new SharedDocumentLocks(service).ApplyAsync(before.ResourceId,
            HostLockOperation.Acquire, "new-epoch", TestActor.Value)).Success);
        var replay = await service.ExecuteAsync(before.ResourceId, DocumentPartitionKind.FileContents, request, Attributes, TestActor.Value); Success(replay);
        Assert.Equal(file.Response.ToByteArray(), replay.Response.ToByteArray());
        Assert.Equal(patched.State.Counters.Revisions, replay.State.Counters.Revisions);
        Assert.Equal(JsonSerializer.Serialize(patched.State.Delivery), JsonSerializer.Serialize(replay.State.Delivery));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActiveLockWithoutTokenFailsBeforeStaging(bool file)
    {
        var content = new HookContent(new InMemoryContentStore());
        var provider = StorageTestDocuments.Memory(content);
        var service = new CellBridgeDocumentService(provider); var before = await Create(service);
        await LockAsync(provider, before);
        var staged = false; content.Hook = () => { staged = true; return Task.CompletedTask; };
        var result = await service.ExecuteAsync(before.ResourceId, file ? DocumentPartitionKind.FileContents : DocumentPartitionKind.Metadata,
            await Upload(service, before, file), Attributes, TestActor.Value);
        Assert.NotNull(result.LockError);
        Assert.False(staged);
        Assert.Equal(0, result.State.Counters.SaveReceipts);
        Assert.Equal((await PartitionAsync(before, provider, 1)), (await PartitionAsync(result.State, provider, 1)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LeaseExpiryDuringStagingCannotPublishWithoutAnEpochTransition(bool file)
    {
        var clock = new ManualClock();
        var content = new HookContent(new InMemoryContentStore());
        var provider = new StorageProvider(new InMemoryDocumentRepository(content, clock: clock), content);
        var service = new CellBridgeDocumentService(provider); var before = await Create(service);
        await LockAsync(provider, before, timeout: 60);
        content.Hook = () => { clock.Now = clock.Now.AddMinutes(2); return Task.CompletedTask; };
        var result = await service.ExecuteAsync(before.ResourceId, file ? DocumentPartitionKind.FileContents : DocumentPartitionKind.Metadata,
            await Upload(service, before, file), new Dictionary<string, string>
            { ["ExclusiveLockID"] = "11111111-1111-1111-1111-111111111111", ["Timeout"] = "3600" }, TestActor.Value);
        Assert.NotNull(result.LockError);
        Assert.Equal(0, result.State.Counters.SaveReceipts);
        Assert.Equal(1, (await provider.Documents.ReadControlAsync(before.ResourceId))!.Coordination.Generation);
        Assert.Equal((await PartitionAsync(before, provider, 1)), (await PartitionAsync(result.State, provider, 1)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RevocationDuringReceiptLoadingCannotReplaySuccess(bool file)
    {
        var content = new HookContent(new InMemoryContentStore());
        var provider = StorageTestDocuments.Memory(content);
        var service = new CellBridgeDocumentService(provider); var before = await Create(service);
        var kind = file ? DocumentPartitionKind.FileContents : DocumentPartitionKind.Metadata;
        var request = await Upload(service, before, file);
        var accepted = await service.ExecuteAsync(before.ResourceId, kind, request, Attributes, TestActor.Value); Success(accepted);
        var receipt = Assert.Single((await provider.Documents.ExportDocumentAsync(before.ResourceId))!.Receipts);
        content.ReadHook = receipt.Response;
        content.OnRead = () => RevokeAsync(provider, before.ResourceId);
        var result = await service.ExecuteAsync(before.ResourceId, kind, request, Attributes, TestActor.Value);
        Assert.Equal(CellBridgeAuthorization.AccessDeniedHResult, Assert.Single(result.Response.SubResponses).Error!.ErrorCode);
        Assert.Empty(result.AcceptedSaves);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    public async Task CompleteReceiptAdmissionUsesExactReplayBoundBeforePublication(int offset)
    {
        var provider = StorageTestDocuments.Memory();
        var service = new CellBridgeDocumentService(provider); var before = await Create(service);
        var index = new DataElement(DataElementType.StorageIndexDataElementData, new(1, Guid.NewGuid()), SerialNumber.Null) { Data = [] };
        var request = Request(index);
        var preview = await Save(service, before, request); Success(preview);
        var length = preview.Response.ToByteArray().LongLength;
        var other = await Create(service);
        var bounded = new CellBridgeDocumentService(new(provider.Documents, new HookContent(provider.Content),
            new StorageLimits { MaxObjectBytes = length + offset }));
        var result = await Save(bounded, other, request);
        Assert.Equal(offset < 0, Assert.Single(result.Response.SubResponses).Status);
        if (offset < 0)
        {
            Assert.Equal(other, result.State);
            Assert.Equal(0, result.State.Counters.SaveReceipts);
        }
        else
        {
            var replay = await Save(bounded, result.State, request); Success(replay);
            Assert.Equal(result.Response.ToByteArray(), replay.Response.ToByteArray());
        }
    }

    private static async Task<FsshttpbCellRequest> Upload(CellBridgeDocumentService service, DocumentHead state, bool file)
    {
        if (!file) return Initial(new GraphFixture([1], blob: true));
        var request = StorageTests.Fixture("save-first");
        StorageTests.Rebase(request, await StorageTestDocuments.GraphAsync(service, state));
        return request;
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("partial")]
    [InlineData("coalesce")]
    [InlineData("coalesce-collapsed")]
    public async Task IncompleteAndUnsupportedGraphsNeverPublish(string mode)
    {
        var provider = StorageTestDocuments.Memory();
        var service = new CellBridgeDocumentService(provider); var before = await Create(service);
        var f = new GraphFixture([1], blob: true); var request = Initial(f);
        var attributes = new Dictionary<string, string>();
        if (mode == "missing") request.DataElementPackage!.DataElements.RemoveAll(e => e.DataElementExtendedGuid.Equals(f.Blob));
        if (mode == "duplicate") request.DataElementPackage!.DataElements.Add(new(DataElementType.ObjectDataBLOBDataElementData, f.Blob, SerialNumber.Null) { Data = [99] });
        if (mode == "partial") Assert.IsType<PutChangesSubRequestData>(request.SubRequests[0].Data).Flags |= 2;
        if (mode == "coalesce") attributes["Coalesce"] = "true";
        if (mode == "coalesce-collapsed") attributes["Coalesce"] = " 1 ";
        var result = await service.ExecuteAsync(before.ResourceId, DocumentPartitionKind.Metadata, request, attributes, TestActor.Value);
        Assert.True(Assert.Single(result.Response.SubResponses).Status);
        Assert.Equal(before, await provider.Documents.FindAsync(new DocumentIdentity.Resource(before.ResourceId)));
        Assert.Equal(0, result.State.Counters.SaveReceipts);
    }

    [Fact]
    public async Task EmptyMetadataAndEmptyPatchHaveDistinctSafeBehavior()
    {
        var provider = StorageTestDocuments.Memory();
        var service = new CellBridgeDocumentService(provider); var before = await Create(service);
        var emptyIndex = new DataElement(DataElementType.StorageIndexDataElementData, new(1, Guid.NewGuid()), SerialNumber.Null) { Data = [] };
        var empty = await Save(service, before, Request(emptyIndex)); Success(empty);
        var read = await service.ExecuteAsync(before.ResourceId, DocumentPartitionKind.Metadata, Query(), Attributes, TestActor.Value);
        Assert.False(Assert.Single(read.Response.SubResponses).Status);
        Assert.Empty(GenericPartitionGraphSnapshot.Create(read.Response.DataElementPackage!.DataElements,
            StorageIds.Restore((await PartitionAsync(empty.State, provider, 1)).StorageIndex!)).Cells);
        var f = new GraphFixture([7]);
        var initial = await Save(service, empty.State, Initial(f)); Success(initial);
        var noOp = await Save(service, initial.State, Request(emptyIndex));
        // Reusing the earlier operation identity after supersession is a stale retry.
        Assert.Equal((ulong)CellErrorCode.CoherencyFailure, Assert.Single(noOp.Response.SubResponses).Error!.ErrorCode);
        emptyIndex = new(DataElementType.StorageIndexDataElementData, new(1, Guid.NewGuid()), SerialNumber.Null) { Data = [] };
        var retained = await Save(service, initial.State, Request(emptyIndex)); Success(retained);
        var graph = await GraphAsync(retained.State, provider);
        Assert.Equal(f.Revision, graph.GetCurrentRevision(f.Cell));
        Assert.Equal(f.OtherRevision, graph.GetCurrentRevision(f.OtherCell));
    }

    [Fact]
    public async Task MetadataReceiptsRemainScopedAcrossFileSavesAndEnforceCurrentPermissions()
    {
        var provider = StorageTestDocuments.Memory();
        var service = new CellBridgeDocumentService(provider); var before = await Create(service);
        var metadataRequest = Initial(new GraphFixture([1], blob: true));
        var metadata = await Save(service, before, metadataRequest); Success(metadata);
        var fileRequest = StorageTests.Fixture("save-first");
        StorageTests.Rebase(fileRequest, await StorageTestDocuments.GraphAsync(service, before));
        var file = await service.ExecuteAsync(before.ResourceId, DocumentPartitionKind.FileContents, fileRequest, Attributes, TestActor.Value); Success(file);
        var replay = await Save(service, file.State, metadataRequest); Success(replay);
        Assert.Equal(metadata.Response.ToByteArray(), replay.Response.ToByteArray());
        Assert.Equal(JsonSerializer.Serialize((await PartitionAsync(metadata.State, provider, 1))), JsonSerializer.Serialize((await PartitionAsync(file.State, provider, 1))));
        var patch = new GraphFixture([1], blob: true).CreatePatch(await Index(file.State, provider), false);
        var updated = await Save(service, file.State, patch); Success(updated);
        var fileReplay = await service.ExecuteAsync(before.ResourceId, DocumentPartitionKind.FileContents, fileRequest, Attributes, TestActor.Value); Success(fileReplay);
        Assert.Equal(file.Response.ToByteArray(), fileReplay.Response.ToByteArray());
        Assert.Equal(updated.State.StateVersion, fileReplay.State.StateVersion);
        var stale = await Save(service, updated.State, metadataRequest);
        Assert.Equal((ulong)CellErrorCode.CoherencyFailure, Assert.Single(stale.Response.SubResponses).Error!.ErrorCode);
        await RevokeAsync(provider, before.ResourceId);
        var denied = await Save(service, updated.State, patch);
        Assert.Equal(CellBridgeAuthorization.AccessDeniedHResult, Assert.Single(denied.Response.SubResponses).Error!.ErrorCode);
        Assert.Empty(denied.AcceptedSaves);
    }

    [Fact]
    public async Task LegacyReceiptDefaultsAndHistoryPinsSurviveCapture()
    {
        var legacy = JsonSerializer.Deserialize<SaveReceipt>("{\"OperationKey\":\"old\",\"Digest\":\"sha\",\"ContentVersion\":1,\"Response\":null}")!;
        Assert.Equal(0, legacy.PartitionKind); Assert.Null(legacy.AcceptedStorageIndex); Assert.Equal(1, legacy.LifecycleGeneration);
        var provider = StorageTestDocuments.Memory();
        var service = new CellBridgeDocumentService(provider); var before = await Create(service);
        var saved = await Save(service, before, Initial(new GraphFixture([1], blob: true))); Success(saved);
        Assert.Equal(2, saved.State.Counters.Revisions);
        await service.RenameDocumentAsync(before.ResourceId, "renamed.docx", TestActor.Value);
        var snapshot = (await provider.Documents.ExportDocumentAsync(before.ResourceId))!;
        var detached = JsonSerializer.Deserialize<NormalizedDocumentSnapshot>(JsonSerializer.Serialize(snapshot))!;
        NormalizedDocumentRecovery.Validate(detached, provider.Limits);
        Assert.Equal(saved.State.ContentVersion, detached.ApplicationRevisions[^1].ContentVersion);
        Assert.True(detached.ApplicationRevisions[^1].RevisionNumber > detached.ApplicationRevisions[0].RevisionNumber);
        Assert.Contains(before.PathKey, detached.PathClaims);
        foreach (var element in detached.Elements)
            Assert.Contains(detached.ContentReferences, reference => reference.Handle == element.Element.Payload);

    }

    [Fact]
    public async Task DecodedMetadataWaterlineUsesStablePartitionIdentityAcrossPutsAndQueryScopes()
    {
        var provider = StorageTestDocuments.Memory();
        var service = new CellBridgeDocumentService(provider); var before = await Create(service);
        var id = StorageIds.Restore((await PartitionAsync(before, provider, 1)).Identity.CellShort);
        var emptyQuery = await service.ExecuteAsync(before.ResourceId, DocumentPartitionKind.Metadata, Query(), Attributes, TestActor.Value);
        Assert.Equal(id, DecodeQuery(emptyQuery.Response).WaterlineCellStorageExtendedGuid);
        var f = new GraphFixture([7]);
        var initial = await Save(service, before, Initial(f)); Success(initial);
        Assert.Equal(id, DecodePutWaterline(initial.Response));
        var patch = await Save(service, initial.State, f.CreatePatch(await Index(initial.State, provider), false)); Success(patch);
        Assert.Equal(id, DecodePutWaterline(patch.Response));
        var full = await service.ExecuteAsync(before.ResourceId, DocumentPartitionKind.Metadata, Query(), Attributes, TestActor.Value);
        Assert.Equal(id, DecodeQuery(full.Response).WaterlineCellStorageExtendedGuid);
        var request = Query(); Assert.IsType<QueryChangesSubRequestData>(request.SubRequests[0].Data).CellId = f.OtherCell;
        var scoped = await service.ExecuteAsync(before.ResourceId, DocumentPartitionKind.Metadata, request, Attributes, TestActor.Value);
        var scopedData = DecodeQuery(scoped.Response);
        Assert.Equal(id, scopedData.WaterlineCellStorageExtendedGuid); Assert.Equal(0UL, scopedData.Waterline);

        static QueryChangesSubResponseData DecodeQuery(FsshttpbResponse response) => Assert.IsType<QueryChangesSubResponseData>(
            Assert.Single(FsshttpbResponse.Deserialize(new(response.ToByteArray())).SubResponses).Data);
        static ExGuid? DecodePutWaterline(FsshttpbResponse response)
        {
            var put = Assert.IsType<PutChangesSubResponseData>(Assert.Single(response.SubResponses).Data);
            var data = new QueryChangesSubResponseData { KnowledgeBytes = put.KnowledgeBytes };
            var writer = new BinaryWriterEx(); data.Serialize(writer);
            return QueryChangesSubResponseData.Deserialize(new(writer.ToArray())).WaterlineCellStorageExtendedGuid;
        }
    }

    [PostgreSqlFact]
    public async Task MetadataProcessDeathBeforeAndAfterCommitPreservesPublicationAndReceipt()
    {
        await using var data = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("ConnectionStrings__cellbridge")!);
        var provider = new StorageProvider(new PostgreSqlStateStore(data), new PostgreSqlContentStore(data));
        var service = new CellBridgeDocumentService(provider); var before = await Create(service);
        var request = Initial(new GraphFixture([1, 2, 3], blob: true));
        var requestPath = Path.Combine(Path.GetTempPath(), "metadata-probe-" + Guid.NewGuid().ToString("N") + ".bin");
        try
        {
            await File.WriteAllBytesAsync(requestPath, request.ToByteArray());
            await KillMetadataProbe(before.ResourceId, "before", requestPath);
            var uncommitted = (await provider.Documents.FindAsync(new DocumentIdentity.Resource(before.ResourceId)))!;
            Assert.Null((await PartitionAsync(uncommitted, provider, 1)).StorageIndex); Assert.Equal(0, uncommitted.Counters.SaveReceipts);
            await KillMetadataProbe(before.ResourceId, "after", requestPath);
            await using var restartedData = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("ConnectionStrings__cellbridge")!);
            var restarted = new CellBridgeDocumentService(new(new PostgreSqlStateStore(restartedData), new PostgreSqlContentStore(restartedData)));
            var published = (await restarted.Provider.Documents.FindAsync(new DocumentIdentity.Resource(before.ResourceId)))!;
            var receipt = Assert.Single((await restarted.Provider.Documents.ExportDocumentAsync(before.ResourceId))!.Receipts);
            var expectedBytes = await restarted.Provider.Content.ReadVerifiedAsync(receipt.Response!);
            var retry = await Save(restarted, published, request); Success(retry);
            Assert.Equal(published.StateVersion, retry.State.StateVersion);
            Assert.Equal(expectedBytes, retry.Response.ToByteArray());
            Assert.Equal(before.Package.Sha256, retry.State.Package.Sha256); Assert.Equal(before.ContentVersion, retry.State.ContentVersion);
        }
        finally { File.Delete(requestPath); }
    }

    private static async Task KillMetadataProbe(Guid id, string phase, string requestPath)
    {
        var signal = Path.Combine(Path.GetTempPath(), "metadata-signal-" + Guid.NewGuid().ToString("N"));
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Probe", "CellBridge.Storage.ProcessProbe.dll"));
        foreach (var argument in new[] { id.ToString(), phase, signal, requestPath }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (!File.Exists(signal))
            {
                if (process.HasExited) throw new InvalidOperationException("Metadata probe exited before boundary: " + await errors);
                await Task.Delay(25, timeout.Token);
            }
            process.Kill(entireProcessTree: true); await process.WaitForExitAsync(timeout.Token);
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            await output; await errors; File.Delete(signal);
        }
    }

    private static async Task<DocumentHead> Create(CellBridgeDocumentService service) =>
        (await service.CreateAsync("/metadata-" + Guid.NewGuid().ToString("N") + ".docx", MinimalDocx.Create(), TestActor.Value))!;
    private static FsshttpbCellRequest Query() => new()
    { SubRequests = { new(RequestTypes.QueryChanges) { RequestId = 1,
        Data = new QueryChangesSubRequestData { IncludeStorageManifest = true, IncludeCellChanges = true } } } };
    internal static FsshttpbCellRequest Initial(GraphFixture f)
    {
        var request = Request(f.Elements.Single(e => e.DataElementExtendedGuid.Equals(f.Index)));
        request.DataElementPackage!.DataElements.Clear(); request.DataElementPackage.DataElements.AddRange(f.Elements);
        return request;
    }
    private static FsshttpbCellRequest Request(DataElement index) => new()
    { DataElementPackage = new() { DataElements = { index } }, SubRequests = { new(RequestTypes.PutChanges)
        { RequestId = 1, Data = new PutChangesSubRequestData { StorageIndex = index.DataElementExtendedGuid,
            HasAdditionalFlags = true, AdditionalFlagsBits = 1 } } } };
    private static ValueTask<CellExecution> Save(CellBridgeDocumentService service, DocumentHead state, FsshttpbCellRequest request)
        => service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.Metadata, request, Attributes, TestActor.Value);
    private static async Task<DataElement> Index(DocumentHead state, StorageProvider provider)
    {
        var response = await new CellBridgeDocumentService(provider).ExecuteAsync(state.ResourceId, DocumentPartitionKind.Metadata,
            Query(), Attributes, TestActor.Value);
        var id = Assert.IsType<QueryChangesSubResponseData>(Assert.Single(response.Response.SubResponses).Data).StorageIndexExtendedGuid;
        return response.Response.DataElementPackage!.DataElements.Single(element => element.DataElementExtendedGuid.Equals(id));
    }
    private static async Task<GenericPartitionGraphSnapshot> GraphAsync(DocumentHead state, StorageProvider provider)
    {
        var response = await new CellBridgeDocumentService(provider).ExecuteAsync(state.ResourceId, DocumentPartitionKind.Metadata,
            Query(), Attributes, TestActor.Value);
        var id = Assert.IsType<QueryChangesSubResponseData>(Assert.Single(response.Response.SubResponses).Data).StorageIndexExtendedGuid;
        return GenericPartitionGraphSnapshot.Create(response.Response.DataElementPackage!.DataElements, id);
    }
    private static async Task<PartitionHead> PartitionAsync(DocumentHead head, StorageProvider provider, int kind = 1)
    {
        if (kind == 2) return (await provider.Documents.ReadControlAsync(head.ResourceId))!.Partitions.Single(partition => partition.Kind == kind);
        return (await provider.Documents.ReadRevisionAsync(head.ResourceId, head.LatestRevisionNumber))!.Revision!.Partitions.Single(partition => partition.Kind == kind);
    }
    private static async Task RevokeAsync(StorageProvider provider, Guid id)
    {
        var current = (await provider.Documents.FindAsync(new DocumentIdentity.Resource(id)))!;
        Assert.True((await provider.Documents.ExecuteControlAsync(new DocumentControlCommand.SetPermissions(id,
            StorageTestDocuments.Admission(current), current.Security with { Owner = "revoked" }, current.AuthorityGeneration))).Changed);
    }
    private static async Task LockAsync(StorageProvider provider, DocumentHead head, int timeout = 3600)
    {
        var result = await provider.Documents.ExecuteControlAsync(new DocumentControlCommand.Office(head.ResourceId,
            StorageTestDocuments.Admission(head), new() { Type = SubRequestType.ExclusiveLock, SubRequestDataAttributes =
            { ["ExclusiveLockRequestType"] = "GetLock", ["ExclusiveLockID"] = "11111111-1111-1111-1111-111111111111", ["ClientID"] = "client", ["Timeout"] = timeout.ToString() } }));
        Assert.True(result.Changed);
    }
    private static void Success(CellExecution execution) => Assert.False(Assert.Single(execution.Response.SubResponses).Status,
        execution.Response.SubResponses[0].Error?.ErrorMessage ?? execution.LockError);
    private sealed class HookContent(IContentStore inner) : IContentStore
    {
        public Func<Task>? Hook { get; set; }
        public ContentHandle? ReadHook { get; set; }
        public Func<Task>? OnRead { get; set; }
        public bool Durable => inner.Durable; public bool Shared => inner.Shared;
        public async ValueTask<ContentHandle> WriteAsync(Stream stream, CancellationToken token = default)
        {
            var handle = await inner.WriteAsync(stream, token);
            var hook = Hook; Hook = null;
            if (hook is not null) await hook();
            return handle;
        }
        public async ValueTask<Stream> OpenReadAsync(ContentHandle handle, CancellationToken token = default)
        {
            var stream = await inner.OpenReadAsync(handle, token);
            if (handle == ReadHook && OnRead is { } hook) { OnRead = null; await hook(); }
            return stream;
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        public DateTime Now { get; set; } = DateTime.UtcNow;
        public override DateTimeOffset GetUtcNow() => new(Now);
    }
}
