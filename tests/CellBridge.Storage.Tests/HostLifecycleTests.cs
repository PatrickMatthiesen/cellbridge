using System.Collections.Immutable;
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
public sealed class HostLifecycleTests
{
    [Fact]
    public Task MemoryDeletionRecreationAndDetachedReader() => CheckLifecycle(StorageTestDocuments.Memory());

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
        RepositoryTestDatabase.WithDatabase(new(), async source =>
        {
            var root = Path.Combine(Path.GetTempPath(), "cellbridge-lifecycle-" + Guid.NewGuid().ToString("N"));
            try
            {
                var content = new PostgreSqlFileSystemContentStore(source, root, shared: true);
                await CheckLifecycle(new(new PostgreSqlStateStore(source, validationContent: content), content));
            }
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
        var repository = provider.Documents;
        var service = new CellBridgeDocumentService(provider);
        var originalBytes = MinimalDocx.Create();
        var state = (await service.CreateAsync("/lifecycle-" + Guid.NewGuid().ToString("N") + ".docx", originalBytes, TestActor.Value))!;
        var oldRequest = await StorageTestDocuments.SaveRequestAsync(service, state, MinimalDocx.Create("old-client"));
        await using var detached = await provider.Packages.OpenReadAsync(state.Package);
        Assert.False((await repository.ChangeLifecycleAsync(new DocumentLifecycleCommand.Delete(state.ResourceId, 1, state.StateVersion + 1))).Applied);
        var deleted = await Task.WhenAll(Enumerable.Range(0, 2).Select(i => (i == 0 ? repository : peer.Documents)
            .ChangeLifecycleAsync(new DocumentLifecycleCommand.Delete(state.ResourceId, 1, state.StateVersion)).AsTask()));
        Assert.All(deleted, r => Assert.True(r.Applied));
        Assert.Null(await peer.Documents.FindAsync(new DocumentIdentity.Resource(state.ResourceId)));
        Assert.Null(await peer.Documents.FindAsync(new DocumentIdentity.Path(state.PathKey)));
        Assert.DoesNotContain(await peer.Documents.ListAsync(0, 10000), x => x.ResourceId == state.ResourceId);
        var rejectedControl = await repository.ExecuteControlAsync(new DocumentControlCommand.ExpireEditors(state.ResourceId, TestStorage.Admit(state)));
        Assert.False(rejectedControl.Changed);
        Assert.Equal("FileNotFound", rejectedControl.ErrorCode);
        Assert.Null(await service.CreateAsync(state.Path, MinimalDocx.Create(), TestActor.Value));
        Assert.Null(await service.CreateAsync(state.ResourceId, "/new-" + Guid.NewGuid() + ".docx", MinimalDocx.Create(), TestActor.Value));
        using var bytes = new MemoryStream();
        await detached.CopyToAsync(bytes);
        Assert.Equal(originalBytes, bytes.ToArray());
        var tombstone = (await peer.Documents.FindAsync(new DocumentIdentity.Resource(state.ResourceId, true)))!;
        var first = await Replacement(provider, state, 2, "replacement");
        var second = await Replacement(peer, state, 2, "competing replacement");
        Assert.False((await repository.ChangeLifecycleAsync(new DocumentLifecycleCommand.Recreate(state.ResourceId, 1, state.StateVersion, first))).Applied);
        var proposals = await Task.WhenAll(
            repository.ChangeLifecycleAsync(new DocumentLifecycleCommand.Recreate(state.ResourceId, 1, tombstone.StateVersion, first)).AsTask(),
            peer.Documents.ChangeLifecycleAsync(new DocumentLifecycleCommand.Recreate(state.ResourceId, 1, tombstone.StateVersion, second)).AsTask());
        Assert.NotEqual(proposals[0].Applied, proposals[1].Applied);
        var replacement = proposals[0].Applied ? first : second;
        Assert.True((await peer.Documents.ChangeLifecycleAsync(new DocumentLifecycleCommand.Recreate(state.ResourceId, 1, tombstone.StateVersion, replacement))).Applied);
        Assert.False((await peer.Documents.ChangeLifecycleAsync(new DocumentLifecycleCommand.Recreate(state.ResourceId, 1, tombstone.StateVersion,
            proposals[0].Applied ? second : first))).Applied);
        var recreated = (await peer.Documents.FindAsync(new DocumentIdentity.Path(state.PathKey)))!;
        Assert.Equal(replacement.Head.ResourceId, recreated.ResourceId);
        Assert.NotEqual(state.Etag, recreated.Etag);
        Assert.Equal(2, recreated.LifecycleGeneration);
        Assert.Null(await peer.Documents.FindAsync(new DocumentIdentity.Resource(state.ResourceId)));
        // The old client still addresses the old file cell and cannot mutate a new lifecycle at the same path.
        var payload = oldRequest.ToByteArray();
        var soap = new CellStorageRequest { Requests = { new FssHttpRequest { Url = state.Path,
            SubRequests = { new FssHttpSubRequest { Type = SubRequestType.Cell, SubRequestToken = 1,
                SubRequestDataBinary = payload, SubRequestDataAttributes = { ["BinaryDataSize"] = payload.Length.ToString() } } } } } };
        var response = await new CellBridgeRequestProcessor(new(peer)).ExecuteAsync(soap, "https://host.test", TestActor.Value);
        Assert.Equal("InvalidCoauthSession", response.Response.Responses[0].SubResponses[0].ErrorCode);
        Assert.Equal(recreated.StateVersion, (await peer.Documents.FindAsync(new DocumentIdentity.Resource(recreated.ResourceId)))!.StateVersion);
        Assert.True((await peer.Documents.ChangeLifecycleAsync(new DocumentLifecycleCommand.Delete(state.ResourceId, 1, state.StateVersion))).Applied);
        Assert.NotNull(await peer.Documents.FindAsync(new DocumentIdentity.Resource(recreated.ResourceId)));
        var peerService = new CellBridgeDocumentService(peer);
        var request = await StorageTestDocuments.SaveRequestAsync(peerService, recreated, MinimalDocx.Create("new client"));
        var saved = await peerService.ExecuteAsync(recreated.ResourceId, DocumentPartitionKind.FileContents,
            request, new Dictionary<string, string>(), TestActor.Value);
        Assert.False(Assert.Single(saved.Response.SubResponses).Status);
        Assert.Equal(1, saved.State.Counters.SaveReceipts);
        Assert.Equal(2, Assert.Single(saved.AcceptedSaves).LifecycleGeneration);
        var replay = await peerService.ExecuteAsync(recreated.ResourceId, DocumentPartitionKind.FileContents,
            request, new Dictionary<string, string>(), TestActor.Value);
        Assert.True(Assert.Single(replay.AcceptedSaves).IsReplay);
        Assert.Equal(2, replay.AcceptedSaves[0].LifecycleGeneration);
        // A create cannot steal the reserved identity or retired lifecycle's path.
        Assert.False((await peer.Documents.CreateAsync(replacement)).Created);
        Assert.Equal(new[] { state.PathKey }, (await peer.Documents.ExportDocumentAsync(recreated.ResourceId))!.PathClaims);
        Assert.True((await peer.Documents.ChangeLifecycleAsync(new DocumentLifecycleCommand.Delete(recreated.ResourceId, 2, replay.State.StateVersion))).Applied);
        var secondTombstone = (await peer.Documents.FindAsync(new DocumentIdentity.Resource(recreated.ResourceId, true)))!;
        var third = await Replacement(peer, recreated, 3, "third");
        Assert.False((await peer.Documents.ChangeLifecycleAsync(new DocumentLifecycleCommand.Recreate(recreated.ResourceId, 2,
            secondTombstone.StateVersion, first))).Applied);
        Assert.True((await peer.Documents.ChangeLifecycleAsync(new DocumentLifecycleCommand.Recreate(recreated.ResourceId, 2,
            secondTombstone.StateVersion, third))).Applied);
        Assert.Equal(third.Head.ResourceId, (await peer.Documents.FindAsync(new DocumentIdentity.Path(state.PathKey)))!.ResourceId);
        Assert.False((await peer.Documents.ChangeLifecycleAsync(new DocumentLifecycleCommand.Recreate(state.ResourceId, 1,
            tombstone.StateVersion, proposals[0].Applied ? second : first))).Applied);
        Assert.Equal(3, (await peer.Documents.FindAsync(new DocumentIdentity.Resource(third.Head.ResourceId)))!.LifecycleGeneration);
    }

    private static ValueTask<PreparedDocumentCreate> Replacement(StorageProvider provider, DocumentHead retired, long generation, string text) =>
        IndexedDocumentPublicationBuilder.CreateAsync(Guid.NewGuid(), retired.Path, MinimalDocx.Create(text), retired.Security,
            provider.Content, provider.Limits, lifecycleGeneration: generation);

    [PostgreSqlFact]
    public Task TombstonesKeepRetiredAliasesAndForeignReservationsBlockRecreation() =>
        RepositoryTestDatabase.WithDatabase(new(), async source =>
        {
            var provider = new StorageProvider(new PostgreSqlStateStore(source), new PostgreSqlContentStore(source));
            var service = new CellBridgeDocumentService(provider);
            var first = (await service.CreateAsync("/old.docx", MinimalDocx.Create(), TestActor.Value))!;
            first = await service.MoveDocumentAsync(first.ResourceId, "/renamed.docx", TestActor.Value);
            Assert.Null(await service.CreateAsync("/old.docx", MinimalDocx.Create(), TestActor.Value));
            Assert.True((await provider.Documents.ChangeLifecycleAsync(new DocumentLifecycleCommand.Delete(first.ResourceId, 1, first.StateVersion))).Applied);
            Assert.Null(await service.CreateAsync("/old.docx", MinimalDocx.Create(), TestActor.Value));
            var retired = (await provider.Documents.FindAsync(new DocumentIdentity.Resource(first.ResourceId, true)))!;
            Assert.Contains("/OLD.DOCX", (await provider.Documents.ExportDocumentAsync(first.ResourceId))!.PathClaims);
            var other = (await service.CreateAsync("/other.docx", MinimalDocx.Create(), TestActor.Value))!;
            // A corrupt foreign reservation must prevent recreation, even though its head is retired.
            await using var command = source.CreateCommand("UPDATE cellbridge_repository_paths SET \"ResourceId\"=$2 WHERE \"PathKey\"=$1");
            command.Parameters.AddWithValue(first.PathKey); command.Parameters.AddWithValue(other.ResourceId);
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
            Assert.True((await provider.Documents.ChangeLifecycleAsync(new DocumentLifecycleCommand.Delete(other.ResourceId, 1, other.StateVersion))).Applied);
            var replacement = await Replacement(provider, first, 2, "replacement");
            Assert.False((await provider.Documents.ChangeLifecycleAsync(new DocumentLifecycleCommand.Recreate(first.ResourceId, 1, retired.StateVersion, replacement))).Applied);
            Assert.Null(await provider.Documents.FindAsync(new DocumentIdentity.Resource(replacement.Head.ResourceId)));
            Assert.Null((await provider.Documents.FindAsync(new DocumentIdentity.Resource(first.ResourceId, true)))!.ReplacedBy);
        });

    [PostgreSqlFact]
    public Task ABlockedRetiredRowDoesNotBlockAnIndependentDocument() =>
        RepositoryTestDatabase.WithDatabase(new(), async source =>
        {
            var provider = new StorageProvider(new PostgreSqlStateStore(source), new PostgreSqlContentStore(source));
            var service = new CellBridgeDocumentService(provider);
            var first = (await service.CreateAsync("/blocked.docx", MinimalDocx.Create(), TestActor.Value))!;
            var second = (await service.CreateAsync("/independent.docx", MinimalDocx.Create(), TestActor.Value))!;
            Assert.True((await provider.Documents.ChangeLifecycleAsync(new DocumentLifecycleCommand.Delete(first.ResourceId, 1, first.StateVersion))).Applied);
            var retired = (await provider.Documents.FindAsync(new DocumentIdentity.Resource(first.ResourceId, true)))!;
            var replacement = await Replacement(provider, retired, 2, "replacement");
            await using var connection = await source.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await using var guard = new NpgsqlCommand("SELECT \"ResourceId\" FROM cellbridge_repository_heads WHERE \"ResourceId\"=$1 FOR UPDATE", connection, transaction);
            guard.Parameters.AddWithValue(first.ResourceId); await guard.ExecuteScalarAsync();
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var recreate = provider.Documents.ChangeLifecycleAsync(new DocumentLifecycleCommand.Recreate(first.ResourceId, 1,
                retired.StateVersion, replacement), cancellation.Token).AsTask();
            // An operation on the second document completes while the first row remains locked.
            var independent = await provider.Documents.ExecuteControlAsync(new DocumentControlCommand.SetHostLease(second.ResourceId,
                TestStorage.Admit(second), new HostLease("independent", TestActor.Value.Identity.Subject, DateTime.UtcNow.AddMinutes(1)), null),
                cancellation.Token).AsTask().WaitAsync(TimeSpan.FromSeconds(4));
            Assert.True(independent.Changed);
            Assert.False(recreate.IsCompleted);
            await transaction.RollbackAsync();
            Assert.True((await recreate).Applied);
        });

    [Fact]
    public async Task DeleteRacingSaveHasOnlyOneWinner()
    {
        var provider = StorageTestDocuments.Memory(); var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/race.docx", MinimalDocx.Create(), TestActor.Value))!;
        var request = await StorageTestDocuments.SaveRequestAsync(service, state, MinimalDocx.Create("saved"));
        var save = service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents, request,
            new Dictionary<string, string>(), TestActor.Value).AsTask();
        var deletion = provider.Documents.ChangeLifecycleAsync(new DocumentLifecycleCommand.Delete(state.ResourceId, 1, state.StateVersion)).AsTask();
        try { await save; } catch (KeyNotFoundException) { }
        if ((await deletion).Applied) Assert.Null(await provider.Documents.FindAsync(new DocumentIdentity.Resource(state.ResourceId)));
        else Assert.Equal(state.ContentVersion + 1, (await provider.Documents.FindAsync(new DocumentIdentity.Resource(state.ResourceId)))!.ContentVersion);
    }

    [Fact]
    public async Task AnOrdinaryPublicationCannotChangeLifecycle()
    {
        var provider = StorageTestDocuments.Memory();
        var state = (await new CellBridgeDocumentService(provider).CreateAsync("/illegal.docx", MinimalDocx.Create(), TestActor.Value))!;
        var control = (await provider.Documents.ReadControlAsync(state.ResourceId))!;
        var prepared = RepositoryTestPublications.Receipt(control, TestActor.Value);
        await Assert.ThrowsAsync<InvalidDataException>(() => provider.Documents.PublishAsync(
            prepared with { Fence = prepared.Fence with { LifecycleGeneration = 2 } }).AsTask());
        Assert.Equal(state, await provider.Documents.FindAsync(new DocumentIdentity.Resource(state.ResourceId)));
    }
}
