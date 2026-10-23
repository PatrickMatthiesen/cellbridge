using CellBridge.TestInfrastructure;
using Microsoft.Data.SqlClient;
using Xunit.Abstractions;

namespace CellBridge.SqlServer.Tests;

[CollectionDefinition("SQL Server", DisableParallelization = true)]
public sealed class SqlServerCollection : ICollectionFixture<SqlServerFixture>;

public sealed class SqlServerFixture : IAsyncLifetime
{
    private SqlServerEnvironment? environment;
    public string Documents => environment!.ConnectionString;
    public string Lists => environment!.ListsConnectionString;
    public Task InitializeAsync() => InitializeCoreAsync();

    private async Task InitializeCoreAsync() => environment = await SqlServerEnvironment.StartAsync();
    public async Task DisposeAsync()
    {
        if (environment is not null) await environment.DisposeAsync();
    }
}

[Collection("SQL Server")]
public sealed class SqlServerSetupTests(SqlServerFixture fixture, ITestOutputHelper output)
{
    [Fact]
    public async Task OwnedEngineUsesDeveloperEditionAndSeparateEmptyDatabases()
    {
        foreach (var connectionString in new[] { fixture.Documents, fixture.Lists })
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();
            await using var version = new SqlCommand("SELECT @@VERSION, DB_NAME(), SERVERPROPERTY('Edition')", connection);
            await using var reader = await version.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            output.WriteLine(reader.GetString(0));
            Assert.Contains("Developer", reader.GetString(2));
            Assert.Contains(reader.GetString(1), new[] { "documents", "lists" });
        }
    }
}
