using CellBridge.AspNetCore;
using CellBridge.FssHttpB;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using CellBridge.TestFixtures;

namespace CellBridge.Benchmarks.Tests;

public sealed class BenchmarkClientGraphTests
{
    [Fact]
    public async Task IncrementalClientUsesServerTokensRetainsMappingsAndReplaysWithoutAnotherCommit()
    {
        var content = new InMemoryContentStore();
        var provider = new StorageProvider(new InMemoryDocumentRepository(content), content);
        var service = new CellBridgeDocumentService(provider);
        var actor = new CellBridgeActor(new("benchmark:client", "client", "Client"), CanCreate: true);
        var workload = new IncrementalOfficeWorkload(256);
        var head = (await service.CreateAsync("/client.docx", workload.CreateContent(), actor))!;
        var graph = await QueryAsync(service, head, actor);
        var original = graph;
        for (var revision = 1; revision <= 3; revision++)
        {
            var save = workload.CreateSave(graph, revision);
            var request = FsshttpbCellRequest.Deserialize(new(save.Binary));
            var put = (PutChangesSubRequestData)request.SubRequests.Single().Data!;
            Assert.Equal(graph.StorageIndex, put.ExpectedStorageIndex);
            Assert.Contains(request.DataElementPackage!.DataElements, element =>
                element.DataElementExtendedGuid.Equals(put.ExpectedStorageIndex) &&
                element.Data!.AsSpan().SequenceEqual(BenchmarkClientGraph.CurrentIndex(graph).Data!));
            if (revision > 1)
            {
                Assert.True(save.ReusedChunks > 0);
                Assert.Equal(1, save.SentChunks);
                Assert.True(save.Binary.Length < save.Content.Length / 2);
            }
            var accepted = await service.ExecuteAsync(head.ResourceId, DocumentPartitionKind.FileContents,
                request, new Dictionary<string, string>(), actor);
            AssertSuccess(accepted);
            var response = FsshttpbResponse.Deserialize(new(accepted.Response.ToByteArray()));
            var applied = ((PutChangesSubResponseData)response.SubResponses.Single().Data!).PutChangesResponse!.AppliedStorageIndexID;
            Assert.NotEqual(put.StorageIndex, applied);
            Assert.NotEqual(graph.StorageIndex, applied);
            graph = BenchmarkClientGraph.ApplyAcceptedGraph(graph, request, response, applied);
            Assert.Equal(applied, graph.StorageIndex);
            Assert.Equal(save.Content, graph.Materialize());
            Assert.True(graph.AnalyzeRetention().Complete);
            var server = await QueryAsync(service, accepted.State, actor);
            Assert.Equal(MappingDescriptions(server), MappingDescriptions(graph));
            Assert.Equal(server.Elements.Where(e => e.DataElementType != DataElementType.StorageIndexDataElementData)
                .OrderBy(e => e.DataElementExtendedGuid.Guid).ThenBy(e => e.DataElementExtendedGuid.Value).Select(e => e.SerialNumber),
                graph.Elements.Where(e => e.DataElementType != DataElementType.StorageIndexDataElementData)
                .OrderBy(e => e.DataElementExtendedGuid.Guid).ThenBy(e => e.DataElementExtendedGuid.Value).Select(e => e.SerialNumber));
            var followup = new FsshttpbCellRequest { SubRequests = { new(RequestTypes.QueryChanges) { RequestId=7,
                Data = new QueryChangesSubRequestData { IncludeStorageManifest=true,IncludeCellChanges=true,
                    Knowledge=ClientKnowledge.Deserialize(new(((PutChangesSubResponseData)response.SubResponses[0].Data!).KnowledgeBytes!)) } } } };
            var queried = await service.ExecuteAsync(head.ResourceId,DocumentPartitionKind.FileContents,followup,new Dictionary<string,string>(),actor);
            graph = BenchmarkClientGraph.ApplyQueryGraph(graph,queried.Response,
                Assert.IsType<QueryChangesSubResponseData>(queried.Response.SubResponses[0].Data).StorageIndexExtendedGuid);
            Assert.Equal(save.Content,graph.Materialize());
            var reordered = new FsshttpbResponse { DataElementPackage = new() { DataElements = server.Elements.ToList() } };
            var logicalIndex = reordered.DataElementPackage.DataElements.Single(e => e.DataElementExtendedGuid.Equals(applied));
            logicalIndex.Data = StorageIndexPatch.Serialize(StorageIndexPatch.Read(logicalIndex).Reverse());
            graph = BenchmarkClientGraph.ApplyQueryGraph(graph,reordered,applied);
            Assert.Equal(save.Content,graph.Materialize());
            await using var package = await provider.Packages.OpenReadAsync(accepted.State.Package);
            using var copy = new MemoryStream();
            await package.CopyToAsync(copy);
            Assert.Equal(save.Content, copy.ToArray());
            var replay = await service.ExecuteAsync(head.ResourceId, DocumentPartitionKind.FileContents,
                request, new Dictionary<string, string>(), actor);
            AssertSuccess(replay);
            Assert.Equal(accepted.State.Package, replay.State.Package);
            Assert.Equal(accepted.State.StateVersion, replay.State.StateVersion);
            Assert.Equal(revision, replay.State.Counters.SaveReceipts);
            Assert.Equal(applied, ((PutChangesSubResponseData)replay.Response.SubResponses.Single().Data!).PutChangesResponse!.AppliedStorageIndexID);
            head = accepted.State;
        }
        Assert.Equal(workload.CreateContent(), original.Materialize());
        // A null mapping removes only that key; it does not replace the rest of the index.
        var deletion = new BinaryWriterEx();
        original.Revision!.Serialize(deletion);
        ExGuid.Null.Serialize(deletion);
        SerialNumber.Null.Serialize(deletion);
        var payload = deletion.ToArray();
        var writer = new BinaryWriterEx();
        new StreamObjectHeaderStart16Bit(StreamObjectTypeHeaderStart.StorageIndexRevisionMapping, payload.Length).Serialize(writer);
        writer.WriteBytes(payload);
        var next = new ExGuid(1, Guid.NewGuid());
        var patch = new FsshttpbResponse { DataElementPackage = new() };
        patch.DataElementPackage.DataElements.Add(new(DataElementType.StorageIndexDataElementData, next, new(Guid.NewGuid(), 1))
            { Data = writer.ToArray() });
        var updated = BenchmarkClientGraph.ApplyAcceptedGraph(graph, new() { DataElementPackage = new() }, patch, next);
        Assert.DoesNotContain(StorageIndexMappingSerials.ReadMappings(BenchmarkClientGraph.CurrentIndex(updated).Data!),
            mapping => mapping.Revision?.Equals(original.Revision) == true);
        Assert.Equal(workload.CreateContent(3), updated.Materialize());
        Assert.Throws<InvalidDataException>(() => BenchmarkClientGraph.ApplyAcceptedGraph(graph,
            new() { DataElementPackage = new() }, new(), new ExGuid(2, Guid.NewGuid())));
    }

    [Fact]
    public async Task CompetingClientWithStaleExpectedMappingsFailsWithoutAdvancingItsGraph()
    {
        var content = new InMemoryContentStore();
        var provider = new StorageProvider(new InMemoryDocumentRepository(content), content);
        var service = new CellBridgeDocumentService(provider);
        var actor = new CellBridgeActor(new("benchmark:client", "client", "Client"), CanCreate: true);
        var workload = new IncrementalOfficeWorkload(256);
        var head = (await service.CreateAsync("/contended.docx", workload.CreateContent(), actor))!;
        var stale = await QueryAsync(service, head, actor);
        var first = workload.CreateSave(stale, 1);
        var second = workload.CreateSave(stale, 2);
        var accepted = await service.ExecuteAsync(head.ResourceId, DocumentPartitionKind.FileContents,
            first.Request, new Dictionary<string, string>(), actor);
        AssertSuccess(accepted);
        var rejected = await service.ExecuteAsync(head.ResourceId, DocumentPartitionKind.FileContents,
            second.Request, new Dictionary<string, string>(), actor);
        Assert.True(rejected.Response.SubResponses.Single().Status);
        Assert.Equal((ulong)CellErrorCode.CoherencyFailure, rejected.Response.SubResponses.Single().Error!.ErrorCode);
        Assert.Equal(accepted.State.Package, rejected.State.Package);
        Assert.Equal(1, rejected.State.Counters.SaveReceipts);
        Assert.Equal(workload.CreateContent(), stale.Materialize());
    }

    private static string[] MappingDescriptions(PartitionGraphSnapshot graph) =>
        StorageIndexMappingSerials.ReadMappings(BenchmarkClientGraph.CurrentIndex(graph).Data!).Select(mapping =>
            $"{mapping.Type}/{mapping.Cell?.LongId}/{mapping.Cell?.ShortId}/{mapping.Revision}/{mapping.Target}/{mapping.Serial.Guid}/{mapping.Serial.Value}")
            .Order(StringComparer.Ordinal).ToArray();

    private static async Task<PartitionGraphSnapshot> QueryAsync(CellBridgeDocumentService service, DocumentHead head, CellBridgeActor actor)
    {
        var request = new FsshttpbCellRequest();
        request.SubRequests.Add(new(RequestTypes.QueryChanges) { RequestId = 1,
            Data = new QueryChangesSubRequestData { IncludeStorageManifest = true, IncludeCellChanges = true } });
        var result = await service.ExecuteAsync(head.ResourceId, DocumentPartitionKind.FileContents, request,
            new Dictionary<string, string>(), actor);
        AssertSuccess(result);
        var query = Assert.IsType<QueryChangesSubResponseData>(result.Response.SubResponses.Single().Data);
        return PartitionGraphSnapshot.Create(result.Response.DataElementPackage!.DataElements, query.StorageIndexExtendedGuid);
    }

    private static void AssertSuccess(CellExecution result)
    {
        Assert.False(result.IsCancelled);
        Assert.False(result.Response.Status);
        Assert.False(Assert.Single(result.Response.SubResponses).Status);
    }
}
