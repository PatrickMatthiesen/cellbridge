using System.Text.Json;
using System.Text.Json.Nodes;
using CellBridge.AspNetCore;
using CellBridge.FssHttp;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using CellBridge.Storage.PostgreSql;
using CellBridge.Web;
using Npgsql;

namespace CellBridge.Storage.Tests;

public sealed class LockTransitionPersistenceTests
{
    [Fact]
    public Task IndependentCoordinatorsPersistMembershipAndAcknowledgement() =>
        Lifecycle(new(new InMemoryStateStore(), new InMemoryContentStore()));

    [PostgreSqlFact]
    public async Task PostgreSqlIndependentCoordinatorsPersistMembershipAndAcknowledgement()
    {
        await using var source = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("ConnectionStrings__cellbridge")!);
        await Lifecycle(new(new PostgreSqlStateStore(source), new PostgreSqlContentStore(source)));
    }

    [Fact]
    public async Task OldJsonStatePromotesOwnerOnlyOnExplicitCoauthorRefresh()
    {
        var provider = new StorageProvider(new InMemoryStateStore(), new InMemoryContentStore());
        var state = (await new CellBridgeDocumentService(provider).CreateAsync("/legacy.docx", MinimalDocx.Create(), TestActor.Value))!;
        var now = DateTime.UtcNow;
        var client = Guid.NewGuid().ToString(); var schema = Guid.NewGuid().ToString();
        var document = StoredDocument.RestoreMetadata(state, now);
        var coordinator = FssHttpLockCoordinator.Restore(document, state.Coordination, now, TestActor.Value.Identity);
        coordinator.ApplySchemaLock(Request("SchemaLockRequestType", "GetLock", client, schema), new());
        var json = JsonNode.Parse(JsonSerializer.Serialize(document.CaptureCoordination(state, coordinator.Capture())))!;
        json["Coordination"]!.AsObject().Remove("CoauthorClients");
        json["Coordination"]!.AsObject().Remove("CoauthorTransitionPending");
        var restored = json.Deserialize<DocumentState>()!;
        Assert.Empty(restored.Coordination.CoauthorClients);
        var second = StoredDocument.RestoreMetadata(restored, now);
        var next = FssHttpLockCoordinator.Restore(second, restored.Coordination, now, TestActor.Value.Identity);
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
        var joined = (await provider.State.FindByResourceIdAsync(state.ResourceId))!;
        Assert.True(joined.Coordination.CoauthorTransitionPending);
        Assert.Equal(2, joined.Coordination.CoauthorClients.Length);
        Assert.Equal(state.Content, joined.Content);
        Assert.Equal(state.ContentVersion, joined.ContentVersion);
        // Fresh metadata/coordinator instances on every transaction model host restart/handoff.
        await Transition("ExitCoauthoring", second);
        var remaining = (await provider.State.FindByResourceIdAsync(state.ResourceId))!;
        Assert.True(remaining.Coordination.CoauthorTransitionPending);
        await Transition("MarkTransitionComplete", first);
        Assert.False((await provider.State.FindByResourceIdAsync(state.ResourceId))!.Coordination.CoauthorTransitionPending);
        await Transition("ConvertToExclusive", first);
        var exclusive = (await provider.State.FindByResourceIdAsync(state.ResourceId))!;
        Assert.NotNull(exclusive.Coordination.Exclusive);
        Assert.Empty(exclusive.Coordination.CoauthorClients); Assert.Empty(exclusive.Editors);
        Assert.Equal(state.Content, exclusive.Content);
        Assert.Equal(state.ContentVersion, exclusive.ContentVersion);

        async Task Transition(string operation, string client) => await provider.State.TransitionAsync(state.ResourceId, (current, now) =>
        {
            var document = StoredDocument.RestoreMetadata(current, now);
            var coordinator = FssHttpLockCoordinator.Restore(document, current.Coordination, now, TestActor.Value.Identity);
            var response = new FssHttpSubResponse();
            coordinator.ApplyCoauthSession(document, Request("CoauthRequestType", operation, client, schema), response);
            Assert.Null(response.ErrorCode);
            return new StateTransition<bool>(document.CaptureCoordination(current, coordinator.Capture()), true);
        });
    }

    private static FssHttpSubRequest Request(string key, string operation, string client, string schema)
    {
        var request = new FssHttpSubRequest
        {
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
