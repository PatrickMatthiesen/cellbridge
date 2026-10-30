using System.Text.Json;
using System.Text.Json.Nodes;
using CellBridge.AspNetCore;
using CellBridge.FssHttp;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using CellBridge.Storage.PostgreSql;
using CellBridge.TestFixtures;
using Npgsql;

namespace CellBridge.Storage.Tests;

[Collection("PostgreSQL")]
public sealed class LockTransitionPersistenceTests
{
    [Fact]
    public Task IndependentCoordinatorsPersistMembershipAndAcknowledgement() =>
        Lifecycle(StorageTestDocuments.Memory());

    [PostgreSqlFact]
    public async Task PostgreSqlIndependentCoordinatorsPersistMembershipAndAcknowledgement()
    {
        await using var source = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("ConnectionStrings__cellbridge")!);
        await Lifecycle(new(new PostgreSqlStateStore(source), new PostgreSqlContentStore(source)));
    }

    [Fact]
    public async Task OldJsonStatePromotesOwnerOnlyOnExplicitCoauthorRefresh()
    {
        var provider = StorageTestDocuments.Memory();
        var state = (await new CellBridgeDocumentService(provider).CreateAsync("/legacy.docx", MinimalDocx.Create(), TestActor.Value))!;
        var client = Guid.NewGuid().ToString(); var schema = Guid.NewGuid().ToString();
        var initial = (await provider.Documents.ReadControlAsync(state.ResourceId))!;
        var granted = await provider.Documents.ExecuteControlAsync(new DocumentControlCommand.Office(state.ResourceId,
            TestStorage.Admit(initial.Head), Request("SchemaLockRequestType", "GetLock", client, schema)));
        Assert.Null(granted.ErrorCode);
        var json = JsonNode.Parse(JsonSerializer.Serialize(granted.Current))!;
        json["Coordination"]!.AsObject().Remove("CoauthorClients");
        json["Coordination"]!.AsObject().Remove("CoauthorTransitionPending");
        var restored = json.Deserialize<DocumentControlRead>()!;
        Assert.Empty(restored.Coordination.CoauthorClients);
        var second = StoredDocument.RestoreControl(restored);
        var next = FssHttpLockCoordinator.Restore(second, restored.Coordination, restored.UtcNow, TestActor.Value.Identity);
        var rejected = new FssHttpSubResponse();
        next.ApplyCoauthTransition(Request("CoauthRequestType", "MarkTransitionComplete", client, schema), rejected);
        Assert.Equal("InvalidCoauthSession", rejected.ErrorCode);
        Assert.Equal(LockOperationResult.Refreshed,
            next.ApplyCoauthSession(second, Request("CoauthRequestType", "RefreshCoauthoring", client, schema), new()));
        Assert.Equal(client, Assert.Single(next.Capture().CoauthorClients));
        Assert.Equal(TestActor.Value.Identity, Assert.Single(second.Sessions).Owner);
    }

    private static async Task Lifecycle(StorageProvider provider)
    {
        var state = (await new CellBridgeDocumentService(provider).CreateAsync("/" + Guid.NewGuid().ToString("N") + ".docx",
            MinimalDocx.Create(), TestActor.Value))!;
        string first = Guid.NewGuid().ToString(), second = Guid.NewGuid().ToString(), schema = Guid.NewGuid().ToString();
        await Transition("JoinCoauthoring", first);
        await Transition("JoinCoauthoring", second);
        var joined = (await provider.Documents.ReadControlAsync(state.ResourceId))!;
        Assert.True(joined.Coordination.CoauthorTransitionPending);
        Assert.Equal(2, joined.Coordination.CoauthorClients.Length);
        Assert.Equal(state.Package, joined.Head.Package);
        Assert.Equal(state.ContentVersion, joined.Head.ContentVersion);
        // Fresh metadata/coordinator instances on every transaction model host restart/handoff.
        await Transition("ExitCoauthoring", second);
        var remaining = (await provider.Documents.ReadControlAsync(state.ResourceId))!;
        Assert.True(remaining.Coordination.CoauthorTransitionPending);
        await Transition("MarkTransitionComplete", first);
        Assert.False((await provider.Documents.ReadControlAsync(state.ResourceId))!.Coordination.CoauthorTransitionPending);
        await Transition("ConvertToExclusive", first);
        var exclusive = (await provider.Documents.ReadControlAsync(state.ResourceId))!;
        Assert.NotNull(exclusive.Coordination.Exclusive);
        Assert.Empty(exclusive.Coordination.CoauthorClients); Assert.Empty(exclusive.Editors);
        Assert.Equal(state.Package, exclusive.Head.Package);
        Assert.Equal(state.ContentVersion, exclusive.Head.ContentVersion);

        async Task Transition(string operation, string client)
        {
            var current = (await provider.Documents.ReadControlAsync(state.ResourceId))!;
            var result = await provider.Documents.ExecuteControlAsync(new DocumentControlCommand.Office(state.ResourceId,
                TestStorage.Admit(current.Head), Request("CoauthRequestType", operation, client, schema)));
            Assert.Null(result.ErrorCode);
        }
    }

    private static FssHttpSubRequest Request(string key, string operation, string client, string schema)
    {
        var request = new FssHttpSubRequest
        {
            Type = key == "SchemaLockRequestType" ? SubRequestType.SchemaLock : SubRequestType.Coauth,
            SubRequestDataAttributes =
            {
                [key] = operation, ["ClientID"] = client, ["SchemaLockID"] = schema, ["Timeout"] = "3600",
                ["ExclusiveLockID"] = "11111111-1111-1111-1111-111111111111",
            },
        };
        if (operation == "ConvertToExclusive")
            request.SubRequestDataAttributes["ReleaseLockOnConversionToExclusiveFailure"] = "false";
        return request;
    }
}
