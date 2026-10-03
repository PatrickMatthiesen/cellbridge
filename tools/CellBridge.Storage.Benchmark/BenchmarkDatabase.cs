using CellBridge.Storage.PostgreSql;
using CellBridge.Storage.Abstractions;
using Npgsql;

/// <summary>Each provider benchmark owns its database; host data and backends never mix.</summary>
internal sealed class BenchmarkDatabase(NpgsqlDataSource admin, string name, NpgsqlDataSource source) : IAsyncDisposable
{
    public NpgsqlDataSource Source { get; } = source;

    public static async Task<BenchmarkDatabase?> OpenAsync(string? connectionString, StorageLimits limits)
    {
        if (connectionString is null) return null;
        var admin = NpgsqlDataSource.Create(connectionString);
        string name = "cellbridge_bench_" + Guid.NewGuid().ToString("N");
        bool created = false;
        NpgsqlDataSource? source = null;
        try
        {
            await using (var create = admin.CreateCommand($"CREATE DATABASE {name}")) await create.ExecuteNonQueryAsync();
            created = true;
            var settings = new NpgsqlConnectionStringBuilder(connectionString) { Database = name };
            source = NpgsqlDataSource.Create(settings.ConnectionString);
            await new PostgreSqlStateStore(source, limits).InitializeAsync();
            return new(admin, name, source);
        }
        catch
        {
            if (source is not null) await source.DisposeAsync();
            if (created)
            {
                await using var drop = admin.CreateCommand($"DROP DATABASE {name} WITH (FORCE)");
                await drop.ExecuteNonQueryAsync();
            }
            await admin.DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Source.DisposeAsync();
        try
        {
            await using var drop = admin.CreateCommand($"DROP DATABASE {name} WITH (FORCE)");
            await drop.ExecuteNonQueryAsync();
        }
        finally { await admin.DisposeAsync(); }
    }
}
