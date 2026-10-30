using System.Collections.Immutable;
using CellBridge.AspNetCore;
using CellBridge.FssHttpB;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using CellBridge.Storage.PostgreSql;
using CellBridge.TestFixtures;
using Npgsql;

namespace CellBridge.Storage.Tests;

[Collection("PostgreSQL")]
public class BudgetAndQueryTests
{
    private static FsshttpbCellRequest Query(ClientKnowledge? knowledge = null) => new()
    { SubRequests = { new(RequestTypes.QueryChanges) { RequestId = 1, Data = new QueryChangesSubRequestData
        { IncludeStorageManifest = true, IncludeCellChanges = true, Knowledge = knowledge } } } };

    private static async Task<FsshttpbCellRequest> NewSave(StorageProvider provider, DocumentHead state, bool nullSerials = false, ulong? mappingMaximum = null)
    {
        var read = (await provider.Documents.ReadPartitionAsync(state.ResourceId, new(0) { IncludeCatalog = true }))!;
        var partition = read.Partition;
        var guid = Guid.NewGuid();
        var identity = new StorageManifestBuilder.StableIdentity(new(1, guid), new(2, guid), new(3, guid),
            new(4, guid), new(5, guid), new(6, guid), new(7, guid),
            new(StorageIds.Restore(partition.Identity.CellLong), StorageIds.Restore(partition.Identity.CellShort)),
            partition.Identity.SerialGuid);
        var package = FileContentPartitionBuilder.BuildQueryChangesResponse(1, MinimalDocx.Create(), identity, 1).DataElementPackage!;
        foreach (var element in package.DataElements)
            element.SerialNumber = nullSerials ? SerialNumber.Null : new(read.Elements[0].Serial.Guid, read.Elements[0].Serial.Value);
        ulong max = mappingMaximum ?? checked(partition.Knowledge + 100);
        {
            var index = package.DataElements.Single(e => e.DataElementType == DataElementType.StorageIndexDataElementData);
            package.DataElements[package.DataElements.IndexOf(index)] = StorageManifestBuilder.BuildStorageIndexDataElement(
                identity.ObjectDataBlobGuid, index.SerialNumber, identity.StorageManifestGuid, new(identity.SerialGuid, max),
                identity.CellId, identity.CellManifestGuid, new(identity.SerialGuid, max - 1), identity.RevisionId,
                identity.RevisionManifestGuid, new(identity.SerialGuid, max - 2));
        }
        var expected = (await StorageTestDocuments.GraphAsync(new(provider), state)).StorageIndexes.Single();
        package.DataElements.Add(expected);
        return new() { DataElementPackage = package, SubRequests = { new(RequestTypes.PutChanges) { RequestId = 2,
            Data = new PutChangesSubRequestData { StorageIndex = identity.ObjectDataBlobGuid,
                ExpectedStorageIndex = expected.DataElementExtendedGuid, Flags = 1 } } } };
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
        var initialRead = (await provider.Documents.ReadPartitionAsync(initial.ResourceId, new(0) { IncludeCatalog = true }))!;
        ulong max = initialRead.Partition.Knowledge + 1000;
        var request = await NewSave(provider, initial, nullSerials, max);
        var saved = await service.ExecuteAsync(initial.ResourceId, DocumentPartitionKind.FileContents, request, new Dictionary<string, string>(), TestActor.Value);
        Assert.False(Assert.Single(saved.Response.SubResponses).Status);
        var additions = (await provider.Documents.ReadPartitionAsync(initial.ResourceId, new(0) { IncludeCatalog = true }))!.Elements
            .Where(e => !initialRead.Elements.Any(old => old.Id == e.Id)).ToArray();
        Assert.Equal(4, additions.Length);
        Assert.All(additions, e => Assert.True(e.Serial.Value > max));
        Assert.Equal(4, additions.Select(e => e.Serial).Distinct().Count());
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
        Assert.Equal(request.DataElementPackage!.DataElements.Count, assignments.Count);
        Assert.All(additions, e => Assert.Equal(new SerialNumber(e.Serial.Guid, e.Serial.Value), assignments[StorageIds.Restore(e.Id)]));
        var restarted = new CellBridgeDocumentService(provider);
        var delta = await restarted.ExecuteAsync(initial.ResourceId, DocumentPartitionKind.FileContents,
            Query(ClientKnowledge.FromElements(known)), new Dictionary<string, string>(), TestActor.Value);
        Assert.All(additions, e => Assert.Contains(delta.Response.DataElementPackage!.DataElements, d => d.DataElementExtendedGuid.Equals(StorageIds.Restore(e.Id))));
        var latest = Assert.IsType<QueryChangesSubResponseData>(delta.Response.SubResponses[0].Data).StorageIndexExtendedGuid;
        Assert.Equal(await StorageTestDocuments.PackageAsync(provider, saved.State.Package), PartitionGraphSnapshot.Create(known.Concat(delta.Response.DataElementPackage!.DataElements), latest).Materialize());
        var retry = await restarted.ExecuteAsync(initial.ResourceId, DocumentPartitionKind.FileContents, request, new Dictionary<string, string>(), TestActor.Value);
        Assert.False(Assert.Single(retry.Response.SubResponses).Status);
        Assert.Equal(saved.State.StateVersion, retry.State.StateVersion);
        var second = await restarted.ExecuteAsync(initial.ResourceId, DocumentPartitionKind.FileContents,
            await NewSave(provider, saved.State, true), new Dictionary<string, string>(), TestActor.Value);
        Assert.False(Assert.Single(second.Response.SubResponses).Status);
        Assert.True((await provider.Documents.ReadPartitionAsync(initial.ResourceId, new(0)))!.Partition.Knowledge > max);
    }

    [Fact]
    public async Task MissingMappingKnowledgeReturnsIndexEvenWhenItsElementSerialIsKnown()
    {
        var provider = Memory();
        var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/shared/mappings.docx", MinimalDocx.Create(), TestActor.Value))!;
        var read = (await provider.Documents.ReadPartitionAsync(state.ResourceId, new(0) { IncludeCatalog = true }))!;
        var partition = read.Partition;
        var metadata = read.Elements.Select(e => new DataElement((DataElementType)e.Type, StorageIds.Restore(e.Id), new(e.Serial.Guid, e.Serial.Value)));
        var delta = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents,
            Query(ClientKnowledge.FromElements(metadata)), new Dictionary<string, string>(), TestActor.Value);
        Assert.Equal(DataElementType.StorageIndexDataElementData, Assert.Single(delta.Response.DataElementPackage!.DataElements).DataElementType);
        var mappingOnly = ClientKnowledge.Deserialize(new(BinaryKnowledgeBuilder.FromElements([], ExGuid.Null, 0,
            mappingSerials: (await provider.Documents.ReadMappingsAsync(new(state.ResourceId, state.LifecycleGeneration, 0), partition.PublicationSequence)).Select(mapping => new SerialNumber(mapping.Serial.Guid, mapping.Serial.Value)))));
        var full = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents, Query(mappingOnly), new Dictionary<string, string>(), TestActor.Value);
        Assert.Equal(read.Elements.Length + 1, full.Response.DataElementPackage!.DataElements.Count);
    }

    [Fact]
    public async Task SerialExhaustionRejectsSaveWithoutPublishing()
    {
        var provider = Memory();
        var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/shared/exhausted.docx", MinimalDocx.Create(), TestActor.Value))!;
        var result = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents,
            await NewSave(provider, state, true, ulong.MaxValue), new Dictionary<string, string>(), TestActor.Value);
        Assert.True(Assert.Single(result.Response.SubResponses).Status);
        Assert.Equal(state.StateVersion, (await provider.Documents.FindAsync(new DocumentIdentity.Resource(state.ResourceId)))!.StateVersion);
    }

    [Fact]
    public async Task ReusingMappingSerialForAnotherTargetRejectsWithoutPublishing()
    {
        var provider = Memory();
        var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/shared/mapping-conflict.docx", MinimalDocx.Create(), TestActor.Value))!;
        // Seed mappings have values 1, 2 and 3 under the server serial GUID.
        var result = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents,
            await NewSave(provider, state, true, 3), new Dictionary<string, string>(), TestActor.Value);
        Assert.True(Assert.Single(result.Response.SubResponses).Status);
        Assert.Contains("mapping serial", Assert.Single(result.Response.SubResponses).Error!.ErrorMessage);
        Assert.Equal(state.StateVersion, (await provider.Documents.FindAsync(new DocumentIdentity.Resource(state.ResourceId)))!.StateVersion);
    }

    [Fact]
    public async Task AliasedSerialsAreRetransmittedEvenWhenClientKnowledgeContainsThem()
    {
        var provider = Memory();
        var service = new CellBridgeDocumentService(provider);
        var seed = await IndexedDocumentPublicationBuilder.CreateAsync(Guid.NewGuid(), "/shared/aliases.docx", MinimalDocx.Create(),
            DocumentSecurity.Create(TestActor.Value.Identity), provider.Content, provider.Limits);
        var part = seed.Partitions.Single(partition => partition.After.Kind == 0);
        var alias = part.Mappings.First().Serial;
        seed = seed with { Partitions = seed.Partitions.Select(partition => partition.After.Kind != 0 ? partition : partition with
            { AddedElements = partition.AddedElements.Select(element => element.Type == (uint)DataElementType.StorageManifestDataElementData
                ? element with { Serial = alias } : element).ToImmutableArray() }).ToImmutableArray() };
        var state = (await provider.Documents.CreateAsync(seed)).Current!;
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
            await Expected(service, state, request);
            var saved = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents, request, new Dictionary<string, string>(), TestActor.Value);
            Assert.False(Assert.Single(saved.Response.SubResponses).Status);
            var graph = await StorageTestDocuments.GraphAsync(service, saved.State);
            var documentBytes = await StorageTestDocuments.PackageAsync(provider, saved.State.Package);
            Assert.True(graph.AnalyzeRetention().Complete);
            var compact = graph.Compact();
            Assert.True(compact.ElementCount <= graph.ElementCount);
            Assert.Equal(documentBytes, compact.Materialize());
            if (previous is not null)
            {
                var protectedGraph = PartitionGraphSnapshot.Create(graph.Elements.Concat(previous.Elements), graph.StorageIndex).Compact([previous.StorageIndex]);
                Assert.Equal(documentBytes, protectedGraph.Materialize());
                Assert.All(previous.Compact().ElementMetadata, e => Assert.Contains(protectedGraph.ElementMetadata,
                    p => p.DataElementExtendedGuid.Equals(e.DataElementExtendedGuid)));
            }
            previous = graph;
            state = saved.State;
        }
    }

    private static StorageProvider Memory(StorageLimits? limits = null) =>
        StorageTestDocuments.Memory(limits: limits);

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
    public void PerDocumentLimitsAreValidatedAtConfiguration()
    {
        Memory().Require(durable: false, multipleInstances: false);
        Assert.Throws<ArgumentOutOfRangeException>(() => Memory(new() { MaxGraphElements = 0 }));
    }

    [Fact]
    public async Task DeduplicationReusesObjectsAndObjectLimitRejectsOversizedWrites()
    {
        var content = new InMemoryContentStore(3);
        var handles = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => content.WriteAsync(new MemoryStream(new byte[] { 1, 2, 3 })).AsTask()));
        Assert.Single(handles.Distinct());
        Assert.Equal(new byte[] { 1, 2, 3 }, await content.ReadVerifiedAsync(handles[0]));
        await content.WriteAsync(new MemoryStream(new byte[] { 4 }));
        await Assert.ThrowsAsync<StorageQuotaExceededException>(() => content.WriteAsync(new MemoryStream(new byte[] { 1, 2, 3, 4 })).AsTask());
        Assert.Equal(new byte[] { 1, 2, 3 }, await content.ReadVerifiedAsync(handles[0]));
    }

    [Fact]
    public async Task GraphQuotaRejectsSaveWithoutChangingStoredRevisionAndRetriesStillWork()
    {
        var provider = Memory(new() { MaxGraphElements = 8 });
        var service = new CellBridgeDocumentService(provider);
        var initial = (await service.CreateAsync("/shared/quota.docx", MinimalDocx.Create(), TestActor.Value))!;
        var first = await NewSave(provider, initial);
        var saved = await service.ExecuteAsync(initial.ResourceId, DocumentPartitionKind.FileContents, first, new Dictionary<string, string>(), TestActor.Value);
        Assert.False(Assert.Single(saved.Response.SubResponses).Status);
        var rejected = await service.ExecuteAsync(initial.ResourceId, DocumentPartitionKind.FileContents,
            await NewSave(provider, saved.State), new Dictionary<string, string>(), TestActor.Value);
        var error = Assert.Single(rejected.Response.SubResponses).Error!;
        Assert.Equal(ErrorType.Win32, error.Type);
        Assert.Equal(112UL, error.ErrorCode);
        Assert.Equal(saved.State, await provider.Documents.FindAsync(new DocumentIdentity.Resource(initial.ResourceId)));
        Assert.Equal(saved.State, rejected.State);
        var retry = await service.ExecuteAsync(initial.ResourceId, DocumentPartitionKind.FileContents, first, new Dictionary<string, string>(), TestActor.Value);
        Assert.False(Assert.Single(retry.Response.SubResponses).Status);
        Assert.Equal(saved.State.StateVersion, retry.State.StateVersion);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CurrentKnowledgeReadsOnlyScopeMetadataAndSurvivesBinaryDecoding(bool round)
    {
        var provider = Memory();
        var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/shared/known.docx", MinimalDocx.Create(), TestActor.Value))!;
        var full = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents, Query(), new Dictionary<string, string>(), TestActor.Value);
        var query = Query(ClientKnowledge.FromElements(full.Response.DataElementPackage!.DataElements));
        ((QueryChangesSubRequestData)query.SubRequests[0].Data!).RoundKnowledgeToWholeCellChanges = round;
        ((QueryChangesSubRequestData)query.SubRequests[0].Data!).MaxDataElements = 1;
        var catalog = (await provider.Documents.ReadPartitionAsync(state.ResourceId, new(0) { IncludeCatalog = true }))!;
        var scopeContent = new ScopeMetadataReads(provider.Content, catalog.Elements.Where(element => element.Type == (uint)DataElementType.StorageManifestDataElementData)
            .Select(element => element.Payload).ToHashSet());
        var withoutContent = new CellBridgeDocumentService(new(provider.Documents, scopeContent));
        var result = await withoutContent.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents, query, new Dictionary<string, string>(), TestActor.Value);
        Assert.False(Assert.Single(result.Response.SubResponses).Status);
        Assert.Empty(result.Response.DataElementPackage!.DataElements);
        Assert.Empty(FsshttpbResponseInspector.Inspect(result.Response.ToByteArray()).Issues);
        Assert.Equal(state.StateVersion, result.State.StateVersion);
        Assert.Equal(1, scopeContent.Reads);
    }

    [PostgreSqlFact]
    public Task QuiescentCollectionPreservesNormalizedCurrentAndHistoricalContent() =>
        WithDatabase(new(), async source =>
        {
            IDocumentRepository state = new PostgreSqlStateStore(source); var content = new PostgreSqlContentStore(source);
            var provider = new StorageProvider(state, content); var service = new CellBridgeDocumentService(provider);
            var document = (await service.CreateAsync("/shared/retention.docx", MinimalDocx.Create(), TestActor.Value))!;
            var locks = new SharedDocumentLocks(service);
            Assert.True((await locks.ApplyAsync(document.ResourceId, HostLockOperation.Acquire, "host", TestActor.Value)).Success);
            for (var i = 0; i < 3; i++) { using var input = new MemoryStream(MinimalDocx.Create("revision " + i)); await service.SaveWholeFileAsync(document.ResourceId, input, "revision-" + i, TestActor.Value, "host"); }
            var before = (await state.ExportDocumentAsync(document.ResourceId))!;
            var orphan = await content.WriteAsync(new MemoryStream(new byte[] { 91, 92, 93 })); var maintenance = new StorageMaintenance(source);
            var dry = await maintenance.CollectOrphansAsync(); Assert.Equal(0, dry.Snapshots); Assert.Equal(1, dry.OrphanObjects); Assert.Equal(3, dry.OrphanBytes);
            await Assert.ThrowsAsync<InvalidOperationException>(() => maintenance.CollectOrphansAsync(apply: true));
            var applied = await maintenance.CollectOrphansAsync(apply: true, quiescent: true); Assert.Equal(dry.StoredBytes - 3, applied.StoredBytes);
            Assert.Equal(PortabilityTests.CanonicalSnapshots([before]), PortabilityTests.CanonicalSnapshots([(await state.ExportDocumentAsync(document.ResourceId))!]));
            foreach (var revision in before.ApplicationRevisions) Assert.Equal(revision.Package.Length, (await StorageTestDocuments.PackageAsync(provider, revision.Package)).LongLength);
            await Assert.ThrowsAsync<StorageCorruptionException>(() => content.OpenReadAsync(orphan).AsTask());
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
        await Expected(service, initial, save);
        var saved = await service.ExecuteAsync(initial.ResourceId, DocumentPartitionKind.FileContents, save, new Dictionary<string, string>(), TestActor.Value);
        Assert.False(Assert.Single(saved.Response.SubResponses).Status);
        var delta = await service.ExecuteAsync(initial.ResourceId, DocumentPartitionKind.FileContents,
            Query(ClientKnowledge.FromElements(known)), new Dictionary<string, string>(), TestActor.Value);
        var incoming = delta.Response.DataElementPackage!.DataElements;
        if (round) Assert.Equal((await provider.Documents.ReadPartitionAsync(initial.ResourceId, new(0) { IncludeCatalog = true }))!.Elements.Length + 1, incoming.Count);
        else Assert.DoesNotContain(incoming, e => known.Any(k => k.SerialNumber.Equals(e.SerialNumber)));
        var merged = known.Concat(incoming).GroupBy(e => e.DataElementExtendedGuid).Select(g => g.Last());
        var index = Assert.IsType<QueryChangesSubResponseData>(delta.Response.SubResponses[0].Data).StorageIndexExtendedGuid;
        var graph = PartitionGraphSnapshot.Create(merged, index);
        Assert.Equal(await StorageTestDocuments.PackageAsync(provider, saved.State.Package), graph.Materialize());
        var advertised = ClientKnowledge.Deserialize(new(Assert.IsType<QueryChangesSubResponseData>(delta.Response.SubResponses[0].Data).KnowledgeBytes!));
        var visible = await service.ExecuteAsync(initial.ResourceId, DocumentPartitionKind.FileContents, Query(null), new Dictionary<string, string>(), TestActor.Value);
        Assert.All(visible.Response.DataElementPackage!.DataElements, element => Assert.True(advertised.Contains(element.SerialNumber)));
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
        foreach (var element in (await provider.Documents.ReadPartitionAsync(state.ResourceId, new(0) { IncludeCatalog = true }))!.Elements)
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
        var first = await NewSave(provider, initial);
        var saved = await service.ExecuteAsync(initial.ResourceId, DocumentPartitionKind.FileContents, first, new Dictionary<string, string>(), TestActor.Value);
        var retry = await service.ExecuteAsync(initial.ResourceId, DocumentPartitionKind.FileContents, first, new Dictionary<string, string>(), TestActor.Value);
        Assert.False(retry.Response.SubResponses[0].Status);
        Assert.Equal(saved.State.StateVersion, retry.State.StateVersion);
        var rejected = await service.ExecuteAsync(initial.ResourceId, DocumentPartitionKind.FileContents, await NewSave(provider, saved.State), new Dictionary<string, string>(), TestActor.Value);
        Assert.Equal(112UL, rejected.Response.SubResponses[0].Error!.ErrorCode);
        Assert.Equal(saved.State.StateVersion, rejected.State.StateVersion);
        Assert.Equal(saved.State.Package, rejected.State.Package);
    }

    [PostgreSqlLinuxFact]
    public Task FilesystemReservationsAreDeduplicatedAndQuiescentOrphansAreReclaimed() =>
        WithDatabase(new(), async source =>
        {
            var root = Path.Combine(Path.GetTempPath(), "cellbridge-budget-" + Guid.NewGuid().ToString("N"));
            try
            {
                var first = new PostgreSqlFileSystemContentStore(source, root);
                var second = new PostgreSqlFileSystemContentStore(source, root);
                var handles = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => (i % 2 == 0 ? first : second)
                    .WriteAsync(new MemoryStream(new byte[] { 1, 2, 3 })).AsTask()));
                Assert.Single(handles.Distinct());
                var report = await new StorageMaintenance(source).CollectOrphansAsync(apply: true, quiescent: true, fileSystemRoot: root);
                Assert.Equal(3, report.OrphanBytes);
                Assert.Equal(0, report.StoredBytes);
                Assert.Empty(Directory.EnumerateFiles(root, "*.blob"));
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        });

    [Fact]
    public async Task DocumentByteAdmissionPrecedesContentStaging()
    {
        var provider = Memory(new() { MaxDocumentBytes = 1 });
        await Assert.ThrowsAsync<StorageQuotaExceededException>(() =>
            new CellBridgeDocumentService(provider).CreateAsync("/shared/too-large.docx", MinimalDocx.Create(), TestActor.Value).AsTask());
    }

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

    private static async Task Expected(CellBridgeDocumentService service, DocumentHead head, FsshttpbCellRequest request)
    {
        var expected = (await StorageTestDocuments.GraphAsync(service, head)).StorageIndexes.Single();
        ((PutChangesSubRequestData)request.SubRequests.Single().Data!).ExpectedStorageIndex = expected.DataElementExtendedGuid;
        request.DataElementPackage!.DataElements.Add(expected);
    }

    private sealed class ScopeMetadataReads(IContentStore inner, HashSet<ContentHandle> allowed) : IContentStore
    {
        public int Reads { get; private set; }
        public bool Durable => inner.Durable;
        public bool Shared => inner.Shared;
        public ValueTask<ContentHandle> WriteAsync(Stream source, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Unexpected write.");
        public ValueTask<Stream> OpenReadAsync(ContentHandle handle, CancellationToken cancellationToken = default)
        {
            Assert.Contains(handle, allowed);
            Reads++;
            return inner.OpenReadAsync(handle, cancellationToken);
        }
    }


}
