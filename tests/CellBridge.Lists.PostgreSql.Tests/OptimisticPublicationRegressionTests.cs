using System.Collections.Concurrent;
using System.Data.Common;
using System.Text.Json.Nodes;
using CellBridge.Lists;
using CellBridge.Lists.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Npgsql;

namespace CellBridge.Lists.PostgreSql.Tests;

[Collection("lists-postgresql")]
public sealed class OptimisticPublicationRegressionTests(ListDatabaseFixture database)
{
    [PostgreSqlFact]
    public async Task TwoPatchesThatReadTheSameRevisionPublishExactlyOneChange()
    {
        var setup = await SetupAsync("two stale patches");
        var before = await ReadStateAsync(setup.Workspace);
        var arrived = Signal();
        var release = Signal();
        var arrivals = 0;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var interceptor = new PublicationInterceptor(setup.Record, async (_, ct) =>
        {
            if (Interlocked.Increment(ref arrivals) == 2) arrived.TrySetResult();
            await release.Task.WaitAsync(ct);
        });
        var engine = Engine(interceptor);
        var first = PatchAsync(engine, setup, "first", cancellation.Token);
        var second = PatchAsync(engine, setup, "second", cancellation.Token);
        try
        {
            await arrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
            release.TrySetResult();
            var outcomes = await Task.WhenAll(OutcomeAsync(first), OutcomeAsync(second));
            Assert.Single(outcomes, failure => failure is null);
            Assert.Single(outcomes, failure => failure == ListFailure.Conflict);
        }
        finally
        {
            release.TrySetResult();
            await DrainAsync(cancellation, first, second);
        }
        var saved = await database.Engine.GetRecordAsync(database.AdminActor, setup.Workspace, setup.List, setup.Record);
        Assert.Equal(2, saved.Revision);
        Assert.Contains(saved.Values[setup.Title.Id.ToString("D")]!.GetValue<string>(), new[] { "first", "second" });
        var after = await ReadStateAsync(setup.Workspace);
        Assert.Equal(before.Audits + 1, after.Audits);
        Assert.Equal(before.AuditRows + 1, after.AuditRows);
        Assert.Equal(before.Records, after.Records);
    }

    [PostgreSqlFact]
    public async Task IndependentPatchesCanPublishBeforeSerializingOnQuota()
    {
        var setup = await SetupAsync("independent optimistic patches");
        var secondRecord = Guid.NewGuid();
        await database.Engine.CreateRecordAsync(database.AdminActor, setup.Workspace, setup.List,
            new(secondRecord, 1, new() { [setup.Title.Id.ToString("D")] = "other" }));
        var before = await ReadStateAsync(setup.Workspace);
        var arrived = Signal();
        var release = Signal();
        var arrivals = 0;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var interceptor = new PublicationInterceptor(null, saved: async (_, ct) =>
        {
            if (Interlocked.Increment(ref arrivals) == 2) arrived.TrySetResult();
            await release.Task.WaitAsync(ct);
        });
        var engine = Engine(interceptor);
        var first = PatchAsync(engine, setup, "first", cancellation.Token);
        var second = PatchAsync(engine, setup with { Record = secondRecord }, "second", cancellation.Token);
        try
        {
            await arrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
            release.TrySetResult();
            Assert.All(await Task.WhenAll(first, second), record => Assert.Equal(2, record.Revision));
        }
        finally
        {
            release.TrySetResult();
            await DrainAsync(cancellation, first, second);
        }
        var after = await ReadStateAsync(setup.Workspace);
        Assert.Equal(before.Audits + 2, after.Audits);
        Assert.Equal(before.AuditRows + 2, after.AuditRows);
    }

    [PostgreSqlTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PatchAndDeleteUseTheRevisionWithoutWaitingForQuotaInOppositeOrder(bool patchPublishesFirst)
    {
        var setup = await SetupAsync("stale patch and delete");
        var reached = Signal();
        var release = Signal();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        async Task PauseAsync(CancellationToken ct)
        {
            reached.TrySetResult();
            await release.Task.WaitAsync(ct);
        }
        var interceptor = new PublicationInterceptor(setup.Record,
            patchPublishesFirst ? null : (_, ct) => PauseAsync(ct),
            patchPublishesFirst ? (_, ct) => PauseAsync(ct) : null);
        var patch = PatchAsync(Engine(interceptor), setup, "updated", cancellation.Token);
        Task? delete = null;
        try
        {
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
            delete = database.Engine.DeleteRecordAsync(database.AdminActor, setup.Workspace,
                setup.List, setup.Record, 1, cancellation.Token);
            if (patchPublishesFirst)
            {
                // The already-published row is held before quota. Delete must wait
                // there, then reject its stale expected revision after patch commits.
                await WaitForRowWaitAsync(delete, cancellation.Token);
                release.TrySetResult();
                Assert.Equal(2, (await patch).Revision);
                Assert.Equal(ListFailure.Conflict, await OutcomeAsync(delete));
            }
            else
            {
                await delete;
                var afterDelete = await ReadStateAsync(setup.Workspace);
                release.TrySetResult();
                Assert.Equal(ListFailure.Conflict, await OutcomeAsync(patch));
                Assert.Equal(afterDelete, await ReadStateAsync(setup.Workspace));
            }
        }
        finally
        {
            release.TrySetResult();
            await DrainAsync(cancellation, delete is null ? [patch] : [patch, delete]);
        }
    }

    [PostgreSqlTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReferencePatchCannotLockAnEdgeBeforeItsSourceRevision(bool replaceReference)
    {
        var setup = await SetupAsync("reference patch versus source delete");
        var firstTarget = Guid.NewGuid();
        var secondTarget = Guid.NewGuid();
        foreach (var target in new[] { firstTarget, secondTarget })
            await database.Engine.CreateRecordAsync(database.AdminActor, setup.Workspace, setup.List,
                new(target, 1, new() { [setup.Title.Id.ToString("D")] = "target" }));
        var reference = new ListField(Guid.NewGuid(), "Target", ListFieldType.Reference,
            ReferenceListId: setup.List, ReferenceDisplayFieldId: setup.Title.Id);
        await database.Engine.PublishSchemaAsync(database.AdminActor, setup.Workspace, setup.List,
            new(1, "Optimistic publication", [setup.Title, reference]));
        await database.Engine.UpdateRecordAsync(database.AdminActor, setup.Workspace, setup.List, setup.Record,
            new(1, 2, [new(reference.Id, JsonValue.Create(firstTarget.ToString("D")))]));
        var before = await ReadStateAsync(setup.Workspace);
        var deleteReady = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDelete = Signal();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var deleteEngine = Engine(new DeleteSaveBarrierInterceptor(setup.Record, deleteReady, releaseDelete));
        var delete = deleteEngine.DeleteRecordAsync(database.AdminActor, setup.Workspace,
            setup.List, setup.Record, 2, cancellation.Token);
        Task<ListRecord>? patch = null;
        try
        {
            // Delete already owns source FOR UPDATE and quota, but has not yet
            // deleted its outgoing edge. The patch can still read revision 2.
            var deletePid = await deleteReady.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var commands = new CommandObserver();
            patch = Engine(commands).UpdateRecordAsync(database.AdminActor, setup.Workspace, setup.List,
                setup.Record, new(2, 2, [replaceReference
                    ? new(reference.Id, JsonValue.Create(secondTarget.ToString("D")))
                    : new(reference.Id, Remove: true)]), cancellation.Token);
            await WaitForBlockerAsync(deletePid, patch, cancellation.Token);
            var sourceUpdate = Assert.Single(commands.Commands,
                command => command.Contains("UPDATE cellbridge_lists.records", StringComparison.Ordinal));
            Assert.DoesNotContain("record_references", sourceUpdate);
            releaseDelete.TrySetResult();
            await delete;
            Assert.Equal(ListFailure.Conflict, await OutcomeAsync(patch));
            var after = await ReadStateAsync(setup.Workspace);
            Assert.Equal(before.Records - 1, after.Records);
            Assert.Equal(0, after.References);
            Assert.Equal(before.Audits + 1, after.Audits);
            Assert.Equal(before.AuditRows + 1, after.AuditRows);
            await database.Engine.DeleteRecordAsync(database.AdminActor, setup.Workspace, setup.List, firstTarget, 1);
            await database.Engine.DeleteRecordAsync(database.AdminActor, setup.Workspace, setup.List, secondTarget, 1);
        }
        finally
        {
            releaseDelete.TrySetResult();
            await DrainAsync(cancellation, patch is null ? [delete] : [delete, patch]);
        }
    }

    [PostgreSqlTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailureOrCancellationAfterPublicationRollsBackRecordAndAccounting(bool cancel)
    {
        var setup = await SetupAsync("early publication rollback");
        var target = Guid.NewGuid();
        await database.Engine.CreateRecordAsync(database.AdminActor, setup.Workspace, setup.List,
            new(target, 1, new() { [setup.Title.Id.ToString("D")] = "target" }));
        var reference = new ListField(Guid.NewGuid(), "Target", ListFieldType.Reference,
            ReferenceListId: setup.List, ReferenceDisplayFieldId: setup.Title.Id);
        await database.Engine.PublishSchemaAsync(database.AdminActor, setup.Workspace, setup.List,
            new(1, "Optimistic publication", [setup.Title, reference]));
        var before = await ReadStateAsync(setup.Workspace);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var published = false;
        var interceptor = new PublicationInterceptor(setup.Record, saved: (_, _) =>
        {
            published = true;
            if (cancel) cancellation.Cancel();
            else throw new InvalidOperationException("Rejected after record publication.");
            return Task.CompletedTask;
        });
        var patch = Engine(interceptor).UpdateRecordAsync(database.AdminActor, setup.Workspace, setup.List,
            setup.Record, new(1, 2, [new(setup.Title.Id, JsonValue.Create("updated")),
                new(reference.Id, JsonValue.Create(target.ToString("D")))]), cancellation.Token);
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => patch);
        else await Assert.ThrowsAsync<InvalidOperationException>(() => patch);
        Assert.True(published);
        Assert.Equal(before, await ReadStateAsync(setup.Workspace));
        var unchanged = await database.Engine.GetRecordAsync(database.AdminActor, setup.Workspace, setup.List, setup.Record);
        Assert.Equal(1, unchanged.Revision);
        Assert.Equal("original", unchanged.Values[setup.Title.Id.ToString("D")]!.GetValue<string>());
        Assert.Equal(2, (await database.Engine.UpdateRecordAsync(database.AdminActor, setup.Workspace,
            setup.List, setup.Record, new(1, 2, [new(setup.Title.Id, JsonValue.Create("retry"))]))).Revision);
    }

    [PostgreSqlFact]
    public async Task InterceptorAddedReferenceLocksItsTargetBeforeQuota()
    {
        var setup = await SetupAsync("intercepted reference lock order");
        var target = Guid.NewGuid();
        await database.Engine.CreateRecordAsync(database.AdminActor, setup.Workspace, setup.List,
            new(target, 1, new() { [setup.Title.Id.ToString("D")] = "target" }));
        var reference = new ListField(Guid.NewGuid(), "Target", ListFieldType.Reference,
            ReferenceListId: setup.List, ReferenceDisplayFieldId: setup.Title.Id);
        await database.Engine.PublishSchemaAsync(database.AdminActor, setup.Workspace, setup.List,
            new(1, "Optimistic publication", [setup.Title, reference]));
        var interceptor = new PublicationInterceptor(setup.Record, (entry, _) =>
        {
            var values = JsonNode.Parse((string)entry.Property("ValuesJson").CurrentValue!)!.AsObject();
            values[reference.Id.ToString("D")] = target.ToString("D");
            entry.Property("ValuesJson").CurrentValue = values.ToJsonString();
            return Task.CompletedTask;
        });
        await using var blocker = await database.RuntimeSource!.OpenConnectionAsync();
        await using var transaction = await blocker.BeginTransactionAsync();
        await SetContextAsync(blocker, transaction, setup.Workspace);
        await using (var command = new NpgsqlCommand("""
            SELECT record_id FROM cellbridge_lists.records
            WHERE workspace_id=$1 AND list_id=$2 AND record_id=$3 FOR UPDATE
            """, blocker, transaction))
        {
            command.Parameters.AddWithValue(setup.Workspace);
            command.Parameters.AddWithValue(setup.List);
            command.Parameters.AddWithValue(target);
            Assert.Equal(target, await command.ExecuteScalarAsync());
        }
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var patch = Engine(interceptor).UpdateRecordAsync(database.AdminActor, setup.Workspace, setup.List,
            setup.Record, new(1, 2, [new(setup.Title.Id, JsonValue.Create("updated"))]), cancellation.Token);
        var released = false;
        try
        {
            await WaitForBlockerAsync(blocker.ProcessID, patch, cancellation.Token);
            await database.SetWorkspaceContextAsync(setup.Workspace, async (connection, quotaTransaction) =>
            {
                await using var quota = new NpgsqlCommand("""
                    SELECT workspace_id FROM cellbridge_lists.workspaces WHERE workspace_id=$1 FOR UPDATE NOWAIT
                    """, connection, quotaTransaction);
                quota.Parameters.AddWithValue(setup.Workspace);
                Assert.Equal(setup.Workspace, await quota.ExecuteScalarAsync(cancellation.Token));
            });
            await transaction.CommitAsync();
            released = true;
            var saved = await patch;
            Assert.Equal(target.ToString("D"), saved.Values[reference.Id.ToString("D")]!.GetValue<string>());
            Assert.Equal(ListFailure.Conflict, await OutcomeAsync(database.Engine.DeleteRecordAsync(
                database.AdminActor, setup.Workspace, setup.List, target, 1)));
        }
        finally
        {
            if (!released) await transaction.RollbackAsync();
            await DrainAsync(cancellation, patch);
        }
    }

    [PostgreSqlTheory]
    [InlineData("40P01")]
    [InlineData("40001")]
    public async Task ProviderConcurrencyExceptionsRemainUnavailable(string sqlState)
    {
        var setup = await SetupAsync("provider concurrency mapping");
        var before = await ReadStateAsync(setup.Workspace);
        var interceptor = new PublicationInterceptor(setup.Record, (_, _) => throw new DbUpdateConcurrencyException(
            "Provider transaction failure.", new PostgresException("Provider transaction failure.", "ERROR", "ERROR", sqlState)));
        Assert.Equal(ListFailure.Unavailable,
            await OutcomeAsync(PatchAsync(Engine(interceptor), setup, "updated", CancellationToken.None)));
        Assert.Equal(before, await ReadStateAsync(setup.Workspace));
    }

    [PostgreSqlFact]
    public async Task EmptyConsumerConcurrencyExceptionIsUnavailable()
    {
        var setup = await SetupAsync("consumer concurrency mapping");
        var before = await ReadStateAsync(setup.Workspace);
        var interceptor = new PublicationInterceptor(setup.Record,
            (_, _) => throw new DbUpdateConcurrencyException("Consumer concurrency failure."));
        Assert.Equal(ListFailure.Unavailable,
            await OutcomeAsync(PatchAsync(Engine(interceptor), setup, "updated", CancellationToken.None)));
        Assert.Equal(before, await ReadStateAsync(setup.Workspace));
    }

    [PostgreSqlTheory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task SuppressedRecordOrAccountingSaveRollsBack(int suppressedSave)
    {
        var setup = await SetupAsync("suppressed optimistic save");
        var before = await ReadStateAsync(setup.Workspace);
        var interceptor = new SuppressedSaveInterceptor(suppressedSave);
        Assert.Equal(ListFailure.Unavailable,
            await OutcomeAsync(PatchAsync(Engine(interceptor), setup, "updated", CancellationToken.None)));
        Assert.Equal(before, await ReadStateAsync(setup.Workspace));
        Assert.Equal(1, (await database.Engine.GetRecordAsync(database.AdminActor,
            setup.Workspace, setup.List, setup.Record)).Revision);
    }

    [PostgreSqlFact]
    public async Task ConsumerModelWithoutRevisionTokenCannotPublishAnUnguardedPatch()
    {
        var setup = await SetupAsync("missing revision token");
        var before = await ReadStateAsync(setup.Workspace);
        var engine = new PostgreSqlListEngine(new DbContextOptionsBuilder<PostgreSqlListDbContext>()
            .UseNpgsql(database.RuntimeSource!)
            .ReplaceService<IModelCustomizer, NoRevisionTokenCustomizer>().Options);
        Assert.Equal(ListFailure.Unavailable,
            await OutcomeAsync(PatchAsync(engine, setup, "updated", CancellationToken.None)));
        Assert.Equal(before, await ReadStateAsync(setup.Workspace));
    }

    [PostgreSqlFact]
    public async Task AccountingSaveRejectsASecondRecordTransformation()
    {
        var setup = await SetupAsync("unstable publication");
        var before = await ReadStateAsync(setup.Workspace);
        var engine = Engine(new RepeatedTransformationInterceptor(setup.Record, setup.Title.Id.ToString("D")));
        Assert.Equal(ListFailure.Unavailable,
            await OutcomeAsync(PatchAsync(engine, setup, "updated", CancellationToken.None)));
        Assert.Equal(before, await ReadStateAsync(setup.Workspace));
        Assert.Equal(1, (await database.Engine.GetRecordAsync(database.AdminActor,
            setup.Workspace, setup.List, setup.Record)).Revision);
    }

    [PostgreSqlFact]
    public async Task WarmPatchHasOneTokenUpdateBeforeQuotaAndOneAccountingSave()
    {
        var setup = await SetupAsync("optimistic command diagnostics");
        var commands = new CommandObserver();
        var engine = Engine(commands);
        await engine.CheckHealthAsync();
        await PatchAsync(engine, setup, "warm", CancellationToken.None);
        commands.Commands.Clear();
        await engine.UpdateRecordAsync(database.AdminActor, setup.Workspace, setup.List, setup.Record,
            new(2, 1, [new(setup.Title.Id, JsonValue.Create("measured"))]));
        var observed = commands.Commands.ToArray();
        var update = Assert.Single(observed, command => command.Contains("UPDATE cellbridge_lists.records", StringComparison.Ordinal));
        Assert.Contains("AND revision =", update);
        var quotaIndex = Array.FindIndex(observed, command => command.Contains("FOR UPDATE", StringComparison.Ordinal));
        Assert.True(quotaIndex > Array.IndexOf(observed, update));
        // EF commands only. Admission, schema lock and JSON-size/time commands use
        // Npgsql directly and the benchmark captures those separately.
        Assert.Equal(4, observed.Length);
        Assert.Contains("UPDATE cellbridge_lists.workspaces", observed[^1]);
    }

    private PostgreSqlListEngine Engine(IInterceptor interceptor) => new(
        new DbContextOptionsBuilder<PostgreSqlListDbContext>().UseNpgsql(database.RuntimeSource!)
            .AddInterceptors(interceptor).Options);

    private async Task<Setup> SetupAsync(string name)
    {
        var workspace = await database.NewWorkspaceAsync(name);
        var title = new ListField(Guid.NewGuid(), "Title", ListFieldType.Text);
        var list = await database.Engine.CreateListAsync(database.AdminActor, workspace,
            new(Guid.NewGuid(), "Optimistic publication", [title]));
        var record = Guid.NewGuid();
        await database.Engine.CreateRecordAsync(database.AdminActor, workspace, list.Id,
            new(record, 1, new() { [title.Id.ToString("D")] = "original" }));
        return new(workspace, list.Id, record, title);
    }

    private Task<ListRecord> PatchAsync(PostgreSqlListEngine engine, Setup setup, string value, CancellationToken ct) =>
        engine.UpdateRecordAsync(database.AdminActor, setup.Workspace, setup.List, setup.Record,
            new(1, 1, [new(setup.Title.Id, JsonValue.Create(value))]), ct);

    private async Task<State> ReadStateAsync(Guid workspace)
    {
        await using var connection = await database.AdminSource!.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("""
            SELECT stored_bytes,audit_count,
              (SELECT count(*) FROM cellbridge_lists.audit_events WHERE workspace_id=$1 AND NOT security),
              (SELECT count(*) FROM cellbridge_lists.records WHERE workspace_id=$1),
              (SELECT count(*) FROM cellbridge_lists.record_references WHERE workspace_id=$1)
            FROM cellbridge_lists.workspaces WHERE workspace_id=$1
            """, connection);
        command.Parameters.AddWithValue(workspace);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4));
    }

    private async Task WaitForBlockerAsync(int blockerPid, Task operation, CancellationToken ct) =>
        await WaitForLockAsync(operation, ct, """
            SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE datname=current_database() AND $1=ANY(pg_blocking_pids(pid)))
            """, blockerPid);

    private async Task WaitForRowWaitAsync(Task operation, CancellationToken ct) =>
        await WaitForLockAsync(operation, ct, """
            SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE datname=current_database()
              AND wait_event_type='Lock' AND query LIKE '%cellbridge_lists.records%')
            """);

    private async Task WaitForLockAsync(Task operation, CancellationToken ct, string sql, int? blockerPid = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (true)
        {
            await using var command = database.AdminSource!.CreateCommand(sql);
            if (blockerPid is { } pid) command.Parameters.AddWithValue(pid);
            if ((bool)(await command.ExecuteScalarAsync(timeout.Token))!) return;
            Assert.False(operation.IsCompleted, "The operation completed before the expected row lock wait.");
            await Task.Delay(20, timeout.Token);
        }
    }

    private static async Task SetContextAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid workspace)
    {
        await using var command = new NpgsqlCommand("SELECT set_config('cellbridge.workspace_id',$1,true)", connection, transaction);
        command.Parameters.AddWithValue(workspace.ToString("D"));
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<ListFailure?> OutcomeAsync(Task operation)
    {
        try { await operation; return null; }
        catch (ListException ex) { return ex.Failure; }
    }

    private static async Task DrainAsync(CancellationTokenSource cancellation, params Task[] operations)
    {
        cancellation.Cancel();
        foreach (var operation in operations)
            try { await operation.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception) { }
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private sealed record Setup(Guid Workspace, Guid List, Guid Record, ListField Title);
    private sealed record State(long Bytes, long Audits, long AuditRows, long Records, long References);

    private sealed class PublicationInterceptor(Guid? recordId,
        Func<EntityEntry, CancellationToken, Task>? saving = null,
        Func<DbContext, CancellationToken, Task>? saved = null) : SaveChangesInterceptor
    {
        private readonly ConcurrentDictionary<Guid, bool> _publishing = new();

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var context = eventData.Context!;
            var entry = context.ChangeTracker.Entries().SingleOrDefault(entry =>
                entry.Metadata.GetTableName() == "records" && entry.State == EntityState.Modified &&
                (recordId is null || Equals(entry.Property("RecordId").CurrentValue, recordId)));
            if (entry is not null && _publishing.TryAdd(context.ContextId.InstanceId, true) && saving is not null)
                await saving(entry, cancellationToken);
            return result;
        }

        public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData,
            int result, CancellationToken cancellationToken = default)
        {
            if (_publishing.TryRemove(eventData.Context!.ContextId.InstanceId, out _) && saved is not null)
                await saved(eventData.Context, cancellationToken);
            return result;
        }
    }

    private sealed class DeleteSaveBarrierInterceptor(Guid recordId, TaskCompletionSource<int> reached,
        TaskCompletionSource release) : SaveChangesInterceptor
    {
        private int _saves;
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _saves) == 1)
            {
                var context = eventData.Context!;
                Assert.Contains(context.ChangeTracker.Entries(), entry => entry.Metadata.GetTableName() == "records" &&
                    Equals(entry.Property("RecordId").CurrentValue, recordId));
                Assert.Contains(context.ChangeTracker.Entries(), entry =>
                    entry.Metadata.GetTableName() == "record_references" && entry.State == EntityState.Deleted);
                reached.TrySetResult(((NpgsqlConnection)context.Database.GetDbConnection()).ProcessID);
                await release.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }

    private sealed class RepeatedTransformationInterceptor(Guid recordId, string titleKey) : SaveChangesInterceptor
    {
        private int _saves;
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _saves) == 2)
            {
                var record = eventData.Context!.ChangeTracker.Entries().Single(entry =>
                    entry.Metadata.GetTableName() == "records" && Equals(entry.Property("RecordId").CurrentValue, recordId));
                var values = JsonNode.Parse((string)record.Property("ValuesJson").CurrentValue!)!.AsObject();
                values[titleKey] = "changed during accounting";
                record.Property("ValuesJson").CurrentValue = values.ToJsonString();
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class SuppressedSaveInterceptor(int suppressedSave) : SaveChangesInterceptor
    {
        private int _saves;
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Interlocked.Increment(ref _saves) == suppressedSave
                ? InterceptionResult<int>.SuppressWithResult(1) : result);
    }

    public sealed class NoRevisionTokenCustomizer(ModelCustomizerDependencies dependencies) : RelationalModelCustomizer(dependencies)
    {
        public override void Customize(ModelBuilder modelBuilder, DbContext context)
        {
            base.Customize(modelBuilder, context);
            modelBuilder.Model.GetEntityTypes().Single(entity => entity.GetTableName() == "records")
                .FindProperty("Revision")!.IsConcurrencyToken = false;
        }
    }

    private sealed class CommandObserver : DbCommandInterceptor
    {
        public ConcurrentQueue<string> Commands { get; } = new();
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Commands.Enqueue(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }
}
