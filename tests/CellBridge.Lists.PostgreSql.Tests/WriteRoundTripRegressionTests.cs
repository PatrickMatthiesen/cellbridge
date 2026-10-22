using System.Text;
using System.Text.Json.Nodes;
using CellBridge.Lists;
using CellBridge.Lists.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CellBridge.Lists.PostgreSql.Tests;

[Collection("lists-postgresql")]
public sealed class WriteRoundTripRegressionTests(ListDatabaseFixture database)
{
    [PostgreSqlFact]
    public async Task CanonicalOversizeFailsBeforeContendedIdentityAndLeavesItReusable()
    {
        var workspace = await database.NewWorkspaceAsync("canonical-create-identity");
        var title = new ListField(Guid.NewGuid(), "Title", ListFieldType.Text);
        var list = await database.Engine.CreateListAsync(database.AdminActor, workspace,
            new(Guid.NewGuid(), "Canonical boundary", [title]));
        var oversized = new CreateRecordRequest(Guid.NewGuid(), 1, Values(title, "canonical boundary"));
        var rawBytes = Encoding.UTF8.GetByteCount(oversized.Values.ToJsonString());
        Assert.True(await database.GetCanonicalJsonBytesAsync(oversized.Values.ToJsonString()) > rawBytes);
        var engine = new PostgreSqlListEngine(database.RuntimeSource!, new ListLimits { MaxRecordBytes = rawBytes });
        await engine.CheckHealthAsync();
        var before = await ReadStateAsync(workspace, list.Id);

        // An uncommitted conflicting identity would block an INSERT even with DO NOTHING.
        await using var connection = await database.RuntimeSource!.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetContextAsync(connection, transaction, workspace);
        await using (var claim = new NpgsqlCommand("""
            INSERT INTO cellbridge_lists.record_tombstones(workspace_id,list_id,record_id)
            VALUES ($1,$2,$3)
            """, connection, transaction))
        {
            claim.Parameters.AddWithValue(workspace);
            claim.Parameters.AddWithValue(list.Id);
            claim.Parameters.AddWithValue(oversized.Id);
            Assert.Equal(1, await claim.ExecuteNonQueryAsync());
        }

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var attempt = engine.CreateRecordAsync(database.AdminActor, workspace, list.Id, oversized, cancellation.Token);
        try
        {
            var failure = await Assert.ThrowsAsync<ListException>(() => attempt.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(ListFailure.Limit, failure.Failure);
        }
        finally
        {
            await transaction.RollbackAsync();
            await CancelAndDrainAsync(cancellation, attempt);
        }
        Assert.Equal(before, await ReadStateAsync(workspace, list.Id));

        var wrongSchema = await Assert.ThrowsAsync<ListException>(() => engine.CreateRecordAsync(
            database.AdminActor, workspace, list.Id, oversized with { SchemaVersion = 2 }));
        Assert.Equal(ListFailure.Conflict, wrongSchema.Failure);
        var unknownField = new ListField(Guid.NewGuid(), "Unknown", ListFieldType.Text);
        var invalid = await Assert.ThrowsAsync<ListException>(() => engine.CreateRecordAsync(
            database.AdminActor, workspace, list.Id, oversized with { Values = Values(unknownField, "canonical boundary") }));
        Assert.Equal(ListFailure.Validation, invalid.Failure);
        Assert.Equal(before, await ReadStateAsync(workspace, list.Id));

        var valid = oversized with { Values = Values(title, "ok") };
        var created = await engine.CreateRecordAsync(database.AdminActor, workspace, list.Id, valid);
        Assert.False(created.Replayed);
        Assert.Equal((1L, 1L), (created.Revision, created.Number));
        var afterCreate = await ReadStateAsync(workspace, list.Id);
        Assert.Equal((1L, 2L, 1L, 1L, 1L), (afterCreate.Identities, afterCreate.NextNumber,
            afterCreate.Records, afterCreate.Receipts, afterCreate.Tombstones));

        var receiptConflict = await Assert.ThrowsAsync<ListException>(() => engine.CreateRecordAsync(
            database.AdminActor, workspace, list.Id, oversized));
        Assert.Equal(ListFailure.Conflict, receiptConflict.Failure);
        var replay = await engine.CreateRecordAsync(database.AdminActor, workspace, list.Id, valid);
        Assert.True(replay.Replayed);
        Assert.Equal((created.Revision, created.Number), (replay.Revision, replay.Number));
        Assert.Equal(afterCreate, await ReadStateAsync(workspace, list.Id));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task WaitingMutationUsesCommittedWorkspaceQuotaAndRollsBackOnLimit(bool delete, bool auditQuota)
    {
        var workspace = await database.NewWorkspaceAsync("waiting-write-quota");
        var title = new ListField(Guid.NewGuid(), "Title", ListFieldType.Text);
        var list = await database.Engine.CreateListAsync(database.AdminActor, workspace,
            new(Guid.NewGuid(), "Quota wait", [title]));
        var request = new CreateRecordRequest(Guid.NewGuid(), 1, Values(title, "x"));
        await database.Engine.CreateRecordAsync(database.AdminActor, workspace, list.Id, request);
        var before = await ReadStateAsync(workspace, list.Id);
        var oldBytes = await database.GetCanonicalJsonBytesAsync(request.Values.ToJsonString());
        var newBytes = await database.GetCanonicalJsonBytesAsync(Values(title, "updated").ToJsonString());
        var delta = delete
            ? AuditBytes("record.delete", 0) - oldBytes
            : AuditBytes("record.update", 1) + newBytes - oldBytes;
        Assert.True(delta > 0);
        var engine = new PostgreSqlListEngine(database.RuntimeSource!, new ListLimits
        {
            MaxWorkspaceBytes = auditQuota ? null : before.Bytes + delta,
            MaxAuditEvents = auditQuota ? before.Audits + 1 : null
        });
        await engine.CheckHealthAsync();

        await using var connection = await database.RuntimeSource!.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetContextAsync(connection, transaction, workspace);
        await using (var consumeQuota = new NpgsqlCommand("""
            UPDATE cellbridge_lists.workspaces
            SET stored_bytes=stored_bytes+$2,audit_count=audit_count+$3
            WHERE workspace_id=$1
            """, connection, transaction))
        {
            consumeQuota.Parameters.AddWithValue(workspace);
            consumeQuota.Parameters.AddWithValue(auditQuota ? 0L : 1L);
            consumeQuota.Parameters.AddWithValue(auditQuota ? 1L : 0L);
            Assert.Equal(1, await consumeQuota.ExecuteNonQueryAsync());
        }

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var mutation = MutateAsync(engine, workspace, list.Id, request.Id, title, delete, cancellation.Token);
        var committed = false;
        try
        {
            await WaitForBlockerAsync(connection.ProcessID, mutation, cancellation.Token);
            await transaction.CommitAsync();
            committed = true;
            var failure = await Assert.ThrowsAsync<ListException>(() => mutation.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(ListFailure.Limit, failure.Failure);
        }
        finally
        {
            if (!committed) await transaction.RollbackAsync();
            await CancelAndDrainAsync(cancellation, mutation);
        }

        var committedState = before with
        {
            Bytes = before.Bytes + (auditQuota ? 0 : 1),
            Audits = before.Audits + (auditQuota ? 1 : 0)
        };
        Assert.Equal(committedState, await ReadStateAsync(workspace, list.Id));
        var unchanged = await database.Engine.GetRecordAsync(database.AdminActor, workspace, list.Id, request.Id);
        Assert.Equal(1, unchanged.Revision);
        Assert.Equal("x", unchanged.Values[title.Id.ToString("D")]!.GetValue<string>());
        var replay = await engine.CreateRecordAsync(database.AdminActor, workspace, list.Id, request);
        Assert.True(replay.Replayed);
        Assert.Equal((1L, 1L), (replay.Revision, replay.Number));
        Assert.Equal(committedState, await ReadStateAsync(workspace, list.Id));

        // The rejected write must leave the original revision usable by a later writer.
        await MutateAsync(database.Engine, workspace, list.Id, request.Id, title, delete, CancellationToken.None);
        Assert.Equal(committedState with
        {
            Bytes = committedState.Bytes + delta,
            Audits = committedState.Audits + 1,
            AuditRows = committedState.AuditRows + 1,
            Records = delete ? 0 : 1
        }, await ReadStateAsync(workspace, list.Id));
        if (delete)
        {
            var missing = await Assert.ThrowsAsync<ListException>(() => database.Engine.GetRecordAsync(
                database.AdminActor, workspace, list.Id, request.Id));
            Assert.Equal(ListFailure.NotFound, missing.Failure);
        }
        else
        {
            var updated = await database.Engine.GetRecordAsync(database.AdminActor, workspace, list.Id, request.Id);
            Assert.Equal(2, updated.Revision);
            Assert.Equal("updated", updated.Values[title.Id.ToString("D")]!.GetValue<string>());
        }
    }

    [PostgreSqlFact]
    public async Task CallerNoTrackingOptionsPreserveWritesCountersAccountingAndReceipts()
    {
        var options = new DbContextOptionsBuilder<PostgreSqlListDbContext>()
            .UseNpgsql(database.RuntimeSource!)
            .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
            .Options;
        var engine = new PostgreSqlListEngine(options);
        var workspace = await database.NewWorkspaceAsync("caller-no-tracking-writes");
        var title = new ListField(Guid.NewGuid(), "Title", ListFieldType.Text);
        var list = await engine.CreateListAsync(database.AdminActor, workspace,
            new(Guid.NewGuid(), "Tracking writes", [title]));
        Assert.Equal(list.Id, (await database.Engine.GetListAsync(database.AdminActor, workspace, list.Id)).Id);
        var baseline = await ReadStateAsync(workspace, list.Id);
        var first = new CreateRecordRequest(Guid.NewGuid(), 1, Values(title, "first"));
        var second = new CreateRecordRequest(Guid.NewGuid(), 1, Values(title, "second"));
        var created = await engine.CreateRecordAsync(database.AdminActor, workspace, list.Id, first);
        var other = await engine.CreateRecordAsync(database.AdminActor, workspace, list.Id, second);
        Assert.Equal((1L, 2L), (created.Number, other.Number));
        var afterCreates = await ReadStateAsync(workspace, list.Id);
        Assert.Equal((2L, 3L, 2L, 2L, 2L), (afterCreates.Identities, afterCreates.NextNumber,
            afterCreates.Records, afterCreates.Receipts, afterCreates.Tombstones));
        Assert.Equal(baseline.Audits + 2, afterCreates.Audits);
        Assert.Equal(baseline.AuditRows + 2, afterCreates.AuditRows);
        Assert.True(afterCreates.Bytes > baseline.Bytes);

        var updated = await engine.UpdateRecordAsync(database.AdminActor, workspace, list.Id, first.Id,
            new(1, 1, [new FieldChange(title.Id, JsonValue.Create("updated first"))]));
        Assert.Equal(2, updated.Revision);
        var persisted = await database.Engine.GetRecordAsync(database.AdminActor, workspace, list.Id, first.Id);
        Assert.Equal(2, persisted.Revision);
        Assert.Equal("updated first", persisted.Values[title.Id.ToString("D")]!.GetValue<string>());
        var updateDelta = await database.GetCanonicalJsonBytesAsync(persisted.Values.ToJsonString())
            - await database.GetCanonicalJsonBytesAsync(first.Values.ToJsonString()) + AuditBytes("record.update", 1);
        var afterUpdate = afterCreates with
        {
            Bytes = afterCreates.Bytes + updateDelta,
            Audits = afterCreates.Audits + 1,
            AuditRows = afterCreates.AuditRows + 1
        };
        Assert.Equal(afterUpdate, await ReadStateAsync(workspace, list.Id));

        await engine.DeleteRecordAsync(database.AdminActor, workspace, list.Id, second.Id, 1);
        var missing = await Assert.ThrowsAsync<ListException>(() => database.Engine.GetRecordAsync(
            database.AdminActor, workspace, list.Id, second.Id));
        Assert.Equal(ListFailure.NotFound, missing.Failure);
        var afterDelete = afterUpdate with
        {
            Bytes = afterUpdate.Bytes - await database.GetCanonicalJsonBytesAsync(second.Values.ToJsonString())
                + AuditBytes("record.delete", 0),
            Audits = afterUpdate.Audits + 1,
            AuditRows = afterUpdate.AuditRows + 1,
            Records = 1
        };
        Assert.Equal(afterDelete, await ReadStateAsync(workspace, list.Id));
        foreach (var request in new[] { first, second })
        {
            var replay = await engine.CreateRecordAsync(database.AdminActor, workspace, list.Id, request);
            Assert.True(replay.Replayed);
            Assert.Equal(1, replay.Revision);
            Assert.Equal(request.Id == first.Id ? created.Number : other.Number, replay.Number);
        }
        Assert.Equal(afterDelete, await ReadStateAsync(workspace, list.Id));
        await using var callerContext = new PostgreSqlListDbContext(options);
        Assert.Equal(QueryTrackingBehavior.NoTracking, callerContext.ChangeTracker.QueryTrackingBehavior);
    }

    private async Task MutateAsync(PostgreSqlListEngine engine, Guid workspace, Guid list, Guid record,
        ListField title, bool delete, CancellationToken cancellationToken)
    {
        if (delete)
            await engine.DeleteRecordAsync(database.AdminActor, workspace, list, record, 1, cancellationToken);
        else
            await engine.UpdateRecordAsync(database.AdminActor, workspace, list, record,
                new(1, 1, [new FieldChange(title.Id, JsonValue.Create("updated"))]), cancellationToken);
    }

    private async Task WaitForBlockerAsync(int blockerPid, Task operation, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            await using var command = database.AdminSource!.CreateCommand("""
                SELECT EXISTS(SELECT 1 FROM pg_stat_activity
                  WHERE datname=current_database() AND $1=ANY(pg_blocking_pids(pid)))
                """);
            command.Parameters.AddWithValue(blockerPid);
            if ((bool)(await command.ExecuteScalarAsync(cancellationToken))!) return;
            Assert.False(operation.IsCompleted, "The mutation completed before waiting for the workspace row lock.");
            await Task.Delay(20, cancellationToken);
        }
        Assert.Fail("The mutation did not wait for the held workspace row lock.");
    }

    private async Task<WriteState> ReadStateAsync(Guid workspace, Guid list)
    {
        // The runtime role intentionally cannot read the insert-only identity ledger.
        await using var connection = await database.AdminSource!.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetContextAsync(connection, transaction, workspace);
        await using var command = new NpgsqlCommand("""
            SELECT w.stored_bytes,w.audit_count,c.identity_count,c.next_number,
              (SELECT count(*) FROM cellbridge_lists.records WHERE workspace_id=$1 AND list_id=$2),
              (SELECT count(*) FROM cellbridge_lists.create_receipts WHERE workspace_id=$1 AND list_id=$2),
              (SELECT count(*) FROM cellbridge_lists.record_tombstones WHERE workspace_id=$1 AND list_id=$2),
              (SELECT count(*) FROM cellbridge_lists.audit_events WHERE workspace_id=$1 AND NOT security)
            FROM cellbridge_lists.workspaces w JOIN cellbridge_lists.list_counters c USING (workspace_id)
            WHERE w.workspace_id=$1 AND c.list_id=$2
            """, connection, transaction);
        command.Parameters.AddWithValue(workspace);
        command.Parameters.AddWithValue(list);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3),
            reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6), reader.GetInt64(7));
    }

    private static async Task SetContextAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid workspace)
    {
        await using var context = new NpgsqlCommand("SELECT set_config('cellbridge.workspace_id',$1,true)", connection, transaction);
        context.Parameters.AddWithValue(workspace.ToString("D"));
        await context.ExecuteNonQueryAsync();
    }

    private static async Task CancelAndDrainAsync(CancellationTokenSource cancellation, Task operation)
    {
        cancellation.Cancel();
        try { await operation.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (Exception) { /* Preserve the test failure after releasing its database lock. */ }
    }

    private long AuditBytes(string action, int fieldCount) =>
        96L + Encoding.UTF8.GetByteCount(database.AdminActor.Subject) + Encoding.UTF8.GetByteCount(action) + fieldCount * 16L;

    private static JsonObject Values(ListField field, string value) => new() { [field.Id.ToString("D")] = value };

    private sealed record WriteState(long Bytes, long Audits, long Identities, long NextNumber,
        long Records, long Receipts, long Tombstones, long AuditRows);
}
