using System.Text.Json;
using CellBridge.AspNetCore;
using CellBridge.FssHttp;
using CellBridge.FssHttpB;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using CellBridge.Web;

namespace CellBridge.Storage.Tests;

public sealed class SaveAcknowledgementTests
{
    [Theory]
    [InlineData(false, 0x04)] // Word: CheckForIdReuse, no requested index or added IDs.
    [InlineData(true, 0x04)]
    [InlineData(false, 0x06)] // Explicitly requests added IDs.
    [InlineData(true, 0x06)]
    [InlineData(false, 0x8004)] // Excel also sets reserved bit 15.
    [InlineData(true, 0x8004)]
    public async Task CompleteSaveAcknowledgesUniqueAdditionsAndRetainsAuthoritativeKnowledge(bool buffered, ushort flags)
    {
        var provider = new StorageProvider(new InMemoryStateStore(), new InMemoryContentStore());
        var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/ack.docx", MinimalDocx.Create(), TestActor.Value))!;
        var document = await StoredDocument.RestoreAsync(state, provider.Content);
        var existing = document.FilePartition.FileGraph.Elements.ToDictionary(e => e.DataElementExtendedGuid);
        var request = StorageTests.Fixture("save-first");
        var put = Assert.IsType<PutChangesSubRequestData>(request.SubRequests[0].Data);
        put.ExpectedStorageIndex = document.FilePartition.FileGraph.StorageIndex;
        put.AdditionalFlagsBits = flags;
        request.DataElementPackage!.DataElements.Add(request.DataElementPackage.DataElements[0]);
        request.DataElementPackage.DataElements.Add(existing.Values.First());
        var addedIds = request.DataElementPackage.DataElements.Select(e => e.DataElementExtendedGuid)
            .Where(id => !existing.ContainsKey(id)).Distinct().ToArray();

        FsshttpbResponse response;
        if (buffered)
        {
            response = CellBinaryRequestExecutor.Execute(document, document.FilePartition, request,
                DocumentAccess.Read | DocumentAccess.Write);
            Assert.Equal(state.ContentVersion + 1, document.ContentVersion);
        }
        else
        {
            var saved = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents,
                request, new Dictionary<string, string>(), TestActor.Value);
            Assert.Equal(state.ContentVersion + 1, saved.State.ContentVersion);
            response = saved.Response;
            document = await StoredDocument.RestoreAsync(saved.State, provider.Content);
            var retry = await new CellBridgeDocumentService(provider).ExecuteAsync(state.ResourceId,
                DocumentPartitionKind.FileContents, request, new Dictionary<string, string>(), TestActor.Value);
            Assert.Equal(saved.State.StateVersion, retry.State.StateVersion);
            Assert.Equal(response.ToByteArray(), retry.Response.ToByteArray());
        }

        var sub = Assert.Single(response.SubResponses);
        Assert.False(sub.Status, sub.Error?.ErrorMessage);
        // Check the actual wire object, not just the values prepared by the handler.
        var decoded = FsshttpbResponse.Deserialize(new(response.ToByteArray()));
        var acknowledgement = Assert.IsType<PutChangesSubResponseData>(decoded.SubResponses[0].Data);
        Assert.True(acknowledgement.PutChangesResponse!.AppliedStorageIndexID.IsNull);
        Assert.NotEmpty(addedIds);
        Assert.Equal(addedIds, acknowledgement.PutChangesResponse.DataElementAdded);
        Assert.NotEmpty(acknowledgement.SerialNumberReassignAllBytes!);
        var knowledge = ClientKnowledge.Deserialize(new(acknowledgement.KnowledgeBytes!));
        foreach (var element in document.FilePartition.FileGraph.Elements)
            Assert.True(knowledge.Contains(element.SerialNumber));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReceiptFreeRepeatCannotAcknowledgeUnpublishedElements(bool buffered)
    {
        var provider = new StorageProvider(new InMemoryStateStore(), new InMemoryContentStore());
        var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/repeat.docx", MinimalDocx.Create(), TestActor.Value))!;
        var document = await StoredDocument.RestoreAsync(state, provider.Content);
        var graph = document.FilePartition.FileGraph;
        var knownIndex = graph.Elements.Single(e => e.DataElementExtendedGuid.Equals(graph.StorageIndex));
        var request = new FsshttpbCellRequest
        {
            DataElementPackage = new DataElementPackage(),
            SubRequests = { new(RequestTypes.PutChanges) { RequestId = 1, Data = new PutChangesSubRequestData
                { StorageIndex = graph.StorageIndex, ExpectedStorageIndex = graph.StorageIndex, AdditionalFlagsBits = 0x04 } } },
        };
        request.DataElementPackage.DataElements.AddRange(graph.Elements);
        var newId = new ExGuid(1, Guid.NewGuid());
        request.DataElementPackage.DataElements.Add(new(knownIndex.DataElementType, newId, SerialNumber.Null)
            { Data = knownIndex.Data!.ToArray() });
        FsshttpbResponse response;
        if (buffered)
        {
            response = CellBinaryRequestExecutor.Execute(document, document.FilePartition, request,
                DocumentAccess.Read | DocumentAccess.Write);
            Assert.Same(graph, document.FilePartition.FileGraph);
            Assert.Equal(state.ContentVersion, document.ContentVersion);
            Assert.Equal(state.Partitions[0].Knowledge, document.FilePartition.KnowledgeSequence);
        }
        else
        {
            var result = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents,
                request, new Dictionary<string, string>(), TestActor.Value);
            response = result.Response;
            Assert.Equal(state.StateVersion, result.State.StateVersion);
            Assert.Equal(state.Content, result.State.Content);
            Assert.Equal(state.ContentVersion, result.State.ContentVersion);
            Assert.Empty(result.State.Receipts);
            Assert.DoesNotContain(result.State.Partitions[0].Elements, e => StorageIds.Restore(e.Id).Equals(newId));
        }
        var sub = Assert.Single(response.SubResponses);
        Assert.True(sub.Status);
        Assert.Equal((ulong)CellErrorCode.InvalidObject, sub.Error!.ErrorCode);
        Assert.Null(sub.Data);
        Assert.Null(response.DataElementPackage);
    }

    [Theory]
    [InlineData("save-first")]
    [InlineData("save-second")]
    public void SharePointWordAcknowledgesAdditionsWithoutRequestingThem(string name)
    {
        var request = StorageTests.Fixture(name);
        var put = Assert.IsType<PutChangesSubRequestData>(request.SubRequests[0].Data);
        Assert.Equal(0x49, put.Flags);
        Assert.Equal(0x04, put.AdditionalFlagsBits);
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name + ".json")));
        var side = fixture.RootElement.GetProperty("response");
        var parts = MtomMessageParser.Parse(Convert.FromBase64String(side.GetProperty("bodyBase64").GetString()!),
            side.GetProperty("contentType").GetString()!);
        var binary = Assert.Single(parts, p => p.ContentType.Contains("application/octet-stream"));
        var response = FsshttpbResponse.Deserialize(new(binary.Content));
        var acknowledgement = Assert.IsType<PutChangesSubResponseData>(response.SubResponses[0].Data);
        Assert.True(acknowledgement.PutChangesResponse!.AppliedStorageIndexID.IsNull);
        Assert.NotEmpty(acknowledgement.PutChangesResponse.DataElementAdded);
    }
}
