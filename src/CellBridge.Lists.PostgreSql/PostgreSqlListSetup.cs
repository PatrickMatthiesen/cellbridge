using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace CellBridge.Lists.PostgreSql;

public static class PostgreSqlListSetup
{
    public const string SchemaName = "cellbridge_lists";
    public const string MigrationsHistoryTable = "__CellBridgeListsMigrationsHistory";

    public static async Task InitializeAsync(NpgsqlDataSource adminSource, string runtimeRole,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(adminSource);
        var options = new DbContextOptionsBuilder<PostgreSqlListDbContext>()
            .UseNpgsql(adminSource, provider => provider.MigrationsHistoryTable(MigrationsHistoryTable, SchemaName))
            .Options;
        await InitializeAsync(options, runtimeRole, cancellationToken);
    }

    /// <summary>Applies the library-owned Lists migration when the consumer chooses to run it.</summary>
    public static async Task InitializeAsync(DbContextOptions<PostgreSqlListDbContext> options, string runtimeRole,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(runtimeRole) || !ListValidation.IsWellFormedText(runtimeRole))
            throw new ArgumentException("A PostgreSQL runtime role is required.", nameof(runtimeRole));

        await using var db = new PostgreSqlListDbContext(NormalizeOptions(options));
        await db.Database.OpenConnectionAsync(cancellationToken);
        await using var efTransaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        var transaction = (NpgsqlTransaction)efTransaction.GetDbTransaction();
        await ExecuteAsync(connection, transaction, "SELECT pg_advisory_xact_lock(1263422021)", cancellationToken);
        // Bootstrap migration infrastructure inside the same transaction as the versioned schema.
        await ExecuteAsync(connection, transaction, """
            CREATE SCHEMA IF NOT EXISTS cellbridge_lists;
            REVOKE ALL ON SCHEMA cellbridge_lists FROM PUBLIC;
            """, cancellationToken);

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

        int? schemaVersion = null;
        await using (var existing = new NpgsqlCommand(
                         "SELECT to_regclass('cellbridge_lists.schema_version')::text", connection, transaction))
        {
            if (await existing.ExecuteScalarAsync(cancellationToken) is string)
            {
                await using var version = new NpgsqlCommand(
                    "SELECT version FROM cellbridge_lists.schema_version", connection, transaction);
                schemaVersion = await version.ExecuteScalarAsync(cancellationToken) as int?;
                if (schemaVersion is not (1 or 2 or 3 or 4))
                    throw new InvalidOperationException("The list schema version is missing or unsupported.");
            }
        }

        bool historyTableExisted;
        await using (var historyTable = new NpgsqlCommand(
                         $"SELECT to_regclass('{SchemaName}.\"{MigrationsHistoryTable}\"') IS NOT NULL",
                         connection, transaction))
            historyTableExisted = (bool)(await historyTable.ExecuteScalarAsync(cancellationToken) ?? false);

        // Npgsql probes applied migrations before applying them. A missing history table
        // would abort our explicit transaction even though EF catches that probe error.
        await ExecuteAsync(connection, transaction, $"""
            CREATE TABLE IF NOT EXISTS {SchemaName}."{MigrationsHistoryTable}" (
              "MigrationId" character varying(150) NOT NULL,
              "ProductVersion" character varying(32) NOT NULL,
              CONSTRAINT "PK_{MigrationsHistoryTable}" PRIMARY KEY ("MigrationId")
            );
            """, cancellationToken);
        if (schemaVersion is null)
        {
            if (historyTableExisted)
                throw new InvalidOperationException(
                    "The Lists migration history exists without a list schema version.");
        }
        else if (schemaVersion <= 3)
        {
            if (historyTableExisted)
                throw new InvalidOperationException(
                    "A legacy Lists schema must not contain EF migration history.");
            await ExecuteAsync(connection, transaction, PostgreSqlListMigrationSql.ReadSchemaV3(),
                cancellationToken);
            await ExecuteAsync(connection, transaction, $"""
                INSERT INTO {SchemaName}."{MigrationsHistoryTable}" ("MigrationId","ProductVersion")
                VALUES ('{PostgreSqlListMigrationSql.CurrentMigrationId}','{PostgreSqlListMigrationSql.ProductVersion}');
                """, cancellationToken);
        }
        else
        {
            if (!historyTableExisted || !await HasExactCurrentHistoryAsync(connection, transaction,
                    cancellationToken))
                throw new InvalidOperationException(
                    "The Lists migration history does not match schema version 4.");
        }

        // The shared advisory lock prevents another initializer from observing the legacy baseline.
        // EF is enlisted in this transaction so schema changes, data transforms and history commit together.
        await db.Database.MigrateAsync(cancellationToken);
        if (await db.SchemaVersions.AsNoTracking().Where(row => row.Singleton)
                .Select(row => row.Version).SingleAsync(cancellationToken) != 4)
            throw new InvalidOperationException("The list schema migration did not reach version 4.");

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
              cellbridge_lists.saved_views TO {quotedRole};
            GRANT SELECT, INSERT, DELETE ON cellbridge_lists.create_receipts TO {quotedRole};
            GRANT SELECT, INSERT, UPDATE ON cellbridge_lists.lists, cellbridge_lists.records TO {quotedRole};
            GRANT SELECT, INSERT, UPDATE ON cellbridge_lists.list_counters TO {quotedRole};
            GRANT INSERT ON cellbridge_lists.record_tombstones TO {quotedRole};
            GRANT SELECT, INSERT, UPDATE, DELETE ON cellbridge_lists.record_references TO {quotedRole};
            GRANT SELECT, INSERT ON cellbridge_lists.list_schemas, cellbridge_lists.audit_events TO {quotedRole};
            GRANT DELETE ON cellbridge_lists.records TO {quotedRole};
            GRANT USAGE, SELECT ON SEQUENCE cellbridge_lists.audit_events_id_seq TO {quotedRole};
            REVOKE ALL ON FUNCTION cellbridge_lists.utf16_key(text),
              cellbridge_lists.timestamp_key(text),cellbridge_lists.utf16_compare(text,text),
              cellbridge_lists.timestamp_compare(text,text),cellbridge_lists.c_compare(text,text) FROM PUBLIC;
            GRANT EXECUTE ON FUNCTION cellbridge_lists.utf16_key(text),
              cellbridge_lists.timestamp_key(text),cellbridge_lists.utf16_compare(text,text),
              cellbridge_lists.timestamp_compare(text,text),cellbridge_lists.c_compare(text,text) TO {quotedRole};
            """;
        await ExecuteAsync(connection, transaction, grants, cancellationToken);
        await efTransaction.CommitAsync(cancellationToken);
    }

    internal static DbContextOptions<PostgreSqlListDbContext> NormalizeOptions(
        DbContextOptions<PostgreSqlListDbContext> options)
    {
        var normalized = new DbContextOptionsBuilder<PostgreSqlListDbContext>(options)
            .UseNpgsql(provider => provider.MigrationsHistoryTable(MigrationsHistoryTable, SchemaName))
            .UseQueryTrackingBehavior(QueryTrackingBehavior.TrackAll)
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.MigrationsUserTransactionWarning))
            .Options;
        using var validationContext = new PostgreSqlListDbContext(normalized);
        if (validationContext.Database.CreateExecutionStrategy().RetriesOnFailure)
            throw new ArgumentException(
                "Retrying EF Core execution strategies are not supported. CellBridge Lists owns explicit transactions and create-receipt retry semantics.",
                nameof(options));
        return normalized;
    }

    public static async Task RegisterWorkspaceAsync(NpgsqlDataSource adminSource, Guid workspaceId,
        string name, string administratorSubject, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(adminSource);
        var options = new DbContextOptionsBuilder<PostgreSqlListDbContext>().UseNpgsql(adminSource).Options;
        await RegisterWorkspaceAsync(options, workspaceId, name, administratorSubject, cancellationToken);
    }

    public static async Task RegisterWorkspaceAsync(DbContextOptions<PostgreSqlListDbContext> options, Guid workspaceId,
        string name, string administratorSubject, CancellationToken cancellationToken = default)
    {
        if (workspaceId == Guid.Empty) throw new ArgumentException("A workspace ID is required.", nameof(workspaceId));
        if (string.IsNullOrWhiteSpace(name) || name.Length > 200 || !ListValidation.IsWellFormedText(name))
            throw new ArgumentException("A valid workspace name is required.", nameof(name));
        if (string.IsNullOrWhiteSpace(administratorSubject) || administratorSubject.Length > 1024 || !ListValidation.IsWellFormedText(administratorSubject))
            throw new ArgumentException("A valid administrator subject is required.", nameof(administratorSubject));

        await using var db = new PostgreSqlListDbContext(NormalizeOptions(options));
        await db.Database.OpenConnectionAsync(cancellationToken);
        await using var efTransaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        var transaction = (NpgsqlTransaction)efTransaction.GetDbTransaction();
        await AcquireWorkspaceAuthorizationLockAsync(connection, transaction, workspaceId, cancellationToken);
        await SetContextAsync(connection, transaction, workspaceId, cancellationToken);
        var workspace = await db.Workspaces.SingleOrDefaultAsync(row => row.WorkspaceId == workspaceId,
            cancellationToken);
        if (workspace is null)
        {
            workspace = new WorkspaceRow { WorkspaceId = workspaceId, Name = name };
            db.Workspaces.Add(workspace);
            await db.SaveChangesAsync(cancellationToken);
        }
        else if (workspace.AuthorizationMode != "builtin")
            throw new ListException(ListFailure.Conflict,
                "A host-authorized workspace cannot be registered as built-in.");
        else
            workspace.Name = name;
        var membership = await db.Memberships.SingleOrDefaultAsync(row => row.WorkspaceId == workspaceId &&
            row.Subject == administratorSubject, cancellationToken);
        if (membership is null)
        {
            membership = new MembershipRow { WorkspaceId = workspaceId, Subject = administratorSubject };
            db.Memberships.Add(membership);
            workspace.StoredBytes = checked(workspace.StoredBytes + 128 +
                System.Text.Encoding.UTF8.GetByteCount(administratorSubject));
        }
        membership.CanCreateLists = true;
        membership.CanManageGrants = true;
        await db.SaveChangesAsync(cancellationToken);
        await efTransaction.CommitAsync(cancellationToken);
    }

    public static async Task RegisterHostWorkspaceAsync(NpgsqlDataSource adminSource, Guid workspaceId,
        string name, string policyDomain, string authorizationScope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(adminSource);
        var options = new DbContextOptionsBuilder<PostgreSqlListDbContext>().UseNpgsql(adminSource).Options;
        await RegisterHostWorkspaceAsync(options, workspaceId, name, policyDomain, authorizationScope,
            cancellationToken);
    }

    public static async Task RegisterHostWorkspaceAsync(DbContextOptions<PostgreSqlListDbContext> options,
        Guid workspaceId, string name, string policyDomain, string authorizationScope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (workspaceId == Guid.Empty) throw new ArgumentException("A workspace ID is required.", nameof(workspaceId));
        if (string.IsNullOrWhiteSpace(name) || name.Length > 200 || !ListValidation.IsWellFormedText(name))
            throw new ArgumentException("A valid workspace name is required.", nameof(name));
        if (string.IsNullOrWhiteSpace(policyDomain) || policyDomain.Length > 200 ||
            !ListValidation.IsWellFormedText(policyDomain) || policyDomain.Any(char.IsControl))
            throw new ArgumentException("A valid policy domain is required.", nameof(policyDomain));
        if (string.IsNullOrWhiteSpace(authorizationScope) || authorizationScope.Length > 1024 ||
            !ListValidation.IsWellFormedText(authorizationScope) || authorizationScope.Any(char.IsControl))
            throw new ArgumentException("A valid authorization scope is required.", nameof(authorizationScope));

        await using var db = new PostgreSqlListDbContext(NormalizeOptions(options));
        await db.Database.OpenConnectionAsync(cancellationToken);
        await using var efTransaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        var transaction = (NpgsqlTransaction)efTransaction.GetDbTransaction();
        await AcquireWorkspaceAuthorizationLockAsync(connection, transaction, workspaceId, cancellationToken);
        await SetContextAsync(connection, transaction, workspaceId, cancellationToken);
        var workspace = await db.Workspaces.SingleOrDefaultAsync(row => row.WorkspaceId == workspaceId,
            cancellationToken);
        if (workspace is null)
        {
            db.Workspaces.Add(new WorkspaceRow { WorkspaceId = workspaceId, Name = name,
                AuthorizationMode = "host", AuthorizationDomain = policyDomain,
                AuthorizationScope = authorizationScope });
        }
        else if (workspace.AuthorizationMode != "host" || workspace.AuthorizationDomain != policyDomain ||
                 workspace.AuthorizationScope != authorizationScope)
            throw new ListException(ListFailure.Conflict, "The workspace is already bound to a different authorization anchor.");
        else
            workspace.Name = name;
        await db.SaveChangesAsync(cancellationToken);
        await efTransaction.CommitAsync(cancellationToken);
    }

    public static async Task RegisterTemplateAsync(NpgsqlDataSource adminSource, ListTemplate template,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(adminSource);
        var options = new DbContextOptionsBuilder<PostgreSqlListDbContext>().UseNpgsql(adminSource).Options;
        await RegisterTemplateAsync(options, template, cancellationToken);
    }

    public static async Task RegisterTemplateAsync(DbContextOptions<PostgreSqlListDbContext> options,
        ListTemplate template, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(template);
        ListTemplates.Validate(template, new ListLimits());
        await using var db = new PostgreSqlListDbContext(NormalizeOptions(options));
        var json = PostgreSqlListJson.Serialize(template);
        var affected = await db.Database.ExecuteSqlInterpolatedAsync($$"""
            INSERT INTO cellbridge_lists.templates AS existing(template_id,version,definition)
            VALUES ({{template.Id}},{{template.Version}},{{json}}::jsonb)
            ON CONFLICT (template_id,version) DO UPDATE SET definition=existing.definition
            WHERE existing.definition=EXCLUDED.definition
            """, cancellationToken);
        if (affected == 0)
            throw new ListException(ListFailure.Conflict,
                "That template version already has a different definition.");
    }

    internal static string QuoteIdentifier(string identifier) => '"' + identifier.Replace("\"", "\"\"") + '"';

    internal static async Task SetContextAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid workspaceId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT set_config('cellbridge.workspace_id',$1,true)", connection, transaction);
        command.Parameters.AddWithValue(workspaceId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task AcquireWorkspaceAuthorizationLockAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, Guid workspaceId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtextextended($1,391813))", connection, transaction);
        command.Parameters.AddWithValue(workspaceId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<bool> HasExactCurrentHistoryAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT count(*)=2 AND count(*) FILTER (WHERE
              ("MigrationId"='{PostgreSqlListMigrationSql.CurrentMigrationId}' AND
               "ProductVersion"='{PostgreSqlListMigrationSql.ProductVersion}') OR
              ("MigrationId"='{PostgreSqlListMigrationSql.QueryFunctionsMigrationId}' AND
               "ProductVersion"='{PostgreSqlListMigrationSql.ProductVersion}'))=2
            FROM {SchemaName}."{MigrationsHistoryTable}"
            """, connection, transaction);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken)
            ?? throw new InvalidOperationException("PostgreSQL did not return the migration history state."));
    }
}
