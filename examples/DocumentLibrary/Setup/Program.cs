using CellBridge.Storage.PostgreSql;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Npgsql;
using CellBridge.Lists.PostgreSql;
using CellBridge.DocumentLibrary;

var builder = Host.CreateApplicationBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("cellbridge")
    ?? throw new InvalidOperationException("ConnectionStrings:cellbridge is required.");
await using var source = NpgsqlDataSource.Create(connectionString);
await new PostgreSqlStateStore(source).InitializeAsync();
if (builder.Configuration.GetValue("Lists:Enabled", false))
{
    var role = builder.Configuration["Lists:RuntimeRole"]
        ?? throw new InvalidOperationException("Lists:RuntimeRole is required.");
    var password = builder.Configuration["Lists:RuntimePassword"]
        ?? throw new InvalidOperationException("Lists:RuntimePassword is required.");
    if (role != "cellbridge_library_runtime")
        throw new InvalidOperationException("The local example provisions only its dedicated cellbridge_library_runtime role.");
    // PostgreSQL formats identifiers/literals. Secrets are never logged.
    await using var connection = await source.OpenConnectionAsync();
    await using (var create = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM pg_roles WHERE rolname=$1)", connection))
    {
        create.Parameters.AddWithValue(role);
        var exists = (bool)(await create.ExecuteScalarAsync() ?? false);
        await using var format = new NpgsqlCommand(exists
            ? "SELECT format('ALTER ROLE %I LOGIN PASSWORD %L NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE NOINHERIT',$1::text,$2::text)"
            : "SELECT format('CREATE ROLE %I LOGIN PASSWORD %L NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE NOINHERIT',$1::text,$2::text)", connection);
        format.Parameters.AddWithValue(role); format.Parameters.AddWithValue(password);
        var sql = (string)(await format.ExecuteScalarAsync() ?? throw new InvalidOperationException("Could not format runtime role provisioning."));
        await using var provision = new NpgsqlCommand(sql, connection);
        await provision.ExecuteNonQueryAsync();
    }
    await PostgreSqlListSetup.InitializeAsync(source, role);
    await using (var grant = new NpgsqlCommand("""
        GRANT USAGE ON SCHEMA public TO cellbridge_library_runtime;
        GRANT SELECT ON public.cellbridge_schema TO cellbridge_library_runtime;
        GRANT SELECT,INSERT,UPDATE,DELETE ON public.cellbridge_documents,public.cellbridge_states,
          public.cellbridge_objects,public.cellbridge_chunks,public.cellbridge_usage TO cellbridge_library_runtime;
        """, connection)) await grant.ExecuteNonQueryAsync();
    var runtimeConnection = new NpgsqlConnectionStringBuilder(connectionString) { Username = role, Password = password };
    await using var runtime = NpgsqlDataSource.Create(runtimeConnection.ConnectionString);
    await DocumentLibraryListSeed.InitializeAsync(source, runtime);
    Console.WriteLine("Local customer workspace lists are ready with separate setup/runtime credentials.");
}
Console.WriteLine("CellBridge PostgreSQL storage schema is ready.");
