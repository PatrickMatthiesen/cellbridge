using System.Collections.Immutable;
using CellBridge.AspNetCore;
using CellBridge.FssHttpB;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using CellBridge.Web;

namespace CellBridge.Storage.Tests;

public sealed class MixedPartitionQueryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AbsentTargetInheritsSoapDefaultAfterAnExplicitFileQuery(bool buffered)
    {
        var provider = Memory();
        var state = (await new CellBridgeDocumentService(provider).CreateAsync("/default.docx", MinimalDocx.Create(), TestActor.Value))!;
        var inherit = Query(2, StoredDocument.MetadataPartitionId); inherit.TargetPartitionId = null;
        var result = await Run(provider, state, new() { SubRequests = { Query(1, Guid.Empty), inherit } }, buffered);
        Assert.All(result.SubResponses, s => Assert.False(s.Status));
        Assert.Equal(new ExGuid(1, state.Partitions[1].Identity.SerialGuid),
            Assert.IsType<QueryChangesSubResponseData>(result.SubResponses[1].Data).StorageIndexExtendedGuid);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GeneratedEditorQueriesHonorInclusionRoundingAndUnsupportedVersionControls(bool buffered)
    {
        var provider = Memory();
        var state = (await new CellBridgeDocumentService(provider).CreateAsync("/controls.docx", MinimalDocx.Create(), TestActor.Value))!;
        var first = await Run(provider, state, new() { SubRequests = { Query(1, StoredDocument.EditorsTablePartitionId) } }, buffered);
        var elements = first.DataElementPackage!.DataElements;
        foreach (bool round in new[] { false, true })
        {
            var query = Query(2, StoredDocument.EditorsTablePartitionId);
            var controls = Assert.IsType<QueryChangesSubRequestData>(query.Data);
            controls.IncludeStorageManifest = false; controls.IncludeCellChanges = false;
            controls.RoundKnowledgeToWholeCellChanges = round;
            controls.Knowledge = ClientKnowledge.FromElements(elements.Take(1));
            var filtered = await Run(provider, state, new() { SubRequests = { query } }, buffered);
            Assert.False(Assert.Single(filtered.SubResponses).Status);
            Assert.Equal(round ? 4 : 3, filtered.DataElementPackage!.DataElements.Count);
            var data = Assert.IsType<QueryChangesSubResponseData>(filtered.SubResponses[0].Data);
            Assert.Equal(0UL, data.Waterline);
            var knowledge = ClientKnowledge.Deserialize(new(data.KnowledgeBytes!));
            Assert.All(elements.Where(e => e.DataElementType is DataElementType.StorageManifestDataElementData or DataElementType.CellManifestDataElementData),
                e => Assert.False(knowledge.Contains(e.SerialNumber)));
        }
        foreach (var target in new[] { StoredDocument.MetadataPartitionId, StoredDocument.EditorsTablePartitionId })
        {
            var unsupported = Query(3, target);
            Assert.IsType<QueryChangesSubRequestData>(unsupported.Data).Waterline = 1;
            var result = await Run(provider, state, new() { SubRequests = { unsupported, Query(4, Guid.Empty) } }, buffered);
            Assert.True(result.SubResponses[0].Status); Assert.False(result.SubResponses[1].Status);
            Assert.NotEmpty(result.DataElementPackage!.DataElements);
        }
    }

    [Fact]
    public async Task MixedAggregateBudgetFailurePreservesEarlierMetadataAndEditorKnowledge()
    {
        var provider = Memory();
        var state = (await new CellBridgeDocumentService(provider).CreateAsync("/aggregate.docx", MinimalDocx.Create(), TestActor.Value))!;
        var request = new FsshttpbCellRequest { SubRequests = { Query(1, StoredDocument.MetadataPartitionId), Query(2, StoredDocument.EditorsTablePartitionId) } };
        var full = await Run(provider, state, request, true);
        long budget = full.DataElementPackage!.DataElements.Sum(e => { var writer = new BinaryWriterEx(); e.Serialize(writer); return (long)writer.Length; });
        request.SubRequests.Add(Query(3, Guid.Empty));
        var doc = await StoredDocument.RestoreAsync(state, provider.Content);
        var result = CellBinaryRequestExecutor.Execute(doc, doc.MetadataPartition, request, DocumentAccess.Read,
            maxResponseBytes: budget, partitionEditorsQueryChanges: (p, id) => EditorsTablePartitionBuilder.BuildSharePointV13QueryChangesResponse(id, [],
                p.ProtocolIdentity.CellId, p.ProtocolIdentity.SerialGuid, p.KnowledgeSequence));
        Assert.False(result.SubResponses[0].Status); Assert.False(result.SubResponses[1].Status); Assert.True(result.SubResponses[2].Status);
        Assert.Equal(full.DataElementPackage.DataElements.Select(e => e.DataElementExtendedGuid), result.DataElementPackage!.DataElements.Select(e => e.DataElementExtendedGuid));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MixedRepeatedQueriesKeepAllPackagesAndIsolateUnsupportedScopeAndBudget(bool buffered)
    {
        var provider = Memory();
        var state = (await new CellBridgeDocumentService(provider).CreateAsync("/mixed.docx", MinimalDocx.Create(), TestActor.Value))!;
        var request = new FsshttpbCellRequest();
        request.SubRequests.Add(Query(1, Guid.Empty));
        request.SubRequests.Add(Query(2, StoredDocument.MetadataPartitionId));
        request.SubRequests.Add(Query(3, StoredDocument.EditorsTablePartitionId));
        request.SubRequests.Add(Query(4, StoredDocument.MetadataPartitionId));
        request.SubRequests.Add(Query(5, StoredDocument.EditorsTablePartitionId));
        request.SubRequests.Add(Query(6, Guid.NewGuid()));
        request.SubRequests.Add(Query(7, StoredDocument.EditorsTablePartitionId, maximum: 1));
        request.SubRequests.Add(Query(8, StoredDocument.MetadataPartitionId,
            cell: new(new(1, Guid.NewGuid()), new(1, Guid.NewGuid()))));
        var response = await Run(provider, state, request, buffered);
        Assert.Equal(Enumerable.Range(1, 8).Select(i => (ulong)i), response.SubResponses.Select(s => s.RequestId));
        Assert.All(response.SubResponses.Take(5), s => Assert.False(s.Status));
        Assert.All(response.SubResponses.Skip(5), s => Assert.True(s.Status));
        var elements = response.DataElementPackage!.DataElements;
        Assert.Equal(state.Partitions[0].Elements.Length + 7, elements.Count);
        Assert.Equal(elements.Count, elements.Select(e => e.DataElementExtendedGuid).Distinct().Count());
        var indexes = response.SubResponses.Take(5).Select(s => Assert.IsType<QueryChangesSubResponseData>(s.Data).StorageIndexExtendedGuid).ToArray();
        Assert.Equal(3, indexes.Distinct().Count());
        Assert.Equal(indexes[1], indexes[3]); Assert.Equal(indexes[2], indexes[4]);
        Assert.All(indexes, id => Assert.Contains(elements, e => e.DataElementExtendedGuid.Equals(id)));
        var wire = FsshttpbResponse.Deserialize(new BinaryReaderEx(response.ToByteArray(FsshttpbSerializationProfile.SharePoint13_11)));
        Assert.Equal(8, wire.SubResponses.Count);
        Assert.Equal(elements.Count, wire.DataElementPackage!.DataElements.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NonFileKnowledgeFiltersOnlyItsOwnQueryAndPreservesOtherPartitions(bool buffered)
    {
        var provider = Memory();
        var state = (await new CellBridgeDocumentService(provider).CreateAsync("/knowledge.docx", MinimalDocx.Create(), TestActor.Value))!;
        foreach (var target in new[] { StoredDocument.MetadataPartitionId, StoredDocument.EditorsTablePartitionId })
        {
            var first = await Run(provider, state, new() { SubRequests = { Query(1, target) } }, buffered);
            var knowledge = ClientKnowledge.Deserialize(new(Assert.IsType<QueryChangesSubResponseData>(first.SubResponses[0].Data).KnowledgeBytes!));
            var known = await Run(provider, state, new() { SubRequests = { Query(2, target, knowledge: knowledge) } }, buffered);
            Assert.False(Assert.Single(known.SubResponses).Status);
            Assert.Empty(known.DataElementPackage!.DataElements);
            var mixed = await Run(provider, state, new() { SubRequests = { Query(2, target, knowledge: knowledge), Query(3, Guid.Empty) } }, buffered);
            Assert.All(mixed.SubResponses, s => Assert.False(s.Status));
            Assert.Equal(state.Partitions[0].Elements.Length, mixed.DataElementPackage!.DataElements.Count);
        }
    }

    [Fact]
    public void RepeatedEditorQueriesKeepDifferentSnapshotsAndEnumerateEachInputOnce()
    {
        var document = new DocumentStore().Put("/editors.docx", MinimalDocx.Create());
        int calls = 0, enumerations = 0;
        IEnumerable<EditorsTableEditor> Editors()
        {
            enumerations++;
            yield return new("client-" + calls, 638923456000000000);
        }
        var request = new FsshttpbCellRequest { SubRequests =
        { Query(1, StoredDocument.EditorsTablePartitionId), Query(2, StoredDocument.EditorsTablePartitionId) } };
        var response = CellBinaryRequestExecutor.Execute(document, document.MetadataPartition, request, DocumentAccess.Read,
            partitionEditorsQueryChanges: (p, id) =>
            {
                calls++;
                return EditorsTablePartitionBuilder.BuildSharePointV13QueryChangesResponse(id, Editors(),
                    p.ProtocolIdentity.CellId, p.ProtocolIdentity.SerialGuid, p.KnowledgeSequence);
            });
        Assert.Equal(2, calls); Assert.Equal(2, enumerations);
        Assert.All(response.SubResponses, s => Assert.False(s.Status));
        Assert.Equal(12, response.DataElementPackage!.DataElements.Count);
        Assert.NotEqual(Assert.IsType<QueryChangesSubResponseData>(response.SubResponses[0].Data).StorageIndexExtendedGuid,
            Assert.IsType<QueryChangesSubResponseData>(response.SubResponses[1].Data).StorageIndexExtendedGuid);
    }

    [Fact]
    public void EditorCallbackKnowledgeStaysAtTheCapturedSnapshotWhenLiveStateAdvances()
    {
        var document = new DocumentStore().Put("/snapshot.docx", MinimalDocx.Create());
        var partition = document.EditorsTablePartition;
        ulong captured = partition.KnowledgeSequence;
        var response = CellBinaryRequestExecutor.Execute(document, document.FilePartition,
            new() { SubRequests = { Query(1, StoredDocument.EditorsTablePartitionId) } }, DocumentAccess.Read,
            partitionEditorsQueryChanges: (p, id) =>
            {
                var result = EditorsTablePartitionBuilder.BuildSharePointV13QueryChangesResponse(id, [],
                    p.ProtocolIdentity.CellId, p.ProtocolIdentity.SerialGuid, p.KnowledgeSequence);
                document.JoinSession(Guid.NewGuid(), "later-editor");
                return result;
            });
        Assert.True(partition.KnowledgeSequence > captured);
        var data = Assert.IsType<QueryChangesSubResponseData>(Assert.Single(response.SubResponses).Data);
        Assert.Equal(captured, data.CellKnowledgeTo); Assert.Equal(captured, data.Waterline);
    }

    [Fact]
    public async Task EditorExpiryBetweenDurableQueriesPreservesBothSnapshotPackages()
    {
        var inner = new InMemoryStateStore();
        var content = new InMemoryContentStore();
        var provider = new StorageProvider(inner, content);
        var state = (await new CellBridgeDocumentService(provider).CreateAsync("/between.docx", MinimalDocx.Create(), TestActor.Value))!;
        await inner.TransitionAsync(state.ResourceId, (current, now) =>
        {
            var doc = StoredDocument.RestoreMetadata(current, now);
            doc.JoinEditingSession(Guid.NewGuid(), 3600, true, "editor", TestActor.Value.Identity);
            return new StateTransition<bool>(doc.CaptureCoordination(current, current.Coordination), true);
        });
        var changing = new BeforeReadStateStore(inner, 3, () => inner.TransitionAsync(state.ResourceId, (current, now) =>
            new StateTransition<bool>(current with { Editors = current.Editors.Select(e => e with
                { ExpiresUtc = now.AddMinutes(-1) }).ToImmutableArray() }, true)).AsTask());
        var result = await new CellBridgeDocumentService(new(changing, content)).ExecuteAsync(state.ResourceId,
            DocumentPartitionKind.Metadata, new() { SubRequests =
                { Query(1, StoredDocument.EditorsTablePartitionId), Query(2, StoredDocument.EditorsTablePartitionId) } },
            new Dictionary<string, string>(), TestActor.Value);
        Assert.All(result.Response.SubResponses, s => Assert.False(s.Status));
        Assert.Equal(12, result.Response.DataElementPackage!.DataElements.Count);
        var before = Assert.IsType<QueryChangesSubResponseData>(result.Response.SubResponses[0].Data);
        var after = Assert.IsType<QueryChangesSubResponseData>(result.Response.SubResponses[1].Data);
        Assert.NotEqual(before.StorageIndexExtendedGuid, after.StorageIndexExtendedGuid);
        Assert.True(after.CellKnowledgeTo > before.CellKnowledgeTo);
        Assert.Empty(result.State.Editors);
    }

    [Fact]
    public async Task OldEditorKnowledgeCannotSuppressAclChangesOrExpiryAfterServiceRecreation()
    {
        var provider = Memory();
        var state = (await new CellBridgeDocumentService(provider).CreateAsync("/expiry.docx", MinimalDocx.Create(), TestActor.Value))!;
        await provider.State.TransitionAsync(state.ResourceId, (current, now) =>
        {
            var document = StoredDocument.RestoreMetadata(current, now);
            document.JoinEditingSession(Guid.NewGuid(), 3600, true, "editor", TestActor.Value.Identity);
            return new StateTransition<bool>(document.CaptureCoordination(current, current.Coordination), true);
        });
        var first = await Run(provider, state, new() { SubRequests = { Query(1, StoredDocument.EditorsTablePartitionId) } }, false);
        var known = ClientKnowledge.Deserialize(new(Assert.IsType<QueryChangesSubResponseData>(first.SubResponses[0].Data).KnowledgeBytes!));
        await provider.State.TransitionAsync(state.ResourceId, (current, now) => new StateTransition<bool>(current with
        { Security = current.Security with { Owner = "new-owner", Grants = current.Security.Grants.SetItem(TestActor.Value.Identity.Subject, DocumentAccess.Read) } }, true));
        var changed = await Run(provider, state, new() { SubRequests = { Query(2, StoredDocument.EditorsTablePartitionId, knowledge: known) } }, false);
        Assert.Equal(6, changed.DataElementPackage!.DataElements.Count);
        Assert.NotEqual(Assert.IsType<QueryChangesSubResponseData>(first.SubResponses[0].Data).StorageIndexExtendedGuid,
            Assert.IsType<QueryChangesSubResponseData>(changed.SubResponses[0].Data).StorageIndexExtendedGuid);
        await provider.State.TransitionAsync(state.ResourceId, (current, now) => new StateTransition<bool>(current with
        { Editors = current.Editors.Select(e => e with { ExpiresUtc = now.AddMinutes(-1) }).ToImmutableArray() }, true));
        known = ClientKnowledge.Deserialize(new(Assert.IsType<QueryChangesSubResponseData>(changed.SubResponses[0].Data).KnowledgeBytes!));
        var expired = await Run(provider, state, new() { SubRequests = { Query(3, StoredDocument.EditorsTablePartitionId, knowledge: known) } }, false);
        Assert.Equal(6, expired.DataElementPackage!.DataElements.Count);
        Assert.Empty((await provider.State.FindByResourceIdAsync(state.ResourceId))!.Editors);
    }

    private static StorageProvider Memory() => new(new InMemoryStateStore(), new InMemoryContentStore());
    private static FsshttpbCellSubRequest Query(ulong id, Guid target, ClientKnowledge? knowledge = null, ulong? maximum = null, CellId? cell = null) =>
        new(RequestTypes.QueryChanges) { RequestId = id, TargetPartitionId = target, Data = new QueryChangesSubRequestData
        { IncludeStorageManifest = true, IncludeCellChanges = true, Knowledge = knowledge, MaxDataElements = maximum, CellId = cell } };
    private static async Task<FsshttpbResponse> Run(StorageProvider provider, DocumentState state, FsshttpbCellRequest request, bool buffered)
    {
        if (!buffered) return (await new CellBridgeDocumentService(provider).ExecuteAsync(state.ResourceId,
            DocumentPartitionKind.Metadata, request, new Dictionary<string, string>(), TestActor.Value)).Response;
        var doc = await StoredDocument.RestoreAsync(state, provider.Content);
        return CellBinaryRequestExecutor.Execute(doc, doc.MetadataPartition, request, DocumentAccess.Read | DocumentAccess.Write,
            partitionEditorsQueryChanges: (p, id) => EditorsTablePartitionBuilder.BuildSharePointV13QueryChangesResponse(id, [],
                p.ProtocolIdentity.CellId, p.ProtocolIdentity.SerialGuid, p.KnowledgeSequence));
    }
}

// Inject a concurrent persisted change between operation reads without timing sleeps.
internal sealed class BeforeReadStateStore(IDocumentStateStore inner, int ordinal, Func<Task> beforeRead) : IDocumentStateStore
{
    private int _reads;
    public bool Durable => inner.Durable;
    public bool Shared => inner.Shared;
    public async ValueTask<DocumentState?> FindByResourceIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        if (++_reads == ordinal) await beforeRead();
        return await inner.FindByResourceIdAsync(id, cancellationToken);
    }
    public ValueTask<DocumentState?> FindByPathKeyAsync(string key, CancellationToken ct = default) => inner.FindByPathKeyAsync(key, ct);
    public ValueTask<IReadOnlyList<DocumentSummary>> ListAsync(int offset, int limit, CancellationToken ct = default) => inner.ListAsync(offset, limit, ct);
    public ValueTask<bool> TryCreateAsync(DocumentState state, CancellationToken ct = default) => inner.TryCreateAsync(state, ct);
    public ValueTask<T> TransitionAsync<T>(Guid id, Func<DocumentState, DateTime, StateTransition<T>> transition, CancellationToken ct = default) => inner.TransitionAsync(id, transition, ct);
    public ValueTask CheckHealthAsync(CancellationToken ct = default) => inner.CheckHealthAsync(ct);
}
