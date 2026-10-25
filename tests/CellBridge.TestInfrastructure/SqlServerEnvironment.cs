using Aspire.Hosting;
using Aspire.Hosting.Testing;

namespace CellBridge.TestInfrastructure;

/// <summary>Owns a disposable SQL Server Developer instance using the main AppHost.</summary>
public sealed class SqlServerEnvironment : IAsyncDisposable
{
    private readonly DistributedApplication application;
    private readonly IDistributedApplicationTestingBuilder builder;
    public string ConnectionString { get; }
    public string ListsConnectionString { get; }

    private SqlServerEnvironment(DistributedApplication application,
        IDistributedApplicationTestingBuilder builder, string documents, string lists)
    {
        this.application = application;
        this.builder = builder;
        ConnectionString = documents;
        ListsConnectionString = lists;
    }

    public static async Task<SqlServerEnvironment> StartAsync(CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        var builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.CellBridge_AppHost>(
            ["--environment", "Testing"], timeout.Token);
        // Modify only this test model, before any resources start. No developer volumes,
        // PostgreSQL initializer, web process, capture process or persisted SQL data.
        builder.Resources.Clear();
        var sql = builder.AddSqlServer("sqlserver")
            .WithImageTag("2022-latest")
            .WithEnvironment("MSSQL_MEMORY_LIMIT_MB", "2048")
            .WithEnvironment("MSSQL_PID", "Developer");
        sql.AddDatabase("documents");
        sql.AddDatabase("lists");
        DistributedApplication? application = null;
        try
        {
            application = await builder.BuildAsync(timeout.Token);
            await application.StartAsync(timeout.Token);
            await application.ResourceNotifications.WaitForResourceHealthyAsync("documents", timeout.Token);
            await application.ResourceNotifications.WaitForResourceHealthyAsync("lists", timeout.Token);
            return new(application, builder,
                (await application.GetConnectionStringAsync("documents", timeout.Token))!,
                (await application.GetConnectionStringAsync("lists", timeout.Token))!);
        }
        catch
        {
            if (application is not null) await application.DisposeAsync();
            await builder.DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { await application.DisposeAsync(); }
        finally { await builder.DisposeAsync(); }
    }
}
