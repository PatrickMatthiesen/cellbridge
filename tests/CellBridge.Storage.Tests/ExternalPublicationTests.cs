using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using CellBridge.AspNetCore;
using CellBridge.FssHttpB;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using CellBridge.Storage.PostgreSql;
using CellBridge.Web;
using Npgsql;

namespace CellBridge.Storage.Tests;

public sealed class ExternalPublicationTests
{
    [Fact]
    public Task LostAcknowledgementTwoPublishersAndRemoteConflict() => CheckDelivery(Memory(), MemoryDestination());

    [PostgreSqlFact]
    public async Task PostgreSqlJournalAndDestinationReceiptsRecoverAcrossTwoHosts()
    {
        var connection = Environment.GetEnvironmentVariable("ConnectionStrings__cellbridge")!;
        await using var first = NpgsqlDataSource.Create(connection);
        await using var second = NpgsqlDataSource.Create(connection);
        var destinationId = Guid.NewGuid();
        var destination = new TestDestination(destinationId, first);
        await destination.InitializeAsync();
        await CheckDelivery(new(new PostgreSqlStateStore(first), new PostgreSqlContentStore(first)), destination,
            new(new PostgreSqlStateStore(second), new PostgreSqlContentStore(second)), new TestDestination(destinationId, second));
    }

    private static async Task CheckDelivery(StorageProvider provider, TestDestination destination,
        StorageProvider? peer = null, TestDestination? restartedDestination = null)
    {
        peer ??= provider;
        restartedDestination ??= destination;
        var (state, first, second) = await PrepareTwo(provider, destination);
        var lost = new AfterWriteFailure(destination);
        var publisher = new ExternalRevisionPublisher(provider, lost);
        var head = (await provider.State.FindByResourceIdAsync(state.ResourceId))!.Publication!.Pending[0];
        Assert.Equal(PublicationAttempt.TransientFailure, await publisher.PublishNextAsync(state.ResourceId));
        Assert.Equal(head, (await peer.State.FindByResourceIdAsync(state.ResourceId))!.Publication!.Pending[0]);
        Assert.Equal(first.State.Content.Sha256, (await destination.ReadAsync()).Hash);
        // Recreate publisher and destination objects over the shared durable stores.
        var replay = new ExternalRevisionPublisher(peer, restartedDestination);
        var attempts = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => replay.PublishNextAsync(state.ResourceId).AsTask()));
        Assert.Contains(PublicationAttempt.Delivered, attempts);
        while ((await provider.State.FindByResourceIdAsync(state.ResourceId))!.Publication!.Pending.Length != 0)
            Assert.Equal(PublicationAttempt.Delivered, await replay.PublishNextAsync(state.ResourceId));
        Assert.Equal(second.State.Content.Sha256, (await destination.ReadAsync()).Hash);
        // A delayed earlier delivery must return its durable receipt without changing the newer bytes.
        await using var oldBytes = await provider.Content.OpenReadAsync(head.Content);
        var old = await destination.CompareExchangeAsync(new(state.Publication!.BindingId, state.Publication.Destination, "r0", head), oldBytes);
        Assert.Equal(ExternalDeliveryStatus.Applied, old.Status);
        Assert.Equal(second.State.Content.Sha256, (await destination.ReadAsync()).Hash);
        // New local commit followed by a non-CellBridge write must conflict and pin the undelivered local revision.
        var current = (await provider.State.FindByResourceIdAsync(state.ResourceId))!;
        await provider.State.TransitionAsync(state.ResourceId, (c, _) =>
            new StateTransition<bool>(ExternalPublication.Append(c, c with { ContentVersion = c.ContentVersion + 1 }, Guid.NewGuid(), provider.Limits), true));
        await destination.RemoteWriteAsync("remote-author");
        Assert.Equal(PublicationAttempt.Conflict, await replay.PublishNextAsync(state.ResourceId));
        var blocked = (await peer.State.FindByResourceIdAsync(state.ResourceId))!;
        Assert.Single(blocked.Publication!.Pending);
        Assert.Contains(blocked.Publication.Pending[0].Content, StorageReferences.Handles(blocked));
        Assert.Equal("remote-author", (await destination.ReadAsync()).Hash);
        Assert.False(await ((IDocumentLifecycleStore)provider.State).TryDeleteAsync(state.ResourceId, 1, blocked.StateVersion));
        Assert.True(await replay.RetryAsync(state.ResourceId, blocked.Publication.BindingId, blocked.Publication.Pending[0].OperationId));
        Assert.Equal(PublicationAttempt.Conflict, await replay.PublishNextAsync(state.ResourceId));
        Assert.Equal(current.ContentVersion + 1, blocked.ContentVersion);
    }

    [Fact]
    public async Task DelayedFailureCannotBlockNextHead()
    {
        var provider = Memory();
        var destination = MemoryDestination();
        var (state, _, _) = await PrepareTwo(provider, destination);
        var delayed = new DelayedConflict();
        var failing = new ExternalRevisionPublisher(provider, delayed).PublishNextAsync(state.ResourceId).AsTask();
        await delayed.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(PublicationAttempt.Delivered, await new ExternalRevisionPublisher(provider, destination).PublishNextAsync(state.ResourceId));
        delayed.Continue.SetResult();
        Assert.Equal(PublicationAttempt.Conflict, await failing);
        var current = (await provider.State.FindByResourceIdAsync(state.ResourceId))!;
        Assert.Single(current.Publication!.Pending);
        Assert.Null(current.Publication.BlockedReason);
        Assert.Equal(PublicationAttempt.Delivered, await new ExternalRevisionPublisher(provider, destination).PublishNextAsync(state.ResourceId));
    }

    [Fact]
    public async Task DelayedAcknowledgementAfterDeletionIsHarmless()
    {
        var provider = Memory();
        var destination = MemoryDestination();
        var (state, _, _) = await PrepareTwo(provider, destination);
        var delayed = new AfterWritePause(destination);
        var pending = new ExternalRevisionPublisher(provider, delayed).PublishNextAsync(state.ResourceId).AsTask();
        await delayed.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var peer = new ExternalRevisionPublisher(provider, destination);
        Assert.Equal(PublicationAttempt.Delivered, await peer.PublishNextAsync(state.ResourceId));
        Assert.Equal(PublicationAttempt.Delivered, await peer.PublishNextAsync(state.ResourceId));
        var delivered = (await provider.State.FindByResourceIdAsync(state.ResourceId))!;
        Assert.True(await ((IDocumentLifecycleStore)provider.State).TryDeleteAsync(state.ResourceId, 1, delivered.StateVersion));
        delayed.Continue.SetResult();
        Assert.Equal(PublicationAttempt.Superseded, await pending);
        Assert.Null(await provider.State.FindByResourceIdAsync(state.ResourceId));
    }

    [Fact]
    public async Task MissingContentBlocksWithoutCallingDestination()
    {
        var provider = Memory();
        var destination = MemoryDestination();
        var (state, _, _) = await PrepareTwo(provider, destination);
        var broken = new StorageProvider(provider.State, new MissingContent());
        Assert.Equal(PublicationAttempt.ContentUnavailable, await new ExternalRevisionPublisher(broken, destination).PublishNextAsync(state.ResourceId));
        Assert.Equal("initial", (await destination.ReadAsync()).Hash);
        var blocked = (await provider.State.FindByResourceIdAsync(state.ResourceId))!.Publication!;
        Assert.Equal(2, blocked.Pending.Length);
        Assert.True(await new ExternalRevisionPublisher(provider, destination).RetryAsync(state.ResourceId, blocked.BindingId, blocked.Pending[0].OperationId));
        Assert.Equal(PublicationAttempt.Delivered, await new ExternalRevisionPublisher(provider, destination).PublishNextAsync(state.ResourceId));
    }

    [Fact]
    public async Task FullQueueRejectsSaveAndReceiptReplayDoesNotEnqueue()
    {
        var limits = new StorageLimits { MaxPendingExternalRevisions = 1 };
        var provider = new StorageProvider(new InMemoryStateStore(), new InMemoryContentStore(), limits);
        var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/capacity.docx", MinimalDocx.Create(), TestActor.Value))!;
        var publisher = new ExternalRevisionPublisher(provider, MemoryDestination());
        Assert.True(await publisher.BindAsync(state.ResourceId, 1, 0, Guid.NewGuid(), "capacity", "r0"));
        var request = await FirstRequest(provider, state);
        var first = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents, request, new Dictionary<string, string>(), TestActor.Value);
        var replay = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents, request, new Dictionary<string, string>(), TestActor.Value);
        Assert.True(Assert.Single(replay.AcceptedSaves).IsReplay);
        Assert.Single(replay.State.Publication!.Pending);
        var refused = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents,
            StorageTests.Fixture("save-second"), new Dictionary<string, string>(), TestActor.Value);
        Assert.True(Assert.Single(refused.Response.SubResponses).Status);
        Assert.Equal(first.State.ContentVersion, (await provider.State.FindByResourceIdAsync(state.ResourceId))!.ContentVersion);
    }

    [PostgreSqlFact]
    public Task LeaseSnapshotsCannotPruneUndeliveredContentPins() =>
        BudgetAndQueryTests.WithDatabase(new() { MaxRetainedStateSnapshots = 2 }, async source =>
        {
            var limits = new StorageLimits { MaxRetainedStateSnapshots = 2 };
            var provider = new StorageProvider(new PostgreSqlStateStore(source, limits), new PostgreSqlContentStore(source), limits);
            var destination = new TestDestination(Guid.NewGuid(), source);
            await destination.InitializeAsync();
            var (state, first, _) = await PrepareTwo(provider, destination);
            for (var i = 0; i < 5; i++)
                await provider.State.TransitionAsync(state.ResourceId, (c, _) => new StateTransition<bool>(c with
                { Coordination = c.Coordination with { Generation = c.Coordination.Generation + 1 } }, true));
            var current = (await provider.State.FindByResourceIdAsync(state.ResourceId))!;
            Assert.Contains(first.State.Content, StorageReferences.Handles(current));
            await new StorageMaintenance(source).CollectOrphansAsync(apply: true, quiescent: true);
            Assert.Equal(first.State.Content.Sha256, Convert.ToHexStringLower(SHA256.HashData(await provider.Content.ReadVerifiedAsync(first.State.Content))));
            var restored = await (await StoredDocument.RestoreAsync(current, provider.Content)).CaptureAsync(provider.Content);
            Assert.Equal(current.Publication, restored.Publication);
            Assert.Equal(current.LifecycleGeneration, restored.LifecycleGeneration);
        });

    private static StorageProvider Memory() => new(new InMemoryStateStore(), new InMemoryContentStore());
    private static TestDestination MemoryDestination() => new(Guid.NewGuid());
    private static async Task<FsshttpbCellRequest> FirstRequest(StorageProvider provider, DocumentState state)
    {
        var request = StorageTests.Fixture("save-first");
        ((PutChangesSubRequestData)request.SubRequests[0].Data!).ExpectedStorageIndex =
            (await StoredDocument.RestoreAsync(state, provider.Content)).FilePartition.FileGraph.StorageIndex;
        return request;
    }
    private static async Task<(DocumentState, CellExecution, CellExecution)> PrepareTwo(StorageProvider provider, TestDestination destination)
    {
        var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/publication-" + Guid.NewGuid().ToString("N") + ".docx", MinimalDocx.Create(), TestActor.Value))!;
        var publisher = new ExternalRevisionPublisher(provider, destination);
        Assert.True(await publisher.BindAsync(state.ResourceId, 1, 0, Guid.NewGuid(), destination.Id.ToString(), "r0"));
        state = (await provider.State.FindByResourceIdAsync(state.ResourceId))!;
        var first = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents, await FirstRequest(provider, state), new Dictionary<string, string>(), TestActor.Value);
        var second = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents, StorageTests.Fixture("save-second"), new Dictionary<string, string>(), TestActor.Value);
        Assert.False(Assert.Single(first.Response.SubResponses).Status);
        Assert.False(Assert.Single(second.Response.SubResponses).Status);
        Assert.Equal(2, second.State.Publication!.Pending.Length);
        return (state, first, second);
    }
    private sealed class MissingContent : IContentStore
    {
        public bool Durable => false;
        public bool Shared => false;
        public ValueTask<ContentHandle> WriteAsync(Stream source, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<Stream> OpenReadAsync(ContentHandle handle, CancellationToken cancellationToken = default) => throw new StorageCorruptionException("Unavailable.");
    }
    private sealed class AfterWriteFailure(IExternalRevisionDestination inner) : IExternalRevisionDestination
    {
        private int _failed;
        public async ValueTask<ExternalDeliveryResult> CompareExchangeAsync(ExternalDeliveryRequest request, Stream bytes, CancellationToken cancellationToken = default)
        {
            var result = await inner.CompareExchangeAsync(request, bytes, cancellationToken);
            if (Interlocked.Exchange(ref _failed, 1) == 0) throw new IOException("Acknowledgement lost after durable external commit.");
            return result;
        }
    }
    private sealed class AfterWritePause(IExternalRevisionDestination inner) : IExternalRevisionDestination
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<ExternalDeliveryResult> CompareExchangeAsync(ExternalDeliveryRequest request, Stream bytes, CancellationToken cancellationToken = default)
        {
            var result = await inner.CompareExchangeAsync(request, bytes, cancellationToken);
            Entered.SetResult();
            await Continue.Task.WaitAsync(cancellationToken);
            return result;
        }
    }
    private sealed class DelayedConflict : IExternalRevisionDestination
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<ExternalDeliveryResult> CompareExchangeAsync(ExternalDeliveryRequest request, Stream bytes, CancellationToken cancellationToken = default)
        {
            Entered.SetResult();
            await Continue.Task.WaitAsync(cancellationToken);
            return new(ExternalDeliveryStatus.Conflict);
        }
    }

    // Contract implementation used only for tests. PostgreSQL couples destination bytes/revision/receipt in a row transaction.
    private sealed class TestDestination(Guid id, NpgsqlDataSource? source = null) : IExternalRevisionDestination
    {
        public Guid Id => id;
        private readonly SemaphoreSlim _gate = new(1);
        private DestinationState _memory = new(0, "initial", []);
        public async Task InitializeAsync()
        {
            if (source is null) return;
            await using var create = source.CreateCommand("CREATE TABLE IF NOT EXISTS cellbridge_test_destinations (id uuid PRIMARY KEY,state_json text NOT NULL)");
            await create.ExecuteNonQueryAsync();
            await using var insert = source.CreateCommand("INSERT INTO cellbridge_test_destinations VALUES($1,$2) ON CONFLICT DO NOTHING");
            insert.Parameters.Add(new NpgsqlParameter { Value = id });
            insert.Parameters.Add(new NpgsqlParameter { Value = JsonSerializer.Serialize(_memory) });
            await insert.ExecuteNonQueryAsync();
        }
        public Task<DestinationState> ReadAsync() => Mutate(s => (s, s));
        public Task<bool> RemoteWriteAsync(string hash) => Mutate(s => (s with { Counter = s.Counter + 1, Hash = hash }, true));
        public async ValueTask<ExternalDeliveryResult> CompareExchangeAsync(ExternalDeliveryRequest request, Stream bytes, CancellationToken cancellationToken = default)
        {
            using var content = new MemoryStream();
            await bytes.CopyToAsync(content, cancellationToken);
            Assert.Equal(request.Revision.Content.Length, content.Length);
            Assert.Equal(request.Revision.Content.Sha256, Convert.ToHexStringLower(SHA256.HashData(content.ToArray())));
            var fingerprint = JsonSerializer.Serialize(request);
            return await Mutate(state =>
            {
                if (state.Receipts.TryGetValue(request.Revision.OperationId, out var prior))
                {
                    if (prior.Fingerprint != fingerprint) throw new InvalidOperationException("An operation identity was reused.");
                    return (state, new ExternalDeliveryResult(ExternalDeliveryStatus.Applied, prior.Revision));
                }
                if (request.ExpectedRevision != "r" + state.Counter) return (state, new ExternalDeliveryResult(ExternalDeliveryStatus.Conflict));
                var revision = "r" + (state.Counter + 1);
                return (state with { Counter = state.Counter + 1, Hash = request.Revision.Content.Sha256,
                    Receipts = state.Receipts.Add(request.Revision.OperationId, new(fingerprint, revision)) },
                    new ExternalDeliveryResult(ExternalDeliveryStatus.Applied, revision));
            });
        }
        private async Task<T> Mutate<T>(Func<DestinationState, (DestinationState Next, T Result)> action)
        {
            if (source is null)
            {
                await _gate.WaitAsync();
                try { var r = action(_memory); _memory = r.Next; return r.Result; }
                finally { _gate.Release(); }
            }
            await using var connection = await source.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await using var read = new NpgsqlCommand("SELECT state_json FROM cellbridge_test_destinations WHERE id=$1 FOR UPDATE", connection, transaction);
            read.Parameters.Add(new NpgsqlParameter { Value = id });
            var state = JsonSerializer.Deserialize<DestinationState>((string)(await read.ExecuteScalarAsync())!)!;
            var result = action(state);
            await using var update = new NpgsqlCommand("UPDATE cellbridge_test_destinations SET state_json=$2 WHERE id=$1", connection, transaction);
            update.Parameters.Add(new NpgsqlParameter { Value = id });
            update.Parameters.Add(new NpgsqlParameter { Value = JsonSerializer.Serialize(result.Next) });
            await update.ExecuteNonQueryAsync();
            await transaction.CommitAsync();
            return result.Result;
        }
    }
    private sealed record DestinationState(long Counter, string Hash, ImmutableDictionary<Guid, DestinationReceipt> Receipts);
    private sealed record DestinationReceipt(string Fingerprint, string Revision);
}
