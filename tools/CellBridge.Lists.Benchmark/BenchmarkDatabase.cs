using CellBridge.Lists.PostgreSql;
using Microsoft.Extensions.Logging;
using Npgsql;

internal sealed class BenchmarkDatabase : IAsyncDisposable
{
    private readonly NpgsqlDataSource cluster;
    private readonly ILoggerFactory measurementsLogger = LoggerFactory.Create(builder =>
        builder.SetMinimumLevel(LogLevel.Debug).AddProvider(new SqlMeasurements.CommitLoggerProvider()));
    private readonly string database = "cellbridge_lists_bench_" + Guid.NewGuid().ToString("N");
    private readonly string role = "cellbridge_lists_bench_" + Guid.NewGuid().ToString("N");
    private bool databaseCreated;
    private bool roleCreated;
    public NpgsqlDataSource Admin { get; private set; } = null!;
    public NpgsqlDataSource Runtime { get; private set; } = null!;

    private BenchmarkDatabase(string connection) => cluster = NpgsqlDataSource.Create(connection);

    public static async Task<BenchmarkDatabase> OpenAsync(string connection)
    {
        var result = new BenchmarkDatabase(connection);
        try
        {
            await using (var create = result.cluster.CreateCommand($"CREATE DATABASE {result.database}"))
                await create.ExecuteNonQueryAsync();
            result.databaseCreated = true;
            var password = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
            await using (var create = result.cluster.CreateCommand($"CREATE ROLE {result.role} LOGIN PASSWORD '{password}' NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE NOINHERIT"))
                await create.ExecuteNonQueryAsync();
            result.roleCreated = true;
            var settings = new NpgsqlConnectionStringBuilder(connection) { Database = result.database };
            result.Admin = NpgsqlDataSource.Create(settings.ConnectionString);
            await PostgreSqlListSetup.InitializeAsync(result.Admin, result.role);
            settings.Username = result.role;
            settings.Password = password;
            settings.ApplicationName = "CellBridge.Lists.Benchmark";
            var builder = new NpgsqlDataSourceBuilder(settings.ConnectionString);
            builder.UseLoggerFactory(result.measurementsLogger);
            builder.ConfigureTracing(o => o
                .ConfigureCommandFilter(SqlMeasurements.Command)
                .ConfigureBatchFilter(SqlMeasurements.Batch));
            result.Runtime = builder.Build();
            return result;
        }
        catch
        {
            await result.DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Runtime is not null) await Runtime.DisposeAsync();
        if (Admin is not null) await Admin.DisposeAsync();
        try
        {
            if (databaseCreated)
            {
                await using var drop = cluster.CreateCommand($"DROP DATABASE {database} WITH (FORCE)");
                await drop.ExecuteNonQueryAsync();
            }
        }
        finally
        {
            try
            {
                if (roleCreated)
                {
                    await using var drop = cluster.CreateCommand($"DROP ROLE {role}");
                    await drop.ExecuteNonQueryAsync();
                }
            }
            finally { await cluster.DisposeAsync(); measurementsLogger.Dispose(); }
        }
    }
}
