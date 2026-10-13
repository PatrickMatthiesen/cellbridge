using System.Text;
using System.Text.Json.Nodes;
using Npgsql;

namespace CellBridge.Lists.PostgreSql;

/// <summary>
/// PostgreSQL list storage. Version 1 evaluates queries in memory after a bounded read of at
/// most <see cref="ListLimits.MaxRecordsPerList"/> records. Workspace writes are serialized.
/// </summary>
public sealed partial class PostgreSqlListEngine : IListEngine
{
    private readonly NpgsqlDataSource _source;
    private readonly ListLimits _limits;
    private readonly SemaphoreSlim _healthGate = new(1, 1);
    private bool _healthy;

    public PostgreSqlListEngine(NpgsqlDataSource runtimeSource, ListLimits? limits = null)
    {
        _source = runtimeSource ?? throw new ArgumentNullException(nameof(runtimeSource));
        _limits = limits ?? new ListLimits();
        _limits.Validate();
    }

    public Task CheckHealthAsync(CancellationToken cancellationToken = default) => EnsureHealthyAsync(cancellationToken);

    public async Task<IReadOnlyList<ListDefinition>> GetListsAsync(ListActor actor, Guid workspaceId,
        CancellationToken ct = default) => await ReadAsync(actor, workspaceId, async (connection, transaction, member) =>
    {
        await using var command = new NpgsqlCommand("""
            SELECT s.definition::text
            FROM cellbridge_lists.lists l
            JOIN cellbridge_lists.list_schemas s ON s.workspace_id=l.workspace_id AND s.list_id=l.list_id AND s.schema_version=l.schema_version
            JOIN cellbridge_lists.list_grants g ON g.workspace_id=l.workspace_id AND g.list_id=l.list_id
            WHERE l.workspace_id=$1 AND g.subject=$2 AND g.access<>0
            ORDER BY l.name COLLATE "C", l.list_id
            """, connection, transaction);
        command.Parameters.AddWithValue(workspaceId);
        command.Parameters.AddWithValue(actor.Subject);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<ListDefinition>();
        while (await reader.ReadAsync(ct)) result.Add(PostgreSqlListJson.Deserialize<ListDefinition>(reader.GetString(0)));
        return result;
    }, ct);

    public async Task<ListDefinition> CreateListAsync(ListActor actor, Guid workspaceId,
        CreateListRequest request, CancellationToken ct = default)
    {
        ValidateActor(actor);
        if (request.Id == Guid.Empty) throw Validation("A list ID is required.");
        return await InTransactionAsync(workspaceId, false, async (connection, transaction) =>
        {
            var member = await AuthorizeMemberAsync(connection, transaction, workspaceId, actor.Subject, ct);
            if (!member.CanCreateLists) throw Forbidden();

            ListDefinition definition;
            if (request.TemplateId is { } templateId)
            {
                if (request.TemplateVersion is not { } templateVersion) throw Validation("A template version is required.");
                var template = await GetTemplateAsync(connection, transaction, templateId, templateVersion, ct);
                definition = ListTemplates.Instantiate(template, workspaceId, request.Id, request.Name,
                    request.Fields, request.NumberPrefix, _limits);
            }
            else
            {
                if (request.TemplateVersion is not null) throw Validation("A template ID is required.");
                definition = new ListDefinition(request.Id, workspaceId, request.Name, 1,
                    request.Fields.Select(x => x with { Protected = false }).ToArray(), NumberPrefix: request.NumberPrefix);
            }
            ListValidation.ValidateDefinition(definition, _limits);
            await ValidateReferenceDefinitionsAsync(connection, transaction, workspaceId, actor.Subject, definition, ct);

            await LockQuotaAsync(connection, transaction, workspaceId, ct);
            var listCount = await ScalarAsync<long>(connection, transaction,
                "SELECT count(*) FROM cellbridge_lists.lists WHERE workspace_id=$1", ct, workspaceId);
            if (listCount >= _limits.MaxLists) throw Limit("The workspace list limit has been reached.");
            var json = PostgreSqlListJson.Serialize(definition);
            await ReserveStorageAsync(connection, transaction, workspaceId,
                await CanonicalJsonBytesAsync(connection, transaction, json, ct) + 256 + 80 + Encoding.UTF8.GetByteCount(actor.Subject), ct);

            try
            {
                await ExecuteAsync(connection, transaction,
                    "INSERT INTO cellbridge_lists.lists(workspace_id,list_id,name,schema_version,template_id,template_version,number_prefix) VALUES ($1,$2,$3,1,$4,$5,$6)", ct,
                    workspaceId, request.Id, request.Name, request.TemplateId, request.TemplateVersion, request.NumberPrefix);
                await ExecuteAsync(connection, transaction,
                    "INSERT INTO cellbridge_lists.list_schemas(workspace_id,list_id,schema_version,definition) VALUES ($1,$2,1,$3::jsonb)", ct,
                    workspaceId, request.Id, json);
                await ExecuteAsync(connection, transaction,
                    "INSERT INTO cellbridge_lists.list_grants(workspace_id,list_id,subject,access) VALUES ($1,$2,$3,$4)", ct,
                    workspaceId, request.Id, actor.Subject, (int)(ListAccess.Read | ListAccess.Write | ListAccess.Design));
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
            { throw Conflict("That list ID already exists."); }
            await AuditAsync(connection, transaction, workspaceId, actor.Subject, "list.create", request.Id, null, 1,
                definition.Fields.Select(x => x.Id), false, ct);
            return definition;
        }, ct);
    }

    public Task<ListDefinition> GetListAsync(ListActor actor, Guid workspaceId, Guid listId,
        CancellationToken ct = default) => ReadAsync(actor, workspaceId, async (connection, transaction, member) =>
    {
        await RequireAccessAsync(connection, transaction, workspaceId, listId, actor.Subject, ListAccess.Read, ct);
        return await GetDefinitionAsync(connection, transaction, workspaceId, listId, false, ct);
    }, ct);

    public async Task<ListDefinition> PublishSchemaAsync(ListActor actor, Guid workspaceId, Guid listId,
        PublishSchemaRequest request, CancellationToken ct = default)
    {
        ValidateActor(actor);
        return await InTransactionAsync(workspaceId, false, async (connection, transaction) =>
        {
            await AuthorizeMemberAsync(connection, transaction, workspaceId, actor.Subject, ct);
            await RequireAccessAsync(connection, transaction, workspaceId, listId, actor.Subject, ListAccess.Design, ct);
            await LockWorkspaceMutationAsync(connection, transaction, workspaceId, ct);
            var previous = await GetDefinitionAsync(connection, transaction, workspaceId, listId, true, ct);
            if (previous.SchemaVersion != request.ExpectedVersion) throw Conflict("The list schema changed.");
            var next = previous with { Name = request.Name, SchemaVersion = checked(previous.SchemaVersion + 1), Fields = request.Fields };
            ListValidation.ValidateEvolution(previous, next, _limits);
            var previousFieldIds = previous.Fields.Select(x => x.Id).ToHashSet();
            var addedReferenceFieldIds = next.Fields
                .Where(x => x.Type == ListFieldType.Reference && !previousFieldIds.Contains(x.Id))
                .Select(x => x.Id)
                .ToHashSet();
            await ValidateReferenceDefinitionsAsync(connection, transaction, workspaceId, actor.Subject, next, ct,
                addedReferenceFieldIds);
            await LockQuotaAsync(connection, transaction, workspaceId, ct);
            var versions = await ScalarAsync<long>(connection, transaction,
                "SELECT count(*) FROM cellbridge_lists.list_schemas WHERE workspace_id=$1 AND list_id=$2", ct, workspaceId, listId);
            if (versions >= _limits.MaxSchemaVersions) throw Limit("The list schema version limit has been reached.");
            var json = PostgreSqlListJson.Serialize(next);
            await ReserveStorageAsync(connection, transaction, workspaceId,
                await CanonicalJsonBytesAsync(connection, transaction, json, ct) + 96, ct);
            await ExecuteAsync(connection, transaction,
                "INSERT INTO cellbridge_lists.list_schemas(workspace_id,list_id,schema_version,definition) VALUES ($1,$2,$3,$4::jsonb)", ct,
                workspaceId, listId, next.SchemaVersion, json);
            await ExecuteAsync(connection, transaction,
                "UPDATE cellbridge_lists.lists SET name=$3,schema_version=$4 WHERE workspace_id=$1 AND list_id=$2", ct,
                workspaceId, listId, next.Name, next.SchemaVersion);
            await AuditAsync(connection, transaction, workspaceId, actor.Subject, "schema.publish", listId, null,
                next.SchemaVersion, next.Fields.Select(x => x.Id), false, ct);
            return next;
        }, ct);
    }

    public async Task<IReadOnlyList<SavedListView>> GetViewsAsync(ListActor actor, Guid workspaceId,
        Guid listId, CancellationToken ct = default) => await ReadAsync(actor, workspaceId, async (connection, transaction, member) =>
    {
        await RequireAccessAsync(connection, transaction, workspaceId, listId, actor.Subject, ListAccess.Read, ct);
        await using var command = Command(connection, transaction,
            "SELECT definition::text FROM cellbridge_lists.saved_views WHERE workspace_id=$1 AND list_id=$2 ORDER BY view_id", workspaceId, listId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var views = new List<SavedListView>();
        while (await reader.ReadAsync(ct)) views.Add(PostgreSqlListJson.Deserialize<SavedListView>(reader.GetString(0)));
        return views;
    }, ct);

    public async Task<SavedListView> SaveViewAsync(ListActor actor, Guid workspaceId, Guid listId,
        SaveViewRequest request, CancellationToken ct = default)
    {
        ValidateActor(actor);
        return await InTransactionAsync(workspaceId, false, async (connection, transaction) =>
        {
            await AuthorizeMemberAsync(connection, transaction, workspaceId, actor.Subject, ct);
            await RequireAccessAsync(connection, transaction, workspaceId, listId, actor.Subject, ListAccess.Design, ct);
            var definition = await GetDefinitionAsync(connection, transaction, workspaceId, listId, true, ct);
            ListValidation.ValidateQuery(definition, request.Query, _limits);
            if (request.Id == Guid.Empty || string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 200 ||
                !ListValidation.IsWellFormedText(request.Name))
                throw Validation("A view ID and valid name are required.");
            if (request.VisibleFields.Distinct().Count() != request.VisibleFields.Count ||
                request.VisibleFields.Any(id => definition.Fields.All(x => x.Id != id)))
                throw Validation("A view contains an unknown or duplicate field.");
            await LockQuotaAsync(connection, transaction, workspaceId, ct);
            var count = await ScalarAsync<long>(connection, transaction,
                "SELECT count(*) FROM cellbridge_lists.saved_views WHERE workspace_id=$1 AND list_id=$2 AND view_id<>$3", ct,
                workspaceId, listId, request.Id);
            var previousBytes = await ScalarAsync<long?>(connection, transaction,
                "SELECT octet_length(definition::text)::bigint FROM cellbridge_lists.saved_views WHERE workspace_id=$1 AND list_id=$2 AND view_id=$3", ct,
                workspaceId, listId, request.Id);
            if (previousBytes is null && count >= _limits.MaxViewsPerList) throw Limit("The saved view limit has been reached.");
            var view = new SavedListView(request.Id, listId, request.Name, request.Query, request.VisibleFields);
            var json = PostgreSqlListJson.Serialize(view);
            var nextBytes = await CanonicalJsonBytesAsync(connection, transaction, json, ct);
            await ReserveStorageAsync(connection, transaction, workspaceId,
                nextBytes - (previousBytes ?? -64), ct);
            await ExecuteAsync(connection, transaction,
                "INSERT INTO cellbridge_lists.saved_views(workspace_id,list_id,view_id,definition) VALUES ($1,$2,$3,$4::jsonb) ON CONFLICT (workspace_id,list_id,view_id) DO UPDATE SET definition=EXCLUDED.definition", ct,
                workspaceId, listId, request.Id, json);
            await AuditAsync(connection, transaction, workspaceId, actor.Subject, "view.save", listId, null, null,
                request.VisibleFields, false, ct);
            return view;
        }, ct);
    }

    private async Task<T> ReadAsync<T>(ListActor actor, Guid workspaceId,
        Func<NpgsqlConnection, NpgsqlTransaction, Membership, Task<T>> operation, CancellationToken ct)
    {
        ValidateActor(actor);
        return await InTransactionAsync(workspaceId, false, async (connection, transaction) =>
        {
            var member = await AuthorizeMemberAsync(connection, transaction, workspaceId, actor.Subject, ct);
            return await operation(connection, transaction, member);
        }, ct);
    }

    private async Task<T> InTransactionAsync<T>(Guid workspaceId, bool exclusiveAuthorization,
        Func<NpgsqlConnection, NpgsqlTransaction, Task<T>> operation, CancellationToken ct)
    {
        if (workspaceId == Guid.Empty) throw Validation("A workspace ID is required.");
        await EnsureHealthyAsync(ct);
        try
        {
            await using var connection = await _source.OpenConnectionAsync(ct);
            await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct);
            await ConfigureAsync(connection, transaction, workspaceId, ct);
            await using (var authLock = new NpgsqlCommand(exclusiveAuthorization
                ? "SELECT pg_advisory_xact_lock(hashtextextended($1, 391813))"
                : "SELECT pg_advisory_xact_lock_shared(hashtextextended($1, 391813))", connection, transaction))
            {
                authLock.Parameters.AddWithValue(workspaceId.ToString("D"));
                await authLock.ExecuteNonQueryAsync(ct);
            }
            var result = await operation(connection, transaction);
            await transaction.CommitAsync(CancellationToken.None);
            return result;
        }
        catch (ListException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.QueryCanceled or PostgresErrorCodes.LockNotAvailable)
        { throw new ListException(ListFailure.Unavailable, "The list operation timed out."); }
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
            await using var connection = await _source.OpenConnectionAsync(ct);
            await using var command = new NpgsqlCommand("""
                WITH expected(table_name, allowed) AS (VALUES
                  ('schema_version', ARRAY['SELECT']),
                  ('templates', ARRAY['SELECT']),
                  ('workspaces', ARRAY['SELECT','UPDATE']),
                  ('memberships', ARRAY['SELECT','INSERT','UPDATE','DELETE']),
                  ('lists', ARRAY['SELECT','INSERT','UPDATE']),
                  ('list_schemas', ARRAY['SELECT','INSERT']),
                  ('list_grants', ARRAY['SELECT','INSERT','UPDATE','DELETE']),
                  ('records', ARRAY['SELECT','INSERT','UPDATE','DELETE']),
                  ('record_tombstones', ARRAY['SELECT','INSERT']),
                  ('create_receipts', ARRAY['SELECT','INSERT','DELETE']),
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
                  has_sequence_privilege(current_user,'cellbridge_lists.audit_events_id_seq','SELECT'),
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
                  )
                FROM cellbridge_lists.schema_version v
                CROSS JOIN pg_roles r
                CROSS JOIN pg_class c
                JOIN pg_namespace n ON n.oid=c.relnamespace
                WHERE v.singleton AND r.rolname=current_user AND n.nspname='cellbridge_lists' AND c.relname='records'
                """, connection);
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct) || reader.GetInt32(0) != 1)
                throw new ListException(ListFailure.Unavailable, "The list schema version is missing or unsupported.");
            if (reader.GetBoolean(1) || reader.GetBoolean(2) || reader.GetBoolean(3) || reader.GetBoolean(4) ||
                !reader.GetBoolean(5) || reader.GetBoolean(6))
                throw new ListException(ListFailure.Unavailable, "The list runtime role has unsafe database privileges.");
            _healthy = true;
        }
        finally { _healthGate.Release(); }
    }

    private async Task ConfigureAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid workspaceId, CancellationToken ct)
    {
        await PostgreSqlListSetup.SetContextAsync(connection, transaction, workspaceId, ct);
        await ExecuteAsync(connection, transaction, "SET LOCAL synchronous_commit=on", ct);
        var milliseconds = Math.Clamp((long)_limits.StatementTimeout.TotalMilliseconds, 1, int.MaxValue);
        await ExecuteAsync(connection, transaction, $"SET LOCAL statement_timeout={milliseconds}; SET LOCAL lock_timeout={milliseconds}", ct);
    }

    private async Task<Membership> AuthorizeMemberAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, Guid workspaceId, string subject, CancellationToken ct)
    {
        await using var command = Command(connection, transaction,
            "SELECT m.can_create_lists,m.can_manage_grants,w.security_audit_count FROM cellbridge_lists.memberships m JOIN cellbridge_lists.workspaces w USING(workspace_id) WHERE m.workspace_id=$1 AND m.subject=$2",
            workspaceId, subject);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw Forbidden();
        if (reader.GetInt32(2) >= _limits.MaxSecurityAuditEvents)
            throw new ListException(ListFailure.Unavailable, "The workspace security audit reserve is exhausted.");
        return new Membership(reader.GetBoolean(0), reader.GetBoolean(1));
    }

    private static async Task RequireAccessAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid workspaceId, Guid listId, string subject, ListAccess required, CancellationToken ct)
    {
        var access = await ScalarAsync<int?>(connection, transaction,
            "SELECT access FROM cellbridge_lists.list_grants WHERE workspace_id=$1 AND list_id=$2 AND subject=$3", ct,
            workspaceId, listId, subject);
        if (access is null || ((ListAccess)access.Value & required) != required) throw NotFound();
    }

    private static async Task<ListDefinition> GetDefinitionAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, Guid workspaceId, Guid listId, bool forUpdate, CancellationToken ct)
    {
        var sql = "SELECT s.definition::text FROM cellbridge_lists.lists l JOIN cellbridge_lists.list_schemas s ON s.workspace_id=l.workspace_id AND s.list_id=l.list_id AND s.schema_version=l.schema_version WHERE l.workspace_id=$1 AND l.list_id=$2" +
            (forUpdate ? " FOR UPDATE OF l" : "");
        var json = await ScalarAsync<string?>(connection, transaction, sql, ct, workspaceId, listId);
        return json is null ? throw NotFound() : PostgreSqlListJson.Deserialize<ListDefinition>(json);
    }

    private static async Task<ListTemplate> GetTemplateAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, Guid templateId, long version, CancellationToken ct)
    {
        var json = await ScalarAsync<string?>(connection, transaction,
            "SELECT definition::text FROM cellbridge_lists.templates WHERE template_id=$1 AND version=$2", ct, templateId, version);
        return json is null ? throw Validation("The template version does not exist.") : PostgreSqlListJson.Deserialize<ListTemplate>(json);
    }

    private static async Task ValidateReferenceDefinitionsAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, Guid workspaceId, string subject, ListDefinition definition, CancellationToken ct,
        IReadOnlySet<Guid>? fieldIds = null)
    {
        foreach (var field in definition.Fields.Where(x => x.Type == ListFieldType.Reference &&
                     (fieldIds is null || fieldIds.Contains(x.Id))))
        {
            var targetListId = field.ReferenceListId!.Value;
            await RequireAccessAsync(connection, transaction, workspaceId, targetListId, subject, ListAccess.Read, ct);
            var target = await GetDefinitionAsync(connection, transaction, workspaceId, targetListId, false, ct);
            if (field.ReferenceDisplayFieldId is { } displayId && target.Fields.All(x => x.Id != displayId))
                throw Validation("The reference display field does not exist in the target list.");
        }
    }

    private static async Task LockQuotaAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid workspaceId, CancellationToken ct)
    {
        if (await ScalarAsync<Guid?>(connection, transaction,
            "SELECT workspace_id FROM cellbridge_lists.workspaces WHERE workspace_id=$1 FOR UPDATE", ct, workspaceId) is null)
            throw NotFound();
    }

    private async Task ReserveStorageAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid workspaceId, long bytes, CancellationToken ct)
    {
        if (bytes < 0)
        {
            await ExecuteAsync(connection, transaction,
                "UPDATE cellbridge_lists.workspaces SET stored_bytes=GREATEST(0,stored_bytes+$2) WHERE workspace_id=$1", ct,
                workspaceId, bytes);
            return;
        }
        await using var command = Command(connection, transaction,
            "UPDATE cellbridge_lists.workspaces SET stored_bytes=stored_bytes+$2 WHERE workspace_id=$1 AND stored_bytes+$2<=$3 RETURNING workspace_id",
            workspaceId, bytes, _limits.MaxWorkspaceBytes);
        if (await command.ExecuteScalarAsync(ct) is null)
            throw Limit("The workspace storage limit has been reached.");
    }

    private async Task AuditAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid workspaceId,
        string actor, string action, Guid? listId, Guid? recordId, long? revision, IEnumerable<Guid> fieldIds,
        bool security, CancellationToken ct, string? targetSubject = null)
    {
        if (security)
        {
            await using var reserve = Command(connection, transaction,
                "UPDATE cellbridge_lists.workspaces SET security_audit_count=security_audit_count+1 WHERE workspace_id=$1 AND security_audit_count<$2 RETURNING workspace_id",
                workspaceId, _limits.MaxSecurityAuditEvents);
            if (await reserve.ExecuteScalarAsync(ct) is null) throw Limit("The security audit reserve is exhausted.");
        }
        else
        {
            var bytes = 96 + Encoding.UTF8.GetByteCount(actor) + Encoding.UTF8.GetByteCount(action) +
                (targetSubject is null ? 0 : Encoding.UTF8.GetByteCount(targetSubject)) + fieldIds.Count() * 16;
            await using var reserve = Command(connection, transaction,
                "UPDATE cellbridge_lists.workspaces SET stored_bytes=stored_bytes+$2,audit_count=audit_count+1 WHERE workspace_id=$1 AND stored_bytes+$2<=$3 AND audit_count<$4 RETURNING workspace_id",
                workspaceId, bytes, _limits.MaxWorkspaceBytes, _limits.MaxAuditEvents);
            if (await reserve.ExecuteScalarAsync(ct) is null) throw Limit("The workspace storage or audit limit has been reached.");
        }
        await ExecuteAsync(connection, transaction,
            "INSERT INTO cellbridge_lists.audit_events(workspace_id,utc,actor,action,list_id,record_id,revision,field_ids,security,target_subject) VALUES ($1,clock_timestamp(),$2,$3,$4,$5,$6,$7,$8,$9)", ct,
            workspaceId, actor, action, listId, recordId, revision, fieldIds.ToArray(), security, targetSubject);
    }

    private static NpgsqlCommand Command(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string sql, params object?[] values)
    {
        var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var value in values) command.Parameters.Add(new NpgsqlParameter { Value = value ?? DBNull.Value });
        return command;
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string sql, CancellationToken ct, params object?[] values)
    {
        await using var command = Command(connection, transaction, sql, values);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string sql, CancellationToken ct, params object?[] values)
    {
        await using var command = Command(connection, transaction, sql, values);
        var result = await command.ExecuteScalarAsync(ct);
        if (result is null or DBNull) return default!;
        return (T)result;
    }

    private static Task<long> CanonicalJsonBytesAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string json, CancellationToken ct) => ScalarAsync<long>(connection, transaction,
            "SELECT octet_length(($1::jsonb)::text)::bigint", ct, json);

    private static void ValidateActor(ListActor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (string.IsNullOrWhiteSpace(actor.Subject) || actor.Subject.Length > 1024 || !ListValidation.IsWellFormedText(actor.Subject))
            throw new ListException(ListFailure.Forbidden, "The actor is missing or invalid.");
    }

    private static ListException Validation(string message) => new(ListFailure.Validation, message);
    private static ListException Forbidden() => new(ListFailure.Forbidden, "Access is denied.");
    private static ListException NotFound() => new(ListFailure.NotFound, "The list resource was not found.");
    private static ListException Conflict(string message) => new(ListFailure.Conflict, message);
    private static ListException Limit(string message) => new(ListFailure.Limit, message);
    private sealed record Membership(bool CanCreateLists, bool CanManageGrants);
}
