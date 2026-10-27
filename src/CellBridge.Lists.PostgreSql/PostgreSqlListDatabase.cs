using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace CellBridge.Lists.PostgreSql;

internal sealed partial class EfListEngine
{
    private readonly SemaphoreSlim _healthGate = new(1, 1);
    private bool _healthy;
    private static async Task<T> MapStoreFailuresAsync<T>(Func<Task<T>> operation, CancellationToken ct)
    {
        try { return await operation(); }
        catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.QueryCanceled or PostgresErrorCodes.LockNotAvailable)
        { throw new ListException(ListFailure.Unavailable, "The list operation timed out."); }
        catch (DbUpdateException ex) when (ex.InnerException is NpgsqlException npgsql)
        { throw new ListException(ListFailure.Unavailable, $"The list store is unavailable: {npgsql.Message}"); }
        catch (NpgsqlException ex)
        { throw new ListException(ListFailure.Unavailable, $"The list store is unavailable: {ex.Message}"); }
    }

    private async Task EnsureHealthyAsync(CancellationToken ct)
    {
        if (_healthy) return;
        await _healthGate.WaitAsync(ct);
        try
        {
            if (_healthy) return;
            await using var db = CreateContext();
            await db.Database.OpenConnectionAsync(ct);
            var connection = (NpgsqlConnection)db.Database.GetDbConnection();
            await using var command = new NpgsqlCommand("""
                WITH expected(table_name, allowed) AS (VALUES
                  ('schema_version', ARRAY['SELECT']),
                  ('templates', ARRAY['SELECT']),
                  ('workspaces', ARRAY['SELECT','UPDATE']),
                  ('memberships', ARRAY['SELECT','INSERT','UPDATE','DELETE']),
                  ('lists', ARRAY['SELECT','INSERT','UPDATE']),
                  ('list_schemas', ARRAY['SELECT','INSERT']),
                  ('list_counters', ARRAY['SELECT','INSERT','UPDATE']),
                  ('list_grants', ARRAY['SELECT','INSERT','UPDATE','DELETE']),
                  ('records', ARRAY['SELECT','INSERT','UPDATE','DELETE']),
                  ('record_tombstones', ARRAY['INSERT']),
                  ('create_receipts', ARRAY['SELECT','INSERT','DELETE']),
                  ('record_references', ARRAY['SELECT','INSERT','UPDATE','DELETE']),
                  ('saved_views', ARRAY['SELECT','INSERT','UPDATE','DELETE']),
                  ('audit_events', ARRAY['SELECT','INSERT'])
                )
                SELECT v.version,
                  EXISTS (SELECT 1 FROM pg_roles inherited WHERE pg_has_role(current_user,inherited.oid,'MEMBER') AND (inherited.rolsuper OR inherited.rolbypassrls)),
                  EXISTS (SELECT 1 FROM pg_roles inherited WHERE pg_has_role(current_user,inherited.oid,'MEMBER') AND
                    has_schema_privilege(inherited.oid,'cellbridge_lists','CREATE')),
                  EXISTS (SELECT 1 FROM pg_class owned JOIN pg_namespace ns ON ns.oid=owned.relnamespace
                    WHERE ns.nspname='cellbridge_lists' AND pg_has_role(current_user,owned.relowner,'MEMBER')),
                  pg_has_role(current_user,n.nspowner,'MEMBER'),
                  (SELECT bool_and(COALESCE(has_table_privilege(current_user,
                    to_regclass('cellbridge_lists.' || e.table_name), privilege), false))
                    FROM expected e CROSS JOIN LATERAL unnest(e.allowed) privilege) AND
                  has_sequence_privilege(current_user,'cellbridge_lists.audit_events_id_seq','USAGE') AND
                  has_sequence_privilege(current_user,'cellbridge_lists.audit_events_id_seq','SELECT') AND
                  has_function_privilege(current_user,'cellbridge_lists.utf16_key(text)','EXECUTE') AND
                  has_function_privilege(current_user,'cellbridge_lists.timestamp_key(text)','EXECUTE') AND
                  has_function_privilege(current_user,'cellbridge_lists.utf16_compare(text,text)','EXECUTE') AND
                  has_function_privilege(current_user,'cellbridge_lists.timestamp_compare(text,text)','EXECUTE') AND
                  has_function_privilege(current_user,'cellbridge_lists.c_compare(text,text)','EXECUTE'),
                  EXISTS (
                    SELECT 1 FROM expected e
                    JOIN pg_class tbl ON tbl.oid=to_regclass('cellbridge_lists.' || e.table_name)
                    CROSS JOIN LATERAL aclexplode(acldefault('r', tbl.relowner)) acl
                    WHERE NOT (acl.privilege_type=ANY(e.allowed)) AND EXISTS (
                      SELECT 1 FROM pg_roles inherited
                      WHERE pg_has_role(current_user,inherited.oid,'MEMBER') AND
                        (has_table_privilege(inherited.oid,tbl.oid,acl.privilege_type) OR
                          CASE WHEN acl.privilege_type IN ('SELECT','INSERT','UPDATE','REFERENCES')
                            THEN has_any_column_privilege(inherited.oid,tbl.oid,acl.privilege_type)
                            ELSE false END)
                    )
                  ) OR EXISTS (
                    SELECT 1 FROM pg_roles inherited
                    WHERE pg_has_role(current_user,inherited.oid,'MEMBER') AND
                      has_sequence_privilege(inherited.oid,'cellbridge_lists.audit_events_id_seq','UPDATE')
                  ),
                  EXISTS (SELECT 1 FROM pg_trigger t
                    WHERE t.tgrelid='cellbridge_lists.workspaces'::regclass
                      AND t.tgname='immutable_workspace_authorization_anchor' AND t.tgenabled='O'
                      AND t.tgfoid='cellbridge_lists.reject_authorization_anchor_rebinding()'::regprocedure)
                FROM cellbridge_lists.schema_version v
                CROSS JOIN pg_roles r
                CROSS JOIN pg_class c
                JOIN pg_namespace n ON n.oid=c.relnamespace
                WHERE v.singleton AND r.rolname=current_user AND n.nspname='cellbridge_lists' AND c.relname='records'
                """, connection);
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct) || reader.GetInt32(0) != 4)
                throw new ListException(ListFailure.Unavailable, "The list schema version is missing or unsupported.");
            if (reader.GetBoolean(1) || reader.GetBoolean(2) || reader.GetBoolean(3) || reader.GetBoolean(4) ||
                !reader.GetBoolean(5) || reader.GetBoolean(6) || !reader.GetBoolean(7))
                throw new ListException(ListFailure.Unavailable, "The list runtime role has unsafe database privileges.");
            _healthy = true;
        }
        finally { _healthGate.Release(); }
    }

    private async Task ConfigureAndLockAuthorizationAsync(DbConnection connection,
        DbTransaction transaction, Guid workspaceId, bool exclusive,
        OperationAuthorization? authorization, CancellationToken ct, RecordLocks? recordLocks = null)
    {
        var milliseconds = Math.Clamp((long)_limits.StatementTimeout.TotalMilliseconds, 1, int.MaxValue);
        await using var batch = new NpgsqlBatch((NpgsqlConnection)connection, (NpgsqlTransaction)transaction);
        var configure = new NpgsqlBatchCommand("""
            SELECT set_config('cellbridge.workspace_id',$1::text,true),
              set_config('synchronous_commit','on',true),
              set_config('statement_timeout',$2::text,true),
              set_config('lock_timeout',$2::text,true)
            """);
        configure.Parameters.AddWithValue(workspaceId);
        configure.Parameters.AddWithValue(milliseconds);
        batch.BatchCommands.Add(configure);

        var anchorPredicate = authorization is null
            ? "AND w.authorization_mode='builtin'"
            : "AND w.authorization_mode=$2 AND w.authorization_domain=$3 AND w.authorization_scope=$4";
        var authorize = new NpgsqlBatchCommand($$"""
            SELECT {{(exclusive ? "pg_advisory_xact_lock" : "pg_advisory_xact_lock_shared")}}
              (hashtextextended($1::text,391813))
            FROM cellbridge_lists.workspaces w
            WHERE w.workspace_id=$1::uuid {{anchorPredicate}}
            """);
        authorize.Parameters.AddWithValue(workspaceId);
        if (authorization is not null)
        {
            authorize.Parameters.AddWithValue("host");
            authorize.Parameters.AddWithValue(authorization.Anchor.Domain!);
            authorize.Parameters.AddWithValue(authorization.Anchor.Scope!);
        }
        batch.BatchCommands.Add(authorize);
        if (recordLocks is not null)
            AppendRecordLocks(batch, workspaceId, recordLocks.ListId, recordLocks.RecordId,
                deleteLock: false, guardAnchor: true);

        await using var reader = await batch.ExecuteReaderAsync(ct);
        if (!await reader.NextResultAsync(ct) || !await reader.ReadAsync(ct)) throw NotFound();
        if (recordLocks is not null)
        {
            if (!await reader.NextResultAsync(ct) || !await reader.ReadAsync(ct)) throw NotFound();
            if (recordLocks.RecordId != Guid.Empty) await reader.NextResultAsync(ct);
        }
    }

    private async Task ConfigureWorkspaceAsync(DbConnection connection,
        DbTransaction transaction, Guid workspaceId, CancellationToken ct)
    {
        var milliseconds = Math.Clamp((long)_limits.StatementTimeout.TotalMilliseconds, 1, int.MaxValue);
        await using var command = new NpgsqlCommand("""
            SELECT set_config('cellbridge.workspace_id',$1::text,true),
              set_config('synchronous_commit','on',true),
              set_config('statement_timeout',$2::text,true),
              set_config('lock_timeout',$2::text,true)
            """, (NpgsqlConnection)connection, (NpgsqlTransaction)transaction);
        command.Parameters.AddWithValue(workspaceId);
        command.Parameters.AddWithValue(milliseconds);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task LockQuotaAsync(DbConnection connection, DbTransaction transaction,
        Guid workspaceId, CancellationToken ct)
    {
        if (await ScalarAsync<Guid?>(connection, transaction,
            "SELECT workspace_id FROM cellbridge_lists.workspaces WHERE workspace_id=$1 FOR UPDATE", ct, workspaceId) is null)
            throw NotFound();
    }

    private static NpgsqlCommand Command(DbConnection connection, DbTransaction transaction,
        string sql, params object?[] values)
    {
        var command = new NpgsqlCommand(sql, (NpgsqlConnection)connection, (NpgsqlTransaction)transaction);
        foreach (var value in values) command.Parameters.Add(new NpgsqlParameter { Value = value ?? DBNull.Value });
        return command;
    }

    private static async Task ExecuteAsync(DbConnection connection, DbTransaction transaction,
        string sql, CancellationToken ct, params object?[] values)
    {
        await using var command = Command(connection, transaction, sql, values);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<T> ScalarAsync<T>(DbConnection connection, DbTransaction transaction,
        string sql, CancellationToken ct, params object?[] values)
    {
        await using var command = Command(connection, transaction, sql, values);
        var result = await command.ExecuteScalarAsync(ct);
        if (result is null or DBNull) return default!;
        return (T)result;
    }

    private static Task<long> CanonicalJsonBytesAsync(DbConnection connection, DbTransaction transaction,
        string json, CancellationToken ct) => ScalarAsync<long>(connection, transaction,
            "SELECT octet_length(($1::jsonb)::text)::bigint", ct, json);

    private PostgreSqlListDbContext CreateContext() =>
        _contextFactory?.CreateDbContext() ?? new PostgreSqlListDbContext(_dbOptions);

    private static async Task LockRecordContextAsync(DbConnection connection,
        DbTransaction transaction, Guid workspaceId, Guid listId, Guid recordId,
        bool deleteLock, CancellationToken ct)
    {
        await using var batch = new NpgsqlBatch((NpgsqlConnection)connection, (NpgsqlTransaction)transaction);
        AppendRecordLocks(batch, workspaceId, listId, recordId, deleteLock, guardAnchor: false);
        await using var reader = await batch.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw NotFound();
        if (recordId != Guid.Empty) await reader.NextResultAsync(ct);
    }

    private static void AppendRecordLocks(NpgsqlBatch batch, Guid workspaceId, Guid listId,
        Guid recordId, bool deleteLock, bool guardAnchor)
    {
        // Batch statements can execute before C# checks an earlier result.
        // Guard each later lock so a mismatched anchor cannot acquire it.
        var guard = guardAnchor
            ? $"AND EXISTS (SELECT 1 FROM cellbridge_lists.workspaces w WHERE w.workspace_id=$1 AND w.authorization_mode='builtin')"
            : "";
        var list = new NpgsqlBatchCommand($$"""
            SELECT list_id FROM cellbridge_lists.lists
            WHERE workspace_id=$1 AND list_id=$2 {{guard}} FOR SHARE
            """);
        list.Parameters.AddWithValue(workspaceId);
        list.Parameters.AddWithValue(listId);
        batch.BatchCommands.Add(list);
        if (recordId != Guid.Empty)
        {
            var record = new NpgsqlBatchCommand($$"""
                SELECT record_id FROM cellbridge_lists.records
                WHERE workspace_id=$1 AND list_id=$2 AND record_id=$3 {{guard}}
                {{(deleteLock ? "FOR UPDATE" : "FOR NO KEY UPDATE")}}
                """);
            record.Parameters.AddWithValue(workspaceId);
            record.Parameters.AddWithValue(listId);
            record.Parameters.AddWithValue(recordId);
            batch.BatchCommands.Add(record);
        }
    }

    private static async Task LockCreateStateAsync(DbConnection connection,
        DbTransaction transaction, Guid workspaceId, Guid listId, CancellationToken ct)
    {
        await using var batch = new NpgsqlBatch((NpgsqlConnection)connection, (NpgsqlTransaction)transaction);
        var workspace = new NpgsqlBatchCommand("""
            SELECT workspace_id FROM cellbridge_lists.workspaces
            WHERE workspace_id=$1 FOR UPDATE
            """);
        workspace.Parameters.AddWithValue(workspaceId);
        batch.BatchCommands.Add(workspace);
        var counter = new NpgsqlBatchCommand("""
            SELECT list_id FROM cellbridge_lists.list_counters
            WHERE workspace_id=$1 AND list_id=$2 FOR UPDATE
            """);
        counter.Parameters.AddWithValue(workspaceId);
        counter.Parameters.AddWithValue(listId);
        batch.BatchCommands.Add(counter);
        await batch.ExecuteNonQueryAsync(ct);
    }

    private static async Task<DateTimeOffset> ClockAsync(DbConnection connection,
        DbTransaction transaction, CancellationToken ct)
    {
        await using var command = Command(connection, transaction, "SELECT clock_timestamp()");
        return await command.ExecuteScalarAsync(ct) switch
        {
            DateTimeOffset value => value,
            DateTime value => new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)),
            _ => throw new InvalidOperationException("PostgreSQL did not return its current timestamp.")
        };
    }

    private static async Task LockWorkspaceMutationAsync(DbConnection connection, DbTransaction transaction,
        Guid workspaceId, CancellationToken ct)
    {
        await using var command = Command(connection, transaction,
            "SELECT pg_advisory_xact_lock(hashtextextended($1,391814))", workspaceId.ToString("D"));
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task LockListSchemaSharedAsync(DbConnection connection, DbTransaction transaction,
        Guid workspaceId, Guid listId, CancellationToken ct)
    {
        if (await ScalarAsync<Guid?>(connection, transaction,
            "SELECT list_id FROM cellbridge_lists.lists WHERE workspace_id=$1 AND list_id=$2 FOR SHARE", ct,
            workspaceId, listId) is null) throw NotFound();
    }

    private async Task<(long Bytes, bool Claimed)> ClaimIdentityAsync(DbConnection connection, DbTransaction transaction,
        Guid workspaceId, Guid listId, Guid recordId, string valuesJson, CancellationToken ct)
    {
        long valueBytes;
        bool claimed;
        // PostgreSQL normalizes JSON sizes and atomically claims identities. An oversized
        // value cannot claim or lock an identity; ordinary record persistence below uses EF.
        await using (var claim = Command(connection, transaction, """
            WITH content AS (
              SELECT octet_length(($4::jsonb)::text)::bigint AS bytes
            ), claimed AS (
              INSERT INTO cellbridge_lists.record_tombstones(workspace_id,list_id,record_id)
              SELECT $1,$2,$3 FROM content WHERE bytes <= $5
              ON CONFLICT DO NOTHING RETURNING 1
            )
            SELECT content.bytes, EXISTS(SELECT 1 FROM claimed) FROM content
            """, workspaceId, listId, recordId, valuesJson, _limits.MaxRecordBytes))
        await using (var reader = await claim.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct)) throw new InvalidOperationException("The identity claim returned no size result.");
            valueBytes = reader.GetInt64(0);
            claimed = reader.GetBoolean(1);
        }
        return (valueBytes, claimed);
    }
    private static async Task<HashSet<Guid>> LockReferenceTargetsAsync(DbConnection connection, DbTransaction transaction,
        Guid workspaceId, ReferenceAssignment[] ordered, Guid[] lockableFieldIds, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            SELECT requested.field_id, target.record_id
            FROM unnest($1::uuid[],$2::uuid[],$3::uuid[]) AS requested(field_id,list_id,record_id)
            LEFT JOIN LATERAL (
              SELECT record_id FROM cellbridge_lists.records
              WHERE workspace_id=$4 AND list_id=requested.list_id AND record_id=requested.record_id
                AND requested.field_id=ANY($5::uuid[])
              FOR KEY SHARE
            ) target ON true
            ORDER BY requested.field_id
            """, (NpgsqlConnection)connection, (NpgsqlTransaction)transaction);
        command.Parameters.AddWithValue(ordered.Select(reference => reference.FieldId).ToArray());
        command.Parameters.AddWithValue(ordered.Select(reference => reference.TargetListId).ToArray());
        command.Parameters.AddWithValue(ordered.Select(reference => reference.TargetRecordId).ToArray());
        command.Parameters.AddWithValue(workspaceId);
        command.Parameters.AddWithValue(lockableFieldIds);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var existingTargets = new HashSet<Guid>();
        while (await reader.ReadAsync(ct))
        {
            if (!reader.IsDBNull(1)) existingTargets.Add(reader.GetGuid(0));
        }
        return existingTargets;
    }
    private static bool IsUniqueViolation(Exception ex) => ex is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } || ex.InnerException is not null && IsUniqueViolation(ex.InnerException);
    private static bool IsReferenceViolation(Exception ex) => ex is PostgresException { SqlState: PostgresErrorCodes.ForeignKeyViolation, ConstraintName: "record_references_target_fk" } || ex.InnerException is not null && IsReferenceViolation(ex.InnerException);
    private static DbContextOptions<PostgreSqlListDbContext> NormalizeOptions(DbContextOptions<PostgreSqlListDbContext> options) => PostgreSqlListSetup.NormalizeOptions(options);
    private static PooledDbContextFactory<PostgreSqlListDbContext>? CreateContextFactory(DbContextOptions<PostgreSqlListDbContext> options) =>
        RelationalOptionsExtension.Extract(options).Connection is null ? new(options) : null;

    private async Task<PostgreSqlListWorkspaceTransaction> OpenOperationSessionAsync(Guid workspaceId, CancellationToken ct)
    {
        await EnsureHealthyAsync(ct);
        return await OpenSessionAsync(workspaceId, ct);
    }

    private async Task<PostgreSqlListWorkspaceTransaction> OpenSessionAsync(Guid workspaceId, CancellationToken ct)
    {
        var db = CreateContext();
        try
        {
            db.Database.AutoSavepointsEnabled = false;
            await db.Database.OpenConnectionAsync(ct);
            var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct);
            return new(db, transaction);
        }
        catch { await db.DisposeAsync(); throw; }
    }
    private Task LockAuthorizationAsync(PostgreSqlListDbContext db, Guid workspaceId, bool exclusive, bool mutation,
        OperationAuthorization? authorization, CancellationToken ct, RecordLocks? recordLocks = null) =>
        ConfigureAndLockAuthorizationAsync(db.Database.GetDbConnection(), db.Database.CurrentTransaction!.GetDbTransaction(),
            // Keep host freshness validation between authorization and record lock waits.
            workspaceId, exclusive, authorization, ct, authorization is null ? recordLocks : null);

    private static Task LockListExclusiveAsync(DbConnection connection, DbTransaction transaction,
        Guid workspaceId, Guid listId, CancellationToken ct) => ExecuteAsync(connection, transaction,
        "SELECT list_id FROM cellbridge_lists.lists WHERE workspace_id=$1 AND list_id=$2 FOR UPDATE", ct, workspaceId, listId);

    private Task LockRecordAsync(PostgreSqlListDbContext db, Guid workspaceId, Guid listId, Guid recordId, bool deleteLock, CancellationToken ct) =>
        LockRecordContextAsync(db.Database.GetDbConnection(), db.Database.CurrentTransaction!.GetDbTransaction(), workspaceId, listId, recordId, deleteLock, ct);
    private Task<(long Bytes, bool Claimed)> ClaimIdentityAsync(PostgreSqlListDbContext db, Guid workspaceId, Guid listId, Guid recordId, string json, CancellationToken ct) =>
        ClaimIdentityAsync(db.Database.GetDbConnection(), db.Database.CurrentTransaction!.GetDbTransaction(), workspaceId, listId, recordId, json, ct);
    private static Task<long> CanonicalJsonBytesAsync(PostgreSqlListDbContext db, string json, CancellationToken ct) =>
        CanonicalJsonBytesAsync(db.Database.GetDbConnection(), db.Database.CurrentTransaction!.GetDbTransaction(), json, ct);
    private static Task<DateTimeOffset> ClockAsync(PostgreSqlListDbContext db, CancellationToken ct) =>
        ClockAsync(db.Database.GetDbConnection(), db.Database.CurrentTransaction!.GetDbTransaction(), ct);
    private static Task<HashSet<Guid>> LockReferenceTargetsAsync(PostgreSqlListDbContext db, Guid workspaceId, ReferenceAssignment[] ordered, Guid[] lockableFieldIds, CancellationToken ct) =>
        LockReferenceTargetsAsync(db.Database.GetDbConnection(), db.Database.CurrentTransaction!.GetDbTransaction(), workspaceId, ordered, lockableFieldIds, ct);

    private Task ConfigureCandidateReadAsync(ListDbContext db, Guid workspaceId, OperationAuthorization authorization, CancellationToken ct) => ConfigureWorkspaceAsync(db.Database.GetDbConnection(), db.Database.CurrentTransaction!.GetDbTransaction(), workspaceId, ct);

    private async Task LockRegistrationAsync(PostgreSqlListDbContext db, Guid workspaceId, bool template, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        var transaction = db.Database.CurrentTransaction!.GetDbTransaction();
        await ConfigureWorkspaceAsync(connection, transaction, workspaceId, ct);
        if (template)
            await ExecuteAsync(connection, transaction, "SELECT pg_advisory_xact_lock(1263422022)", ct);
        else
            await ExecuteAsync(connection, transaction, "SELECT pg_advisory_xact_lock(hashtextextended($1,391813))", ct, workspaceId.ToString("D"));
    }
    private static Task<bool> TemplateDefinitionsEqualAsync(PostgreSqlListDbContext db, string left, string right, CancellationToken ct) =>
        db.Database.SqlQuery<bool>($"SELECT {left}::jsonb={right}::jsonb AS \"Value\"").SingleAsync(ct);

}
