using System.IO.Compression;
using CellBridge.AspNetCore;
using CellBridge.FssHttp;
using CellBridge.FssHttpB;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.TestFixtures;

namespace CellBridge.Storage.Tests;

[Collection("PostgreSQL")]
public sealed class CoauthoringFallbackSaveTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SuccessfulCoauthoringAllowsSaveAndReplayAfterSkippedSchemaFallback(bool postgreSql)
    {
        await using var test = ProtocolExecutionTestProvider.Create(postgreSql);
        var service = new CellBridgeDocumentService(test.Provider);
        var state = (await service.CreateAsync("/fallback-" + Guid.NewGuid().ToString("N") + ".docx",
            MinimalDocx.Create(), TestActor.Value))!;
        var request = await Save(service,state);
        var processor = new CellBridgeRequestProcessor(service);

        var saved = await processor.ExecuteAsync(request, "https://host.test", TestActor.Value);
        AssertResponses(saved.Response);
        var accepted = Assert.Single(saved.AcceptedSaves);
        Assert.False(accepted.IsReplay);
        Assert.Equal(state.ContentVersion + 1, accepted.ContentVersion);
        var stored = (await test.Provider.Documents.FindAsync(new DocumentIdentity.Resource(state.ResourceId)))!;
        Assert.Equal(accepted.Package, stored.Package);
        Assert.Equal(accepted.ContentVersion, stored.ContentVersion);
        Assert.Equal(1,stored.Counters.SaveReceipts);
        await using (var content = await test.Provider.Packages.OpenReadAsync(stored.Package))
        {
            using var downloaded = new MemoryStream();
            await content.CopyToAsync(downloaded);
            downloaded.Position = 0;
            using var package = new ZipArchive(downloaded, ZipArchiveMode.Read);
            Assert.NotNull(package.GetEntry("word/document.xml"));
        }

        var replay = await processor.ExecuteAsync(request, "https://host.test", TestActor.Value);
        AssertResponses(replay.Response);
        Assert.Equal(accepted with { IsReplay = true }, Assert.Single(replay.AcceptedSaves));
        Assert.Equal(stored.ContentVersion,
            (await test.Provider.Documents.FindAsync(new DocumentIdentity.Resource(state.ResourceId)))!.ContentVersion);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConflictingLockStillPreventsSaveThroughSkippedFallback(bool postgreSql)
    {
        await using var test = ProtocolExecutionTestProvider.Create(postgreSql);
        var service = new CellBridgeDocumentService(test.Provider);
        var state = (await service.CreateAsync("/fallback-conflict-" + Guid.NewGuid().ToString("N") + ".docx",
            MinimalDocx.Create(), TestActor.Value))!;
        var processor = new CellBridgeRequestProcessor(service);
        var locked = HostIntegrationTests.File(state.ResourceId, SubRequestType.ExclusiveLock);
        var attributes = locked.SubRequests[0].SubRequestDataAttributes;
        attributes["ExclusiveLockRequestType"] = "GetLock";
        attributes["ExclusiveLockID"] = Guid.NewGuid().ToString();
        attributes["Timeout"] = "3600";
        var granted = await processor.ExecuteAsync(new() { Requests = { locked } }, "https://host.test", TestActor.Value);
        Assert.Equal("Success", granted.Response.Responses[0].SubResponses[0].ErrorCode);

        var rejected = await processor.ExecuteAsync(await Save(service,state), "https://host.test", TestActor.Value);
        Assert.NotEqual("Success", rejected.Response.Responses[0].SubResponses[0].ErrorCode);
        Assert.Equal("DependentOnlyOnNotSupportedRequestGetSupported", rejected.Response.Responses[0].SubResponses[1].ErrorCode);
        Assert.Equal("DependentOnlyOnSuccessRequestFailed", rejected.Response.Responses[0].SubResponses[2].ErrorCode);
        Assert.Empty(rejected.AcceptedSaves);
        var stored = (await test.Provider.Documents.FindAsync(new DocumentIdentity.Resource(state.ResourceId)))!;
        Assert.Equal(state.Package, stored.Package);
        Assert.Equal(state.ContentVersion, stored.ContentVersion);
        Assert.Equal(0,stored.Counters.SaveReceipts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadOnlyActorCannotSaveThroughCoauthoringFallback(bool postgreSql)
    {
        await using var test = ProtocolExecutionTestProvider.Create(postgreSql);
        var service = new CellBridgeDocumentService(test.Provider);
        var state = (await service.CreateAsync("/fallback-reader-" + Guid.NewGuid().ToString("N") + ".docx",
            MinimalDocx.Create(), TestActor.Value))!;
        var reader = TestActor.Value with { AccessLimit = new(state.ResourceId, DocumentAccess.Read) };
        var result = await new CellBridgeRequestProcessor(service).ExecuteAsync(await Save(service,state), "https://host.test", reader);
        Assert.Empty(result.AcceptedSaves);
        Assert.NotEqual("Success", result.Response.Responses[0].SubResponses[2].ErrorCode);
        var stored = (await test.Provider.Documents.FindAsync(new DocumentIdentity.Resource(state.ResourceId)))!;
        Assert.Equal(state.Package, stored.Package);
        Assert.Equal(state.ContentVersion, stored.ContentVersion);
        Assert.Equal(0,stored.Counters.SaveReceipts);
    }

    private static async Task<CellStorageRequest> Save(CellBridgeDocumentService service,DocumentHead state)
    {
        var file = HostIntegrationTests.File(state.ResourceId, SubRequestType.Coauth, SubRequestType.SchemaLock, SubRequestType.Cell);
        var client = Guid.NewGuid().ToString();
        var schema = Guid.NewGuid().ToString();
        var join = file.SubRequests[0];
        join.SubRequestDataAttributes["CoauthRequestType"] = "JoinCoauthoring";
        join.SubRequestDataAttributes["ClientID"] = client;
        join.SubRequestDataAttributes["SchemaLockID"] = schema;
        join.SubRequestDataAttributes["Timeout"] = "2580";
        var fallback = file.SubRequests[1];
        fallback.DependsOn = join.SubRequestToken;
        fallback.DependencyType = "OnNotSupported";
        fallback.SubRequestDataAttributes["SchemaLockRequestType"] = "GetLock";
        fallback.SubRequestDataAttributes["ClientID"] = client;
        fallback.SubRequestDataAttributes["SchemaLockID"] = schema;
        fallback.SubRequestDataAttributes["Timeout"] = "2580";
        var cell = file.SubRequests[2];
        cell.DependsOn = fallback.SubRequestToken;
        cell.DependencyType = "OnSuccessOrNotSupported";
        cell.SubRequestDataAttributes["BypassLockID"] = schema;
        cell.SubRequestDataAttributes["SchemaLockID"] = schema;
        var binary = StorageTests.Fixture("save-first");
        StorageTests.Rebase(binary,await StorageTestDocuments.GraphAsync(service,state));
        HostIntegrationTests.SetBinary(cell, binary.ToByteArray());
        return new() { Requests = { file } };
    }

    private static void AssertResponses(CellStorageResponse response)
    {
        var subResponses = Assert.Single(response.Responses).SubResponses;
        Assert.Equal("Success", subResponses[0].ErrorCode);
        Assert.Equal("DependentOnlyOnNotSupportedRequestGetSupported", subResponses[1].ErrorCode);
        Assert.Equal("Success", subResponses[2].ErrorCode);
        Assert.False(Assert.Single(FsshttpbResponse.Deserialize(new(subResponses[2].SubResponseDataBase64!)).SubResponses).Status);
    }
}
