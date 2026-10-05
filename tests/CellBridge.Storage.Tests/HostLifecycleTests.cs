using CellBridge.AspNetCore;
using CellBridge.FssHttp;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using CellBridge.Storage.PostgreSql;
using CellBridge.Web;
using Npgsql;

namespace CellBridge.Storage.Tests;

public sealed class HostLifecycleTests
{
    [Fact]
    public Task MemoryDeletionRecreationAndDetachedReader() => CheckLifecycle(new(new InMemoryStateStore(), new InMemoryContentStore()));

    [PostgreSqlFact]
    public async Task PostgreSqlDeletionRecreationSurvivesTwoHostsAndRestart()
    {
        await using var first = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("ConnectionStrings__cellbridge")!);
        await using var second = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("ConnectionStrings__cellbridge")!);
        await CheckLifecycle(new(new PostgreSqlStateStore(first), new PostgreSqlContentStore(first)),
            new(new PostgreSqlStateStore(second), new PostgreSqlContentStore(second)));
    }

    private static async Task CheckLifecycle(StorageProvider provider, StorageProvider? peer = null)
    {
        peer ??= provider;
        var lifecycle = (IDocumentLifecycleStore)provider.State;
        var peerLifecycle = (IDocumentLifecycleStore)peer.State;
        var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/lifecycle-" + Guid.NewGuid().ToString("N") + ".docx", MinimalDocx.Create(), TestActor.Value))!;
        await using var detached = await provider.Content.OpenReadAsync(state.Content);
        Assert.False(await lifecycle.TryDeleteAsync(state.ResourceId, state.LifecycleGeneration, state.StateVersion + 1));
        var deleted = await Task.WhenAll(Enumerable.Range(0, 2).Select(i => (i == 0 ? lifecycle : peerLifecycle)
            .TryDeleteAsync(state.ResourceId, state.LifecycleGeneration, state.StateVersion).AsTask()));
        Assert.All(deleted, Assert.True);
        Assert.Null(await peer.State.FindByResourceIdAsync(state.ResourceId));
        Assert.Null(await peer.State.FindByPathKeyAsync(state.PathKey));
        Assert.DoesNotContain(await peer.State.ListAsync(0, 10000), x => x.ResourceId == state.ResourceId);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => peer.State.TransitionAsync(state.ResourceId,
            (current, _) => new StateTransition<bool>(current with { ContentVersion = current.ContentVersion + 1 }, true)).AsTask());
        Assert.Null(await service.CreateAsync(state.Path, MinimalDocx.Create(), TestActor.Value));
        Assert.Null(await service.CreateAsync(state.ResourceId, "/new-" + Guid.NewGuid() + ".docx", MinimalDocx.Create(), TestActor.Value));
        using var bytes = new MemoryStream();
        await detached.CopyToAsync(bytes);
        Assert.Equal(MinimalDocx.Create(), bytes.ToArray());
        var tombstone = (await peerLifecycle.FindLifecycleAsync(state.ResourceId))!;
        var replacement = await new DocumentStore().Put(state.Path, MinimalDocx.Create("replacement")).CaptureAsync(provider.Content);
        replacement = replacement with { LifecycleGeneration = state.LifecycleGeneration + 1, Security = state.Security };
        Assert.False(await lifecycle.TryRecreateAsync(state.ResourceId, state.LifecycleGeneration, state.StateVersion, replacement));
        Assert.True(await lifecycle.TryRecreateAsync(state.ResourceId, state.LifecycleGeneration, tombstone.StateVersion, replacement));
        Assert.True(await peerLifecycle.TryRecreateAsync(state.ResourceId, state.LifecycleGeneration, tombstone.StateVersion, replacement));
        Assert.False(await peerLifecycle.TryRecreateAsync(state.ResourceId, state.LifecycleGeneration, tombstone.StateVersion,
            replacement with { ResourceId = Guid.NewGuid() }));
        var recreated = (await peer.State.FindByPathKeyAsync(state.PathKey))!;
        Assert.Equal(replacement.ResourceId, recreated.ResourceId);
        Assert.NotEqual(state.Etag, recreated.Etag);
        Assert.Equal(2, recreated.LifecycleGeneration);
        Assert.Null(await peer.State.FindByResourceIdAsync(state.ResourceId));
        var oldRequest = StorageTests.Fixture("save-first");
        var soap = new CellStorageRequest { Requests = { new FssHttpRequest { Url = state.Path,
            SubRequests = { new FssHttpSubRequest { Type = SubRequestType.Cell, SubRequestToken = 1, SubRequestDataBinary = oldRequest.ToByteArray() } } } } };
        var response = await new CellBridgeRequestProcessor(new(peer)).ExecuteAsync(soap, "https://host.test", TestActor.Value);
        Assert.Equal("InvalidCoauthSession", response.Response.Responses[0].SubResponses[0].ErrorCode);
        Assert.Equal(recreated.StateVersion, (await peer.State.FindByResourceIdAsync(recreated.ResourceId))!.StateVersion);
        Assert.True(await peerLifecycle.TryDeleteAsync(state.ResourceId, state.LifecycleGeneration, state.StateVersion));
        Assert.NotNull(await peer.State.FindByResourceIdAsync(recreated.ResourceId));
    }

    [Fact]
    public async Task DeleteRacingSaveHasOnlyOneWinner()
    {
        var provider = new StorageProvider(new InMemoryStateStore(), new InMemoryContentStore());
        var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/race.docx", MinimalDocx.Create(), TestActor.Value))!;
        var request = StorageTests.Fixture("save-first");
        ((CellBridge.FssHttpB.PutChangesSubRequestData)request.SubRequests[0].Data!).ExpectedStorageIndex =
            (await StoredDocument.RestoreAsync(state, provider.Content)).FilePartition.FileGraph.StorageIndex;
        var save = service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents, request,
            new Dictionary<string, string>(), TestActor.Value).AsTask();
        var deletion = ((IDocumentLifecycleStore)provider.State).TryDeleteAsync(state.ResourceId, 1, 0).AsTask();
        try { await save; } catch (KeyNotFoundException) { }
        if (await deletion) Assert.Null(await provider.State.FindByResourceIdAsync(state.ResourceId));
        else Assert.Equal(state.ContentVersion + 1, (await provider.State.FindByResourceIdAsync(state.ResourceId))!.ContentVersion);
    }

    [Fact]
    public async Task OrdinaryTransitionCannotChangeLifecycle()
    {
        var provider = new StorageProvider(new InMemoryStateStore(), new InMemoryContentStore());
        var state = (await new CellBridgeDocumentService(provider).CreateAsync("/illegal.docx", MinimalDocx.Create(), TestActor.Value))!;
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.State.TransitionAsync(state.ResourceId,
            (current, _) => new StateTransition<bool>(current with { LifecycleGeneration = 2 }, true)).AsTask());
    }
}
