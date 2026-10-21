using System.Collections.Immutable;
using CellBridge.AspNetCore;
using CellBridge.FssHttpB;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.TestFixtures;

namespace CellBridge.Storage.Tests;

[Collection("PostgreSQL")]
public sealed class MixedPartitionQueryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AbsentTargetInheritsSoapDefaultAfterAnExplicitFileQuery(bool postgreSql)
    {
        await using var testProvider = ProtocolExecutionTestProvider.Create(postgreSql);
        var provider = testProvider.Provider;
        var state = (await new CellBridgeDocumentService(provider).CreateAsync("/default-" + Guid.NewGuid().ToString("N") + ".docx", MinimalDocx.Create(), TestActor.Value))!;
        var inherit = Query(2, StoredDocument.MetadataPartitionId); inherit.TargetPartitionId = null;
        var result = (await new CellBridgeDocumentService(provider).ExecuteAsync(state.ResourceId, DocumentPartitionKind.Metadata,
            new() { SubRequests = { Query(1, Guid.Empty), inherit } }, new Dictionary<string, string>(), TestActor.Value)).Response;
        Assert.All(result.SubResponses, s => Assert.False(s.Status));
        Assert.Equal(new ExGuid(1, state.Partitions[1].Identity.SerialGuid),
            Assert.IsType<QueryChangesSubResponseData>(result.SubResponses[1].Data).StorageIndexExtendedGuid);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GeneratedEditorQueriesHonorInclusionRoundingAndUnsupportedVersionControls(bool postgreSql)
    {
        await using var testProvider = ProtocolExecutionTestProvider.Create(postgreSql);
        var provider = testProvider.Provider;
        var state = (await new CellBridgeDocumentService(provider).CreateAsync("/controls-" + Guid.NewGuid().ToString("N") + ".docx", MinimalDocx.Create(), TestActor.Value))!;
        var first = (await new CellBridgeDocumentService(provider).ExecuteAsync(state.ResourceId, DocumentPartitionKind.Metadata,
            new() { SubRequests = { Query(1, StoredDocument.EditorsTablePartitionId) } }, new Dictionary<string, string>(), TestActor.Value)).Response;
        var elements = first.DataElementPackage!.DataElements;
        foreach (bool round in new[] { false, true })
        {
            var query = Query(2, StoredDocument.EditorsTablePartitionId);
            var controls = Assert.IsType<QueryChangesSubRequestData>(query.Data);
            controls.IncludeStorageManifest = false; controls.IncludeCellChanges = false;
            controls.RoundKnowledgeToWholeCellChanges = round;
            controls.Knowledge = ClientKnowledge.FromElements(elements.Take(1));
            var filtered = (await new CellBridgeDocumentService(provider).ExecuteAsync(state.ResourceId, DocumentPartitionKind.Metadata,
                new() { SubRequests = { query } }, new Dictionary<string, string>(), TestActor.Value)).Response;
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
            var result = (await new CellBridgeDocumentService(provider).ExecuteAsync(state.ResourceId, DocumentPartitionKind.Metadata,
                new() { SubRequests = { unsupported, Query(4, Guid.Empty) } }, new Dictionary<string, string>(), TestActor.Value)).Response;
            Assert.True(result.SubResponses[0].Status); Assert.False(result.SubResponses[1].Status);
            Assert.NotEmpty(result.DataElementPackage!.DataElements);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MixedAggregateBudgetFailurePreservesEarlierMetadataAndEditorKnowledge(bool postgreSql)
    {
        await using var testProvider = ProtocolExecutionTestProvider.Create(postgreSql);
        var provider = testProvider.Provider;
        var state = (await new CellBridgeDocumentService(provider).CreateAsync("/aggregate-" + Guid.NewGuid().ToString("N") + ".docx", MinimalDocx.Create(), TestActor.Value))!;
        var request = new FsshttpbCellRequest { SubRequests = { Query(1, StoredDocument.MetadataPartitionId), Query(2, StoredDocument.EditorsTablePartitionId) } };
        var full = (await new CellBridgeDocumentService(provider).ExecuteAsync(state.ResourceId, DocumentPartitionKind.Metadata,
            request, new Dictionary<string, string>(), TestActor.Value)).Response;
        long budget = full.DataElementPackage!.DataElements.Sum(e => { var writer = new BinaryWriterEx(); e.Serialize(writer); return (long)writer.Length; });
        var file = (await new CellBridgeDocumentService(provider).ExecuteAsync(state.ResourceId, DocumentPartitionKind.Metadata,
            new() { SubRequests = { Query(3, Guid.Empty) } }, new Dictionary<string, string>(), TestActor.Value)).Response;
        var result = new FsshttpbResponse();
        var assembler = new QueryChangesResponseAssembler(result, budget);
        assembler.Append(full);
        assembler.Append(file);
        Assert.False(result.SubResponses[0].Status); Assert.False(result.SubResponses[1].Status); Assert.True(result.SubResponses[2].Status);
        Assert.Equal(full.DataElementPackage.DataElements.Select(e => e.DataElementExtendedGuid), result.DataElementPackage!.DataElements.Select(e => e.DataElementExtendedGuid));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MixedRepeatedQueriesKeepAllPackagesAndIsolateUnsupportedScopeAndBudget(bool postgreSql)
    {
        await using var testProvider = ProtocolExecutionTestProvider.Create(postgreSql);
        var provider = testProvider.Provider;
        var state = (await new CellBridgeDocumentService(provider).CreateAsync("/mixed-" + Guid.NewGuid().ToString("N") + ".docx", MinimalDocx.Create(), TestActor.Value))!;
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
        var response = (await new CellBridgeDocumentService(provider).ExecuteAsync(state.ResourceId, DocumentPartitionKind.Metadata,
            request, new Dictionary<string, string>(), TestActor.Value)).Response;
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
    public async Task NonFileKnowledgeFiltersOnlyItsOwnQueryAndPreservesOtherPartitions(bool postgreSql)
    {
        await using var testProvider = ProtocolExecutionTestProvider.Create(postgreSql);
        var provider = testProvider.Provider;
        var state = (await new CellBridgeDocumentService(provider).CreateAsync("/knowledge-" + Guid.NewGuid().ToString("N") + ".docx", MinimalDocx.Create(), TestActor.Value))!;
        foreach (var target in new[] { StoredDocument.MetadataPartitionId, StoredDocument.EditorsTablePartitionId })
        {
            var first = (await new CellBridgeDocumentService(provider).ExecuteAsync(state.ResourceId, DocumentPartitionKind.Metadata,
                new() { SubRequests = { Query(1, target) } }, new Dictionary<string, string>(), TestActor.Value)).Response;
            var knowledge = ClientKnowledge.Deserialize(new(Assert.IsType<QueryChangesSubResponseData>(first.SubResponses[0].Data).KnowledgeBytes!));
            var known = (await new CellBridgeDocumentService(provider).ExecuteAsync(state.ResourceId, DocumentPartitionKind.Metadata,
                new() { SubRequests = { Query(2, target, knowledge: knowledge) } }, new Dictionary<string, string>(), TestActor.Value)).Response;
            Assert.False(Assert.Single(known.SubResponses).Status);
            Assert.Empty(known.DataElementPackage!.DataElements);
            var mixed = (await new CellBridgeDocumentService(provider).ExecuteAsync(state.ResourceId, DocumentPartitionKind.Metadata,
                new() { SubRequests = { Query(2, target, knowledge: knowledge), Query(3, Guid.Empty) } }, new Dictionary<string, string>(), TestActor.Value)).Response;
            Assert.All(mixed.SubResponses, s => Assert.False(s.Status));
            Assert.Equal(state.Partitions[0].Elements.Length, mixed.DataElementPackage!.DataElements.Count);
        }
    }

    [Fact]
    public void AssemblerKeepsDifferentEditorSnapshotsAndEnumeratesEachInputOnce()
    {
        var document = new DocumentStore().Put("/editors.docx", MinimalDocx.Create());
        var partition = document.EditorsTablePartition;
        int enumerations = 0;
        IEnumerable<EditorsTableEditor> Editors(string client)
        {
            enumerations++;
            yield return new(client, 638923456000000000);
        }
        var first = EditorsTablePartitionBuilder.BuildSharePointV13QueryChangesResponse(1, Editors("client-1"),
            partition.ProtocolIdentity.CellId, partition.ProtocolIdentity.SerialGuid, partition.KnowledgeSequence);
        var second = EditorsTablePartitionBuilder.BuildSharePointV13QueryChangesResponse(2, Editors("client-2"),
            partition.ProtocolIdentity.CellId, partition.ProtocolIdentity.SerialGuid, partition.KnowledgeSequence);
        var response = new FsshttpbResponse();
        var assembler = new QueryChangesResponseAssembler(response, long.MaxValue);
        assembler.Append(first);
        assembler.Append(second);
        Assert.Equal(2, enumerations);
        Assert.All(response.SubResponses, s => Assert.False(s.Status));
        Assert.Equal(12, response.DataElementPackage!.DataElements.Count);
        Assert.NotEqual(Assert.IsType<QueryChangesSubResponseData>(response.SubResponses[0].Data).StorageIndexExtendedGuid,
            Assert.IsType<QueryChangesSubResponseData>(response.SubResponses[1].Data).StorageIndexExtendedGuid);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EditorKnowledgeStaysAtTheCapturedSnapshotWhenPersistedStateAdvances(bool postgreSql)
    {
        await using var testProvider = ProtocolExecutionTestProvider.Create(postgreSql);
        var provider = testProvider.Provider;
        var state = (await new CellBridgeDocumentService(provider).CreateAsync("/snapshot-" + Guid.NewGuid().ToString("N") + ".docx", MinimalDocx.Create(), TestActor.Value))!;
        ulong captured = state.Partitions.Single(p => p.Kind == (int)DocumentPartitionKind.EditorsTable).Knowledge;
        var changing = new BeforeReadStateStore(provider.State, 3, () => provider.State.TransitionAsync(state.ResourceId, (current, now) =>
        {
            var document = StoredDocument.RestoreMetadata(current, now);
            document.JoinEditingSession(Guid.NewGuid(), 3600, true, "later-editor", TestActor.Value.Identity);
            return new StateTransition<bool>(document.CaptureCoordination(current, current.Coordination), true);
        }).AsTask());
        var result = await new CellBridgeDocumentService(new(changing, provider.Content)).ExecuteAsync(state.ResourceId,
            DocumentPartitionKind.FileContents, new() { SubRequests =
                { Query(1, StoredDocument.EditorsTablePartitionId), Query(2, StoredDocument.EditorsTablePartitionId) } },
            new Dictionary<string, string>(), TestActor.Value);
        Assert.All(result.Response.SubResponses, sub => Assert.False(sub.Status));
        var before = Assert.IsType<QueryChangesSubResponseData>(result.Response.SubResponses[0].Data);
        var after = Assert.IsType<QueryChangesSubResponseData>(result.Response.SubResponses[1].Data);
        Assert.Equal(captured, before.CellKnowledgeTo);
        Assert.Equal(captured, before.Waterline);
        Assert.True(after.CellKnowledgeTo > captured);
        Assert.NotEqual(before.StorageIndexExtendedGuid, after.StorageIndexExtendedGuid);
        Assert.Equal(12, result.Response.DataElementPackage!.DataElements.Count);
        Assert.Single(result.State.Editors);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EditorExpiryBetweenDurableQueriesPreservesBothSnapshotPackages(bool postgreSql)
    {
        await using var testProvider = ProtocolExecutionTestProvider.Create(postgreSql);
        var provider = testProvider.Provider;
        var inner = provider.State;
        var content = provider.Content;
        var state = (await new CellBridgeDocumentService(provider).CreateAsync("/between-" + Guid.NewGuid().ToString("N") + ".docx", MinimalDocx.Create(), TestActor.Value))!;
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OldEditorKnowledgeCannotSuppressAclChangesOrExpiryAfterServiceRecreation(bool postgreSql)
    {
        await using var testProvider = ProtocolExecutionTestProvider.Create(postgreSql);
        var provider = testProvider.Provider;
        var state = (await new CellBridgeDocumentService(provider).CreateAsync("/expiry-" + Guid.NewGuid().ToString("N") + ".docx", MinimalDocx.Create(), TestActor.Value))!;
        await provider.State.TransitionAsync(state.ResourceId, (current, now) =>
        {
            var document = StoredDocument.RestoreMetadata(current, now);
            document.JoinEditingSession(Guid.NewGuid(), 3600, true, "editor", TestActor.Value.Identity);
            return new StateTransition<bool>(document.CaptureCoordination(current, current.Coordination), true);
        });
        var first = (await new CellBridgeDocumentService(provider).ExecuteAsync(state.ResourceId, DocumentPartitionKind.Metadata,
            new() { SubRequests = { Query(1, StoredDocument.EditorsTablePartitionId) } }, new Dictionary<string, string>(), TestActor.Value)).Response;
        var known = ClientKnowledge.Deserialize(new(Assert.IsType<QueryChangesSubResponseData>(first.SubResponses[0].Data).KnowledgeBytes!));
        await provider.State.TransitionAsync(state.ResourceId, (current, now) => new StateTransition<bool>(current with
        { Security = current.Security with { Owner = "new-owner", Grants = current.Security.Grants.SetItem(TestActor.Value.Identity.Subject, DocumentAccess.Read) } }, true));
        var changed = (await new CellBridgeDocumentService(provider).ExecuteAsync(state.ResourceId, DocumentPartitionKind.Metadata,
            new() { SubRequests = { Query(2, StoredDocument.EditorsTablePartitionId, knowledge: known) } }, new Dictionary<string, string>(), TestActor.Value)).Response;
        Assert.Equal(6, changed.DataElementPackage!.DataElements.Count);
        Assert.NotEqual(Assert.IsType<QueryChangesSubResponseData>(first.SubResponses[0].Data).StorageIndexExtendedGuid,
            Assert.IsType<QueryChangesSubResponseData>(changed.SubResponses[0].Data).StorageIndexExtendedGuid);
        await provider.State.TransitionAsync(state.ResourceId, (current, now) => new StateTransition<bool>(current with
        { Editors = current.Editors.Select(e => e with { ExpiresUtc = now.AddMinutes(-1) }).ToImmutableArray() }, true));
        known = ClientKnowledge.Deserialize(new(Assert.IsType<QueryChangesSubResponseData>(changed.SubResponses[0].Data).KnowledgeBytes!));
        var expired = (await new CellBridgeDocumentService(provider).ExecuteAsync(state.ResourceId, DocumentPartitionKind.Metadata,
            new() { SubRequests = { Query(3, StoredDocument.EditorsTablePartitionId, knowledge: known) } }, new Dictionary<string, string>(), TestActor.Value)).Response;
        Assert.Equal(6, expired.DataElementPackage!.DataElements.Count);
        Assert.Empty((await provider.State.FindByResourceIdAsync(state.ResourceId))!.Editors);
    }

    private static FsshttpbCellSubRequest Query(ulong id, Guid target, ClientKnowledge? knowledge = null, ulong? maximum = null, CellId? cell = null) =>
        new(RequestTypes.QueryChanges) { RequestId = id, TargetPartitionId = target, Data = new QueryChangesSubRequestData
        { IncludeStorageManifest = true, IncludeCellChanges = true, Knowledge = knowledge, MaxDataElements = maximum, CellId = cell } };
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
