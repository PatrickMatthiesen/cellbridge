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
    public async Task UnsupportedLegacyScopePreservesEarlierQueryAndSaveAcknowledgement(bool postgreSql)
    {
        await using var testProvider = ProtocolExecutionTestProvider.Create(postgreSql);
        var provider = testProvider.Provider;
        var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/legacy-scope-" + Guid.NewGuid().ToString("N") + ".docx", MinimalDocx.Create(), TestActor.Value))!;
        var f = new GraphFixture(MinimalDocx.Create("after"));
        f.MakeSelfContainedWithUnmappedAncestor();
        var request = Query(1);
        request.DataElementPackage = new(); request.DataElementPackage.DataElements.AddRange(f.Elements);
        request.SubRequests.Add(Save(state, f));
        request.SubRequests.Add(Query(3, cell: f.Cell).SubRequests[0]);
        var result = (await new CellBridgeDocumentService(provider).ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents,
            request, new Dictionary<string, string>(), TestActor.Value)).Response;
        Assert.False(result.SubResponses[0].Status);
        Assert.False(result.SubResponses[1].Status);
        Assert.True(result.SubResponses[2].Status);
        Assert.Equal((ulong)ProtocolErrorCode.RequestNotSupported, result.SubResponses[2].Error!.ErrorCode);
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
        var other = state.Partitions[0].Elements.Single(e => e.Id == StorageIds.Capture(f.OtherGroup));
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
        var stale = new CellId(StorageIds.Restore(state.Partitions[0].Identity.CellLong), StorageIds.Restore(state.Partitions[0].Identity.CellShort));
        var unsupported = await new CellBridgeDocumentService(provider).ExecuteAsync(state.ResourceId,
            DocumentPartitionKind.FileContents, Query(3, cell: stale), new Dictionary<string, string>(), TestActor.Value);
        Assert.True(Assert.Single(unsupported.Response.SubResponses).Status);
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
        var other = state.Partitions[0].Elements.Single(e => e.Id == StorageIds.Capture(f.OtherGroup));
        var shared = other.Serial;
        var aliasedId = StorageIds.Capture(f.BaseGroup);
        static PartitionState ApplyAlias(PartitionState value, ExtendedId id, SerialId serial) => value.Kind != 0 ? value : value with
        {
            Elements = value.Elements.Select(element => element.Id == id
                ? element with { Serial = serial } : element).ToImmutableArray(),
        };
        await provider.State.TransitionAsync(state.ResourceId, (current, _) =>
        {
            // Keep retained copies consistent while preserving ambiguity between the two distinct group IDs.
            var next = current with
            {
                Partitions = current.Partitions.Select(partition => ApplyAlias(partition, aliasedId, shared)).ToImmutableArray(),
                Revisions = current.Revisions.Select(revision => revision with
                {
                    Partitions = revision.Partitions.Select(partition => ApplyAlias(partition, aliasedId, shared)).ToImmutableArray(),
                }).ToImmutableArray(),
            };
            return new StateTransition<bool>(next, true);
        });
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
        request.SubRequests.Add(Save(before, f));
        request.SubRequests.Add(Query(3).SubRequests[0]);
        var result = await service.ExecuteAsync(before.ResourceId, DocumentPartitionKind.FileContents,
            request, new Dictionary<string, string>(), TestActor.Value);
        Assert.All(result.Response.SubResponses, s => Assert.False(s.Status));
        Assert.Equal(StorageIds.Restore(before.Partitions[0].StorageIndex!),
            Assert.IsType<QueryChangesSubResponseData>(result.Response.SubResponses[0].Data).StorageIndexExtendedGuid);
        Assert.Equal(f.Index, Assert.IsType<QueryChangesSubResponseData>(result.Response.SubResponses[2].Data).StorageIndexExtendedGuid);
        Assert.Equal(before.ContentVersion + 1, result.State.ContentVersion);
        Assert.Contains(result.Response.DataElementPackage!.DataElements,
            e => e.DataElementExtendedGuid.Equals(StorageIds.Restore(before.Partitions[0].StorageIndex!)));
        Assert.Contains(result.Response.DataElementPackage.DataElements, e => e.DataElementExtendedGuid.Equals(f.Index));
    }

    private static async Task<DocumentState> PublishGraph(StorageProvider provider)
    {
        var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/scopes-" + Guid.NewGuid().ToString("N") + ".docx", MinimalDocx.Create(), TestActor.Value))!;
        var f = new GraphFixture(MinimalDocx.Create("after"), blob: true);
        var request = new FsshttpbCellRequest { DataElementPackage = new() };
        request.DataElementPackage.DataElements.AddRange(f.Elements); request.SubRequests.Add(Save(state, f));
        var result = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents, request,
            new Dictionary<string, string>(), TestActor.Value);
        Assert.False(Assert.Single(result.Response.SubResponses).Status);
        return result.State;
    }
    private static FsshttpbCellSubRequest Save(DocumentState state, GraphFixture f) => new(RequestTypes.PutChanges)
    { RequestId = 2, Data = new PutChangesSubRequestData { StorageIndex = f.Index,
        ExpectedStorageIndex = StorageIds.Restore(state.Partitions[0].StorageIndex!), Flags = 1 } };
    private static FsshttpbCellRequest Query(ulong id, ClientKnowledge? knowledge = null, ulong? maximum = null, CellId? cell = null) => new()
    { SubRequests = { new(RequestTypes.QueryChanges) { RequestId = id, Data = new QueryChangesSubRequestData
        { IncludeStorageManifest = true, IncludeCellChanges = true, Knowledge = knowledge, MaxDataElements = maximum, CellId = cell } } } };
}
