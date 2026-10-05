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

    [PostgreSqlFact]
    public Task PostgreSqlFileSystemLifecycleUsesTheSameAtomicStateRules() =>
        BudgetAndQueryTests.WithDatabase(new(), async source =>
        {
            var root = Path.Combine(Path.GetTempPath(), "cellbridge-lifecycle-" + Guid.NewGuid().ToString("N"));
            try { await CheckLifecycle(new(new PostgreSqlStateStore(source), new PostgreSqlFileSystemContentStore(source, root, shared: true))); }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        });

    [Fact]
    public void LocalEvictionCannotRemoveANewerAttachmentOrContent()
    {
        var cache = new DocumentStore();
        var old = cache.Put("/cache.docx", MinimalDocx.Create());
        var version = old.ContentVersion;
        cache.Put("/cache.docx", MinimalDocx.Create("new"));
        Assert.False(cache.TryEvict(old, version));
        var newer = new DocumentStore().Put("/cache.docx", MinimalDocx.Create());
        cache.Attach(newer);
        Assert.False(cache.TryEvict(old, old.ContentVersion));
        Assert.True(cache.TryEvict(newer, newer.ContentVersion));
        Assert.Null(cache.Get("/cache.docx"));
        Assert.NotEmpty(newer.Content);
    }

    private static async Task CheckLifecycle(StorageProvider provider, StorageProvider? peer = null)
    {
        peer ??= provider;
        var lifecycle = (IDocumentLifecycleStore)provider.State;
        var peerLifecycle = (IDocumentLifecycleStore)peer.State;
        var service = new CellBridgeDocumentService(provider);
        var originalBytes = MinimalDocx.Create();
        var state = (await service.CreateAsync("/lifecycle-" + Guid.NewGuid().ToString("N") + ".docx", originalBytes, TestActor.Value))!;
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
        Assert.Equal(originalBytes, bytes.ToArray());
        var tombstone = (await peerLifecycle.FindLifecycleAsync(state.ResourceId))!;
        var replacement = await new DocumentStore().Put(state.Path, MinimalDocx.Create("replacement")).CaptureAsync(provider.Content);
        replacement = replacement with { LifecycleGeneration = state.LifecycleGeneration + 1, Security = state.Security };
        Assert.False(await lifecycle.TryRecreateAsync(state.ResourceId, state.LifecycleGeneration, state.StateVersion, replacement));
        var proposals = await Task.WhenAll(
            lifecycle.TryRecreateAsync(state.ResourceId, state.LifecycleGeneration, tombstone.StateVersion, replacement).AsTask(),
            peerLifecycle.TryRecreateAsync(state.ResourceId, state.LifecycleGeneration, tombstone.StateVersion,
                replacement with { ResourceId = Guid.NewGuid() }).AsTask());
        Assert.NotEqual(proposals[0], proposals[1]);
        if (!proposals[0])
        {
            var winner = (await peer.State.FindByPathKeyAsync(state.PathKey))!;
            replacement = replacement with { ResourceId = winner.ResourceId };
        }
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
        ((CellBridge.FssHttpB.PutChangesSubRequestData)oldRequest.SubRequests[0].Data!).ExpectedStorageIndex =
            (await StoredDocument.RestoreAsync(recreated, peer.Content)).FilePartition.FileGraph.StorageIndex;
        var peerService = new CellBridgeDocumentService(peer);
        var saved = await peerService.ExecuteAsync(recreated.ResourceId, DocumentPartitionKind.FileContents,
            oldRequest, new Dictionary<string, string>(), TestActor.Value);
        Assert.False(Assert.Single(saved.Response.SubResponses).Status);
        Assert.Equal(2, Assert.Single(saved.State.Receipts).LifecycleGeneration);
        var replay = await peerService.ExecuteAsync(recreated.ResourceId, DocumentPartitionKind.FileContents,
            oldRequest, new Dictionary<string, string>(), TestActor.Value);
        Assert.True(Assert.Single(replay.AcceptedSaves).IsReplay);
        Assert.Equal(2, replay.AcceptedSaves[0].LifecycleGeneration);
        await Assert.ThrowsAsync<InvalidOperationException>(() => peer.State.TransitionAsync(recreated.ResourceId,
            (current, _) => new StateTransition<bool>(current with { RetiredPathKeys = ["/FORGED.DOCX"] }, true)).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() => peer.State.TryCreateAsync(recreated with
            { ResourceId = Guid.NewGuid(), Path = "/forged.docx", PathKey = "/FORGED.DOCX", RetiredPathKeys = [state.PathKey] }).AsTask());
        Assert.True(await peerLifecycle.TryDeleteAsync(recreated.ResourceId, 2, replay.State.StateVersion));
        var secondTombstone = (await peerLifecycle.FindLifecycleAsync(recreated.ResourceId))!;
        var third = await new DocumentStore().Put(state.Path, MinimalDocx.Create("third")).CaptureAsync(peer.Content);
        third = third with { LifecycleGeneration = 3, Security = state.Security };
        Assert.False(await peerLifecycle.TryRecreateAsync(recreated.ResourceId, 2, secondTombstone.StateVersion,
            third with { RetiredPathKeys = ["/FORGED.DOCX"] }));
        Assert.True(await peerLifecycle.TryRecreateAsync(recreated.ResourceId, 2, secondTombstone.StateVersion, third));
        Assert.Equal(third.ResourceId, (await peer.State.FindByPathKeyAsync(state.PathKey))!.ResourceId);
        Assert.False(await peerLifecycle.TryRecreateAsync(state.ResourceId, 1, tombstone.StateVersion, third with { LifecycleGeneration = 2 }));
        Assert.Equal(3, (await peer.State.FindByResourceIdAsync(third.ResourceId))!.LifecycleGeneration);
    }

    [PostgreSqlFact]
    public Task TombstonesKeepRetiredAliasesAndForeignReservationsBlockRecreation() =>
        BudgetAndQueryTests.WithDatabase(new(), async source =>
        {
            var provider = new StorageProvider(new PostgreSqlStateStore(source), new PostgreSqlContentStore(source));
            var service = new CellBridgeDocumentService(provider);
            var first = (await service.CreateAsync("/renamed.docx", MinimalDocx.Create(), TestActor.Value))!;
            // Seed a persisted reservation from the separately owned atomic rename capability.
            await SeedReservation(source, first, "/OLD.DOCX");
            first = (await provider.State.FindByResourceIdAsync(first.ResourceId))!;
            Assert.Null(await service.CreateAsync("/old.docx", MinimalDocx.Create(), TestActor.Value));
            var lifecycle = (IDocumentLifecycleStore)provider.State;
            Assert.True(await lifecycle.TryDeleteAsync(first.ResourceId, 1, first.StateVersion));
            Assert.Null(await service.CreateAsync("/old.docx", MinimalDocx.Create(), TestActor.Value));
            var retired = (await lifecycle.FindLifecycleAsync(first.ResourceId))!;
            Assert.Equal(first.RetiredPathKeys.ToArray(), retired.RetiredPathKeys.ToArray());
            var other = (await service.CreateAsync("/other.docx", MinimalDocx.Create(), TestActor.Value))!;
            await SeedReservation(source, other, first.PathKey);
            Assert.True(await lifecycle.TryDeleteAsync(other.ResourceId, 1, other.StateVersion));
            var replacement = await new DocumentStore().Put(first.Path, MinimalDocx.Create()).CaptureAsync(provider.Content);
            replacement = replacement with { LifecycleGeneration = 2, Security = first.Security };
            Assert.False(await lifecycle.TryRecreateAsync(first.ResourceId, 1, retired.StateVersion, replacement));
            Assert.Null(await provider.State.FindByResourceIdAsync(replacement.ResourceId));
            Assert.Null((await lifecycle.FindLifecycleAsync(first.ResourceId))!.ReplacedBy);
        });

    private static async Task SeedReservation(NpgsqlDataSource source, DocumentState state, string key)
    {
        await using var command = source.CreateCommand("UPDATE cellbridge_states SET state_json=$3 WHERE resource_id=$1 AND state_version=$2");
        command.Parameters.Add(new NpgsqlParameter { Value = state.ResourceId });
        command.Parameters.Add(new NpgsqlParameter { Value = state.StateVersion });
        command.Parameters.Add(new NpgsqlParameter { Value = System.Text.Json.JsonSerializer.Serialize(state with { RetiredPathKeys = [key] }) });
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    [PostgreSqlFact]
    public Task RecreationWaitsForNamespaceBeforeLockingTheRetiredRow() =>
        BudgetAndQueryTests.WithDatabase(new(), async source =>
        {
            var provider = new StorageProvider(new PostgreSqlStateStore(source), new PostgreSqlContentStore(source));
            var state = (await new CellBridgeDocumentService(provider).CreateAsync("/namespace.docx", MinimalDocx.Create(), TestActor.Value))!;
            var lifecycle = (IDocumentLifecycleStore)provider.State;
            Assert.True(await lifecycle.TryDeleteAsync(state.ResourceId, 1, state.StateVersion));
            var retired = (await lifecycle.FindLifecycleAsync(state.ResourceId))!;
            var replacement = await new DocumentStore().Put(state.Path, MinimalDocx.Create()).CaptureAsync(provider.Content);
            replacement = replacement with { LifecycleGeneration = 2, Security = state.Security };
            await using var guardConnection = await source.OpenConnectionAsync();
            await using var guardTransaction = await guardConnection.BeginTransactionAsync();
            await PostgreSqlNamespaceLock.AcquireAsync(guardConnection, guardTransaction);
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var recreate = lifecycle.TryRecreateAsync(state.ResourceId, 1, retired.StateVersion, replacement, cancel.Token).AsTask();
            var until = DateTime.UtcNow.AddSeconds(5);
            while (true)
            {
                await using var waiting = source.CreateCommand("SELECT EXISTS(SELECT 1 FROM pg_locks WHERE locktype='advisory' AND objid=748219352 AND NOT granted)");
                if ((bool)(await waiting.ExecuteScalarAsync(cancel.Token))!) break;
                Assert.True(DateTime.UtcNow < until, "Recreation did not wait for the namespace guard.");
                await Task.Delay(20, cancel.Token);
            }
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                await using var row = new NpgsqlCommand("SELECT state_version FROM cellbridge_documents WHERE resource_id=$1 FOR UPDATE NOWAIT", connection, transaction);
                row.Parameters.Add(new NpgsqlParameter { Value = state.ResourceId });
                Assert.Equal(retired.StateVersion, await row.ExecuteScalarAsync());
            }
            await guardTransaction.RollbackAsync();
            Assert.True(await recreate);
        });

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
