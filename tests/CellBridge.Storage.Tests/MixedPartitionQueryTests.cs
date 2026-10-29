using System.Collections.Immutable;
using CellBridge.AspNetCore;
using CellBridge.FssHttp;
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
        Assert.Equal(new ExGuid(1, (await provider.Documents.ReadPartitionAsync(state.ResourceId, new(1)))!.Partition.Identity.SerialGuid),
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
        Assert.Equal((await provider.Documents.ReadPartitionAsync(state.ResourceId, new(0) { IncludeCatalog = true }))!.Elements.Length + 8, elements.Count);
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
            Assert.Equal((await provider.Documents.ReadPartitionAsync(state.ResourceId, new(0) { IncludeCatalog = true }))!.Elements.Length + 1, mixed.DataElementPackage!.DataElements.Count);
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
        ulong captured = (await provider.Documents.ReadPartitionAsync(state.ResourceId, new(2)))!.Partition.Knowledge;
        var changing = new AfterFirstControlRead(provider.Documents, () => JoinAsync(provider, state.ResourceId));
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
        Assert.Equal(1, result.State.ActiveEditors);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EditorExpiryBetweenDurableQueriesPreservesBothSnapshotPackages(bool postgreSql)
    {
        await using var testProvider = ProtocolExecutionTestProvider.Create(postgreSql);
        var provider = testProvider.Provider;
        var inner = provider.Documents;
        var content = provider.Content;
        var state = await CreateWithExpiringEditorAsync(provider, "/between-" + Guid.NewGuid() + ".docx");
        var expires = (await inner.ReadControlAsync(state.ResourceId))!.Editors.Single().ExpiresUtc;
        var changing = new AfterFirstControlRead(inner, () => WaitForExpiryAsync(expires));
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
        Assert.Equal(0, result.State.ActiveEditors);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OldEditorKnowledgeCannotSuppressAclChangesOrExpiryAfterServiceRecreation(bool postgreSql)
    {
        await using var testProvider = ProtocolExecutionTestProvider.Create(postgreSql);
        var provider = testProvider.Provider;
        var state = await CreateWithExpiringEditorAsync(provider, "/expiry-" + Guid.NewGuid() + ".docx");
        var expires = (await provider.Documents.ReadControlAsync(state.ResourceId))!.Editors.Single().ExpiresUtc;
        var first = (await new CellBridgeDocumentService(provider).ExecuteAsync(state.ResourceId, DocumentPartitionKind.Metadata,
            new() { SubRequests = { Query(1, StoredDocument.EditorsTablePartitionId) } }, new Dictionary<string, string>(), TestActor.Value)).Response;
        var known = ClientKnowledge.Deserialize(new(Assert.IsType<QueryChangesSubResponseData>(first.SubResponses[0].Data).KnowledgeBytes!));
        var current = (await provider.Documents.FindAsync(new DocumentIdentity.Resource(state.ResourceId)))!;
        var permissions = await provider.Documents.ExecuteControlAsync(new DocumentControlCommand.SetPermissions(state.ResourceId,
            StorageTestDocuments.Admission(current), current.Security with { Owner = "new-owner",
                Grants = current.Security.Grants.SetItem(TestActor.Value.Identity.Subject, DocumentAccess.Read) }, current.AuthorityGeneration));
        Assert.True(permissions.Changed);
        var changed = (await new CellBridgeDocumentService(provider).ExecuteAsync(state.ResourceId, DocumentPartitionKind.Metadata,
            new() { SubRequests = { Query(2, StoredDocument.EditorsTablePartitionId, knowledge: known) } }, new Dictionary<string, string>(), TestActor.Value)).Response;
        Assert.Equal(6, changed.DataElementPackage!.DataElements.Count);
        Assert.NotEqual(Assert.IsType<QueryChangesSubResponseData>(first.SubResponses[0].Data).StorageIndexExtendedGuid,
            Assert.IsType<QueryChangesSubResponseData>(changed.SubResponses[0].Data).StorageIndexExtendedGuid);
        await WaitForExpiryAsync(expires);
        known = ClientKnowledge.Deserialize(new(Assert.IsType<QueryChangesSubResponseData>(changed.SubResponses[0].Data).KnowledgeBytes!));
        var expired = (await new CellBridgeDocumentService(provider).ExecuteAsync(state.ResourceId, DocumentPartitionKind.Metadata,
            new() { SubRequests = { Query(3, StoredDocument.EditorsTablePartitionId, knowledge: known) } }, new Dictionary<string, string>(), TestActor.Value)).Response;
        Assert.Equal(6, expired.DataElementPackage!.DataElements.Count);
        Assert.Empty((await provider.Documents.ReadControlAsync(state.ResourceId))!.Editors);
    }

    private static async Task JoinAsync(StorageProvider provider, Guid id, int timeout = 3600)
    {
        var head = (await provider.Documents.FindAsync(new DocumentIdentity.Resource(id)))!;
        var joined = await provider.Documents.ExecuteControlAsync(new DocumentControlCommand.Office(id,
            StorageTestDocuments.Admission(head), new() { Type = SubRequestType.EditorsTable,
                SubRequestDataAttributes = { ["EditorsTableRequestType"] = "JoinEditingSession", ["ClientID"] = Guid.NewGuid().ToString(),
                    ["Timeout"] = timeout.ToString(), ["AsEditor"] = "true" } }));
        Assert.True(joined.Changed);
    }

    private static async Task<DocumentHead> CreateWithExpiringEditorAsync(StorageProvider provider, string path)
    {
        var prepared = await IndexedDocumentPublicationBuilder.CreateAsync(Guid.NewGuid(), path, MinimalDocx.Create(),
            DocumentSecurity.Create(TestActor.Value.Identity), provider.Content, provider.Limits);
        var earlier = new DocumentControlRead(prepared.Head, DateTime.UtcNow.AddSeconds(-3598), prepared.Coordination,
            prepared.Editors, prepared.Partitions.Select(partition => partition.After).ToImmutableArray());
        var joined = DocumentControlReducer.Apply(earlier, new DocumentControlCommand.Office(prepared.Head.ResourceId,
            StorageTestDocuments.Admission(prepared.Head), new() { Type = SubRequestType.EditorsTable, SubRequestDataAttributes =
            { ["EditorsTableRequestType"] = "JoinEditingSession", ["ClientID"] = Guid.NewGuid().ToString(), ["Timeout"] = "3600", ["AsEditor"] = "true" } }), provider.Limits);
        Assert.True(joined.Changed);
        prepared = prepared with { Head = joined.Current.Head, Editors = joined.Current.Editors,
            Partitions = prepared.Partitions.Select(partition => partition.After.Kind == 2
                ? partition with { After = joined.Current.Partitions.Single(head => head.Kind == 2) } : partition).ToImmutableArray() };
        var created = await provider.Documents.CreateAsync(prepared);
        Assert.True(created.Created);
        return created.Current!;
    }

    private static Task WaitForExpiryAsync(DateTime expiry) => Task.Delay(TimeSpan.FromMilliseconds(Math.Max(0, (expiry - DateTime.UtcNow).TotalMilliseconds) + 100));

    private static FsshttpbCellSubRequest Query(ulong id, Guid target, ClientKnowledge? knowledge = null, ulong? maximum = null, CellId? cell = null) =>
        new(RequestTypes.QueryChanges) { RequestId = id, TargetPartitionId = target, Data = new QueryChangesSubRequestData
        { IncludeStorageManifest = true, IncludeCellChanges = true, Knowledge = knowledge, MaxDataElements = maximum, CellId = cell } };
}

// Inject a concurrent persisted change between operation reads without timing sleeps.
internal sealed class BeforeReadStateStore(IDocumentRepository inner, int ordinal, Func<Task> beforeRead) : RepositoryTestProxy(inner)
{
    private int _reads;
    public override async ValueTask<DocumentHead?> FindAsync(DocumentIdentity identity, CancellationToken ct = default)
    {
        if (Interlocked.Increment(ref _reads) == ordinal) await beforeRead();
        return await Inner.FindAsync(identity, ct);
    }
}

internal sealed class AfterFirstControlRead(IDocumentRepository inner,Func<Task> afterRead) : RepositoryTestProxy(inner)
{
    private int reads;
    public override async ValueTask<DocumentControlRead?> ReadControlAsync(Guid id,CancellationToken cancellationToken=default)
    {
        var snapshot = await Inner.ReadControlAsync(id,cancellationToken);
        if (Interlocked.Increment(ref reads) == 1) await afterRead();
        return snapshot;
    }
}
