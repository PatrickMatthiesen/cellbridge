using CellBridge.AspNetCore;
using CellBridge.FssHttp;
using CellBridge.FssHttpB;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.TestFixtures;

namespace CellBridge.SqlServer.Tests;

[Collection("SQL Server")]
public sealed class SqlServerProtocolCoordinationTests(SqlServerFixture fixture)
{
    [Fact]
    public async Task OuterCoauthorFallbackSaveReplayAuthorizationAndExactResourceIdentity()
    {
        var database = await SqlServerDocuments.CreateAsync(fixture);
        var provider = database.Provider();
        var initial = (await new CellBridgeDocumentService(provider).CreateAsync("/fallback.docx", MinimalDocx.Create(), SqlServerDocuments.Actor))!;
        var request = Save(initial);
        var saved = await Processor().ExecuteAsync(request, "https://host.test", SqlServerDocuments.Actor);
        AssertResponses(saved.Response);
        var accepted = Assert.Single(saved.AcceptedSaves);
        Assert.False(accepted.IsReplay);
        var replay = await Processor().ExecuteAsync(request, "https://host.test", SqlServerDocuments.Actor);
        AssertResponses(replay.Response);
        Assert.Equal(accepted with { IsReplay = true }, Assert.Single(replay.AcceptedSaves));
        var current = (await provider.State.FindByResourceIdAsync(initial.ResourceId))!;
        Assert.Equal(accepted.Content, current.Content);
        Assert.Single(current.Receipts);
        var reader = SqlServerDocuments.Actor with { AccessLimit = new(current.ResourceId, DocumentAccess.Read) };
        var denied = await Processor().ExecuteAsync(Save(current), "https://host.test", reader);
        Assert.Empty(denied.AcceptedSaves);
        Assert.NotEqual("Success", denied.Response.Responses[0].SubResponses[2].ErrorCode);

        var missing = File(Guid.NewGuid(), SubRequestType.GetDocMetaInfo);
        missing.Url = "https://host.test/fallback.docx";
        var unknown = await Processor().ExecuteAsync(new() { Requests = { missing } }, "https://host.test", SqlServerDocuments.Actor);
        Assert.NotEqual("Success", Assert.Single(Assert.Single(unknown.Response.Responses).SubResponses).ErrorCode);
        Assert.Single(await provider.State.ListAsync(0, 100));
        Assert.Equal(current.ContentVersion, (await provider.State.FindByResourceIdAsync(current.ResourceId))!.ContentVersion);
        await database.AssertAccountingAsync();

        CellBridgeRequestProcessor Processor() => new(new CellBridgeDocumentService(database.Provider()));
    }

    [Fact]
    public async Task HostLockFencingAndCoauthorMembershipSurviveIndependentProviders()
    {
        var database = await SqlServerDocuments.CreateAsync(fixture);
        var first = database.Provider(); var second = database.Provider();
        var state = (await new CellBridgeDocumentService(first).CreateAsync("/coordination.docx", MinimalDocx.Create(), SqlServerDocuments.Actor))!;
        var host = new SharedDocumentLocks(new(first));
        var peer = new SharedDocumentLocks(new(second));
        var acquired = await host.ApplyAsync(state.ResourceId, HostLockOperation.Acquire, "CaseSensitive", SqlServerDocuments.Actor);
        Assert.True(acquired.Success);
        Assert.False((await peer.ApplyAsync(state.ResourceId, HostLockOperation.Acquire, "casesensitive", SqlServerDocuments.Actor)).Success);
        Assert.True((await peer.ApplyAsync(state.ResourceId, HostLockOperation.Refresh, "CaseSensitive", SqlServerDocuments.Actor)).Success);
        var current = (await second.State.FindByResourceIdAsync(state.ResourceId))!;
        Assert.False(await host.TryCommitAsync(acquired.WriteToken!, current.StateVersion, SqlServerDocuments.Actor, (row, _) => row, Guid.NewGuid()));
        var replaced = await host.ApplyAsync(state.ResourceId, HostLockOperation.Replace, "CaseSensitive", SqlServerDocuments.Actor, "replacement");
        Assert.True(replaced.Success);
        current = (await second.State.FindByResourceIdAsync(state.ResourceId))!;
        Assert.True(await peer.TryCommitAsync(replaced.WriteToken!, current.StateVersion, SqlServerDocuments.Actor, (row, _) => row, Guid.NewGuid()));
        Assert.True((await peer.ApplyAsync(state.ResourceId, HostLockOperation.Release, "replacement", SqlServerDocuments.Actor)).Success);
        Assert.False(await host.TryCommitAsync(replaced.WriteToken!, current.StateVersion, SqlServerDocuments.Actor, (row, _) => row, Guid.NewGuid()));

        var client = Guid.NewGuid().ToString(); var other = Guid.NewGuid().ToString(); var schema = Guid.NewGuid().ToString();
        await Transition("JoinCoauthoring", client);
        await Transition("JoinCoauthoring", other);
        current = (await second.State.FindByResourceIdAsync(state.ResourceId))!;
        Assert.Equal(2, current.Coordination.CoauthorClients.Length);
        Assert.True(current.Coordination.CoauthorTransitionPending);
        await Transition("ExitCoauthoring", other);
        await Transition("MarkTransitionComplete", client);
        await Transition("ConvertToExclusive", client);
        current = (await first.State.FindByResourceIdAsync(state.ResourceId))!;
        Assert.NotNull(current.Coordination.Exclusive);
        Assert.Empty(current.Coordination.CoauthorClients);
        Assert.Equal(state.Content, current.Content);
        Assert.Equal(state.ContentVersion, current.ContentVersion);
        await database.AssertAccountingAsync();

        async Task Transition(string operation, string id) => await database.Provider().State.TransitionAsync(state.ResourceId, (row, now) =>
        {
            var document = StoredDocument.RestoreMetadata(row, now);
            var coordinator = FssHttpLockCoordinator.Restore(document, row.Coordination, now, SqlServerDocuments.Actor.Identity);
            var request = new FssHttpSubRequest { SubRequestDataAttributes =
            {
                ["CoauthRequestType"] = operation, ["ClientID"] = id, ["SchemaLockID"] = schema,
                ["Timeout"] = "3600", ["ExclusiveLockID"] = "11111111-1111-1111-1111-111111111111"
            } };
            if (operation == "ConvertToExclusive")
                request.SubRequestDataAttributes["ReleaseLockOnConversionToExclusiveFailure"] = "false";
            var response = new FssHttpSubResponse();
            coordinator.ApplyCoauthSession(document, request, response);
            Assert.Null(response.ErrorCode);
            return new StateTransition<bool>(document.CaptureCoordination(row, coordinator.Capture()), true);
        });
    }

    private static CellStorageRequest Save(DocumentState state)
    {
        var file = File(state.ResourceId, SubRequestType.Coauth, SubRequestType.SchemaLock, SubRequestType.Cell);
        var client = Guid.NewGuid().ToString(); var schema = Guid.NewGuid().ToString();
        var join = file.SubRequests[0];
        join.SubRequestDataAttributes["CoauthRequestType"] = "JoinCoauthoring";
        foreach (var subrequest in file.SubRequests.Take(2))
        {
            subrequest.SubRequestDataAttributes["ClientID"] = client;
            subrequest.SubRequestDataAttributes["SchemaLockID"] = schema;
            subrequest.SubRequestDataAttributes["Timeout"] = "2580";
        }
        var fallback = file.SubRequests[1];
        fallback.DependsOn = join.SubRequestToken; fallback.DependencyType = "OnNotSupported";
        fallback.SubRequestDataAttributes["SchemaLockRequestType"] = "GetLock";
        var cell = file.SubRequests[2];
        cell.DependsOn = fallback.SubRequestToken; cell.DependencyType = "OnSuccessOrNotSupported";
        cell.SubRequestDataAttributes["BypassLockID"] = schema;
        cell.SubRequestDataAttributes["SchemaLockID"] = schema;
        cell.SubRequestDataBinary = SqlServerDocumentTests.Proposal(state, MinimalDocx.Create("outer save")).ToByteArray();
        cell.SubRequestDataAttributes["BinaryDataSize"] = cell.SubRequestDataBinary.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return new() { Requests = { file } };
    }

    private static FssHttpRequest File(Guid id, params SubRequestType[] types) => new()
    {
        UseResourceId = true, ResourceId = id.ToString("D"), Url = "https://host.test/unused.docx", RequestToken = 1,
        SubRequests = types.Select((type, index) => new FssHttpSubRequest
        { Type = type, SubRequestToken = (ulong)index + 1, SubRequestDataXml = type == SubRequestType.Cell ? null : "<SubRequestData />" }).ToList()
    };

    private static void AssertResponses(CellStorageResponse response)
    {
        var parts = Assert.Single(response.Responses).SubResponses;
        Assert.Equal("Success", parts[0].ErrorCode);
        Assert.Equal("DependentOnlyOnNotSupportedRequestGetSupported", parts[1].ErrorCode);
        Assert.Equal("Success", parts[2].ErrorCode);
        Assert.False(Assert.Single(FsshttpbResponse.Deserialize(new(parts[2].SubResponseDataBase64!)).SubResponses).Status);
    }
}
