using System.Collections.Immutable;
using CellBridge.AspNetCore;
using CellBridge.FssHttpB;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using CellBridge.Tests;
using CellBridge.Web;

namespace CellBridge.Storage.Tests;

public class RepeatedQueryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnsupportedLegacyScopePreservesEarlierQueryAndSaveAcknowledgement(bool buffered)
    {
        var provider = Memory();
        var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/legacy-scope.docx", MinimalDocx.Create(), TestActor.Value))!;
        var f = new GraphFixture(MinimalDocx.Create("after"));
        f.MakeSelfContainedWithUnmappedAncestor();
        var request = Query(1);
        request.DataElementPackage = new(); request.DataElementPackage.DataElements.AddRange(f.Elements);
        request.SubRequests.Add(Save(state, f));
        request.SubRequests.Add(Query(3, cell: f.Cell).SubRequests[0]);
        var result = await Run(state, provider, request, buffered);
        Assert.False(result.SubResponses[0].Status);
        Assert.False(result.SubResponses[1].Status);
        Assert.True(result.SubResponses[2].Status);
        Assert.Equal((ulong)ProtocolErrorCode.RequestNotSupported, result.SubResponses[2].Error!.ErrorCode);
        Assert.NotEmpty(result.DataElementPackage!.DataElements);
    }

    [Fact]
    public async Task AggregateBudgetRejectsLaterCellWithoutDiscardingEarlierPayload()
    {
        var provider = Memory();
        var state = await PublishGraph(provider);
        var f = new GraphFixture(MinimalDocx.Create("after"), blob: true);
        var document = await StoredDocument.RestoreAsync(state, provider.Content);
        var first = CellBinaryRequestExecutor.Execute(document, document.FilePartition, Query(1, cell: f.Cell), DocumentAccess.Read);
        var budget = first.DataElementPackage!.DataElements.Sum(e =>
        {
            var writer = new BinaryWriterEx(); e.Serialize(writer); return (long)writer.Length;
        });
        var request = Query(1, cell: f.Cell);
        request.SubRequests.Add(Query(2, cell: f.OtherCell).SubRequests[0]);
        var combined = CellBinaryRequestExecutor.Execute(document, document.FilePartition, request,
            DocumentAccess.Read, maxResponseBytes: budget);
        Assert.False(combined.SubResponses[0].Status);
        Assert.True(combined.SubResponses[1].Status);
        Assert.Equal((ulong)ProtocolErrorCode.RequestNotSupported, combined.SubResponses[1].Error!.ErrorCode);
        Assert.Equal(first.DataElementPackage.DataElements.Select(e => e.DataElementExtendedGuid),
            combined.DataElementPackage!.DataElements.Select(e => e.DataElementExtendedGuid));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RepeatedQueriesKeepIndependentKnowledgeAndFailuresWithoutDiscardingEarlierPayloads(bool buffered)
    {
        var provider = Memory();
        var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/queries.docx", MinimalDocx.Create(), TestActor.Value))!;
        var full = await Run(state, provider, Query(1), buffered);
        var fullElements = full.DataElementPackage!.DataElements;
        var knowledge = ClientKnowledge.FromElements(fullElements);
        var request = Query(11, knowledge);
        request.SubRequests.Add(Query(22).SubRequests[0]);
        request.SubRequests.Add(Query(33, maximum: 1).SubRequests[0]);
        var combined = await Run(state, provider, request, buffered);
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
    public async Task CellQueriesFollowDependenciesAndKeepOtherCellsOutOfKnowledge(bool buffered)
    {
        var provider = Memory();
        var state = await PublishGraph(provider);
        var f = new GraphFixture(MinimalDocx.Create("after"), blob: true);
        var response = await Run(state, provider, Query(1, cell: f.Cell), buffered);
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
        var union = await Run(state, provider, repeated, buffered);
        Assert.All(union.SubResponses, s => Assert.False(s.Status));
        Assert.Contains(union.DataElementPackage!.DataElements, e => e.DataElementExtendedGuid.Equals(f.OtherGroup));
        Assert.False(ClientKnowledge.Deserialize(new BinaryReaderEx(
            Assert.IsType<QueryChangesSubResponseData>(union.SubResponses[0].Data).KnowledgeBytes!))
            .Contains(new SerialNumber(other.Serial.Guid, other.Serial.Value)));
        var stale = new CellId(StorageIds.Restore(state.Partitions[0].Identity.CellLong), StorageIds.Restore(state.Partitions[0].Identity.CellShort));
        Assert.True(Assert.Single((await Run(state, provider, Query(3, cell: stale), buffered)).SubResponses).Status);
    }

    [Fact]
    public async Task ScopedSerialAmbiguityIsDetectedAcrossOtherCells()
    {
        var provider = Memory();
        var state = await PublishGraph(provider);
        var f = new GraphFixture(MinimalDocx.Create("after"), blob: true);
        var other = state.Partitions[0].Elements.Single(e => e.Id == StorageIds.Capture(f.OtherGroup));
        var shared = other.Serial;
        await provider.State.TransitionAsync(state.ResourceId, (current, now) => new StateTransition<bool>(current with
        { Partitions = current.Partitions.Select(p => p.Kind != 0 ? p : p with
            { Elements = p.Elements.Select(e => e.Id == StorageIds.Capture(f.BaseGroup)
                ? e with { Serial = shared } : e).ToImmutableArray() }).ToImmutableArray() }, true));
        var known = ClientKnowledge.FromElements([new DataElement(DataElementType.ObjectGroupDataElementData,
            f.OtherGroup, new SerialNumber(shared.Guid, shared.Value))]);
        var query = await new CellBridgeDocumentService(provider).ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents,
            Query(1, known, cell: f.Cell), new Dictionary<string, string>(), TestActor.Value);
        Assert.Contains(query.Response.DataElementPackage!.DataElements, e => e.DataElementExtendedGuid.Equals(f.BaseGroup));
        Assert.DoesNotContain(query.Response.DataElementPackage.DataElements, e => e.DataElementExtendedGuid.Equals(f.OtherGroup));
    }

    [Fact]
    public async Task QuerySaveQueryPreservesBothImmutableGraphsAndVersions()
    {
        var provider = Memory();
        var service = new CellBridgeDocumentService(provider);
        var before = (await service.CreateAsync("/order.docx", MinimalDocx.Create(), TestActor.Value))!;
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

    private static StorageProvider Memory() => new(new InMemoryStateStore(), new InMemoryContentStore());
    private static async Task<DocumentState> PublishGraph(StorageProvider provider)
    {
        var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/scopes.docx", MinimalDocx.Create(), TestActor.Value))!;
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
    private static async Task<FsshttpbResponse> Run(DocumentState state, StorageProvider provider, FsshttpbCellRequest request, bool buffered)
    {
        if (buffered)
        {
            var document = await StoredDocument.RestoreAsync(state, provider.Content);
            return CellBinaryRequestExecutor.Execute(document, document.FilePartition, request, DocumentAccess.Read | DocumentAccess.Write);
        }
        return (await new CellBridgeDocumentService(provider).ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents,
            request, new Dictionary<string, string>(), TestActor.Value)).Response;
    }
}
