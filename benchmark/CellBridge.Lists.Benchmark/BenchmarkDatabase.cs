using CellBridge.Lists.PostgreSql;
using Npgsql;

// Aspire owns the database and runtime role. This helper owns connections and
// the untimed dataset reset used by the warm provider benchmarks.
internal sealed class BenchmarkDatabase : IAsyncDisposable
{
    public NpgsqlDataSource Admin { get; }
    public NpgsqlDataSource Runtime { get; }
    private readonly string role;

    private BenchmarkDatabase(string adminConnection, string runtimeConnection)
    {
        role = new NpgsqlConnectionStringBuilder(runtimeConnection).Username
            ?? throw new ArgumentException("A restricted runtime login is required.", nameof(runtimeConnection));
        Admin = NpgsqlDataSource.Create(adminConnection);
        Runtime = NpgsqlDataSource.Create(runtimeConnection);
    }

    public static Task<BenchmarkDatabase> OpenAsync(string adminConnection, string runtimeConnection) =>
        Task.FromResult(new BenchmarkDatabase(adminConnection, runtimeConnection));

    public async Task ResetListsAsync()
    {
        await using (var drop = Admin.CreateCommand("DROP SCHEMA cellbridge_lists CASCADE"))
            await drop.ExecuteNonQueryAsync();
        await PostgreSqlListSetup.InitializeAsync(Admin, role);
    }

    public async Task<Dictionary<string, long>> CountsAsync()
    {
        var result = new Dictionary<string, long>();
        foreach (var table in new[] { "records", "audit_events", "create_receipts" })
        {
            await using var command = Admin.CreateCommand($"SELECT count(*) FROM cellbridge_lists.{table}");
            result[$"cellbridge_lists.{table}"] = (long)(await command.ExecuteScalarAsync())!;
        }
        return result;
    }

    public async ValueTask DisposeAsync()
    {
        try { await Runtime.DisposeAsync(); }
        finally { await Admin.DisposeAsync(); }
    }
}
