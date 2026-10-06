using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using CellBridge.AspNetCore;
using CellBridge.FssHttpB;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using CellBridge.Storage.PostgreSql;
using CellBridge.Tests;
using CellBridge.Web;
using Npgsql;

namespace CellBridge.Storage.Tests;

public sealed class MetadataPublicationTests
{
    private static readonly Dictionary<string, string> Attributes = new();

    [Fact]
    public Task CompleteMetadataGraphReadsAndRetriesAfterRecreation() => CheckRecreation(new(new InMemoryStateStore(), new InMemoryContentStore()));

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
        Assert.Equal(before.Content, saved.State.Content);
        Assert.Equal(JsonSerializer.Serialize(before.Partitions[2]), JsonSerializer.Serialize(saved.State.Partitions[2]));
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
        var persisted = JsonSerializer.Deserialize<DocumentState>(JsonSerializer.Serialize(saved.State))!;
        var captured = await (await StoredDocument.RestoreAsync(persisted, reopenedProvider.Content)).CaptureAsync(reopenedProvider.Content);
        Assert.Equal(JsonSerializer.Serialize(saved.State.Partitions[1].Elements), JsonSerializer.Serialize(captured.Partitions[1].Elements));
    }

    [Fact]
    public async Task MappingPatchPreservesUntouchedCellsAndUsesSeparateAppliedAndSelectedIndexes()
    {
        var provider = new StorageProvider(new InMemoryStateStore(), new InMemoryContentStore());
        var service = new CellBridgeDocumentService(provider); var before = await Create(service);
        var f = new GraphFixture([1, 2, 3], blob: true);
        var first = await Save(service, before, Initial(f)); Success(first);
        var expected = await Index(first.State, provider);
        var patch = f.CreatePatch(expected, false);
        var saved = await Save(service, first.State, patch); Success(saved);
        var applied = Assert.IsType<PutChangesSubResponseData>(saved.Response.SubResponses[0].Data).PutChangesResponse!.AppliedStorageIndexID;
        var selected = StorageIds.Restore(saved.State.Partitions[1].StorageIndex!);
        Assert.NotEqual(applied, selected);
        Assert.Equal(2, StorageIndexPatch.Read(Assert.Single(saved.Response.DataElementPackage!.DataElements)).Count);
        var graph = await DocumentPartition.RestoreGraphAsync(saved.State.Partitions[1], provider.Content, new());
        Assert.Equal(f.OtherRevision, graph.GetCurrentRevision(f.OtherCell));
        Assert.NotEqual(f.Revision, graph.GetCurrentRevision(f.Cell));
        Assert.Contains(f.Blob, graph.RequiredElements);
        var staleSameKey = await Save(service, saved.State, f.CreatePatch(expected, false));
        Assert.Equal((ulong)CellErrorCode.CoherencyFailure, Assert.Single(staleSameKey.Response.SubResponses).Error!.ErrorCode);
        var unaffected = await Save(service, saved.State, f.CreatePatch(expected, true)); Success(unaffected);
        var merged = await DocumentPartition.RestoreGraphAsync(unaffected.State.Partitions[1], provider.Content, new());
        Assert.Equal(graph.GetCurrentRevision(f.Cell), merged.GetCurrentRevision(f.Cell));
        Assert.NotEqual(f.OtherRevision, merged.GetCurrentRevision(f.OtherCell));
    }

    [Fact]
    public async Task TwoServicesMergeCompetingDistinctMappingPatchesWithoutLostUpdates()
    {
        var inner = new InMemoryContentStore();
        var content = new HookContent(inner);
        var provider = new StorageProvider(new InMemoryStateStore(), content);
        var firstService = new CellBridgeDocumentService(provider); var before = await Create(firstService);
        var f = new GraphFixture([1], blob: true);
        var initial = await Save(firstService, before, Initial(f)); Success(initial);
        var expected = await Index(initial.State, provider);
        CellExecution? competitor = null;
        content.Hook = async () => { competitor = await Save(new(provider), initial.State, f.CreatePatch(expected, true)); Success(competitor); };
        var saved = await Save(firstService, initial.State, f.CreatePatch(expected, false)); Success(saved);
        Assert.NotNull(competitor);
        var graph = await DocumentPartition.RestoreGraphAsync(saved.State.Partitions[1], provider.Content, new());
        Assert.NotEqual(f.Revision, graph.GetCurrentRevision(f.Cell));
        Assert.NotEqual(f.OtherRevision, graph.GetCurrentRevision(f.OtherCell));
        Assert.Equal(3, saved.State.Receipts.Count(r => r.PartitionKind == 1));
    }

    [Theory]
    [InlineData("permission")]
    [InlineData("lease")]
    public async Task AuthorityChangesDuringPreparationCannotPublish(string change)
    {
        var content = new HookContent(new InMemoryContentStore());
        var provider = new StorageProvider(new InMemoryStateStore(), content);
        var service = new CellBridgeDocumentService(provider); var before = await Create(service);
        content.Hook = () => provider.State.TransitionAsync(before.ResourceId, (current, _) => new StateTransition<bool>(change switch
        {
            "permission" => current with { Security = current.Security with { Owner = "revoked" } },
            _ => current with { Coordination = current.Coordination with { Generation = current.Coordination.Generation + 1 } },
        }, true)).AsTask();
        var saved = await Save(service, before, Initial(new GraphFixture([1], blob: true)));
        Assert.True(Assert.Single(saved.Response.SubResponses).Status);
        Assert.Equal(before.Partitions[1], saved.State.Partitions[1]);
        Assert.Empty(saved.State.Receipts);
        Assert.Equal(before.Content, saved.State.Content);
    }

    [Fact]
    public async Task DeletedAndRecreatedResourceCannotReceivePreparedMetadata()
    {
        var content = new HookContent(new InMemoryContentStore());
        var provider = new StorageProvider(new InMemoryStateStore(), content);
        var service = new CellBridgeDocumentService(provider); var before = await Create(service);
        var lifecycle = (IDocumentLifecycleStore)provider.State;
        DocumentState? replacement = null;
        content.Hook = async () =>
        {
            Assert.True(await lifecycle.TryDeleteAsync(before.ResourceId, before.LifecycleGeneration, before.StateVersion));
            var tombstone = (await lifecycle.FindLifecycleAsync(before.ResourceId))!;
            replacement = await new DocumentStore().Put(before.Path, MinimalDocx.Create("replacement")).CaptureAsync(provider.Content);
            replacement = replacement with { LifecycleGeneration = before.LifecycleGeneration + 1, Security = before.Security };
            Assert.True(await lifecycle.TryRecreateAsync(before.ResourceId, before.LifecycleGeneration, tombstone.StateVersion, replacement));
        };
        await Assert.ThrowsAsync<KeyNotFoundException>(() => Save(service, before, Initial(new GraphFixture([1], blob: true))).AsTask());
        var retired = (await lifecycle.FindLifecycleAsync(before.ResourceId))!;
        Assert.Equal(before.Partitions[1], retired.Partitions[1]); Assert.Empty(retired.Receipts);
        var current = (await provider.State.FindByResourceIdAsync(replacement!.ResourceId))!;
        Assert.Null(current.Partitions[1].StorageIndex); Assert.Empty(current.Receipts);
        Assert.Equal(replacement.Content, current.Content);
    }

    [Fact]
    public async Task HostLockRejectsMetadataBeforeStaging()
    {
        var content = new HookContent(new InMemoryContentStore());
        var provider = new StorageProvider(new InMemoryStateStore(), content);
        var service = new CellBridgeDocumentService(provider); var before = await Create(service);
        Assert.True((await new SharedDocumentLocks(service).ApplyAsync(before.ResourceId,
            HostLockOperation.Acquire, "host-token", TestActor.Value)).Success);
        var staged = false; content.Hook = () => { staged = true; return Task.CompletedTask; };
        var result = await Save(service, before, Initial(new GraphFixture([1], blob: true)));
        Assert.Equal("FileAlreadyLockedOnServer", result.LockError); Assert.False(staged);
        Assert.Equal("host-token", result.State.Coordination.HostLock!.Token);
        Assert.Empty(result.State.Receipts); Assert.Equal(before.Partitions[1], result.State.Partitions[1]);
    }

    [Fact]
    public Task MetadataAndAppliedFileSavesPreservePublicationAndHistory() =>
        CheckPublicationIntegration(new(new InMemoryStateStore(), new InMemoryContentStore(),
            new StorageLimits { MaxPendingExternalRevisions = 1 }));

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
        await provider.State.TransitionAsync(before.ResourceId, (current, _) => new StateTransition<bool>(current with
        { Publication = new(Guid.NewGuid(), "unchanged-destination", "remote-r0", 1, []) }, true));
        var metadata = await Save(service, before, Initial(new GraphFixture([1], blob: true))); Success(metadata);
        Assert.Empty(metadata.State.Publication!.Pending); Assert.Empty(metadata.AcceptedSaves);
        var request = Upload(metadata.State, file: true);
        var put = Assert.IsType<PutChangesSubRequestData>(request.SubRequests[0].Data);
        put.HasAdditionalFlags = true; put.AdditionalFlagsBits |= 1;
        var file = await service.ExecuteAsync(before.ResourceId, DocumentPartitionKind.FileContents, request, Attributes, TestActor.Value); Success(file);
        var queued = Assert.Single(file.State.Publication!.Pending);
        Assert.Equal(file.State.Content, queued.Content); Assert.Equal(file.State.ContentVersion, queued.ContentVersion);
        Assert.Equal(file.State.LifecycleGeneration, queued.LifecycleGeneration);
        Assert.Equal(JsonSerializer.Serialize(metadata.State.Partitions[1]), JsonSerializer.Serialize(file.State.Partitions[1]));
        Assert.Equal(file.State.ContentVersion, file.State.Revisions[^1].ContentVersion);
        Assert.Contains(queued.Content, StorageReferences.Handles(file.State));
        await provider.State.TransitionAsync(before.ResourceId, (current, _) => new StateTransition<bool>(current with
        { Publication = current.Publication! with { BlockedReason = "conflict" } }, true));
        var full = (await provider.State.FindByResourceIdAsync(before.ResourceId))!;
        var patched = await Save(service, full, new GraphFixture([1], blob: true).CreatePatch(await Index(full, provider), false)); Success(patched);
        Assert.Equal(JsonSerializer.Serialize(full.Publication), JsonSerializer.Serialize(patched.State.Publication));
        Assert.Equal(full.ContentVersion, patched.State.ContentVersion);
        Assert.Equal(full.Revisions.Length + 1, patched.State.Revisions.Length);
        // A replay acknowledges the committed revision even after a lease epoch changes.
        await provider.State.TransitionAsync(before.ResourceId, (current, _) => new StateTransition<bool>(current with
        { Coordination = current.Coordination with { Generation = current.Coordination.Generation + 1 } }, true));
        var replay = await service.ExecuteAsync(before.ResourceId, DocumentPartitionKind.FileContents, request, Attributes, TestActor.Value); Success(replay);
        Assert.Equal(file.Response.ToByteArray(), replay.Response.ToByteArray());
        Assert.Equal(patched.State.Revisions.Length, replay.State.Revisions.Length);
        Assert.Equal(JsonSerializer.Serialize(patched.State.Publication), JsonSerializer.Serialize(replay.State.Publication));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActiveLockWithoutTokenFailsBeforeStaging(bool file)
    {
        var content = new HookContent(new InMemoryContentStore());
        var provider = new StorageProvider(new InMemoryStateStore(), content);
        var service = new CellBridgeDocumentService(provider); var before = await Create(service);
        await provider.State.TransitionAsync(before.ResourceId, (current, now) => new StateTransition<bool>(current with
        { Coordination = new(null, [], new("11111111-1111-1111-1111-111111111111", "client", now.AddMinutes(1), 0, null, TestActor.Value.Identity.Subject), 1) }, true));
        var staged = false; content.Hook = () => { staged = true; return Task.CompletedTask; };
        var result = await service.ExecuteAsync(before.ResourceId, file ? DocumentPartitionKind.FileContents : DocumentPartitionKind.Metadata,
            Upload(before, file), Attributes, TestActor.Value);
        Assert.NotNull(result.LockError);
        Assert.False(staged);
        Assert.Empty(result.State.Receipts);
        Assert.Equal(before.Partitions[1], result.State.Partitions[1]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LeaseExpiryDuringStagingCannotPublishWithoutAnEpochTransition(bool file)
    {
        var state = new ClockState(new InMemoryStateStore());
        var content = new HookContent(new InMemoryContentStore());
        var provider = new StorageProvider(state, content);
        var service = new CellBridgeDocumentService(provider); var before = await Create(service);
        await state.TransitionAsync(before.ResourceId, (current, now) => new StateTransition<bool>(current with
        { Coordination = new(null, [], new("11111111-1111-1111-1111-111111111111", "client", now.AddMinutes(1), 0, null, TestActor.Value.Identity.Subject), 1) }, true));
        content.Hook = () => { state.Now = state.Now.AddMinutes(2); return Task.CompletedTask; };
        var result = await service.ExecuteAsync(before.ResourceId, file ? DocumentPartitionKind.FileContents : DocumentPartitionKind.Metadata,
            Upload(before, file), new Dictionary<string, string>
            { ["ExclusiveLockID"] = "11111111-1111-1111-1111-111111111111", ["Timeout"] = "3600" }, TestActor.Value);
        Assert.NotNull(result.LockError);
        Assert.Empty(result.State.Receipts);
        Assert.Equal(1, result.State.Coordination.Generation);
        Assert.Equal(before.Partitions[1], result.State.Partitions[1]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RevocationDuringReceiptLoadingCannotReplaySuccess(bool file)
    {
        var content = new HookContent(new InMemoryContentStore());
        var provider = new StorageProvider(new InMemoryStateStore(), content);
        var service = new CellBridgeDocumentService(provider); var before = await Create(service);
        var kind = file ? DocumentPartitionKind.FileContents : DocumentPartitionKind.Metadata;
        var request = Upload(before, file);
        var accepted = await service.ExecuteAsync(before.ResourceId, kind, request, Attributes, TestActor.Value); Success(accepted);
        var receipt = Assert.Single(accepted.State.Receipts);
        content.ReadHook = receipt.Response;
        content.OnRead = () => provider.State.TransitionAsync(before.ResourceId, (current, _) =>
            new StateTransition<bool>(current with { Security = current.Security with { Owner = "revoked" } }, true)).AsTask();
        var result = await service.ExecuteAsync(before.ResourceId, kind, request, Attributes, TestActor.Value);
        Assert.Equal(CellBridgeAuthorization.AccessDeniedHResult, Assert.Single(result.Response.SubResponses).Error!.ErrorCode);
        Assert.Empty(result.AcceptedSaves);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    public async Task CompleteReceiptAdmissionUsesExactReplayBoundBeforePublication(int offset)
    {
        var provider = new StorageProvider(new InMemoryStateStore(), new InMemoryContentStore());
        var service = new CellBridgeDocumentService(provider); var before = await Create(service);
        var index = new DataElement(DataElementType.StorageIndexDataElementData, new(1, Guid.NewGuid()), SerialNumber.Null) { Data = [] };
        var request = Request(index);
        var preview = await Save(service, before, request); Success(preview);
        var length = preview.Response.ToByteArray().LongLength;
        var other = await Create(service);
        var bounded = new CellBridgeDocumentService(new(new ClockState(provider.State), new HookContent(provider.Content),
            new StorageLimits { MaxObjectBytes = length + offset }));
        var result = await Save(bounded, other, request);
        Assert.Equal(offset < 0, Assert.Single(result.Response.SubResponses).Status);
        if (offset < 0)
        {
            Assert.Equal(other, result.State);
            Assert.Empty(result.State.Receipts);
        }
        else
        {
            var replay = await Save(bounded, result.State, request); Success(replay);
            Assert.Equal(result.Response.ToByteArray(), replay.Response.ToByteArray());
        }
    }

    private static FsshttpbCellRequest Upload(DocumentState state, bool file)
    {
        if (!file) return Initial(new GraphFixture([1], blob: true));
        var request = StorageTests.Fixture("save-first");
        Assert.IsType<PutChangesSubRequestData>(request.SubRequests[0].Data).ExpectedStorageIndex = StorageIds.Restore(state.Partitions[0].StorageIndex!);
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
        var provider = new StorageProvider(new InMemoryStateStore(), new InMemoryContentStore());
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
        Assert.Equal(before, await provider.State.FindByResourceIdAsync(before.ResourceId));
        Assert.Empty(result.State.Receipts);
    }

    [Fact]
    public async Task EmptyMetadataAndEmptyPatchHaveDistinctSafeBehavior()
    {
        var provider = new StorageProvider(new InMemoryStateStore(), new InMemoryContentStore());
        var service = new CellBridgeDocumentService(provider); var before = await Create(service);
        var emptyIndex = new DataElement(DataElementType.StorageIndexDataElementData, new(1, Guid.NewGuid()), SerialNumber.Null) { Data = [] };
        var empty = await Save(service, before, Request(emptyIndex)); Success(empty);
        var read = await service.ExecuteAsync(before.ResourceId, DocumentPartitionKind.Metadata, Query(), Attributes, TestActor.Value);
        Assert.False(Assert.Single(read.Response.SubResponses).Status);
        Assert.Empty(GenericPartitionGraphSnapshot.Create(read.Response.DataElementPackage!.DataElements,
            StorageIds.Restore(empty.State.Partitions[1].StorageIndex!)).Cells);
        var f = new GraphFixture([7]);
        var initial = await Save(service, empty.State, Initial(f)); Success(initial);
        var noOp = await Save(service, initial.State, Request(emptyIndex));
        // Reusing the earlier operation identity after supersession is a stale retry.
        Assert.Equal((ulong)CellErrorCode.CoherencyFailure, Assert.Single(noOp.Response.SubResponses).Error!.ErrorCode);
        emptyIndex = new(DataElementType.StorageIndexDataElementData, new(1, Guid.NewGuid()), SerialNumber.Null) { Data = [] };
        var retained = await Save(service, initial.State, Request(emptyIndex)); Success(retained);
        var graph = await DocumentPartition.RestoreGraphAsync(retained.State.Partitions[1], provider.Content, new());
        Assert.Equal(f.Revision, graph.GetCurrentRevision(f.Cell));
        Assert.Equal(f.OtherRevision, graph.GetCurrentRevision(f.OtherCell));
    }

    [Fact]
    public async Task MetadataReceiptsRemainScopedAcrossFileSavesAndEnforceCurrentPermissions()
    {
        var provider = new StorageProvider(new InMemoryStateStore(), new InMemoryContentStore());
        var service = new CellBridgeDocumentService(provider); var before = await Create(service);
        var metadataRequest = Initial(new GraphFixture([1], blob: true));
        var metadata = await Save(service, before, metadataRequest); Success(metadata);
        var fileRequest = StorageTests.Fixture("save-first");
        Assert.IsType<PutChangesSubRequestData>(fileRequest.SubRequests[0].Data).ExpectedStorageIndex = StorageIds.Restore(before.Partitions[0].StorageIndex!);
        var file = await service.ExecuteAsync(before.ResourceId, DocumentPartitionKind.FileContents, fileRequest, Attributes, TestActor.Value); Success(file);
        var replay = await Save(service, file.State, metadataRequest); Success(replay);
        Assert.Equal(metadata.Response.ToByteArray(), replay.Response.ToByteArray());
        Assert.Equal(JsonSerializer.Serialize(metadata.State.Partitions[1]), JsonSerializer.Serialize(file.State.Partitions[1]));
        var patch = new GraphFixture([1], blob: true).CreatePatch(await Index(file.State, provider), false);
        var updated = await Save(service, file.State, patch); Success(updated);
        var fileReplay = await service.ExecuteAsync(before.ResourceId, DocumentPartitionKind.FileContents, fileRequest, Attributes, TestActor.Value); Success(fileReplay);
        Assert.Equal(file.Response.ToByteArray(), fileReplay.Response.ToByteArray());
        Assert.Equal(updated.State.StateVersion, fileReplay.State.StateVersion);
        var stale = await Save(service, updated.State, metadataRequest);
        Assert.Equal((ulong)CellErrorCode.CoherencyFailure, Assert.Single(stale.Response.SubResponses).Error!.ErrorCode);
        await provider.State.TransitionAsync(before.ResourceId, (current, _) => new StateTransition<bool>(current with
            { Security = current.Security with { Owner = "new-owner" } }, true));
        var denied = await Save(service, updated.State, patch);
        Assert.Equal(CellBridgeAuthorization.AccessDeniedHResult, Assert.Single(denied.Response.SubResponses).Error!.ErrorCode);
        Assert.Empty(denied.AcceptedSaves);
    }

    [Fact]
    public async Task LegacyReceiptDefaultsAndHistoryPinsSurviveCapture()
    {
        var legacy = JsonSerializer.Deserialize<SaveReceipt>("{\"OperationKey\":\"old\",\"Digest\":\"sha\",\"ContentVersion\":1,\"Response\":null}")!;
        Assert.Equal(0, legacy.PartitionKind); Assert.Null(legacy.AcceptedStorageIndex); Assert.Equal(1, legacy.LifecycleGeneration);
        var provider = new StorageProvider(new InMemoryStateStore(), new InMemoryContentStore());
        var service = new CellBridgeDocumentService(provider); var before = await Create(service);
        var saved = await Save(service, before, Initial(new GraphFixture([1], blob: true))); Success(saved);
        Assert.Equal(2, saved.State.Revisions.Length);
        Assert.Equal(saved.State.ContentVersion, saved.State.Revisions[^1].ContentVersion);
        Assert.True(saved.State.Revisions[^1].RevisionNumber > saved.State.Revisions[0].RevisionNumber);
        var detached = saved.State with { RetiredPathKeys = ["/PREVIOUS.DOCX"] };
        var capture = await (await StoredDocument.RestoreAsync(detached, provider.Content)).CaptureAsync(provider.Content);
        Assert.Equal(saved.State.Revisions, capture.Revisions);
        Assert.Equal(detached.RetiredPathKeys, capture.RetiredPathKeys);
        foreach (var handle in saved.State.Revisions.SelectMany(r => r.Partitions).SelectMany(p => p.Elements).Select(e => e.Payload))
            Assert.Contains(handle, StorageReferences.Handles(capture));
    }

    [Fact]
    public async Task DecodedMetadataWaterlineUsesStablePartitionIdentityAcrossPutsAndQueryScopes()
    {
        var provider = new StorageProvider(new InMemoryStateStore(), new InMemoryContentStore());
        var service = new CellBridgeDocumentService(provider); var before = await Create(service);
        var id = StorageIds.Restore(before.Partitions[1].Identity.CellShort);
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
            var uncommitted = (await provider.State.FindByResourceIdAsync(before.ResourceId))!;
            Assert.Null(uncommitted.Partitions[1].StorageIndex); Assert.Empty(uncommitted.Receipts);
            await KillMetadataProbe(before.ResourceId, "after", requestPath);
            await using var restartedData = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("ConnectionStrings__cellbridge")!);
            var restarted = new CellBridgeDocumentService(new(new PostgreSqlStateStore(restartedData), new PostgreSqlContentStore(restartedData)));
            var published = (await restarted.Provider.State.FindByResourceIdAsync(before.ResourceId))!;
            var receipt = Assert.Single(published.Receipts);
            var expectedBytes = await restarted.Provider.Content.ReadVerifiedAsync(receipt.Response!);
            var retry = await Save(restarted, published, request); Success(retry);
            Assert.Equal(published.StateVersion, retry.State.StateVersion);
            Assert.Equal(expectedBytes, retry.Response.ToByteArray());
            Assert.Equal(before.Content, retry.State.Content); Assert.Equal(before.ContentVersion, retry.State.ContentVersion);
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

    private static async Task<DocumentState> Create(CellBridgeDocumentService service) =>
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
    private static ValueTask<CellExecution> Save(CellBridgeDocumentService service, DocumentState state, FsshttpbCellRequest request)
        => service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.Metadata, request, Attributes, TestActor.Value);
    private static async Task<DataElement> Index(DocumentState state, StorageProvider provider)
    {
        var p = state.Partitions[1]; var e = p.Elements.Single(e => e.Id == p.StorageIndex);
        return new(DataElementType.StorageIndexDataElementData, StorageIds.Restore(e.Id), new(e.Serial.Guid, e.Serial.Value))
        { Data = await provider.Content.ReadVerifiedAsync(e.Payload) };
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

    private sealed class ClockState(IDocumentStateStore inner) : IDocumentStateStore
    {
        public DateTime Now { get; set; } = DateTime.UtcNow;
        public bool Durable => inner.Durable;
        public bool Shared => inner.Shared;
        public ValueTask<DocumentState?> FindByResourceIdAsync(Guid id, CancellationToken token = default) => inner.FindByResourceIdAsync(id, token);
        public ValueTask<DocumentState?> FindByPathKeyAsync(string path, CancellationToken token = default) => inner.FindByPathKeyAsync(path, token);
        public ValueTask<IReadOnlyList<DocumentSummary>> ListAsync(int offset, int limit, CancellationToken token = default) => inner.ListAsync(offset, limit, token);
        public ValueTask<bool> TryCreateAsync(DocumentState document, CancellationToken token = default) => inner.TryCreateAsync(document, token);
        public ValueTask<T> TransitionAsync<T>(Guid id, Func<DocumentState, DateTime, StateTransition<T>> callback, CancellationToken token = default)
            => inner.TransitionAsync(id, (current, _) => callback(current, Now), token);
        public ValueTask CheckHealthAsync(CancellationToken token = default) => inner.CheckHealthAsync(token);
    }
}
