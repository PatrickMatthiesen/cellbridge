using CellBridge.TestInfrastructure;

namespace CellBridge.Storage.Tests;

[CollectionDefinition("PostgreSQL", DisableParallelization = true)]
public sealed class PostgreSqlCollection : ICollectionFixture<PostgreSqlFixture>;

public sealed class PostgreSqlFixture : IAsyncLifetime
{
    private PostgreSqlEnvironment? environment;
    private string? previousConnection;

    public async Task InitializeAsync()
    {
        previousConnection = Environment.GetEnvironmentVariable("ConnectionStrings__cellbridge");
        environment = await PostgreSqlEnvironment.StartAsync();
        Environment.SetEnvironmentVariable("ConnectionStrings__cellbridge", environment.ConnectionString);
    }

    public async Task DisposeAsync()
    {
        Environment.SetEnvironmentVariable("ConnectionStrings__cellbridge", previousConnection);
        if (environment is not null) await environment.DisposeAsync();
    }
}
