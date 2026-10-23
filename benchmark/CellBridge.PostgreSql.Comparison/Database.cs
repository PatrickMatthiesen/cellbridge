using System.Reflection;
using CellBridge.Lists.PostgreSql;
using CellBridge.Storage.Abstractions;
using Npgsql;

// Adapted from the investigation probe. Owns only its generated database and role.
internal sealed class Database : IAsyncDisposable
{
    private readonly NpgsqlDataSource cluster;
    private readonly string name = "ef_matched_" + Guid.NewGuid().ToString("N");
    private readonly string role = "ef_matched_" + Guid.NewGuid().ToString("N");
    private readonly string variant;
    private readonly bool lists;
    private bool databaseCreated, roleCreated;
    public NpgsqlDataSource Admin { get; private set; } = null!;
    public NpgsqlDataSource Runtime { get; private set; } = null!;
    private Database(string connection, string variant, bool lists)
    { cluster = NpgsqlDataSource.Create(connection); this.variant = variant; this.lists = lists; }

    public static async Task<Database> OpenAsync(string connection, string variant, bool lists)
    {
        var db = new Database(connection, variant, lists);
        try
        {
            await using (var create = db.cluster.CreateCommand($"CREATE DATABASE {db.name}")) await create.ExecuteNonQueryAsync();
            db.databaseCreated = true;
            var password = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
            await using (var create = db.cluster.CreateCommand($"CREATE ROLE {db.role} LOGIN PASSWORD '{password}' NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE NOINHERIT"))
                await create.ExecuteNonQueryAsync();
            db.roleCreated = true;
            var settings = new NpgsqlConnectionStringBuilder(connection) { Database = db.name };
            db.Admin = NpgsqlDataSource.Create(settings.ConnectionString);
            if (lists) await db.InitializeListsAsync();
            else
            {
                var initializer = Providers.State(variant, db.Admin, new StorageLimits());
                await (Task)initializer.GetType().GetMethod("InitializeAsync")!.Invoke(initializer, [CancellationToken.None])!;
                // Documents have no Lists RLS. Use a non-owner DML role for both providers.
                await using var grant = db.Admin.CreateCommand($"""
                    GRANT USAGE ON SCHEMA public TO {db.role};
                    GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO {db.role};
                    GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO {db.role};
                    """);
                await grant.ExecuteNonQueryAsync();
            }
            settings.Username = db.role; settings.Password = password;
            var runtimeBuilder = new NpgsqlDataSourceBuilder(settings.ConnectionString);
            if (CommandDiagnostics.Enabled)
                runtimeBuilder.ConfigureTracing(o => o.ConfigureCommandFilter(CommandDiagnostics.Command)
                    .ConfigureBatchFilter(CommandDiagnostics.Batch));
            db.Runtime = runtimeBuilder.Build();
            await using var version = db.Admin.CreateCommand(lists ? "SELECT version FROM cellbridge_lists.schema_version" : "SELECT version FROM cellbridge_schema");
            Settings.Require(Convert.ToInt32(await version.ExecuteScalarAsync()) == (lists ? (variant == "sql" ? 3 : 4) : 6), "Unexpected schema version.");
            await using var safety = db.Runtime.CreateCommand("SELECT NOT rolsuper AND NOT rolbypassrls AND NOT rolcreatedb AND NOT rolcreaterole FROM pg_roles WHERE rolname=current_user");
            Settings.Require((bool)(await safety.ExecuteScalarAsync())!, "Unsafe runtime role.");
            return db;
        }
        catch { await db.DisposeAsync(); throw; }
    }
    private static Task InvokeListsAsync(string name, params object[] parameters) => (Task)Providers.SqlLists
        .GetType("CellBridge.Lists.PostgreSql.PostgreSqlListSetup")!.GetMethod(name)!.Invoke(null, parameters)!;
    private Task InitializeListsAsync() => variant == "ef"
        ? PostgreSqlListSetup.InitializeAsync(Admin, role)
        : InvokeListsAsync("InitializeAsync", Admin, role, CancellationToken.None);
    public async Task ResetListsAsync()
    {
        Settings.Require(lists, "Only Lists fixtures can reset the Lists schema.");
        await using (var drop = Admin.CreateCommand("DROP SCHEMA cellbridge_lists CASCADE"))
            await drop.ExecuteNonQueryAsync();
        await InitializeListsAsync();
    }
    public Task RegisterWorkspaceAsync(Guid id, string subject) => variant == "ef"
        ? PostgreSqlListSetup.RegisterWorkspaceAsync(Admin, id, "Matched measurement", subject)
        : InvokeListsAsync("RegisterWorkspaceAsync", Admin, id, "Matched measurement", subject, CancellationToken.None);

    public async Task<object> MetadataAsync()
    {
        var settings = new Dictionary<string, string>();
        await using (var command = Admin.CreateCommand("SELECT name,setting FROM pg_settings ORDER BY name"))
        await using (var reader = await command.ExecuteReaderAsync())
            while (await reader.ReadAsync()) settings.Add(reader.GetString(0), reader.GetString(1));
        await using var version = Admin.CreateCommand("SELECT version()");
        var pool = new NpgsqlConnectionStringBuilder(Runtime.ConnectionString);
        return new { postgres = await version.ExecuteScalarAsync(), settings,
            schema = lists ? (variant == "sql" ? "Lists v3" : "Lists v4") : "Documents v6",
            runtimeRole = "generated non-owner NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE NOINHERIT",
            pool = new { pool.Pooling, pool.MinPoolSize, pool.MaxPoolSize, pool.ConnectionIdleLifetime,
                pool.ConnectionPruningInterval, pool.ConnectionLifetime, pool.Timeout, pool.CommandTimeout,
                pool.Multiplexing, pool.MaxAutoPrepare, pool.AutoPrepareMinUsages, pool.ReadBufferSize,
                pool.WriteBufferSize, pool.NoResetOnClose, pool.Enlist, sslMode = pool.SslMode.ToString() } };
    }
    public async Task<Dictionary<string, long>> CountsAsync()
    {
        string[] tables = lists ? ["cellbridge_lists.records", "cellbridge_lists.audit_events", "cellbridge_lists.create_receipts"]
            : ["cellbridge_documents", "cellbridge_states", "cellbridge_objects", "cellbridge_chunks"];
        var counts = new Dictionary<string, long>();
        foreach (var table in tables)
        {
            await using var count = Admin.CreateCommand($"SELECT count(*) FROM {table}");
            counts[table] = (long)(await count.ExecuteScalarAsync())!;
        }
        return counts;
    }
    public async Task<object> StatisticsAsync()
    {
        await using var command = Admin.CreateCommand("""
            SELECT (SELECT count(*) FROM pg_stats WHERE schemaname='cellbridge_lists' AND tablename='records'),
              c.reltuples, coalesce(array_to_string(c.reloptions,','),'')
            FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
            WHERE n.nspname='cellbridge_lists' AND c.relname='records'
            """);
        await using var reader = await command.ExecuteReaderAsync();
        Settings.Require(await reader.ReadAsync(), "Missing records statistics.");
        return new { columnStatistics = reader.GetInt64(0), estimatedRows = reader.GetFloat(1), tableOptions = reader.GetString(2) };
    }
    public async ValueTask DisposeAsync()
    {
        if (Runtime is not null) await Runtime.DisposeAsync();
        if (Admin is not null) await Admin.DisposeAsync();
        try
        {
            if (databaseCreated) { await using var drop = cluster.CreateCommand($"DROP DATABASE {name} WITH (FORCE)"); await drop.ExecuteNonQueryAsync(); }
        }
        finally
        {
            try { if (roleCreated) { await using var drop = cluster.CreateCommand($"DROP ROLE {role}"); await drop.ExecuteNonQueryAsync(); } }
            finally { await cluster.DisposeAsync(); }
        }
    }
}
