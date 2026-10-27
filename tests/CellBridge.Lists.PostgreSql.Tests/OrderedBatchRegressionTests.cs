using System.Text.Json.Nodes;
using CellBridge.Lists;
using CellBridge.Lists.PostgreSql;
using Npgsql;

namespace CellBridge.Lists.PostgreSql.Tests;

[Collection("lists-postgresql")]
public sealed class OrderedBatchRegressionTests(ListDatabaseFixture database)
{
    [PostgreSqlTheory]
    [InlineData("builtin", false)]
    [InlineData("builtin", true)]
    [InlineData("domain", false)]
    [InlineData("domain", true)]
    [InlineData("scope", false)]
    [InlineData("scope", true)]
    [InlineData("denied", false)]
    [InlineData("denied", true)]
    public async Task RejectedAuthorityDoesNotWaitForSourceLocks(string mode, bool recordLock)
    {
        var workspace = Guid.NewGuid();
        await PostgreSqlListSetup.RegisterHostWorkspaceAsync(database.AdminSource!, workspace,
            "Ordered batch authority", "ordered-batch", "scope");
        var actor = new ListActor("test:ordered-batch", "scope");
        var host = new PostgreSqlListEngine(database.RuntimeSource!, null, new Authority("ordered-batch", true));
        var field = new ListField(Guid.NewGuid(), "Title", ListFieldType.Text);
        var list = await host.CreateListAsync(actor, workspace, new(Guid.NewGuid(), "Host", [field]));
        var request = new CreateRecordRequest(Guid.NewGuid(), 1,
            new JsonObject { [field.Id.ToString("D")] = "saved" });
        await host.CreateRecordAsync(actor, workspace, list.Id, request);
        var rejected = mode == "builtin" ? database.Engine : new PostgreSqlListEngine(database.RuntimeSource!, null,
            new Authority(mode == "domain" ? "wrong-domain" : "ordered-batch", mode != "denied"));
        if (mode == "scope") actor = actor with { TenantId = "wrong-scope" };
        await rejected.CheckHealthAsync();
        await using var connection = await database.RuntimeSource!.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetContextAsync(connection, transaction, workspace);
        await LockSourceAsync(connection, transaction, workspace, list.Id, request.Id, recordLock);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        foreach (var patch in new[] { false, true })
        {
            Task attempt = patch
                ? rejected.UpdateRecordAsync(actor, workspace, list.Id, request.Id, new(1, 1, []), cancellation.Token)
                : rejected.CreateRecordAsync(actor, workspace, list.Id, request, cancellation.Token);
            try
            {
                var error = await Assert.ThrowsAsync<ListException>(() => attempt.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.Equal(mode == "denied" ? ListFailure.Forbidden : ListFailure.NotFound, error.Failure);
            }
            finally
            {
                if (!attempt.IsCompleted) await cancellation.CancelAsync();
                try { await attempt; } catch { }
            }
        }
    }

    [PostgreSqlTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellingSourceLockWaitRollsBackAndLeavesPoolUsable(bool recordLock)
    {
        var workspace = await database.NewWorkspaceAsync("ordered-batch-cancel");
        var field = new ListField(Guid.NewGuid(), "Title", ListFieldType.Text);
        var list = await database.Engine.CreateListAsync(database.AdminActor, workspace,
            new(Guid.NewGuid(), "Cancellation", [field]));
        var id = Guid.NewGuid();
        await database.Engine.CreateRecordAsync(database.AdminActor, workspace, list.Id,
            new(id, 1, new JsonObject { [field.Id.ToString("D")] = "original" }));
        var before = await StateAsync(workspace);
        var request = new CreateRecordRequest(Guid.NewGuid(), 1,
            new JsonObject { [field.Id.ToString("D")] = "retry" });
        await using var connection = await database.RuntimeSource!.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetContextAsync(connection, transaction, workspace);
        await LockSourceAsync(connection, transaction, workspace, list.Id, id, recordLock);
        await using var pidCommand = new NpgsqlCommand("SELECT pg_backend_pid()", connection, transaction);
        var blocker = (int)(await pidCommand.ExecuteScalarAsync())!;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Task write = recordLock
            ? database.Engine.UpdateRecordAsync(database.AdminActor, workspace, list.Id, id,
                new(1, 1, [new(field.Id, JsonValue.Create("updated"))]), cancellation.Token)
            : database.Engine.CreateRecordAsync(database.AdminActor, workspace, list.Id, request, cancellation.Token);
        try
        {
            await WaitForBlockerAsync(blocker);
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write);
        }
        finally
        {
            await cancellation.CancelAsync();
            await transaction.RollbackAsync();
            try { await write; } catch { }
        }
        Assert.Equal(before, await StateAsync(workspace));
        Assert.Equal(1, (await database.Engine.GetRecordAsync(database.AdminActor, workspace, list.Id, id)).Revision);
        var saved = await database.Engine.CreateRecordAsync(database.AdminActor, workspace, list.Id, request);
        Assert.Equal(2, saved.Number);
        Assert.False(saved.Replayed);
    }

    [PostgreSqlFact]
    public async Task ExpiredHostDecisionRejectsAfterAuthorizationWaitBeforeSourceLock()
    {
        var workspace = Guid.NewGuid();
        var domain = "ordered-expiry:" + Guid.NewGuid().ToString("N");
        await PostgreSqlListSetup.RegisterHostWorkspaceAsync(database.AdminSource!, workspace,
            "Ordered batch expiry", domain, "scope");
        var actor = new ListActor("test:ordered-expiry", "scope");
        var authority = new ExpiringAuthority(domain);
        var engine = new PostgreSqlListEngine(database.RuntimeSource!, null, authority);
        var title = new ListField(Guid.NewGuid(), "Title", ListFieldType.Text);
        var target = await engine.CreateListAsync(actor, workspace, new(Guid.NewGuid(), "Target", [title]));
        var targetId = Guid.NewGuid();
        await engine.CreateRecordAsync(actor, workspace, target.Id,
            new(targetId, 1, new JsonObject { [title.Id.ToString("D")] = "target" }));
        var reference = new ListField(Guid.NewGuid(), "Target", ListFieldType.Reference,
            ReferenceListId: target.Id, ReferenceDisplayFieldId: title.Id);
        var source = await engine.CreateListAsync(actor, workspace,
            new(Guid.NewGuid(), "Source", [title, reference]));
        var recordId = Guid.NewGuid();
        await engine.CreateRecordAsync(actor, workspace, source.Id,
            new(recordId, 1, new JsonObject { [title.Id.ToString("D")] = "original" }));
        var before = await StateAsync(workspace);
        await using var sourceConnection = await database.RuntimeSource!.OpenConnectionAsync();
        await using var sourceTransaction = await sourceConnection.BeginTransactionAsync();
        await SetContextAsync(sourceConnection, sourceTransaction, workspace);
        await LockSourceAsync(sourceConnection, sourceTransaction, workspace, source.Id, recordId, recordLock: true);
        await using var authorizationConnection = await database.RuntimeSource!.OpenConnectionAsync();
        await using var authorizationTransaction = await authorizationConnection.BeginTransactionAsync();
        await using var pidCommand = new NpgsqlCommand("SELECT pg_backend_pid()", authorizationConnection, authorizationTransaction);
        var blocker = (int)(await pidCommand.ExecuteScalarAsync())!;
        authority.TargetListId = target.Id;
        authority.FreshUntilUtc = DateTimeOffset.UtcNow.AddSeconds(5);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        authority.OnTargetRead = async () =>
        {
            await using var command = new NpgsqlCommand(
                "SELECT pg_advisory_xact_lock(hashtextextended($1::text,391813))", authorizationConnection, authorizationTransaction);
            command.Parameters.AddWithValue(workspace);
            await command.ExecuteNonQueryAsync(cancellation.Token);
        };
        var write = engine.UpdateRecordAsync(actor, workspace, source.Id, recordId,
            new(1, 1, [new(reference.Id, JsonValue.Create(targetId.ToString("D")))]), cancellation.Token);
        var released = false;
        try
        {
            await WaitForBlockerAsync(blocker);
            var untilExpired = authority.FreshUntilUtc.Value - DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(50);
            if (untilExpired > TimeSpan.Zero) await Task.Delay(untilExpired, cancellation.Token);
            await authorizationTransaction.CommitAsync(cancellation.Token);
            released = true;
            // The source record remains locked. A batched host path would wait
            // for it instead of rejecting the expired decision at this checkpoint.
            var failure = await Assert.ThrowsAsync<ListException>(() => write.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(ListFailure.Unavailable, failure.Failure);
        }
        finally
        {
            await cancellation.CancelAsync();
            if (!released) await authorizationTransaction.RollbackAsync();
            await sourceTransaction.RollbackAsync();
            try { await write; } catch { }
        }
        Assert.Equal(before, await StateAsync(workspace));
        authority.FreshUntilUtc = null;
        authority.OnTargetRead = null;
        Assert.Equal(1, (await engine.GetRecordAsync(actor, workspace, source.Id, recordId)).Revision);
    }

    private async Task WaitForBlockerAsync(int blocker)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            await using var command = database.AdminSource!.CreateCommand(
                "SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE datname=current_database() AND $1=ANY(pg_blocking_pids(pid)))");
            command.Parameters.AddWithValue(blocker);
            if ((bool)(await command.ExecuteScalarAsync())!) return;
            await Task.Delay(20);
        }
        Assert.Fail("The operation did not wait for its held source lock.");
    }

    private async Task<string> StateAsync(Guid workspace)
    {
        await using var command = database.AdminSource!.CreateCommand("""
            SELECT concat_ws(',', stored_bytes,audit_count,security_audit_count,
              (SELECT count(*) FROM cellbridge_lists.records WHERE workspace_id=$1),
              (SELECT count(*) FROM cellbridge_lists.create_receipts WHERE workspace_id=$1),
              (SELECT count(*) FROM cellbridge_lists.record_tombstones WHERE workspace_id=$1),
              (SELECT count(*) FROM cellbridge_lists.audit_events WHERE workspace_id=$1),
              (SELECT sum(next_number) FROM cellbridge_lists.list_counters WHERE workspace_id=$1))
            FROM cellbridge_lists.workspaces WHERE workspace_id=$1
            """);
        command.Parameters.AddWithValue(workspace);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static async Task LockSourceAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid workspace, Guid list, Guid record, bool recordLock)
    {
        var sql = recordLock
            ? "SELECT record_id FROM cellbridge_lists.records WHERE workspace_id=$1 AND list_id=$2 AND record_id=$3 FOR UPDATE"
            : "SELECT list_id FROM cellbridge_lists.lists WHERE workspace_id=$1 AND list_id=$2 FOR UPDATE";
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue(workspace);
        command.Parameters.AddWithValue(list);
        if (recordLock) command.Parameters.AddWithValue(record);
        Assert.Equal(recordLock ? record : list, await command.ExecuteScalarAsync());
    }

    private static async Task SetContextAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid workspace)
    {
        await using var command = new NpgsqlCommand(
            "SELECT set_config('cellbridge.workspace_id',$1,true)", connection, transaction);
        command.Parameters.AddWithValue(workspace.ToString("D"));
        await command.ExecuteNonQueryAsync();
    }

    private sealed class ExpiringAuthority(string domain) : IListAuthorizationProvider
    {
        public string PolicyDomain => domain;
        public Guid TargetListId { get; set; }
        public DateTimeOffset? FreshUntilUtc { get; set; }
        public Func<Task>? OnTargetRead { get; set; }
        public async ValueTask<ListAuthorizationDecision> AuthorizeAsync(ListAuthorizationRequest request,
            CancellationToken ct = default)
        {
            if (request.ListId == TargetListId && request.Action == ListAuthorizationAction.Read &&
                OnTargetRead is { } callback) await callback();
            var now = DateTimeOffset.UtcNow;
            return new(true, now, FreshUntilUtc ?? now.AddMinutes(5));
        }
    }

    private sealed class Authority(string domain, bool allowed) : IListAuthorizationProvider
    {
        public string PolicyDomain => domain;
        public ValueTask<ListAuthorizationDecision> AuthorizeAsync(ListAuthorizationRequest request,
            CancellationToken ct = default) => ValueTask.FromResult(new ListAuthorizationDecision(allowed));
    }
}
