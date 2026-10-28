using CellBridge.Storage.PostgreSql;
using CellBridge.Lists.PostgreSql;
using CellBridge.Authentication;
using CellBridge.Storage.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Npgsql;
using CellBridge.Lists.SqlServer;
using CellBridge.Storage.SqlServer;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

var flags = new HashSet<string>(["--collect-orphans", "--apply", "--quiescent", "--initialize-lists"], StringComparer.Ordinal);
if (args.Any(argument => !flags.Contains(argument)))
    throw new ArgumentException("Supported options: --initialize-lists or --collect-orphans [--apply --quiescent]. Document initialization migrates PostgreSQL storage schema 3 through 6 to 7; older development schemas require recreation.");
var initializeLists = args.Contains("--initialize-lists", StringComparer.Ordinal);
if (initializeLists && args.Length != 1)
    throw new ArgumentException("--initialize-lists cannot be combined with other options.");
if (!initializeLists && !args.Contains("--collect-orphans", StringComparer.Ordinal) && args.Length != 0)
    throw new ArgumentException("--apply and --quiescent require --collect-orphans.");
var builder = Host.CreateApplicationBuilder();
var sqlServer = string.Equals(builder.Configuration[initializeLists ? "Lists:Provider" : "Storage:Provider"], "SqlServer", StringComparison.OrdinalIgnoreCase);
var connectionName = initializeLists ? "lists" : sqlServer ? "documents" : "cellbridge";
var connectionString = builder.Configuration.GetConnectionString(connectionName)
    ?? throw new InvalidOperationException($"Set ConnectionStrings:{connectionName} for storage setup.");
var limits = builder.Configuration.GetSection("Storage").Get<StorageLimits>() ?? new StorageLimits();
if (sqlServer)
{
    if (args.Contains("--collect-orphans", StringComparer.Ordinal))
        throw new ArgumentException("SQL Server orphan collection uses the public StorageMaintenance API.");
    var section = initializeLists ? "Lists" : "Storage";
    var role = builder.Configuration[section + ":RuntimeRole"];
    var password = builder.Configuration[section + ":RuntimePassword"];
    if (string.IsNullOrWhiteSpace(role) || role.Length > 128 || string.IsNullOrEmpty(password) || password.Length > 128)
        throw new InvalidOperationException("SQL Server initialization requires a runtime role and password of at most 128 characters.");
    if (initializeLists)
        await new SqlServerListMigrator(new DbContextOptionsBuilder<SqlServerListDbContext>().UseSqlServer(connectionString).Options).MigrateAsync();
    else
        await new SqlServerStorageMigrator(new DbContextOptionsBuilder<SqlServerDocumentDbContext>().UseSqlServer(connectionString).Options, limits).MigrateAsync();
    var identifier = "[" + role.Replace("]", "]]", StringComparison.Ordinal) + "]";
    await using var connection = new SqlConnection(connectionString);
    await connection.OpenAsync();
    await using (var create = connection.CreateCommand())
    {
        create.CommandText = """
            IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name=@role)
            BEGIN
                DECLARE @login nvarchar(max)=N'CREATE LOGIN '+QUOTENAME(@role)+N' WITH PASSWORD='+QUOTENAME(@password, '''');
                EXEC(@login);
            END;
            IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name=@role)
            BEGIN
                DECLARE @user nvarchar(max)=N'CREATE USER '+QUOTENAME(@role)+N' FOR LOGIN '+QUOTENAME(@role);
                EXEC(@user);
            END;
            """;
        create.Parameters.AddWithValue("@role", role);
        create.Parameters.AddWithValue("@password", password);
        await create.ExecuteNonQueryAsync();
    }
    var grants = initializeLists ? $$"""
        GRANT VIEW DEFINITION ON SCHEMA::cellbridge_lists TO {{identifier}};
        GRANT SELECT ON cellbridge_lists.schema_version TO {{identifier}};
        GRANT SELECT ON cellbridge_lists.templates TO {{identifier}};
        GRANT SELECT ON cellbridge_lists.__CellBridgeListsMigrationsHistory TO {{identifier}};
        GRANT SELECT,UPDATE ON cellbridge_lists.workspaces TO {{identifier}};
        GRANT SELECT,INSERT,UPDATE,DELETE ON cellbridge_lists.memberships TO {{identifier}};
        GRANT SELECT,INSERT,UPDATE ON cellbridge_lists.lists TO {{identifier}};
        GRANT SELECT,INSERT ON cellbridge_lists.list_schemas TO {{identifier}};
        GRANT SELECT,INSERT,UPDATE ON cellbridge_lists.list_counters TO {{identifier}};
        GRANT SELECT,INSERT,UPDATE,DELETE ON cellbridge_lists.list_grants TO {{identifier}};
        GRANT SELECT,INSERT,UPDATE,DELETE ON cellbridge_lists.records TO {{identifier}};
        GRANT SELECT,INSERT,UPDATE,DELETE ON cellbridge_lists.record_references TO {{identifier}};
        GRANT SELECT,INSERT,UPDATE ON cellbridge_lists.saved_views TO {{identifier}};
        GRANT SELECT,INSERT ON cellbridge_lists.record_tombstones TO {{identifier}};
        GRANT SELECT,INSERT,DELETE ON cellbridge_lists.create_receipts TO {{identifier}};
        GRANT SELECT,INSERT ON cellbridge_lists.audit_events TO {{identifier}};
        GRANT EXECUTE ON cellbridge_lists.scalar_key TO {{identifier}};
        GRANT EXECUTE ON cellbridge_lists.uuid_key TO {{identifier}};
        GRANT EXECUTE ON cellbridge_lists.key_compare TO {{identifier}};
        """ : $$"""
        GRANT SELECT,INSERT,UPDATE,DELETE ON cellbridge_documents.documents TO {{identifier}};
        GRANT SELECT,INSERT,UPDATE,DELETE ON cellbridge_documents.snapshots TO {{identifier}};
        GRANT SELECT,INSERT,UPDATE,DELETE ON cellbridge_documents.metadata TO {{identifier}};
        GRANT SELECT,INSERT,UPDATE,DELETE ON cellbridge_documents.revisions TO {{identifier}};
        GRANT SELECT,INSERT,UPDATE,DELETE ON cellbridge_documents.reservations TO {{identifier}};
        GRANT SELECT,INSERT,UPDATE,DELETE ON cellbridge_documents.recovery_receipts TO {{identifier}};
        GRANT SELECT,INSERT ON cellbridge_documents.objects TO {{identifier}};
        GRANT SELECT,INSERT ON cellbridge_documents.chunks TO {{identifier}};
        GRANT SELECT ON cellbridge_documents.usage TO {{identifier}};
        GRANT UPDATE (StoredBytes,DocumentCount) ON cellbridge_documents.usage TO {{identifier}};
        GRANT SELECT ON cellbridge_documents.usage_buckets TO {{identifier}};
        GRANT UPDATE (AvailableBytes) ON cellbridge_documents.usage_buckets TO {{identifier}};
        GRANT SELECT ON cellbridge_documents.schema_version TO {{identifier}};
        """;
    await using (var grant = new SqlCommand(grants, connection)) await grant.ExecuteNonQueryAsync();
    var runtimeConnection = new SqlConnectionStringBuilder(connectionString) { UserID = role, Password = password, IntegratedSecurity = false };
    if (initializeLists)
        await new SqlServerListEngine(new DbContextOptionsBuilder<SqlServerListDbContext>().UseSqlServer(runtimeConnection.ConnectionString).Options).CheckHealthAsync();
    else
    {
        var options = new DbContextOptionsBuilder<SqlServerDocumentDbContext>().UseSqlServer(runtimeConnection.ConnectionString).Options;
        await new StorageProvider(new SqlServerStateStore(options, limits), new SqlServerContentStore(options, limits.MaxObjectBytes), limits).CheckHealthAsync();
    }
    Console.WriteLine("CellBridge SQL Server schema and restricted runtime login are ready.");
    return;
}
await using var source = NpgsqlDataSource.Create(connectionString);
if (initializeLists)
{
    var role = builder.Configuration["Lists:RuntimeRole"];
    var password = builder.Configuration["Lists:RuntimePassword"];
    if (string.IsNullOrWhiteSpace(role) || string.IsNullOrEmpty(password))
        throw new InvalidOperationException("Lists initialization requires Lists:RuntimeRole and Lists:RuntimePassword.");
    await using (var exists = source.CreateCommand("SELECT EXISTS (SELECT 1 FROM pg_roles WHERE rolname=$1)"))
    {
        exists.Parameters.AddWithValue(role);
        if (!(bool)(await exists.ExecuteScalarAsync())!)
        {
            await using var format = source.CreateCommand("SELECT format('CREATE ROLE %I LOGIN PASSWORD %L NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE NOINHERIT', $1::text, $2::text)");
            format.Parameters.AddWithValue(role);
            format.Parameters.AddWithValue(password);
            var sql = (string)(await format.ExecuteScalarAsync())!;
            await using var create = source.CreateCommand(sql);
            await create.ExecuteNonQueryAsync();
        }
    }
    await PostgreSqlListSetup.InitializeAsync(source, role);
    // Verify the supplied runtime credential, including on a repeated setup.
    var runtime = new NpgsqlConnectionStringBuilder(connectionString) { Username = role, Password = password };
    await using var check = new NpgsqlConnection(runtime.ConnectionString);
    await check.OpenAsync();
    Console.WriteLine("CellBridge Lists schema and restricted runtime login are ready.");
}
else if (args.Contains("--collect-orphans", StringComparer.Ordinal))
{
    var report = await new CellBridge.Storage.PostgreSql.StorageMaintenance(source, limits).CollectOrphansAsync(
        apply: args.Contains("--apply", StringComparer.Ordinal),
        quiescent: args.Contains("--quiescent", StringComparer.Ordinal),
        fileSystemRoot: builder.Configuration["Storage:ContentRoot"]);
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(report));
}
else
{
    await new PostgreSqlStorageMigrator(source, limits).MigrateAsync();
    await AuthenticationDatabase.InitializeAsync(connectionString);
    Console.WriteLine("CellBridge storage schema version 7 and authentication schema version 1 are ready.");
}
