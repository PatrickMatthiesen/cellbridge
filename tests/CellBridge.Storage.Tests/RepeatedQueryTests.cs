using System.Collections.Immutable;
using CellBridge.AspNetCore;
using CellBridge.FssHttpB;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.Tests;
using CellBridge.TestFixtures;

namespace CellBridge.Storage.Tests;

[Collection("PostgreSQL")]
public class RepeatedQueryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SelfContainedLegacyScopePreservesEarlierQueryAndSaveAcknowledgement(bool postgreSql)
    {
        await using var testProvider = ProtocolExecutionTestProvider.Create(postgreSql);
        var provider = testProvider.Provider;
        var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/legacy-scope-" + Guid.NewGuid().ToString("N") + ".docx", MinimalDocx.Create(), TestActor.Value))!;
        var f = new GraphFixture(MinimalDocx.Create("after"));
        f.MakeSelfContainedWithUnmappedAncestor();
        var request = Query(1);
        request.DataElementPackage = new(); request.DataElementPackage.DataElements.AddRange(f.Elements);
        request.SubRequests.Add(Save(f));
        StorageTests.Rebase(request,await StorageTestDocuments.GraphAsync(service,state));
        request.SubRequests.Add(Query(3, cell: f.Cell).SubRequests[0]);
        var result = (await new CellBridgeDocumentService(provider).ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents,
            request, new Dictionary<string, string>(), TestActor.Value)).Response;
        Assert.False(result.SubResponses[0].Status);
        Assert.False(result.SubResponses[1].Status);
        Assert.False(result.SubResponses[2].Status);
        var selected = Assert.IsType<QueryChangesSubResponseData>(result.SubResponses[2].Data).StorageIndexExtendedGuid;
        Assert.Equal(f.Bytes,PartitionGraphSnapshot.Create(result.DataElementPackage!.DataElements,selected).Materialize());
        Assert.NotEmpty(result.DataElementPackage!.DataElements);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AggregateBudgetRejectsLaterCellWithoutDiscardingEarlierPayload(bool postgreSql)
    {
        await using var testProvider = ProtocolExecutionTestProvider.Create(postgreSql);
        var provider = testProvider.Provider;
        var state = await PublishGraph(provider);
        var f = new GraphFixture(MinimalDocx.Create("after"), blob: true);
        var service = new CellBridgeDocumentService(provider);
        var first = (await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents,
            Query(1, cell: f.Cell), new Dictionary<string, string>(), TestActor.Value)).Response;
        var second = (await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents,
            Query(2, cell: f.OtherCell), new Dictionary<string, string>(), TestActor.Value)).Response;
        var budget = first.DataElementPackage!.DataElements.Sum(e =>
        {
            var writer = new BinaryWriterEx(); e.Serialize(writer); return (long)writer.Length;
        });
        // Exercise response aggregation independently of the provider's graph storage limit.
        var combined = new FsshttpbResponse();
        var assembler = new QueryChangesResponseAssembler(combined, budget);
        assembler.Append(first);
        assembler.Append(second);
        Assert.False(combined.SubResponses[0].Status);
        Assert.True(combined.SubResponses[1].Status);
        Assert.Equal((ulong)ProtocolErrorCode.RequestNotSupported, combined.SubResponses[1].Error!.ErrorCode);
        Assert.Equal(first.DataElementPackage.DataElements.Select(e => e.DataElementExtendedGuid),
            combined.DataElementPackage!.DataElements.Select(e => e.DataElementExtendedGuid));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RepeatedQueriesKeepIndependentKnowledgeAndFailuresWithoutDiscardingEarlierPayloads(bool postgreSql)
    {
        await using var testProvider = ProtocolExecutionTestProvider.Create(postgreSql);
        var provider = testProvider.Provider;
        var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/queries-" + Guid.NewGuid().ToString("N") + ".docx", MinimalDocx.Create(), TestActor.Value))!;
        var full = (await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents,
            Query(1), new Dictionary<string, string>(), TestActor.Value)).Response;
        var fullElements = full.DataElementPackage!.DataElements;
        var knowledge = ClientKnowledge.FromElements(fullElements);
        var request = Query(11, knowledge);
        request.SubRequests.Add(Query(22).SubRequests[0]);
        request.SubRequests.Add(Query(33, maximum: 1).SubRequests[0]);
        var combined = (await new CellBridgeDocumentService(provider).ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents,
            request, new Dictionary<string, string>(), TestActor.Value)).Response;
        Assert.Equal(new ulong[] { 11, 22, 33 }, combined.SubResponses.Select(s => s.RequestId));
        Assert.False(combined.SubResponses[0].Status);
        Assert.False(combined.SubResponses[1].Status);
        Assert.True(combined.SubResponses[2].Status);
        Assert.Equal(fullElements.Select(e => e.DataElementExtendedGuid).OrderBy(i => i.Value),
            combined.DataElementPackage!.DataElements.Select(e => e.DataElementExtendedGuid).OrderBy(i => i.Value));
        Assert.Equal(combined.DataElementPackage.DataElements.Count,
            combined.DataElementPackage.DataElements.Select(e => e.DataElementExtendedGuid).Distinct().Count());
        var wire = FsshttpbResponse.Deserialize(new BinaryReaderEx(combined.ToByteArray()));
        Assert.Equal(3, wire.SubResponses.Count);
        Assert.NotEmpty(wire.DataElementPackage!.DataElements);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CellQueriesFollowDependenciesAndKeepOtherCellsOutOfKnowledge(bool postgreSql)
    {
        await using var testProvider = ProtocolExecutionTestProvider.Create(postgreSql);
        var provider = testProvider.Provider;
        var state = await PublishGraph(provider);
        var f = new GraphFixture(MinimalDocx.Create("after"), blob: true);
        var response = (await new CellBridgeDocumentService(provider).ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents,
            Query(1, cell: f.Cell), new Dictionary<string, string>(), TestActor.Value)).Response;
        var data = Assert.IsType<QueryChangesSubResponseData>(Assert.Single(response.SubResponses).Data);
        Assert.Equal(0UL, data.Waterline);
        Assert.Equal(f.Cell.LongId.Guid, data.CellKnowledgeCellGuid);
        Assert.DoesNotContain(response.DataElementPackage!.DataElements, e => e.DataElementExtendedGuid.Equals(f.OtherGroup));
        Assert.Contains(response.DataElementPackage.DataElements, e => e.DataElementExtendedGuid.Equals(f.Blob));
        var other = (await provider.Documents.ReadElementsAsync(new(state.ResourceId,state.LifecycleGeneration,0),[StorageIds.Capture(f.OtherGroup)])).Single();
        var knowledge = ClientKnowledge.Deserialize(new BinaryReaderEx(data.KnowledgeBytes!));
        Assert.False(knowledge.Contains(new SerialNumber(other.Serial.Guid, other.Serial.Value)));
        var repeated = Query(1, cell: f.Cell);
        repeated.SubRequests.Add(Query(2, cell: f.OtherCell).SubRequests[0]);
        var union = (await new CellBridgeDocumentService(provider).ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents,
            repeated, new Dictionary<string, string>(), TestActor.Value)).Response;
        Assert.All(union.SubResponses, s => Assert.False(s.Status));
        Assert.Contains(union.DataElementPackage!.DataElements, e => e.DataElementExtendedGuid.Equals(f.OtherGroup));
        Assert.False(ClientKnowledge.Deserialize(new BinaryReaderEx(
            Assert.IsType<QueryChangesSubResponseData>(union.SubResponses[0].Data).KnowledgeBytes!))
            .Contains(new SerialNumber(other.Serial.Guid, other.Serial.Value)));
        var identity = (await provider.Documents.ReadPartitionAsync(state.ResourceId,new(0)))!.Partition.Identity;
        var stale = new CellId(StorageIds.Restore(identity.CellLong),StorageIds.Restore(identity.CellShort));
        var unsupported = await new CellBridgeDocumentService(provider).ExecuteAsync(state.ResourceId,
            DocumentPartitionKind.FileContents, Query(3, cell: stale), new Dictionary<string, string>(), TestActor.Value);
        // Key updates retain cells omitted from the new mapping envelope.
        Assert.False(Assert.Single(unsupported.Response.SubResponses).Status);
        var missing = new CellId(new ExGuid(1,Guid.NewGuid()),new ExGuid(1,Guid.NewGuid()));
        var unknown = await new CellBridgeDocumentService(provider).ExecuteAsync(state.ResourceId,
            DocumentPartitionKind.FileContents,Query(4,cell:missing),new Dictionary<string,string>(),TestActor.Value);
        Assert.True(Assert.Single(unknown.Response.SubResponses).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScopedSerialAmbiguityIsDetectedAcrossOtherCells(bool postgreSql)
    {
        await using var testProvider = ProtocolExecutionTestProvider.Create(postgreSql);
        var provider = testProvider.Provider;
        var state = await PublishGraph(provider);
        var f = new GraphFixture(MinimalDocx.Create("after"), blob: true);
        var other = (await provider.Documents.ReadElementsAsync(new(state.ResourceId,state.LifecycleGeneration,0),[StorageIds.Capture(f.OtherGroup)])).Single();
        var shared = other.Serial;
        var aliasedId = StorageIds.Capture(f.BaseGroup);
        var aliased = new AliasReadRepository(provider.Documents,aliasedId,shared);
        provider = new StorageProvider(aliased,provider.Content,provider.Limits);
        var known = ClientKnowledge.FromElements([new DataElement(DataElementType.ObjectGroupDataElementData,
            f.OtherGroup, new SerialNumber(shared.Guid, shared.Value))]);
        var query = await new CellBridgeDocumentService(provider).ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents,
            Query(1, known, cell: f.Cell), new Dictionary<string, string>(), TestActor.Value);
        Assert.Contains(query.Response.DataElementPackage!.DataElements, e => e.DataElementExtendedGuid.Equals(f.BaseGroup));
        Assert.DoesNotContain(query.Response.DataElementPackage.DataElements, e => e.DataElementExtendedGuid.Equals(f.OtherGroup));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QuerySaveQueryPreservesBothImmutableGraphsAndVersions(bool postgreSql)
    {
        await using var testProvider = ProtocolExecutionTestProvider.Create(postgreSql);
        var provider = testProvider.Provider;
        var service = new CellBridgeDocumentService(provider);
        var before = (await service.CreateAsync("/order-" + Guid.NewGuid().ToString("N") + ".docx", MinimalDocx.Create(), TestActor.Value))!;
        var f = new GraphFixture(MinimalDocx.Create("after"), blob: true);
        var request = Query(1);
        request.DataElementPackage = new(); request.DataElementPackage.DataElements.AddRange(f.Elements);
        request.SubRequests.Add(Save(f));
        var beforeIndex = (await provider.Documents.ReadPartitionAsync(before.ResourceId,new(0)))!.Partition.StorageIndex!;
        StorageTests.Rebase(request,await StorageTestDocuments.GraphAsync(service,before));
        request.SubRequests.Add(Query(3).SubRequests[0]);
        var result = await service.ExecuteAsync(before.ResourceId, DocumentPartitionKind.FileContents,
            request, new Dictionary<string, string>(), TestActor.Value);
        Assert.All(result.Response.SubResponses, s => Assert.False(s.Status));
        Assert.Equal(StorageIds.Restore(beforeIndex),
            Assert.IsType<QueryChangesSubResponseData>(result.Response.SubResponses[0].Data).StorageIndexExtendedGuid);
        var applied = Assert.IsType<PutChangesSubResponseData>(result.Response.SubResponses[1].Data).PutChangesResponse!.AppliedStorageIndexID;
        Assert.Equal(applied,Assert.IsType<QueryChangesSubResponseData>(result.Response.SubResponses[2].Data).StorageIndexExtendedGuid);
        Assert.Equal(before.ContentVersion + 1, result.State.ContentVersion);
        Assert.Contains(result.Response.DataElementPackage!.DataElements,
            e => e.DataElementExtendedGuid.Equals(StorageIds.Restore(beforeIndex)));
        Assert.Contains(result.Response.DataElementPackage.DataElements, e => e.DataElementExtendedGuid.Equals(applied));
    }

    private static async Task<DocumentHead> PublishGraph(StorageProvider provider)
    {
        var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/scopes-" + Guid.NewGuid().ToString("N") + ".docx", MinimalDocx.Create(), TestActor.Value))!;
        var f = new GraphFixture(MinimalDocx.Create("after"), blob: true);
        var request = new FsshttpbCellRequest { DataElementPackage = new() };
        request.DataElementPackage.DataElements.AddRange(f.Elements); request.SubRequests.Add(Save(f));
        StorageTests.Rebase(request,await StorageTestDocuments.GraphAsync(service,state));
        var result = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents, request,
            new Dictionary<string, string>(), TestActor.Value);
        Assert.False(Assert.Single(result.Response.SubResponses).Status);
        return result.State;
    }
    private static FsshttpbCellSubRequest Save(GraphFixture f) => new(RequestTypes.PutChanges)
    { RequestId = 2, Data = new PutChangesSubRequestData { StorageIndex = f.Index,
        Flags = 1, HasAdditionalFlags = true, AdditionalFlagsBits = 1 } };
    private sealed class AliasReadRepository(IDocumentRepository inner,ExtendedId id,SerialId serial) : RepositoryTestProxy(inner)
    {
        public override async ValueTask<PartitionRead?> ReadPartitionAsync(Guid resourceId,PartitionReadRequest request,CancellationToken cancellationToken=default)
        {
            var read = await base.ReadPartitionAsync(resourceId,request,cancellationToken);
            return read is null ? null : read with { Elements = read.Elements.Select(element => element.Id == id ? element with { Serial = serial } : element).ToImmutableArray() };
        }
    }
    private static FsshttpbCellRequest Query(ulong id, ClientKnowledge? knowledge = null, ulong? maximum = null, CellId? cell = null) => new()
    { SubRequests = { new(RequestTypes.QueryChanges) { RequestId = id, Data = new QueryChangesSubRequestData
        { IncludeStorageManifest = true, IncludeCellChanges = true, Knowledge = knowledge, MaxDataElements = maximum, CellId = cell } } } };
}
