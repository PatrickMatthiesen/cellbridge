using CellBridge.Storage.Abstractions;
using CellBridge.Storage.PostgreSql;
using Npgsql;

namespace CellBridge.Storage.Tests;

internal static class RepositoryTestDatabase
{
    internal static async Task WithDatabase(StorageLimits limits, Func<NpgsqlDataSource, Task> test)
    {
        var connection = Environment.GetEnvironmentVariable("ConnectionStrings__cellbridge")
            ?? throw new InvalidOperationException("The disposable PostgreSQL fixture is not running.");
        await using var admin = NpgsqlDataSource.Create(connection);
        var name = "cellbridge_repository_test_" + Guid.NewGuid().ToString("N");
        await using (var create = admin.CreateCommand("CREATE DATABASE " + name)) await create.ExecuteNonQueryAsync();
        try
        {
            await using var source = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(connection) { Database = name }.ConnectionString);
            await new PostgreSqlStateStore(source, limits).InitializeAsync();
            await test(source);
        }
        finally
        {
            await using var drop = admin.CreateCommand("DROP DATABASE " + name + " WITH (FORCE)");
            await drop.ExecuteNonQueryAsync();
        }
    }
}
