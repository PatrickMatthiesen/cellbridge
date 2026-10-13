using System.Reflection;
using Npgsql;

namespace CellBridge.Lists.PostgreSql;

public static class PostgreSqlListSetup
{
    public static async Task InitializeAsync(NpgsqlDataSource adminSource, string runtimeRole,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(adminSource);
        if (string.IsNullOrWhiteSpace(runtimeRole) || !ListValidation.IsWellFormedText(runtimeRole))
            throw new ArgumentException("A PostgreSQL runtime role is required.", nameof(runtimeRole));

        await using var connection = await adminSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await ExecuteAsync(connection, transaction, "SELECT pg_advisory_xact_lock(1263422021)", cancellationToken);

        await using (var role = new NpgsqlCommand("""
            SELECT EXISTS (
              SELECT 1 FROM pg_roles inherited
              WHERE pg_has_role($1,inherited.oid,'MEMBER') AND (inherited.rolsuper OR inherited.rolbypassrls)
            )
            """, connection, transaction))
        {
            role.Parameters.AddWithValue(runtimeRole);
            var unsafeRole = await role.ExecuteScalarAsync(cancellationToken);
            if (unsafeRole is null)
                throw new InvalidOperationException("The runtime role does not exist. Create it outside list setup.");
            if ((bool)unsafeRole)
                throw new InvalidOperationException("The runtime role must not be a superuser or have BYPASSRLS.");
        }

        await using (var existing = new NpgsqlCommand("SELECT to_regclass('cellbridge_lists.schema_version')::text", connection, transaction))
            if (await existing.ExecuteScalarAsync(cancellationToken) is string)
            {
                await using var version = new NpgsqlCommand("SELECT version FROM cellbridge_lists.schema_version", connection, transaction);
                var value = await version.ExecuteScalarAsync(cancellationToken);
                if (value is not int schemaVersion || schemaVersion != 1)
                    throw new InvalidOperationException("The list schema version is missing or unsupported.");
            }

        await using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("CellBridge.Lists.PostgreSql.Schema.sql")
            ?? throw new InvalidOperationException("The list schema resource is missing.");
        using var text = new StreamReader(stream);
        await ExecuteAsync(connection, transaction, await text.ReadToEndAsync(cancellationToken), cancellationToken);

        await using (var ownership = new NpgsqlCommand("""
            SELECT EXISTS (
              SELECT 1 FROM pg_namespace n WHERE n.nspname='cellbridge_lists' AND pg_has_role($1,n.nspowner,'MEMBER')
              UNION ALL
              SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
              WHERE n.nspname='cellbridge_lists' AND pg_has_role($1,c.relowner,'MEMBER')
            )
            """, connection, transaction))
        {
            ownership.Parameters.AddWithValue(runtimeRole);
            if ((bool)(await ownership.ExecuteScalarAsync(cancellationToken) ?? false))
                throw new InvalidOperationException("The runtime role must not own or inherit ownership of list database objects.");
        }

        var quotedRole = QuoteIdentifier(runtimeRole);
        var grants = $"""
            REVOKE ALL ON SCHEMA cellbridge_lists FROM {quotedRole};
            REVOKE ALL PRIVILEGES ON ALL TABLES IN SCHEMA cellbridge_lists FROM {quotedRole};
            REVOKE ALL PRIVILEGES ON ALL SEQUENCES IN SCHEMA cellbridge_lists FROM {quotedRole};
            GRANT USAGE ON SCHEMA cellbridge_lists TO {quotedRole};
            GRANT SELECT ON cellbridge_lists.schema_version, cellbridge_lists.templates TO {quotedRole};
            GRANT SELECT, UPDATE ON cellbridge_lists.workspaces TO {quotedRole};
            GRANT SELECT, INSERT, UPDATE, DELETE ON cellbridge_lists.memberships, cellbridge_lists.list_grants,
              cellbridge_lists.create_receipts, cellbridge_lists.saved_views TO {quotedRole};
            GRANT SELECT, INSERT, UPDATE ON cellbridge_lists.lists, cellbridge_lists.records TO {quotedRole};
            GRANT SELECT, INSERT ON cellbridge_lists.list_schemas, cellbridge_lists.record_tombstones,
              cellbridge_lists.audit_events TO {quotedRole};
            GRANT DELETE ON cellbridge_lists.records TO {quotedRole};
            GRANT USAGE, SELECT ON SEQUENCE cellbridge_lists.audit_events_id_seq TO {quotedRole};
            """;
        await ExecuteAsync(connection, transaction, grants, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public static async Task RegisterWorkspaceAsync(NpgsqlDataSource adminSource, Guid workspaceId,
        string name, string administratorSubject, CancellationToken cancellationToken = default)
    {
        if (workspaceId == Guid.Empty) throw new ArgumentException("A workspace ID is required.", nameof(workspaceId));
        if (string.IsNullOrWhiteSpace(name) || name.Length > 200 || !ListValidation.IsWellFormedText(name))
            throw new ArgumentException("A valid workspace name is required.", nameof(name));
        if (string.IsNullOrWhiteSpace(administratorSubject) || administratorSubject.Length > 1024 || !ListValidation.IsWellFormedText(administratorSubject))
            throw new ArgumentException("A valid administrator subject is required.", nameof(administratorSubject));

        await using var connection = await adminSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await SetContextAsync(connection, transaction, workspaceId, cancellationToken);
        await using (var workspace = new NpgsqlCommand("INSERT INTO cellbridge_lists.workspaces(workspace_id,name) VALUES ($1,$2) ON CONFLICT (workspace_id) DO UPDATE SET name=EXCLUDED.name", connection, transaction))
        {
            workspace.Parameters.AddWithValue(workspaceId);
            workspace.Parameters.AddWithValue(name);
            await workspace.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var member = new NpgsqlCommand("INSERT INTO cellbridge_lists.memberships(workspace_id,subject,can_create_lists,can_manage_grants) VALUES ($1,$2,true,true) ON CONFLICT (workspace_id,subject) DO UPDATE SET can_create_lists=true,can_manage_grants=true", connection, transaction))
        {
            member.Parameters.AddWithValue(workspaceId);
            member.Parameters.AddWithValue(administratorSubject);
            await member.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public static async Task RegisterTemplateAsync(NpgsqlDataSource adminSource, ListTemplate template,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(template);
        ListTemplates.Validate(template, new ListLimits());
        await using var command = adminSource.CreateCommand("INSERT INTO cellbridge_lists.templates(template_id,version,definition) VALUES ($1,$2,$3::jsonb) ON CONFLICT (template_id,version) DO UPDATE SET definition=CASE WHEN templates.definition=EXCLUDED.definition THEN templates.definition ELSE NULL END");
        command.Parameters.AddWithValue(template.Id);
        command.Parameters.AddWithValue(template.Version);
        command.Parameters.AddWithValue(PostgreSqlListJson.Serialize(template));
        try { await command.ExecuteNonQueryAsync(cancellationToken); }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.NotNullViolation)
        { throw new ListException(ListFailure.Conflict, "That template version already has a different definition."); }
    }

    internal static string QuoteIdentifier(string identifier) => '"' + identifier.Replace("\"", "\"\"") + '"';

    internal static async Task SetContextAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid workspaceId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT set_config('cellbridge.workspace_id',$1,true)", connection, transaction);
        command.Parameters.AddWithValue(workspaceId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
