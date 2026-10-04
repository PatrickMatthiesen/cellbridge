using System.Collections.Immutable;
using CellBridge.AspNetCore;
using CellBridge.FssHttpB;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using CellBridge.Storage.PostgreSql;
using CellBridge.Web;
using Npgsql;

namespace CellBridge.Storage.Tests;

public class BudgetAndQueryTests
{
    private static FsshttpbCellRequest Query(ClientKnowledge? knowledge = null) => new()
    { SubRequests = { new(RequestTypes.QueryChanges) { RequestId = 1, Data = new QueryChangesSubRequestData
        { IncludeStorageManifest = true, IncludeCellChanges = true, Knowledge = knowledge } } } };

    private static FsshttpbCellRequest NewSave(DocumentState state, bool nullSerials = false, ulong? mappingMaximum = null)
    {
        var partition = state.Partitions.Single(p => p.Kind == 0);
        var guid = Guid.NewGuid();
        var identity = new StorageManifestBuilder.StableIdentity(new(1, guid), new(2, guid), new(3, guid),
            new(4, guid), new(5, guid), new(6, guid), new(7, guid),
            new(StorageIds.Restore(partition.Identity.CellLong), StorageIds.Restore(partition.Identity.CellShort)),
            partition.Identity.SerialGuid);
        var package = FileContentPartitionBuilder.BuildQueryChangesResponse(1, MinimalDocx.Create(), identity, 1).DataElementPackage!;
        foreach (var element in package.DataElements)
            element.SerialNumber = nullSerials ? SerialNumber.Null : new(partition.Elements[0].Serial.Guid, partition.Elements[0].Serial.Value);
        ulong max = mappingMaximum ?? checked(partition.Knowledge + 100);
        {
            var index = package.DataElements.Single(e => e.DataElementType == DataElementType.StorageIndexDataElementData);
            package.DataElements[package.DataElements.IndexOf(index)] = StorageManifestBuilder.BuildStorageIndexDataElement(
                identity.ObjectDataBlobGuid, index.SerialNumber, identity.StorageManifestGuid, new(identity.SerialGuid, max),
                identity.CellId, identity.CellManifestGuid, new(identity.SerialGuid, max - 1), identity.RevisionId,
                identity.RevisionManifestGuid, new(identity.SerialGuid, max - 2));
        }
        return new() { DataElementPackage = package, SubRequests = { new(RequestTypes.PutChanges) { RequestId = 2,
            Data = new PutChangesSubRequestData { StorageIndex = identity.ObjectDataBlobGuid,
                ExpectedStorageIndex = StorageIds.Restore(partition.StorageIndex!), Flags = 1 } } } };
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReusedOrNullClientSerialsCannotHideNewDataAfterRestart(bool nullSerials)
    {
        var provider = Memory();
        var service = new CellBridgeDocumentService(provider);
        var initial = (await service.CreateAsync("/shared/serials.docx", MinimalDocx.Create(), TestActor.Value))!;
        var before = await service.ExecuteAsync(initial.ResourceId, DocumentPartitionKind.FileContents, Query(), new Dictionary<string, string>(), TestActor.Value);
        var known = before.Response.DataElementPackage!.DataElements;
        ulong max = initial.Partitions.Single(p => p.Kind == 0).Knowledge + 1000;
        var request = NewSave(initial, nullSerials, max);
        var saved = await service.ExecuteAsync(initial.ResourceId, DocumentPartitionKind.FileContents, request, new Dictionary<string, string>(), TestActor.Value);
        Assert.False(Assert.Single(saved.Response.SubResponses).Status);
        var additions = saved.State.Partitions.Single(p => p.Kind == 0).Elements
            .Where(e => !initial.Partitions.Single(p => p.Kind == 0).Elements.Any(old => old.Id == e.Id)).ToArray();
        Assert.Equal(5, additions.Length);
        Assert.All(additions, e => Assert.True(e.Serial.Value > max));
        Assert.Equal(5, additions.Select(e => e.Serial).Distinct().Count());
        var response = Assert.IsType<PutChangesSubResponseData>(saved.Response.SubResponses[0].Data);
        var serialReader = new BinaryReaderEx(response.SerialNumberReassignAllBytes!);
        Assert.Equal(StreamObjectTypeHeaderStart.PutChangesResponseSerialNumberReassignAll, StreamObjectHeaderStart.Parse(serialReader).Type);
        Assert.True(SerialNumber.Deserialize(serialReader).IsNull);
        var assignments = new Dictionary<ExGuid, SerialNumber>();
        while (serialReader.Remaining > 0)
        {
            var header = StreamObjectHeaderStart.Parse(serialReader);
            Assert.Equal(StreamObjectTypeHeaderStart.PutChangesResponseSerialNumberReassign, header.Type);
            var body = new BinaryReaderEx(serialReader.ReadMemory(header.Length));
            assignments.Add(ExGuid.Deserialize(body), SerialNumber.Deserialize(body));
            Assert.Equal(0, body.Remaining);
        }
        Assert.Equal(additions.Length, assignments.Count);
        Assert.All(additions, e => Assert.Equal(new SerialNumber(e.Serial.Guid, e.Serial.Value), assignments[StorageIds.Restore(e.Id)]));
        var restarted = new CellBridgeDocumentService(provider);
        var delta = await restarted.ExecuteAsync(initial.ResourceId, DocumentPartitionKind.FileContents,
            Query(ClientKnowledge.FromElements(known)), new Dictionary<string, string>(), TestActor.Value);
        Assert.All(additions, e => Assert.Contains(delta.Response.DataElementPackage!.DataElements, d => d.DataElementExtendedGuid.Equals(StorageIds.Restore(e.Id))));
        var latest = Assert.IsType<QueryChangesSubResponseData>(delta.Response.SubResponses[0].Data).StorageIndexExtendedGuid;
        var restored = await StoredDocument.RestoreAsync(saved.State, provider.Content);
        Assert.Equal(restored.Content, PartitionGraphSnapshot.Create(known.Concat(delta.Response.DataElementPackage!.DataElements), latest).Materialize());
        var retry = await restarted.ExecuteAsync(initial.ResourceId, DocumentPartitionKind.FileContents, request, new Dictionary<string, string>(), TestActor.Value);
        Assert.False(Assert.Single(retry.Response.SubResponses).Status);
        Assert.Equal(saved.State.StateVersion, retry.State.StateVersion);
        var second = await restarted.ExecuteAsync(initial.ResourceId, DocumentPartitionKind.FileContents,
            NewSave(saved.State, true), new Dictionary<string, string>(), TestActor.Value);
        Assert.False(Assert.Single(second.Response.SubResponses).Status);
        Assert.True(second.State.Partitions.Single(p => p.Kind == 0).Knowledge > saved.State.Partitions.Single(p => p.Kind == 0).Knowledge);
    }

    [Fact]
    public async Task MissingMappingKnowledgeReturnsIndexEvenWhenItsElementSerialIsKnown()
    {
        var provider = Memory();
        var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/shared/mappings.docx", MinimalDocx.Create(), TestActor.Value))!;
        var partition = state.Partitions.Single(p => p.Kind == 0);
        var metadata = partition.Elements.Select(e => new DataElement((DataElementType)e.Type, StorageIds.Restore(e.Id), new(e.Serial.Guid, e.Serial.Value)));
        var delta = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents,
            Query(ClientKnowledge.FromElements(metadata)), new Dictionary<string, string>(), TestActor.Value);
        Assert.Equal(DataElementType.StorageIndexDataElementData, Assert.Single(delta.Response.DataElementPackage!.DataElements).DataElementType);
        var mappingOnly = ClientKnowledge.Deserialize(new(BinaryKnowledgeBuilder.FromElements([], ExGuid.Null, 0,
            mappingSerials: partition.Elements.SelectMany(e => e.MappingSerials).Select(s => new SerialNumber(s.Guid, s.Value)))));
        var full = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents, Query(mappingOnly), new Dictionary<string, string>(), TestActor.Value);
        Assert.Equal(partition.Elements.Length, full.Response.DataElementPackage!.DataElements.Count);
    }

    [Fact]
    public async Task SerialExhaustionRejectsSaveWithoutPublishing()
    {
        var provider = Memory();
        var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/shared/exhausted.docx", MinimalDocx.Create(), TestActor.Value))!;
        var result = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents,
            NewSave(state, true, ulong.MaxValue), new Dictionary<string, string>(), TestActor.Value);
        Assert.True(Assert.Single(result.Response.SubResponses).Status);
        Assert.Equal(state.StateVersion, (await provider.State.FindByResourceIdAsync(state.ResourceId))!.StateVersion);
    }

    [Fact]
    public async Task ReusingMappingSerialForAnotherTargetRejectsWithoutPublishing()
    {
        var provider = Memory();
        var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/shared/mapping-conflict.docx", MinimalDocx.Create(), TestActor.Value))!;
        // Seed mappings have values 1, 2 and 3 under the server serial GUID.
        var result = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents,
            NewSave(state, true, 3), new Dictionary<string, string>(), TestActor.Value);
        Assert.True(Assert.Single(result.Response.SubResponses).Status);
        Assert.Contains("mapping serial", Assert.Single(result.Response.SubResponses).Error!.ErrorMessage);
        Assert.Equal(state.StateVersion, (await provider.State.FindByResourceIdAsync(state.ResourceId))!.StateVersion);
    }

    [Fact]
    public async Task AliasedSerialsAreRetransmittedEvenWhenClientKnowledgeContainsThem()
    {
        var provider = Memory();
        var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/shared/aliases.docx", MinimalDocx.Create(), TestActor.Value))!;
        var partition = state.Partitions.Single(p => p.Kind == 0);
        var index = partition.Elements.Single(e => e.Type == (uint)DataElementType.StorageIndexDataElementData);
        Assert.NotEmpty(index.MappingSerials);
        var alias = index.MappingSerials.First();
        await provider.State.TransitionAsync(state.ResourceId, (current, now) => new StateTransition<bool>(current with
        { Partitions = current.Partitions.Select(p => p.Kind != 0 ? p : p with
            { Elements = p.Elements.Select(e => e with { Serial = e.Type == (uint)DataElementType.StorageManifestDataElementData ? alias : e.Serial }).ToImmutableArray() }).ToImmutableArray() }, true));
        var full = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents, Query(), new Dictionary<string, string>(), TestActor.Value);
        var known = ClientKnowledge.FromElements(full.Response.DataElementPackage!.DataElements);
        var delta = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents, Query(known), new Dictionary<string, string>(), TestActor.Value);
        Assert.Equal(DataElementType.StorageManifestDataElementData, Assert.Single(delta.Response.DataElementPackage!.DataElements).DataElementType);
        Assert.False(Assert.Single(delta.Response.SubResponses).Status);
    }

    [Fact]
    public async Task CapturedSaveGraphsCompactWithoutChangingTheMaterializedFile()
    {
        var provider = Memory();
        var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/shared/compact-proof.docx", MinimalDocx.Create(), TestActor.Value))!;
        PartitionGraphSnapshot? previous = null;
        foreach (var name in new[] { "save-first", "save-second" })
        {
            var request = StorageTests.Fixture(name);
            if (previous is null) ((PutChangesSubRequestData)request.SubRequests.Single().Data!).ExpectedStorageIndex =
                StorageIds.Restore(state.Partitions.Single(p => p.Kind == 0).StorageIndex!);
            var saved = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents, request, new Dictionary<string, string>(), TestActor.Value);
            Assert.False(Assert.Single(saved.Response.SubResponses).Status);
            var document = await StoredDocument.RestoreAsync(saved.State, provider.Content);
            var graph = document.FilePartition.FileGraph;
            Assert.True(graph.AnalyzeRetention().Complete);
            var compact = graph.Compact();
            Assert.True(compact.ElementCount < graph.ElementCount);
            Assert.Equal(document.Content, compact.Materialize());
            if (previous is not null)
            {
                var protectedGraph = graph.Compact([previous.StorageIndex]);
                Assert.Equal(document.Content, protectedGraph.Materialize());
                Assert.All(previous.Compact().ElementMetadata, e => Assert.Contains(protectedGraph.ElementMetadata,
                    p => p.DataElementExtendedGuid.Equals(e.DataElementExtendedGuid)));
            }
            previous = graph;
            state = saved.State;
        }
    }

    private static StorageProvider Memory(StorageLimits? limits = null) =>
        new(new InMemoryStateStore(), new InMemoryContentStore(), limits ?? new StorageLimits());

    [Fact]
    public async Task LargeEchoedKnowledgeReturnsAFullGraphInsteadOfRejectingOrUsingPartialRanges()
    {
        var provider = Memory();
        var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/shared/large-knowledge.docx", MinimalDocx.Create(), TestActor.Value))!;
        var full = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents, Query(), new Dictionary<string, string>(), TestActor.Value);
        var elements = full.Response.DataElementPackage!.DataElements;
        var serialGuid = Guid.NewGuid();
        var advertised = BinaryKnowledgeBuilder.FromElements(elements, ExGuid.Null, 0,
            mappingSerials: Enumerable.Range(0, 10_001).Select(i => new SerialNumber(serialGuid, (ulong)i * 2 + 1)));
        var request = Query(ClientKnowledge.Deserialize(new(advertised)));
        // Exercise the binary request decoder used when a client echoes server knowledge.
        request = FsshttpbCellRequest.Deserialize(new(request.ToByteArray()));
        var decoded = Assert.IsType<QueryChangesSubRequestData>(Assert.Single(request.SubRequests).Data);
        Assert.True(decoded.Knowledge!.RequiresFullResponse);
        Assert.Empty(decoded.Knowledge.Ranges);
        var result = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents, request, new Dictionary<string, string>(), TestActor.Value);
        Assert.False(Assert.Single(result.Response.SubResponses).Status);
        Assert.Equal(elements.Select(e => e.DataElementExtendedGuid).OrderBy(id => id.ToString()),
            result.Response.DataElementPackage!.DataElements.Select(e => e.DataElementExtendedGuid).OrderBy(id => id.ToString()));
    }

    [Fact]
    public void ExplicitAggregateLimitsRequirePairedAtomicAccounting()
    {
        var memory = Memory();
        memory.Require(durable: false, multipleInstances: false);
        Assert.True(memory.Capabilities.AtomicBudgets);
        var unsupported = new StorageProvider(new InMemoryStateStore(), new NoReads(), new StorageLimits());
        Assert.False(unsupported.Capabilities.AtomicBudgets);
        Assert.Throws<InvalidOperationException>(() => unsupported.Require(durable: false, multipleInstances: false));
    }

    [Fact]
    public async Task ConcurrentCreationCannotExceedDocumentCount()
    {
        var provider = Memory(new() { MaxDocuments = 1 });
        var service = new CellBridgeDocumentService(provider);
        async Task<bool> Create(int i)
        {
            try { return await service.CreateAsync($"/shared/{i}.docx", MinimalDocx.Create(), TestActor.Value) is not null; }
            catch (StorageQuotaExceededException) { return false; }
        }
        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(Create));
        Assert.Single(results, x => x);
        Assert.Single(await provider.State.ListAsync(0, 100));
    }

    [Fact]
    public async Task DeduplicationAndFailedContentWriteCannotConsumeExtraBudget()
    {
        var budget = new StorageBudget(new() { MaxStoredBytes = 3, MaxObjectBytes = 3 });
        var content = new InMemoryContentStore(budget);
        var handles = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => content.WriteAsync(new MemoryStream(new byte[] { 1, 2, 3 })).AsTask()));
        Assert.Single(handles.Distinct());
        Assert.Equal(3, budget.StoredBytes);
        await Assert.ThrowsAsync<StorageQuotaExceededException>(() => content.WriteAsync(new MemoryStream(new byte[] { 4 })).AsTask());
        await Assert.ThrowsAsync<StorageQuotaExceededException>(() => content.WriteAsync(new MemoryStream(new byte[] { 1, 2, 3, 4 })).AsTask());
        Assert.Equal(3, budget.StoredBytes);
    }

    [Fact]
    public async Task GraphQuotaRejectsSaveWithoutChangingStoredRevisionAndRetriesStillWork()
    {
        var provider = Memory(new() { MaxGraphElements = 30 });
        var service = new CellBridgeDocumentService(provider);
        var initial = (await service.CreateAsync("/shared/quota.docx", MinimalDocx.Create(), TestActor.Value))!;
        var first = StorageTests.Fixture("save-first");
        ((PutChangesSubRequestData)first.SubRequests.Single().Data!).ExpectedStorageIndex =
            StorageIds.Restore(initial.Partitions.Single(p => p.Kind == 0).StorageIndex!);
        var saved = await service.ExecuteAsync(initial.ResourceId, DocumentPartitionKind.FileContents, first, new Dictionary<string, string>(), TestActor.Value);
        Assert.False(Assert.Single(saved.Response.SubResponses).Status);
        var rejected = await service.ExecuteAsync(initial.ResourceId, DocumentPartitionKind.FileContents,
            StorageTests.Fixture("save-second"), new Dictionary<string, string>(), TestActor.Value);
        var error = Assert.Single(rejected.Response.SubResponses).Error!;
        Assert.Equal(ErrorType.Win32, error.Type);
        Assert.Equal(112UL, error.ErrorCode);
        Assert.Equal(saved.State, await provider.State.FindByResourceIdAsync(initial.ResourceId));
        Assert.Equal(saved.State, rejected.State);
        var retry = await service.ExecuteAsync(initial.ResourceId, DocumentPartitionKind.FileContents, first, new Dictionary<string, string>(), TestActor.Value);
        Assert.False(Assert.Single(retry.Response.SubResponses).Status);
        Assert.Equal(saved.State.StateVersion, retry.State.StateVersion);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CurrentKnowledgeAvoidsReadingAnyContentAndSurvivesBinaryDecoding(bool round)
    {
        var provider = Memory();
        var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/shared/known.docx", MinimalDocx.Create(), TestActor.Value))!;
        var metadata = state.Partitions.Single(p => p.Kind == 0).Elements.Select(e =>
            new DataElement((DataElementType)e.Type, StorageIds.Restore(e.Id), new(e.Serial.Guid, e.Serial.Value)));
        var query = new FsshttpbCellRequest();
        query.SubRequests.Add(new(RequestTypes.QueryChanges) { RequestId = 1,
            Data = new QueryChangesSubRequestData { IncludeStorageManifest = true, IncludeCellChanges = true,
                RoundKnowledgeToWholeCellChanges = round, Knowledge = ClientKnowledge.Deserialize(new(BinaryKnowledgeBuilder.FromElements(
                    metadata, ExGuid.Null, 0, mappingSerials: state.Partitions.Single(p => p.Kind == 0).Elements
                        .SelectMany(e => e.MappingSerials).Select(s => new SerialNumber(s.Guid, s.Value))))), MaxDataElements = 1 } });
        var withoutContent = new CellBridgeDocumentService(new(provider.State, new NoReads()));
        var result = await withoutContent.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents, query, new Dictionary<string, string>(), TestActor.Value);
        Assert.False(Assert.Single(result.Response.SubResponses).Status);
        Assert.Empty(result.Response.DataElementPackage!.DataElements);
        Assert.Empty(FsshttpbResponseInspector.Inspect(result.Response.ToByteArray()).Issues);
        Assert.Equal(state.StateVersion, result.State.StateVersion);
    }

    [PostgreSqlFact]
    public Task MetadataRetentionAndQuiescentCollectionPreserveCurrentContentAndLedger() =>
        WithDatabase(new() { MaxRetainedStateSnapshots = 2 }, async source =>
        {
            var limits = new StorageLimits { MaxRetainedStateSnapshots = 2 };
            var state = new PostgreSqlStateStore(source, limits);
            await state.InitializeAsync(); // Repeated initialization must be idempotent.
            var content = new PostgreSqlContentStore(source);
            var provider = new StorageProvider(state, content, limits);
            var document = (await new CellBridgeDocumentService(provider).CreateAsync("/shared/retention.docx", MinimalDocx.Create(), TestActor.Value))!;
            for (int i = 0; i < 10; i++)
                await state.TransitionAsync(document.ResourceId, (current, now) =>
                    new StateTransition<bool>(current with { ModifiedUtc = now }, true));
            var detached = await state.FindByResourceIdAsync(document.ResourceId);
            var orphan = await content.WriteAsync(new MemoryStream(new byte[] { 91, 92, 93 }));
            var maintenance = new StorageMaintenance(source);
            var dry = await maintenance.CollectOrphansAsync();
            Assert.Equal(2, dry.Snapshots);
            Assert.Equal(1, dry.OrphanObjects);
            Assert.Equal(3, dry.OrphanBytes);
            await Assert.ThrowsAsync<InvalidOperationException>(() => maintenance.CollectOrphansAsync(apply: true));
            var applied = await maintenance.CollectOrphansAsync(apply: true, quiescent: true);
            Assert.Equal(dry.StoredBytes - 3, applied.StoredBytes);
            Assert.Equal(System.Text.Json.JsonSerializer.Serialize(detached),
                System.Text.Json.JsonSerializer.Serialize(await state.FindByResourceIdAsync(document.ResourceId)));
            await using var read = await content.OpenReadAsync(document.Content);
            Assert.Equal(document.Content.Length, read.Length);
            await Assert.ThrowsAsync<StorageCorruptionException>(() => content.OpenReadAsync(orphan).AsTask());
            await using var total = source.CreateCommand("SELECT (SELECT stored_bytes FROM cellbridge_usage) = (SELECT COALESCE(SUM(length),0) FROM cellbridge_objects) + (SELECT COALESCE(SUM(octet_length(state_json)),0) FROM cellbridge_states)");
            Assert.Equal(true, await total.ExecuteScalarAsync());
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreviousKnowledgeReconstructsSavedFileAndRoundingReturnsWholeVisibleCell(bool round)
    {
        var provider = Memory();
        var service = new CellBridgeDocumentService(provider);
        var initial = (await service.CreateAsync("/shared/delta.docx", MinimalDocx.Create(), TestActor.Value))!;
        FsshttpbCellRequest Query(ClientKnowledge? knowledge) => new()
        { SubRequests = { new(RequestTypes.QueryChanges) { RequestId = 1, Data = new QueryChangesSubRequestData
            { IncludeStorageManifest = true, IncludeCellChanges = true, Knowledge = knowledge,
                RoundKnowledgeToWholeCellChanges = round } } } };
        var before = await service.ExecuteAsync(initial.ResourceId, DocumentPartitionKind.FileContents, Query(null), new Dictionary<string, string>(), TestActor.Value);
        var known = before.Response.DataElementPackage!.DataElements;
        var save = StorageTests.Fixture("save-first");
        ((PutChangesSubRequestData)save.SubRequests.Single().Data!).ExpectedStorageIndex =
            StorageIds.Restore(initial.Partitions.Single(p => p.Kind == 0).StorageIndex!);
        var saved = await service.ExecuteAsync(initial.ResourceId, DocumentPartitionKind.FileContents, save, new Dictionary<string, string>(), TestActor.Value);
        Assert.False(Assert.Single(saved.Response.SubResponses).Status);
        var delta = await service.ExecuteAsync(initial.ResourceId, DocumentPartitionKind.FileContents,
            Query(ClientKnowledge.FromElements(known)), new Dictionary<string, string>(), TestActor.Value);
        var incoming = delta.Response.DataElementPackage!.DataElements;
        if (round) Assert.Equal(saved.State.Partitions.Single(p => p.Kind == 0).Elements.Length, incoming.Count);
        else Assert.DoesNotContain(incoming, e => known.Any(k => k.SerialNumber.Equals(e.SerialNumber)));
        var merged = known.Concat(incoming).GroupBy(e => e.DataElementExtendedGuid).Select(g => g.Last());
        var index = Assert.IsType<QueryChangesSubResponseData>(delta.Response.SubResponses[0].Data).StorageIndexExtendedGuid;
        var graph = PartitionGraphSnapshot.Create(merged, index);
        var restored = await StoredDocument.RestoreAsync(saved.State, provider.Content);
        Assert.Equal(restored.Content, graph.Materialize());
        var advertised = ClientKnowledge.Deserialize(new(Assert.IsType<QueryChangesSubResponseData>(delta.Response.SubResponses[0].Data).KnowledgeBytes!));
        Assert.All(merged, e => Assert.True(advertised.Contains(e.SerialNumber)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FilteredKnowledgeOnlyIncludesWithheldElementsWhenRequested(bool includeFiltered)
    {
        var provider = Memory();
        var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/shared/filter.docx", MinimalDocx.Create(), TestActor.Value))!;
        var request = new FsshttpbCellRequest();
        request.SubRequests.Add(new(RequestTypes.QueryChanges) { RequestId = 1,
            Data = new QueryChangesSubRequestData { IncludeFilteredOutDataElementsInKnowledge = includeFiltered } });
        var result = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents, request, new Dictionary<string, string>(), TestActor.Value);
        var data = Assert.IsType<QueryChangesSubResponseData>(result.Response.SubResponses[0].Data);
        var knowledge = ClientKnowledge.Deserialize(new(data.KnowledgeBytes!));
        foreach (var element in state.Partitions.Single(p => p.Kind == 0).Elements)
        {
            bool withheld = element.Type is (uint)DataElementType.StorageManifestDataElementData or (uint)DataElementType.CellManifestDataElementData;
            Assert.Equal(!withheld || includeFiltered, knowledge.Contains(new(element.Serial.Guid, element.Serial.Value)));
        }
    }

    [Fact]
    public async Task ReceiptLimitKeepsCurrentRetryAndRejectsNewSaveWithoutAdvancingRevision()
    {
        var provider = Memory(new() { MaxSaveReceipts = 1 });
        var service = new CellBridgeDocumentService(provider);
        var initial = (await service.CreateAsync("/shared/receipts.docx", MinimalDocx.Create(), TestActor.Value))!;
        var first = StorageTests.Fixture("save-first");
        ((PutChangesSubRequestData)first.SubRequests.Single().Data!).ExpectedStorageIndex =
            StorageIds.Restore(initial.Partitions.Single(p => p.Kind == 0).StorageIndex!);
        var saved = await service.ExecuteAsync(initial.ResourceId, DocumentPartitionKind.FileContents, first, new Dictionary<string, string>(), TestActor.Value);
        var retry = await service.ExecuteAsync(initial.ResourceId, DocumentPartitionKind.FileContents, first, new Dictionary<string, string>(), TestActor.Value);
        Assert.False(retry.Response.SubResponses[0].Status);
        Assert.Equal(saved.State.StateVersion, retry.State.StateVersion);
        var rejected = await service.ExecuteAsync(initial.ResourceId, DocumentPartitionKind.FileContents, StorageTests.Fixture("save-second"), new Dictionary<string, string>(), TestActor.Value);
        Assert.Equal(112UL, rejected.Response.SubResponses[0].Error!.ErrorCode);
        Assert.Equal(saved.State.StateVersion, rejected.State.StateVersion);
        Assert.Equal(saved.State.Content, rejected.State.Content);
    }

    [PostgreSqlFact]
    public Task FilesystemReservationsAreDeduplicatedAndQuiescentOrphansAreReclaimed() =>
        WithDatabase(new() { MaxStoredBytes = 3 }, async source =>
        {
            var root = Path.Combine(Path.GetTempPath(), "cellbridge-budget-" + Guid.NewGuid().ToString("N"));
            try
            {
                var first = new PostgreSqlFileSystemContentStore(source, root);
                var second = new PostgreSqlFileSystemContentStore(source, root);
                var handles = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => (i % 2 == 0 ? first : second)
                    .WriteAsync(new MemoryStream(new byte[] { 1, 2, 3 })).AsTask()));
                Assert.Single(handles.Distinct());
                await Assert.ThrowsAsync<StorageQuotaExceededException>(() => first.WriteAsync(new MemoryStream(new byte[] { 4 })).AsTask());
                var report = await new StorageMaintenance(source).CollectOrphansAsync(apply: true, quiescent: true, fileSystemRoot: root);
                Assert.Equal(3, report.OrphanBytes);
                Assert.Equal(0, report.StoredBytes);
                Assert.Empty(Directory.EnumerateFiles(root, "*.blob"));
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        });

    [PostgreSqlFact]
    public Task PostgreSqlDocumentCountAdmissionIsAtomicAcrossProviders() =>
        WithDatabase(new() { MaxDocuments = 1 }, async source =>
        {
            var limits = new StorageLimits { MaxDocuments = 1 };
            var providers = Enumerable.Range(0, 2).Select(_ => new StorageProvider(
                new PostgreSqlStateStore(source, limits), new PostgreSqlContentStore(source), limits)).ToArray();
            async Task<bool> Create(int i)
            {
                try { return await new CellBridgeDocumentService(providers[i % 2]).CreateAsync($"/shared/race{i}.docx", MinimalDocx.Create(), TestActor.Value) is not null; }
                catch (StorageQuotaExceededException) { return false; }
            }
            Assert.Single(await Task.WhenAll(Enumerable.Range(0, 8).Select(Create)), x => x);
            Assert.Single(await providers[0].State.ListAsync(0, 100));
            await using var count = source.CreateCommand("SELECT document_count FROM cellbridge_usage");
            Assert.Equal(1L, await count.ExecuteScalarAsync());
        });

    [Fact]
    public async Task DocumentByteAdmissionPrecedesContentStaging()
    {
        var provider = Memory(new() { MaxDocumentBytes = 1 });
        await Assert.ThrowsAsync<StorageQuotaExceededException>(() =>
            new CellBridgeDocumentService(provider).CreateAsync("/shared/too-large.docx", MinimalDocx.Create(), TestActor.Value).AsTask());
        Assert.Equal(0, ((InMemoryContentStore)provider.Content).Budget.StoredBytes);
    }

    [PostgreSqlFact]
    public Task PostgreSqlLedgerRejectsCrossInstanceLastByteRaceAndCountsDeduplicatedObjectsOnce() =>
        WithDatabase(new() { MaxStoredBytes = 3 }, async source =>
        {
            var first = new PostgreSqlContentStore(source);
            var second = new PostgreSqlContentStore(source);
            async Task<bool> Write(IContentStore content, byte value)
            {
                try { await content.WriteAsync(new MemoryStream(new byte[] { value, value, value })); return true; }
                catch (StorageQuotaExceededException) { return false; }
            }
            var results = await Task.WhenAll(Write(first, 1), Write(second, 2));
            Assert.Single(results, x => x);
            byte accepted = results[0] ? (byte)1 : (byte)2;
            await second.WriteAsync(new MemoryStream(new byte[] { accepted, accepted, accepted }));
            await using var count = source.CreateCommand("SELECT stored_bytes FROM cellbridge_usage");
            Assert.Equal(3L, await count.ExecuteScalarAsync());
        });

    internal static async Task WithDatabase(StorageLimits limits, Func<NpgsqlDataSource, Task> test)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__cellbridge")!;
        await using var admin = NpgsqlDataSource.Create(connectionString);
        string database = "cellbridge_quota_" + Guid.NewGuid().ToString("N");
        await using (var create = admin.CreateCommand($"CREATE DATABASE {database}")) await create.ExecuteNonQueryAsync();
        try
        {
            var builder = new NpgsqlConnectionStringBuilder(connectionString) { Database = database };
            await using var source = NpgsqlDataSource.Create(builder.ConnectionString);
            await new PostgreSqlStateStore(source, limits).InitializeAsync();
            await test(source);
        }
        finally
        {
            await using var drop = admin.CreateCommand($"DROP DATABASE {database} WITH (FORCE)");
            await drop.ExecuteNonQueryAsync();
        }
    }

    private sealed class NoReads : IContentStore
    {
        public bool Durable => false;
        public bool Shared => false;
        public ValueTask<ContentHandle> WriteAsync(Stream source, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Unexpected write.");
        public ValueTask<Stream> OpenReadAsync(ContentHandle handle, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Unexpected read.");
    }
}
