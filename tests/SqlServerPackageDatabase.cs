using System.Security.Cryptography;
using Microsoft.Data.SqlClient;
using Xunit;
using Xunit.Abstractions;

namespace CellBridge.PackageChecks;

// The source-test orchestrator passes credentials for its own disposable engine.
// This helper contains no CellBridge runtime implementation or repository fixtures.
internal sealed class SqlServerPackageDatabase
{
    internal const string ConnectionVariable = "CELLBRIDGE_PACKAGE_SQLSERVER_CONNECTION";
    internal string Admin { get; } = Environment.GetEnvironmentVariable(ConnectionVariable)
        ?? throw new InvalidOperationException("Run tools/verify_packages.py --sqlserver to supply an owned database.");

    internal async Task AssertEmptyAsync(ITestOutputHelper output)
    {
        output.WriteLine(await ScalarAsync<string>("SELECT @@VERSION"));
        output.WriteLine("Database: " + await ScalarAsync<string>("SELECT DB_NAME()"));
        Assert.Equal(0, await ScalarAsync<int>("SELECT COUNT(*) FROM sys.tables WHERE is_ms_shipped=0"));
    }

    internal async Task<string> RuntimeAsync(string grants)
    {
        var user = "cb_package_" + Guid.NewGuid().ToString("N");
        var password = "Cb!" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        await ExecuteAsync($"CREATE LOGIN [{user}] WITH PASSWORD='{password}'; CREATE USER [{user}] FOR LOGIN [{user}];");
        await ExecuteAsync(grants.Replace("[runtime]", $"[{user}]", StringComparison.Ordinal));
        return new SqlConnectionStringBuilder(Admin) { UserID = user, Password = password }.ConnectionString;
    }

    internal async Task<T> ScalarAsync<T>(string sql)
    {
        await using var connection = new SqlConnection(Admin);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    internal async Task ExecuteAsync(string sql)
    {
        await using var connection = new SqlConnection(Admin);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}

public sealed class OwnedSqlServerFactAttribute : FactAttribute
{
    public OwnedSqlServerFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(SqlServerPackageDatabase.ConnectionVariable)))
            Skip = "Run tools/verify_packages.py --sqlserver for an owned disposable SQL Server database.";
    }
}
