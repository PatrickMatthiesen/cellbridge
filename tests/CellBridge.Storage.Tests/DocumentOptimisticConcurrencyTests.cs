using System.Data.Common;
using CellBridge.AspNetCore;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.PostgreSql;
using CellBridge.TestFixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Npgsql;

namespace CellBridge.Storage.Tests;

[Collection("PostgreSQL")]
public sealed class DocumentOptimisticConcurrencyTests
{
    [PostgreSqlFact]
    public Task PreparedWritersPublishOnceAndReportConflictWithoutReplayingCallbacks() =>
        BudgetAndQueryTests.WithDatabase(new() { MaxRetainedStateSnapshots = 1 }, async source =>
        {
            var limits = new StorageLimits { MaxRetainedStateSnapshots = 1 };
            var normal = new PostgreSqlStateStore(source, limits);
            var state = await CreateAsync(source, normal);
            var barrier = new PreparedWritersBarrier();
            var callbacks = new int[2];
            var proposed = new DateTime[2];
            var errors = await Task.WhenAll(Enumerable.Range(0, 2).Select(index => Record.ExceptionAsync(async () =>
            {
                var observed = await Store(source, limits, barrier).TransitionAsync(state.ResourceId, (current, now) =>
                {
                    Interlocked.Increment(ref callbacks[index]);
                    Assert.Equal(0, current.StateVersion);
                    proposed[index] = now.AddTicks(index);
                    return new StateTransition<long>(current with { ModifiedUtc = proposed[index] }, current.StateVersion);
                });
                Assert.Equal(0, observed);
            })));

            Assert.Single(errors, error => error is null);
            Assert.IsType<StorageConflictException>(Assert.Single(errors, error => error is not null));
            Assert.Equal(new[] { 1, 1 }, callbacks);
            var winner = Array.FindIndex(errors, error => error is null);
            var published = (await normal.FindByResourceIdAsync(state.ResourceId))!;
            Assert.Equal(1, published.StateVersion);
            Assert.Equal(proposed[winner], published.ModifiedUtc);
            Assert.Equal(1, (await normal.FindReadMetadataByResourceIdAsync(state.ResourceId))!.StateVersion);
            await AssertAccountingAsync(source, 1);
        });

    [PostgreSqlFact]
    public Task NoOpReadsThroughAnExternalRowLockWithoutUpdatingOrAdvancingVersion() =>
        BudgetAndQueryTests.WithDatabase(new(), async source =>
        {
            var normal = new PostgreSqlStateStore(source);
            var state = await CreateAsync(source, normal);
            await using var gate = await source.OpenConnectionAsync();
            await using var transaction = await gate.BeginTransactionAsync();
            await LockAsync(gate, transaction, state.ResourceId);
            var commands = new TransitionCommands();
            var callbacks = 0;
            var observed = await Store(source, new(), commands).TransitionAsync(state.ResourceId, (current, _) =>
            {
                Interlocked.Increment(ref callbacks);
                return new StateTransition<long>(null, current.StateVersion);
            }).AsTask().WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(0, observed);
            Assert.Equal(1, callbacks);
            Assert.DoesNotContain(commands.Sql, sql => sql.Contains("UPDATE", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(0, (await normal.FindByResourceIdAsync(state.ResourceId))!.StateVersion);
            await AssertAccountingAsync(source, 1);
        });

    [PostgreSqlFact]
    public Task LeaseDeadlineIsRecheckedAfterThePublicationWaitAndRollsBackThePointer() =>
        BudgetAndQueryTests.WithDatabase(new(), async source =>
        {
            var normal = new PostgreSqlStateStore(source);
            var state = await CreateAsync(source, normal);
            await using var gate = await source.OpenConnectionAsync();
            await using var transaction = await gate.BeginTransactionAsync();
            await LockAsync(gate, transaction, state.ResourceId);
            var observer = new ObservePublication();
            var callbacks = 0;
            DateTime expiry = default;
            var waiting = Store(source, new(), observer).TransitionAsync(state.ResourceId, (current, now) =>
            {
                Interlocked.Increment(ref callbacks);
                expiry = now.AddSeconds(2);
                return new StateTransition<bool>(current with { ModifiedUtc = now }, true) { ValidUntilUtc = expiry };
            }).AsTask();
            try
            {
                await observer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await WaitForDatabaseWaitAsync(source, gate.ProcessID, waiting);
                Assert.Equal(1, Volatile.Read(ref callbacks));
                Assert.True(await ClockAsync(gate, transaction) < expiry);
                while (await ClockAsync(gate, transaction) <= expiry) await Task.Delay(20);
            }
            finally { await transaction.CommitAsync(); }

            await Assert.ThrowsAsync<StorageConflictException>(() => waiting.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(1, callbacks);
            Assert.Equal(0, (await normal.FindByResourceIdAsync(state.ResourceId))!.StateVersion);
            Assert.Equal(0, (await normal.FindReadMetadataByResourceIdAsync(state.ResourceId))!.StateVersion);
            await AssertAccountingAsync(source, 1);
        });

    [Theory]
    [InlineData(HostLockOperation.Refresh)]
    [InlineData(HostLockOperation.Replace)]
    [InlineData(HostLockOperation.Release)]
    public Task HostLockMutationCannotPublishAfterTheOriginalLeaseExpires(HostLockOperation operation) =>
        BudgetAndQueryTests.WithDatabase(new() { MaxRetainedStateSnapshots = 1 }, async source =>
        {
            var limits = new StorageLimits { MaxRetainedStateSnapshots = 1 };
            var normal = new PostgreSqlStateStore(source, limits);
            var content = new PostgreSqlContentStore(source);
            var state = await CreateAsync(source, normal);
            var locks = new SharedDocumentLocks(new(new(normal, content, limits)));
            var acquired = await locks.ApplyAsync(state.ResourceId, HostLockOperation.Acquire, "lease", TestActor.Value);
            Assert.True(acquired.Success);
            var expiry = await normal.TransitionAsync(state.ResourceId, (current, now) =>
            {
                var deadline = now.AddSeconds(2);
                return new StateTransition<DateTime>(current with
                {
                    Coordination = current.Coordination with
                    {
                        HostLock = current.Coordination.HostLock! with { ExpiresUtc = deadline },
                    },
                }, deadline);
            });
            state = (await normal.FindByResourceIdAsync(state.ResourceId))!;
            await using var gate = await source.OpenConnectionAsync();
            await using var transaction = await gate.BeginTransactionAsync();
            await LockAsync(gate, transaction, state.ResourceId);
            var observer = new ObservePublication();
            var waitingLocks = new SharedDocumentLocks(new(new(Store(source, limits, observer), content, limits)));
            var waiting = waitingLocks.ApplyAsync(state.ResourceId, operation, "lease", TestActor.Value,
                replacementToken: operation == HostLockOperation.Replace ? "replacement" : null).AsTask();
            try
            {
                await observer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await WaitForDatabaseWaitAsync(source, gate.ProcessID, waiting);
                Assert.True(await ClockAsync(gate, transaction) < expiry);
                while (await ClockAsync(gate, transaction) <= expiry) await Task.Delay(20);
            }
            finally { await transaction.CommitAsync(); }

            Assert.Equal(new HostLockResult(false, null, null), await waiting.WaitAsync(TimeSpan.FromSeconds(10)));
            var published = (await normal.FindByResourceIdAsync(state.ResourceId))!;
            Assert.Equal(state.StateVersion, published.StateVersion);
            Assert.Equal(state.Coordination.Generation, published.Coordination.Generation);
            Assert.Equal(state.Coordination.HostLock, published.Coordination.HostLock);
            Assert.Equal(state.ModifiedUtc, published.ModifiedUtc);
            Assert.Equal(state.Publication, published.Publication);
            Assert.Equal(state.StateVersion, (await normal.FindReadMetadataByResourceIdAsync(state.ResourceId))!.StateVersion);
            await AssertAccountingAsync(source, 1);
        });

    [PostgreSqlFact]
    public Task PreparedHostWriteReturnsFalseWhenItsLeaseExpiresDuringPublicationWait() =>
        BudgetAndQueryTests.WithDatabase(new(), async source =>
        {
            var normal = new PostgreSqlStateStore(source);
            var provider = new StorageProvider(normal, new PostgreSqlContentStore(source));
            var state = await CreateAsync(source, normal);
            var locks = new SharedDocumentLocks(new(provider));
            var acquired = await locks.ApplyAsync(state.ResourceId, HostLockOperation.Acquire, "lease", TestActor.Value);
            Assert.True(acquired.Success);
            var expiry = await normal.TransitionAsync(state.ResourceId, (current, now) =>
            {
                var deadline = now.AddSeconds(2);
                return new StateTransition<DateTime>(current with
                {
                    Coordination = current.Coordination with
                    {
                        HostLock = current.Coordination.HostLock! with { ExpiresUtc = deadline },
                    },
                }, deadline);
            });
            state = (await normal.FindByResourceIdAsync(state.ResourceId))!;
            await using var gate = await source.OpenConnectionAsync();
            await using var transaction = await gate.BeginTransactionAsync();
            await LockAsync(gate, transaction, state.ResourceId);
            var observer = new ObservePublication();
            var waitingLocks = new SharedDocumentLocks(new(new(Store(source, new(), observer), provider.Content)));
            var callbacks = 0;
            var waiting = waitingLocks.TryCommitAsync(acquired.WriteToken!, state.StateVersion, TestActor.Value,
                (current, now) =>
                {
                    Interlocked.Increment(ref callbacks);
                    return current with { ModifiedUtc = now };
                }, Guid.NewGuid()).AsTask();
            try
            {
                await observer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await WaitForDatabaseWaitAsync(source, gate.ProcessID, waiting);
                Assert.Equal(1, Volatile.Read(ref callbacks));
                Assert.True(await ClockAsync(gate, transaction) < expiry);
                while (await ClockAsync(gate, transaction) <= expiry) await Task.Delay(20);
            }
            finally { await transaction.CommitAsync(); }

            Assert.False(await waiting.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(1, callbacks);
            var published = (await normal.FindByResourceIdAsync(state.ResourceId))!;
            Assert.Equal(state.StateVersion, published.StateVersion);
            Assert.Equal(state.ModifiedUtc, published.ModifiedUtc);
            Assert.Equal(state.Publication, published.Publication);
            Assert.Equal(state.StateVersion, (await normal.FindReadMetadataByResourceIdAsync(state.ResourceId))!.StateVersion);
            await AssertAccountingAsync(source, state.StateVersion + 1);
        });

    [PostgreSqlFact]
    public Task CancellationDuringPublicationWaitRollsBackWithoutReplayingCallbackAndLeavesStoreReusable() =>
        BudgetAndQueryTests.WithDatabase(new(), async source =>
        {
            var normal = new PostgreSqlStateStore(source);
            var state = await CreateAsync(source, normal);
            await using var gate = await source.OpenConnectionAsync();
            await using var transaction = await gate.BeginTransactionAsync();
            await LockAsync(gate, transaction, state.ResourceId);
            var observer = new ObservePublication();
            var waitingStore = Store(source, new(), observer);
            using var cancellation = new CancellationTokenSource();
            var callbacks = 0;
            var waiting = waitingStore.TransitionAsync(state.ResourceId, (current, now) =>
            {
                Interlocked.Increment(ref callbacks);
                return new StateTransition<bool>(current with { ModifiedUtc = now }, true);
            }, cancellation.Token).AsTask();
            try
            {
                await observer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await WaitForDatabaseWaitAsync(source, gate.ProcessID, waiting);
                Assert.Equal(1, Volatile.Read(ref callbacks));
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(TimeSpan.FromSeconds(10)));
                Assert.Equal(1, callbacks);
            }
            finally { await transaction.RollbackAsync(); }

            Assert.Equal(0, (await normal.FindByResourceIdAsync(state.ResourceId))!.StateVersion);
            Assert.Equal(0, (await normal.FindReadMetadataByResourceIdAsync(state.ResourceId))!.StateVersion);
            await AssertAccountingAsync(source, 1);
            Assert.True(await waitingStore.TransitionAsync(state.ResourceId, (current, _) => new StateTransition<bool>(current, true)));
            Assert.Equal(1, (await normal.FindByResourceIdAsync(state.ResourceId))!.StateVersion);
        });

    [PostgreSqlFact]
    public Task PreparedWriterWhoseSnapshotWasPrunedReportsConflictInsteadOfCorruption() =>
        BudgetAndQueryTests.WithDatabase(new() { MaxRetainedStateSnapshots = 1 }, async source =>
        {
            var limits = new StorageLimits { MaxRetainedStateSnapshots = 1 };
            var normal = new PostgreSqlStateStore(source, limits);
            var state = await CreateAsync(source, normal);
            var hold = new HoldPublication();
            var callbacks = 0;
            var stale = Store(source, limits, hold).TransitionAsync(state.ResourceId, (current, now) =>
            {
                Interlocked.Increment(ref callbacks);
                return new StateTransition<bool>(current with { ModifiedUtc = now }, true);
            }).AsTask();
            try
            {
                await hold.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.True(await normal.TransitionAsync(state.ResourceId, (current, now) =>
                    new StateTransition<bool>(current with { ModifiedUtc = now }, true)));
                await AssertAccountingAsync(source, 1);
            }
            finally { hold.Release.TrySetResult(); }

            await Assert.ThrowsAsync<StorageConflictException>(() => stale.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(1, callbacks);
            Assert.Equal(1, (await normal.FindByResourceIdAsync(state.ResourceId))!.StateVersion);
            Assert.Equal(1, (await normal.FindReadMetadataByResourceIdAsync(state.ResourceId))!.StateVersion);
            await AssertAccountingAsync(source, 1);
        });

    [PostgreSqlFact]
    public Task PublicationFailureRollsBackPruningPointersProjectionsAndAccountingWithoutCallbackReplay() =>
        BudgetAndQueryTests.WithDatabase(new() { MaxRetainedStateSnapshots = 1 }, async source =>
        {
            var limits = new StorageLimits { MaxRetainedStateSnapshots = 1 };
            var normal = new PostgreSqlStateStore(source, limits);
            var state = await CreateAsync(source, normal);
            var callbacks = 0;
            await Assert.ThrowsAsync<InvalidOperationException>(() => Store(source, limits, new FailPublication())
                .TransitionAsync(state.ResourceId, (current, now) =>
                {
                    callbacks++;
                    return new StateTransition<bool>(current with { ModifiedUtc = now }, true);
                }).AsTask());
            Assert.Equal(1, callbacks);
            Assert.Equal(0, (await normal.FindByResourceIdAsync(state.ResourceId))!.StateVersion);
            Assert.Equal(0, (await normal.FindReadMetadataByResourceIdAsync(state.ResourceId))!.StateVersion);
            await AssertAccountingAsync(source, 1);
            Assert.True(await normal.TransitionAsync(state.ResourceId, (current, _) => new StateTransition<bool>(current, true)));
            await AssertAccountingAsync(source, 1);
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task SuppressedPublicationRollsBackWithoutReplayingCallback(bool snapshotSave) =>
        BudgetAndQueryTests.WithDatabase(new() { MaxRetainedStateSnapshots = 1 }, async source =>
        {
            var limits = new StorageLimits { MaxRetainedStateSnapshots = 1 };
            var normal = new PostgreSqlStateStore(source, limits);
            var state = await CreateAsync(source, normal);
            var callbacks = 0;
            await Assert.ThrowsAsync<InvalidOperationException>(() => Store(source, limits, new SuppressPublication(snapshotSave))
                .TransitionAsync(state.ResourceId, (current, now) =>
                {
                    callbacks++;
                    return new StateTransition<bool>(current with { ModifiedUtc = now }, true);
                }).AsTask());
            Assert.Equal(1, callbacks);
            Assert.Equal(0, (await normal.FindByResourceIdAsync(state.ResourceId))!.StateVersion);
            Assert.Equal(0, (await normal.FindReadMetadataByResourceIdAsync(state.ResourceId))!.StateVersion);
            await AssertAccountingAsync(source, 1);
        });

    [PostgreSqlFact]
    public Task ProviderSerializationFailureReportsUnavailabilityWithoutRetryingCallback() =>
        BudgetAndQueryTests.WithDatabase(new(), async source =>
        {
            var normal = new PostgreSqlStateStore(source);
            var state = await CreateAsync(source, normal);
            await using (var trigger = source.CreateCommand("""
                CREATE FUNCTION reject_document_publication() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN RAISE EXCEPTION 'Injected serialization failure' USING ERRCODE='40001'; END $$;
                CREATE TRIGGER reject_document_publication BEFORE UPDATE ON cellbridge_documents
                FOR EACH ROW EXECUTE FUNCTION reject_document_publication();
                """)) await trigger.ExecuteNonQueryAsync();
            var callbacks = 0;
            await Assert.ThrowsAsync<StorageUnavailableException>(() => normal.TransitionAsync(state.ResourceId, (current, _) =>
            {
                callbacks++;
                return new StateTransition<bool>(current, true);
            }).AsTask());
            Assert.Equal(1, callbacks);
            Assert.Equal(0, (await normal.FindByResourceIdAsync(state.ResourceId))!.StateVersion);
            await AssertAccountingAsync(source, 1);
        });

    [PostgreSqlFact]
    public Task MissingConcurrencyTokenFailsBeforeInvokingCallback() =>
        BudgetAndQueryTests.WithDatabase(new(), async source =>
        {
            var normal = new PostgreSqlStateStore(source);
            var state = await CreateAsync(source, normal);
            var store = new PostgreSqlStateStore(new DbContextOptionsBuilder<CellBridgeDbContext>().UseNpgsql(source)
                .ReplaceService<IModelCustomizer, MissingTokenCustomizer>().Options);
            var callbacks = 0;
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.TransitionAsync(state.ResourceId, (current, _) =>
            {
                callbacks++;
                return new StateTransition<bool>(null, true);
            }).AsTask());
            Assert.Equal(0, callbacks);
            Assert.Equal(0, (await normal.FindByResourceIdAsync(state.ResourceId))!.StateVersion);
            await AssertAccountingAsync(source, 1);
        });

    [PostgreSqlFact]
    public Task TransitionReadsMatchingPointerSnapshotAndDatabaseClockTogetherBeforePublication() =>
        BudgetAndQueryTests.WithDatabase(new(), async source =>
        {
            var normal = new PostgreSqlStateStore(source);
            var state = await CreateAsync(source, normal);
            var commands = new TransitionCommands();
            var store = Store(source, new(), commands);
            foreach (var publish in new[] { false, true })
            {
                commands.Sql.Clear();
                var observed = await store.TransitionAsync(state.ResourceId, (current, now) =>
                    new StateTransition<DateTime>(publish ? current with { ModifiedUtc = now } : null, now));
                Assert.Equal(DateTimeKind.Utc, observed.Kind);
                var snapshot = Assert.Single(commands.Sql, sql => sql.Contains("state_json", StringComparison.Ordinal) &&
                    sql.Contains("clock_timestamp()", StringComparison.Ordinal));
                Assert.Contains("JOIN cellbridge_states", snapshot, StringComparison.Ordinal);
                Assert.Contains("state_version", snapshot, StringComparison.Ordinal);
                var publication = commands.Sql.FindIndex(sql => sql.Contains("UPDATE cellbridge_documents", StringComparison.Ordinal));
                if (publish)
                {
                    Assert.True(publication > commands.Sql.IndexOf(snapshot));
                    Assert.Contains("AND state_version =", commands.Sql[publication], StringComparison.Ordinal);
                    var insert = commands.Sql.FindIndex(sql => sql.Contains("INSERT INTO cellbridge_states", StringComparison.Ordinal));
                    Assert.True(insert > publication);
                }
                else Assert.Equal(-1, publication);
                Assert.DoesNotContain(commands.Sql, sql => sql.Contains("clock_timestamp()", StringComparison.Ordinal) &&
                    !sql.Contains("state_json", StringComparison.Ordinal));
            }
            Assert.Equal(1, (await normal.FindByResourceIdAsync(state.ResourceId))!.StateVersion);
        });

    private static PostgreSqlStateStore Store(NpgsqlDataSource source, StorageLimits limits, IInterceptor interceptor) =>
        new(new DbContextOptionsBuilder<CellBridgeDbContext>().UseNpgsql(source).AddInterceptors(interceptor).Options, limits);

    private static async Task<DocumentState> CreateAsync(NpgsqlDataSource source, PostgreSqlStateStore store) =>
        (await new CellBridgeDocumentService(new(store, new PostgreSqlContentStore(source)))
            .CreateAsync("/optimistic.docx", MinimalDocx.Create(), TestActor.Value))!;

    private static bool IsPublication(DbContext context) => context.ChangeTracker.Entries().Any(entry =>
        entry.Metadata.GetTableName() == "cellbridge_documents" && entry.State == EntityState.Modified &&
        entry.Property("StateVersion").IsModified && !Equals(entry.Property("StateVersion").CurrentValue,
            entry.Property("StateVersion").OriginalValue));

    private static async Task LockAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid id)
    {
        await using var command = new NpgsqlCommand("SELECT state_version FROM cellbridge_documents WHERE resource_id=$1 FOR UPDATE", connection, transaction);
        command.Parameters.AddWithValue(id);
        await command.ExecuteScalarAsync();
    }

    private static async Task<DateTime> ClockAsync(NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        await using var command = new NpgsqlCommand("SELECT clock_timestamp()", connection, transaction);
        return (DateTime)(await command.ExecuteScalarAsync())!;
    }

    private static async Task WaitForDatabaseWaitAsync(NpgsqlDataSource source, int blockerPid, Task operation)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            Assert.False(operation.IsCompleted);
            await using var command = source.CreateCommand("""
                SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE $1=ANY(pg_blocking_pids(pid)))
                """);
            command.Parameters.AddWithValue(blockerPid);
            if ((bool)(await command.ExecuteScalarAsync(timeout.Token))!) return;
            await Task.Delay(10, timeout.Token);
        }
    }

    private static async Task AssertAccountingAsync(NpgsqlDataSource source, long snapshots)
    {
        await using var command = source.CreateCommand("""
            SELECT (SELECT count(*) FROM cellbridge_states),
                stored_bytes = (SELECT COALESCE(SUM(length),0) FROM cellbridge_objects)
                    + (SELECT COALESCE(SUM(octet_length(state_json)),0) FROM cellbridge_states)
            FROM cellbridge_usage
            """);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(snapshots, reader.GetInt64(0));
        Assert.True(reader.GetBoolean(1));
    }

    private sealed class PreparedWritersBarrier : SaveChangesInterceptor
    {
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int prepared;
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (IsPublication(eventData.Context!))
            {
                if (Interlocked.Increment(ref prepared) == 2) release.TrySetResult();
                await release.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            }
            return result;
        }
    }

    private sealed class HoldPublication : SaveChangesInterceptor
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (IsPublication(eventData.Context!))
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            }
            return result;
        }
    }

    private sealed class ObservePublication : DbCommandInterceptor
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("UPDATE cellbridge_documents", StringComparison.Ordinal)) Entered.TrySetResult();
            return ValueTask.FromResult(result);
        }
    }

    private sealed class TransitionCommands : DbCommandInterceptor
    {
        public List<string> Sql { get; } = [];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        { Sql.Add(command.CommandText); return ValueTask.FromResult(result); }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        { Sql.Add(command.CommandText); return ValueTask.FromResult(result); }
    }

    private sealed class FailPublication : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries().Any(entry =>
                entry.Metadata.GetTableName() == "cellbridge_states" && entry.State == EntityState.Added))
                throw new InvalidOperationException("Injected publication failure after pruning.");
            return ValueTask.FromResult(result);
        }
    }

    private sealed class SuppressPublication(bool snapshotSave) : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult((snapshotSave
                ? eventData.Context!.ChangeTracker.Entries().Any(entry =>
                    entry.Metadata.GetTableName() == "cellbridge_states" && entry.State == EntityState.Added)
                : IsPublication(eventData.Context!)) ? InterceptionResult<int>.SuppressWithResult(1) : result);
    }

    public sealed class MissingTokenCustomizer(ModelCustomizerDependencies dependencies) : ModelCustomizer(dependencies)
    {
        public override void Customize(ModelBuilder modelBuilder, DbContext context)
        {
            base.Customize(modelBuilder, context);
            modelBuilder.Model.GetEntityTypes().Single(entity => entity.GetTableName() == "cellbridge_documents")
                .FindProperty("StateVersion")!.IsConcurrencyToken = false;
        }
    }
}
