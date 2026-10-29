using System.Text.Json;
using CellBridge.AspNetCore;
using CellBridge.FssHttp;
using CellBridge.FssHttpB;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.TestFixtures;

namespace CellBridge.Storage.Tests;

[Collection("PostgreSQL")]
public sealed class SaveAcknowledgementTests
{
    [Theory]
    [InlineData(false, 0x04)] // Word: CheckForIdReuse, no requested index or added IDs.
    [InlineData(true, 0x04)]
    [InlineData(false, 0x06)] // Explicitly requests added IDs.
    [InlineData(true, 0x06)]
    [InlineData(false, 0x8004)] // Excel also sets reserved bit 15.
    [InlineData(true, 0x8004)]
    public async Task CompleteSaveAcknowledgesUniqueAdditionsAndRetainsAuthoritativeKnowledge(bool postgreSql, ushort flags)
    {
        await using var testProvider = ProtocolExecutionTestProvider.Create(postgreSql);
        var provider = testProvider.Provider;
        var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/ack-" + Guid.NewGuid().ToString("N") + ".docx", MinimalDocx.Create(), TestActor.Value))!;
        var graph = await StorageTestDocuments.GraphAsync(service,state);
        var existing = graph.Elements.ToDictionary(e => e.DataElementExtendedGuid);
        var request = StorageTests.Fixture("save-first");
        var put = Assert.IsType<PutChangesSubRequestData>(request.SubRequests[0].Data);
        StorageTests.Rebase(request,graph);
        put.AdditionalFlagsBits = flags;
        request.DataElementPackage!.DataElements.Add(request.DataElementPackage.DataElements[0]);
        request.DataElementPackage.DataElements.Add(existing.Values.First());
        var addedIds = request.DataElementPackage.DataElements.Where(e => e.DataElementType != DataElementType.StorageIndexDataElementData).Select(e => e.DataElementExtendedGuid)
            .Where(id => !existing.ContainsKey(id)).Distinct().ToArray();

        var saved = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents,
            request, new Dictionary<string, string>(), TestActor.Value);
        Assert.Equal(state.ContentVersion + 1, saved.State.ContentVersion);
        var response = saved.Response;
        graph = await StorageTestDocuments.GraphAsync(service,saved.State);
        var retry = await new CellBridgeDocumentService(provider).ExecuteAsync(state.ResourceId,
            DocumentPartitionKind.FileContents, request, new Dictionary<string, string>(), TestActor.Value);
        Assert.Equal(saved.State.StateVersion, retry.State.StateVersion);
        Assert.Equal(response.ToByteArray(), retry.Response.ToByteArray());

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
        foreach (var element in graph.Elements.Where(e => addedIds.Contains(e.DataElementExtendedGuid)))
            Assert.True(knowledge.Contains(element.SerialNumber));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReceiptFreeRepeatCannotAcknowledgeUnpublishedElements(bool postgreSql)
    {
        await using var testProvider = ProtocolExecutionTestProvider.Create(postgreSql);
        var provider = testProvider.Provider;
        var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/repeat-" + Guid.NewGuid().ToString("N") + ".docx", MinimalDocx.Create(), TestActor.Value))!;
        var graph = await StorageTestDocuments.GraphAsync(service,state);
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
        var result = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents,
            request, new Dictionary<string, string>(), TestActor.Value);
        var response = result.Response;
        Assert.Equal(state.StateVersion, result.State.StateVersion);
        Assert.Equal(state.Package, result.State.Package);
        Assert.Equal(state.ContentVersion, result.State.ContentVersion);
        Assert.Equal(state, result.State);
        Assert.Equal(0,result.State.Counters.SaveReceipts);
        Assert.Empty(await provider.Documents.ReadElementsAsync(new(state.ResourceId,state.LifecycleGeneration,0),[StorageIds.Capture(newId)]));
        var sub = Assert.Single(response.SubResponses);
        Assert.False(sub.Status);
        Assert.Empty(Assert.IsType<PutChangesSubResponseData>(sub.Data).PutChangesResponse!.DataElementAdded);
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
