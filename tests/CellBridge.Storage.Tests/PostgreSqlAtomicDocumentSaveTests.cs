using System.Data.Common;
using System.Security.Cryptography;
using CellBridge.AspNetCore;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.PostgreSql;
using CellBridge.TestFixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace CellBridge.Storage.Tests;

[Collection("PostgreSQL")]
public sealed class PostgreSqlAtomicDocumentSaveTests
{
    [PostgreSqlFact]
    public Task ContentAndAuthoritativeStateUseOneDurableCommitAndExactAccounting() => WithDatabase(async source =>
    {
        var initial = await SeedAsync(source);
        var before = await InventoryAsync(source);
        var commits = new CommitObserver();
        var options = new DbContextOptionsBuilder<CellBridgeDbContext>().UseNpgsql(source).AddInterceptors(commits).Options;
        var states = new PostgreSqlStateStore(options);
        var content = new PostgreSqlContentStore(options);
        var atomic = Assert.IsAssignableFrom<IAtomicDocumentSaveStore>(states);
        Assert.True(atomic.SupportsContentStore(content));
        Assert.False(atomic.SupportsContentStore(new PostgreSqlContentStore(source)));
        Assert.False(atomic.SupportsContentStore(new ForwardContent(content)));
        var bytes = RandomNumberGenerator.GetBytes(65536 + 17);
        var handle = Handle(bytes);
        var callbacks = 0;

        var accepted = await atomic.TransitionWithContentAsync(initial.ResourceId, content,
            new ReadOnlyMemory<byte>[] { bytes, bytes }, (current, now) =>
            {
                callbacks++;
                Assert.Equal(initial.StateVersion, current.StateVersion);
                Assert.Equal(DateTimeKind.Utc, now.Kind);
                return new StateTransition<bool>(AddReceipt(current, handle), true);
            });

        Assert.True(accepted);
        Assert.Equal(1, callbacks);
        Assert.Equal(1, commits.Commits);
        var saved = (await states.FindByResourceIdAsync(initial.ResourceId))!;
        Assert.Equal(initial.StateVersion + 1, saved.StateVersion);
        Assert.Equal(handle, Assert.Single(saved.Receipts).Response);
        Assert.Equal(bytes, await content.ReadVerifiedAsync(handle));
        var after = await InventoryAsync(source);
        Assert.Equal(before.Objects + 1, after.Objects);
        Assert.Equal(before.Chunks + 2, after.Chunks);
        Assert.Equal(before.States + 1, after.States);
        Assert.Equal(before.StoredBytes + bytes.LongLength + DocumentStateJson.GetSerializedSize(saved), after.StoredBytes);
        Assert.Equal(before.Documents, after.Documents);
    });

    [PostgreSqlFact]
    public Task ExistingCommonContentDoesNotSerializeIndependentAtomicSaves() => WithDatabase(async source =>
    {
        var initial = await SeedAsync(source);
        var states = new PostgreSqlStateStore(source);
        var content = new PostgreSqlContentStore(source);
        var other = (await new CellBridgeDocumentService(new(states, content))
            .CreateAsync("/atomic-other.docx", MinimalDocx.Create(), TestActor.Value))!;
        var common = RandomNumberGenerator.GetBytes(4096);
        var commonHandle = await content.WriteAsync(new MemoryStream(common, writable: false));
        var firstBytes = RandomNumberGenerator.GetBytes(65537);
        var secondBytes = RandomNumberGenerator.GetBytes(65538);
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = states.TransitionWithContentAsync(initial.ResourceId, content,
            new ReadOnlyMemory<byte>[] { common, firstBytes }, (current, _) =>
            {
                entered.TrySetResult();
                Assert.True(release.Wait(TimeSpan.FromSeconds(15)), "The first save was not released.");
                return new StateTransition<bool>(AddReceipt(current, Handle(firstBytes)), true);
            }).AsTask();
        Task<bool>? second = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            second = states.TransitionWithContentAsync(other.ResourceId, content,
                new ReadOnlyMemory<byte>[] { common, secondBytes },
                (current, _) => new StateTransition<bool>(AddReceipt(current, Handle(secondBytes)), true)).AsTask();
            // The first callback holds its document lock before taking the quota
            // row. Reusing common immutable content must let the second commit.
            Assert.True(await second.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.False(first.IsCompleted);
        }
        finally
        {
            release.Set();
            await first.WaitAsync(TimeSpan.FromSeconds(10));
            if (second is not null) await second.WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert.Equal(common, await content.ReadVerifiedAsync(commonHandle));
        Assert.Equal(firstBytes, await content.ReadVerifiedAsync(Handle(firstBytes)));
        Assert.Equal(secondBytes, await content.ReadVerifiedAsync(Handle(secondBytes)));
        Assert.Single((await states.FindByResourceIdAsync(initial.ResourceId))!.Receipts);
        Assert.Single((await states.FindByResourceIdAsync(other.ResourceId))!.Receipts);
    });

    [PostgreSqlFact]
    public Task UncommittedStateWritesDoNotHoldTheGlobalQuotaRow() => WithDatabase(async source =>
    {
        var initial = await SeedAsync(source);
        var otherStates = new PostgreSqlStateStore(source);
        var otherContent = new PostgreSqlContentStore(source);
        var other = (await new CellBridgeDocumentService(new(otherStates, otherContent))
            .CreateAsync("/atomic-other.docx", MinimalDocx.Create(), TestActor.Value))!;
        var gate = new PauseAfterStateWrite();
        var options = new DbContextOptionsBuilder<CellBridgeDbContext>().UseNpgsql(source).AddInterceptors(gate).Options;
        var states = new PostgreSqlStateStore(options);
        var content = new PostgreSqlContentStore(options);
        var firstBytes = RandomNumberGenerator.GetBytes(65537);
        var secondBytes = RandomNumberGenerator.GetBytes(65538);
        var first = states.TransitionWithContentAsync(initial.ResourceId, content,
            new ReadOnlyMemory<byte>[] { firstBytes },
            (current, _) => new StateTransition<bool>(AddReceipt(current, Handle(firstBytes)), true)).AsTask();
        Task<bool>? second = null;
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            // State SQL has run, but the old committed document remains visible.
            await AssertUnchangedAsync(otherStates, initial);
            second = otherStates.TransitionWithContentAsync(other.ResourceId, otherContent,
                new ReadOnlyMemory<byte>[] { secondBytes },
                (current, _) => new StateTransition<bool>(AddReceipt(current, Handle(secondBytes)), true)).AsTask();
            Assert.True(await second.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.False(first.IsCompleted);
        }
        finally
        {
            gate.Release.TrySetResult();
            await first.WaitAsync(TimeSpan.FromSeconds(10));
            if (second is not null) await second.WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert.Single((await otherStates.FindByResourceIdAsync(initial.ResourceId))!.Receipts);
        Assert.Single((await otherStates.FindByResourceIdAsync(other.ResourceId))!.Receipts);
        Assert.Equal(firstBytes, await otherContent.ReadVerifiedAsync(Handle(firstBytes)));
        Assert.Equal(secondBytes, await otherContent.ReadVerifiedAsync(Handle(secondBytes)));
    });

    [PostgreSqlFact]
    public Task StorageQuotaRejectionRollsBackStagedObjectsStateAndProjections() => WithDatabase(async source =>
    {
        var initial = await SeedAsync(source);
        var before = await InventoryAsync(source);
        var bytes = RandomNumberGenerator.GetBytes(17 * 65536);
        await using (var quota = source.CreateCommand("UPDATE cellbridge_usage SET max_stored_bytes=$1"))
        {
            quota.Parameters.AddWithValue(before.StoredBytes + bytes.LongLength - 1);
            await quota.ExecuteNonQueryAsync();
        }
        var states = new PostgreSqlStateStore(source);
        var content = new PostgreSqlContentStore(source);
        var error = await Assert.ThrowsAsync<StorageQuotaExceededException>(() => states.TransitionWithContentAsync(
            initial.ResourceId, content, new ReadOnlyMemory<byte>[] { bytes },
            (current, _) => new StateTransition<bool>(AddReceipt(current, Handle(bytes)), true)).AsTask());

        Assert.Equal("total stored bytes", error.Budget);
        Assert.Equal(before, await InventoryAsync(source));
        await AssertUnchangedAsync(states, initial);
        var metadata = await states.FindReadMetadataByResourceIdAsync(initial.ResourceId);
        Assert.Equal(initial.StateVersion, metadata!.StateVersion);
    });

    [PostgreSqlFact]
    public Task ConcurrentAtomicQuotaContendersAdmitOnlyOneCompleteSave() => WithDatabase(async source =>
    {
        var initial = await SeedAsync(source);
        var plainStates = new PostgreSqlStateStore(source);
        var plainContent = new PostgreSqlContentStore(source);
        var other = (await new CellBridgeDocumentService(new(plainStates, plainContent))
            .CreateAsync("/atomic-other.docx", MinimalDocx.Create(), TestActor.Value))!;
        var before = await InventoryAsync(source);
        var firstBytes = RandomNumberGenerator.GetBytes(65537);
        var secondBytes = RandomNumberGenerator.GetBytes(65537);
        var firstState = AddReceipt(initial, Handle(firstBytes)) with { StateVersion = initial.StateVersion + 1 };
        var secondState = AddReceipt(other, Handle(secondBytes)) with { StateVersion = other.StateVersion + 1 };
        var largestSnapshot = Math.Max(DocumentStateJson.GetSerializedSize(firstState), DocumentStateJson.GetSerializedSize(secondState));
        await using (var quota = source.CreateCommand("UPDATE cellbridge_usage SET max_stored_bytes=$1"))
        {
            quota.Parameters.AddWithValue(before.StoredBytes + firstBytes.LongLength + largestSnapshot);
            await quota.ExecuteNonQueryAsync();
        }
        var options = new DbContextOptionsBuilder<CellBridgeDbContext>().UseNpgsql(source)
            .AddInterceptors(new ConcurrentStateWrites()).Options;
        var states = new PostgreSqlStateStore(options);
        var content = new PostgreSqlContentStore(options);
        var callbacks = 0;
        var outcomes = await Task.WhenAll(TrySaveAsync(initial.ResourceId, firstBytes),
            TrySaveAsync(other.ResourceId, secondBytes)).WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Single(outcomes, accepted => accepted);
        Assert.Single(outcomes, accepted => !accepted);
        Assert.Equal(2, callbacks);
        var first = (await plainStates.FindByResourceIdAsync(initial.ResourceId))!;
        var second = (await plainStates.FindByResourceIdAsync(other.ResourceId))!;
        var saved = outcomes[0] ? first : second;
        Assert.Single(saved.Receipts);
        Assert.Empty((outcomes[0] ? second : first).Receipts);
        var after = await InventoryAsync(source);
        Assert.Equal(before with { Objects = before.Objects + 1, Chunks = before.Chunks + 2,
            States = before.States + 1, StoredBytes = before.StoredBytes + firstBytes.LongLength + DocumentStateJson.GetSerializedSize(saved) }, after);

        async Task<bool> TrySaveAsync(Guid id, byte[] bytes)
        {
            try
            {
                return await states.TransitionWithContentAsync(id, content, new ReadOnlyMemory<byte>[] { bytes },
                    (current, _) =>
                    {
                        Interlocked.Increment(ref callbacks);
                        return new StateTransition<bool>(AddReceipt(current, Handle(bytes)), true);
                    });
            }
            catch (StorageQuotaExceededException error)
            {
                Assert.Equal("total stored bytes", error.Budget);
                return false;
            }
        }
    });

    [PostgreSqlFact]
    public Task LedgerArithmeticOverflowKeepsTheCheckedExceptionAndRollsBack() => WithDatabase(async source =>
    {
        var initial = await SeedAsync(source);
        await using (var full = source.CreateCommand("UPDATE cellbridge_usage SET stored_bytes=$1, max_stored_bytes=$1"))
        {
            full.Parameters.AddWithValue(long.MaxValue);
            await full.ExecuteNonQueryAsync();
        }
        var before = await InventoryAsync(source);
        var states = new PostgreSqlStateStore(source);
        var content = new PostgreSqlContentStore(source);
        var bytes = RandomNumberGenerator.GetBytes(65537);
        await Assert.ThrowsAsync<OverflowException>(() => states.TransitionWithContentAsync(initial.ResourceId,
            content, new ReadOnlyMemory<byte>[] { bytes },
            (current, _) => new StateTransition<bool>(AddReceipt(current, Handle(bytes)), true)).AsTask());

        Assert.Equal(before, await InventoryAsync(source));
        await AssertUnchangedAsync(states, initial);
    });

    [PostgreSqlFact]
    public Task ZeroSnapshotDeltaStillRequiresTheUsageLedger() => WithDatabase(async source =>
    {
        var initial = await SeedAsync(source);
        var next = initial with { StateVersion = initial.StateVersion + 1 };
        Assert.Equal(DocumentStateJson.GetSerializedSize(initial), DocumentStateJson.GetSerializedSize(next));
        await using (var missing = source.CreateCommand("DELETE FROM cellbridge_usage")) await missing.ExecuteNonQueryAsync();
        var states = new PostgreSqlStateStore(source, new StorageLimits { MaxRetainedStateSnapshots = 1 });
        var error = await Assert.ThrowsAsync<StorageCorruptionException>(() => states.TransitionWithContentAsync(
            initial.ResourceId, new PostgreSqlContentStore(source), Array.Empty<ReadOnlyMemory<byte>>(),
            (_, _) => new StateTransition<bool>(next, true)).AsTask());

        Assert.Equal("Missing storage usage ledger.", error.Message);
        await AssertUnchangedAsync(states, initial);
        Assert.Equal(initial.StateVersion, (await states.FindReadMetadataByResourceIdAsync(initial.ResourceId))!.StateVersion);
    });

    [PostgreSqlFact]
    public Task StateAdmissionFailureRollsBackChunksAlreadyUploaded() => WithDatabase(async source =>
    {
        var initial = await SeedAsync(source);
        var before = await InventoryAsync(source);
        var states = new PostgreSqlStateStore(source, new StorageLimits { MaxSaveReceipts = 1 });
        var content = new PostgreSqlContentStore(source);
        var bytes = RandomNumberGenerator.GetBytes(17 * 65536);
        var handle = Handle(bytes);
        var error = await Assert.ThrowsAsync<StorageQuotaExceededException>(() => states.TransitionWithContentAsync(
            initial.ResourceId, content, new ReadOnlyMemory<byte>[] { bytes }, (current, _) =>
                new StateTransition<bool>(AddReceipt(AddReceipt(current, handle), handle), true)).AsTask());

        Assert.Equal("save receipts", error.Budget);
        Assert.Equal(before, await InventoryAsync(source));
        await AssertUnchangedAsync(states, initial);
    });

    [PostgreSqlFact]
    public Task CancellationDuringChunkStagingRollsBackBeforeInvokingTheCallback() => WithDatabase(async source =>
    {
        var initial = await SeedAsync(source);
        var before = await InventoryAsync(source);
        using var cancellation = new CancellationTokenSource();
        var options = new DbContextOptionsBuilder<CellBridgeDbContext>().UseNpgsql(source)
            .AddInterceptors(new CancelFirstChunkBatch(cancellation)).Options;
        var states = new PostgreSqlStateStore(options);
        var content = new PostgreSqlContentStore(options);
        var callbacks = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => states.TransitionWithContentAsync(
            initial.ResourceId, content, new ReadOnlyMemory<byte>[] { new byte[17 * 65536] }, (current, _) =>
            {
                callbacks++;
                return new StateTransition<bool>(current, true);
            }, cancellation.Token).AsTask());

        Assert.Equal(0, callbacks);
        Assert.Equal(before, await InventoryAsync(source));
        await AssertUnchangedAsync(new PostgreSqlStateStore(source), initial);
    });

    [PostgreSqlFact]
    public Task CancellationInsideANullTransitionRollsBackItsStagedContent() => WithDatabase(async source =>
    {
        var initial = await SeedAsync(source);
        var before = await InventoryAsync(source);
        var states = new PostgreSqlStateStore(source);
        var content = new PostgreSqlContentStore(source);
        using var cancellation = new CancellationTokenSource();
        var callbacks = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => states.TransitionWithContentAsync(
            initial.ResourceId, content, new ReadOnlyMemory<byte>[] { new byte[65537] }, (_, _) =>
            {
                callbacks++;
                cancellation.Cancel();
                return new StateTransition<bool>(null, false);
            }, cancellation.Token).AsTask());

        Assert.Equal(1, callbacks);
        Assert.Equal(before, await InventoryAsync(source));
        await AssertUnchangedAsync(states, initial);
    });

    [PostgreSqlFact]
    public Task ADeclinedTransitionCanCommitStagedObjectsWithoutChangingTheDocument() => WithDatabase(async source =>
    {
        var initial = await SeedAsync(source);
        var before = await InventoryAsync(source);
        var states = new PostgreSqlStateStore(source);
        var content = new PostgreSqlContentStore(source);
        var bytes = RandomNumberGenerator.GetBytes(65537);
        Assert.False(await states.TransitionWithContentAsync(initial.ResourceId, content,
            new ReadOnlyMemory<byte>[] { bytes }, (_, _) => new StateTransition<bool>(null, false)));

        Assert.Equal(bytes, await content.ReadVerifiedAsync(Handle(bytes)));
        var after = await InventoryAsync(source);
        Assert.Equal(before with { Objects = before.Objects + 1, Chunks = before.Chunks + 2,
            StoredBytes = before.StoredBytes + bytes.LongLength }, after);
        await AssertUnchangedAsync(states, initial);
    });

    [PostgreSqlFact]
    public Task ALostCommitReplyDoesNotRetryTheCallbackOrLoseTheAcceptedObjects() => WithDatabase(async source =>
    {
        var initial = await SeedAsync(source);
        var commits = new CommitObserver { LoseReply = true };
        var options = new DbContextOptionsBuilder<CellBridgeDbContext>().UseNpgsql(source).AddInterceptors(commits).Options;
        var states = new PostgreSqlStateStore(options);
        var content = new PostgreSqlContentStore(options);
        var bytes = RandomNumberGenerator.GetBytes(65537);
        var handle = Handle(bytes);
        var callbacks = 0;
        await Assert.ThrowsAsync<StorageUnavailableException>(() => states.TransitionWithContentAsync(initial.ResourceId,
            content, new ReadOnlyMemory<byte>[] { bytes }, (current, _) =>
            {
                callbacks++;
                return new StateTransition<bool>(AddReceipt(current, handle), true);
            }).AsTask());

        Assert.Equal(1, callbacks);
        Assert.Equal(1, commits.Commits);
        var saved = (await new PostgreSqlStateStore(source).FindByResourceIdAsync(initial.ResourceId))!;
        Assert.Equal(initial.StateVersion + 1, saved.StateVersion);
        Assert.Equal(handle, Assert.Single(saved.Receipts).Response);
        Assert.Equal(bytes, await new PostgreSqlContentStore(source).ReadVerifiedAsync(handle));
    });

    private static DocumentState AddReceipt(DocumentState current, ContentHandle handle) => current with
    {
        Receipts = current.Receipts.Add(new("atomic-" + current.Receipts.Length, handle.Sha256,
            current.ContentVersion, handle, TestActor.Value.Identity.Subject)),
    };

    private static ContentHandle Handle(byte[] bytes)
    {
        var digest = Convert.ToHexStringLower(SHA256.HashData(bytes));
        return new(digest, bytes.LongLength, digest);
    }

    private static async Task<DocumentState> SeedAsync(NpgsqlDataSource source)
    {
        await new PostgreSqlStorageMigrator(source).MigrateAsync();
        return (await new CellBridgeDocumentService(new(new PostgreSqlStateStore(source), new PostgreSqlContentStore(source)))
            .CreateAsync("/atomic.docx", MinimalDocx.Create(), TestActor.Value))!;
    }

    private static async Task AssertUnchangedAsync(PostgreSqlStateStore states, DocumentState initial) =>
        Assert.Equal(DocumentStateJson.Serialize(initial),
            DocumentStateJson.Serialize((await states.FindByResourceIdAsync(initial.ResourceId))!));

    private sealed record Inventory(long Objects, long Chunks, long States, long StoredBytes, long Documents);

    private static async Task<Inventory> InventoryAsync(NpgsqlDataSource source)
    {
        await using var command = source.CreateCommand("""
            SELECT (SELECT count(*) FROM cellbridge_objects), (SELECT count(*) FROM cellbridge_chunks),
                (SELECT count(*) FROM cellbridge_states), stored_bytes, document_count FROM cellbridge_usage
            """);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4));
    }

    private static async Task WithDatabase(Func<NpgsqlDataSource, Task> test)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__cellbridge")!;
        await using var admin = NpgsqlDataSource.Create(connectionString);
        var database = "cellbridge_atomic_" + Guid.NewGuid().ToString("N");
        await using (var create = admin.CreateCommand($"CREATE DATABASE {database}")) await create.ExecuteNonQueryAsync();
        try
        {
            await using var source = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(connectionString)
                { Database = database }.ConnectionString);
            await test(source);
        }
        finally
        {
            await using var drop = admin.CreateCommand($"DROP DATABASE {database} WITH (FORCE)");
            await drop.ExecuteNonQueryAsync();
        }
    }

    private sealed class CommitObserver : DbTransactionInterceptor
    {
        public int Commits { get; private set; }
        public bool LoseReply { get; init; }
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            Commits++;
            if (LoseReply) throw new NpgsqlException("Injected lost commit reply.");
            return Task.CompletedTask;
        }
    }

    private sealed class CancelFirstChunkBatch(CancellationTokenSource cancellation) : DbCommandInterceptor
    {
        public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("INSERT INTO cellbridge_chunks", StringComparison.Ordinal))
            {
                Assert.True(result > 0);
                cancellation.Cancel();
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class PauseAfterStateWrite : SaveChangesInterceptor
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int paused;

        public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries().Any(entry =>
                    entry.Metadata.GetTableName() == "cellbridge_states") && Interlocked.Exchange(ref paused, 1) == 0)
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
            }
            return result;
        }
    }

    private sealed class ConcurrentStateWrites : SaveChangesInterceptor
    {
        private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int writes;

        public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries().Any(entry => entry.Metadata.GetTableName() == "cellbridge_states"))
            {
                if (Interlocked.Increment(ref writes) == 2) ready.TrySetResult();
                await ready.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            }
            return result;
        }
    }

    private sealed class ForwardContent(IContentStore inner) : IContentStore
    {
        public bool Durable => inner.Durable;
        public bool Shared => inner.Shared;
        public ValueTask<ContentHandle> WriteAsync(Stream source, CancellationToken cancellationToken = default) => inner.WriteAsync(source, cancellationToken);
        public ValueTask<Stream> OpenReadAsync(ContentHandle handle, CancellationToken cancellationToken = default) => inner.OpenReadAsync(handle, cancellationToken);
    }
}
