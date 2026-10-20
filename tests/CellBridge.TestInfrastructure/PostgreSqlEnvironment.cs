using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;

namespace CellBridge.TestInfrastructure;

/// <summary>Owns an Aspire session with disposable PostgreSQL and the current schema setup.</summary>
public sealed class PostgreSqlEnvironment : IAsyncDisposable
{
    private readonly DistributedApplication application;
    public string ConnectionString { get; }
    public string ListsConnectionString { get; }

    private PostgreSqlEnvironment(DistributedApplication application, string connectionString, string listsConnectionString)
    {
        this.application = application;
        ConnectionString = connectionString;
        ListsConnectionString = listsConnectionString;
    }

    public static async Task<PostgreSqlEnvironment> StartAsync(CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        var builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.CellBridge_TestAppHost>(
            ["--environment", "Testing"], timeout.Token);
        var application = await builder.BuildAsync(timeout.Token);
        try
        {
            await application.StartAsync(timeout.Token);
            await application.ResourceNotifications.WaitForResourceAsync("storage-init", KnownResourceStates.Finished, timeout.Token);
            if (application.ResourceNotifications.TryGetCurrentState("storage-init", out var initialized) &&
                initialized.Snapshot.ExitCode is { } exitCode && exitCode != 0)
                throw new InvalidOperationException($"PostgreSQL test schema setup failed with exit code {exitCode}.");
            await application.ResourceNotifications.WaitForResourceHealthyAsync("lists", timeout.Token);
            return new(application, (await application.GetConnectionStringAsync("cellbridge", timeout.Token))!,
                (await application.GetConnectionStringAsync("lists", timeout.Token))!);
        }
        catch
        {
            await application.DisposeAsync();
            throw;
        }
    }

    public ValueTask DisposeAsync() => application.DisposeAsync();
}
