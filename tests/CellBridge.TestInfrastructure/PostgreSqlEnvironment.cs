using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Dotnet;
using Aspire.Hosting.Testing;

namespace CellBridge.TestInfrastructure;

/// <summary>Owns an Aspire session with disposable PostgreSQL and the current schema setup.</summary>
public sealed class PostgreSqlEnvironment : IAsyncDisposable
{
    private readonly DistributedApplication application;
    private readonly IDistributedApplicationTestingBuilder builder;
    public string ConnectionString { get; }
    public string ListsConnectionString { get; }

    private PostgreSqlEnvironment(DistributedApplication application, IDistributedApplicationTestingBuilder builder,
        string connectionString, string listsConnectionString)
    {
        this.application = application;
        this.builder = builder;
        ConnectionString = connectionString;
        ListsConnectionString = listsConnectionString;
    }

    public static async Task<PostgreSqlEnvironment> StartAsync(CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        var builder = await CreateBuilderAsync(timeout.Token);
        builder.Resources.Remove(builder.Resources.Single(resource => resource.Name == "webdemo"));
        builder.CreateResourceBuilder<PostgresServerResource>("storage").AddDatabase("lists");
        var application = await builder.BuildAsync(timeout.Token);
        try
        {
            await application.StartAsync(timeout.Token);
            await application.ResourceNotifications.WaitForResourceAsync("storage-init", KnownResourceStates.Finished, timeout.Token);
            if (application.ResourceNotifications.TryGetCurrentState("storage-init", out var initialized) &&
                initialized.Snapshot.ExitCode is { } exitCode && exitCode != 0)
                throw new InvalidOperationException($"PostgreSQL test schema setup failed with exit code {exitCode}.");
            await application.ResourceNotifications.WaitForResourceHealthyAsync("lists", timeout.Token);
            return new(application, builder, (await application.GetConnectionStringAsync("cellbridge", timeout.Token))!,
                (await application.GetConnectionStringAsync("lists", timeout.Token))!);
        }
        catch
        {
            await application.DisposeAsync();
            await builder.DisposeAsync();
            throw;
        }
    }

    /// <summary>Reuse the application model with a fresh database and no capture process.</summary>
    public static async Task<IDistributedApplicationTestingBuilder> CreateBuilderAsync(
        CancellationToken cancellationToken = default)
    {
        var builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.CellBridge_AppHost>(
            ["--environment", "Testing"], cancellationToken);
        var storage = builder.Resources.Single(resource => resource.Name == "storage");
        foreach (var mount in storage.Annotations.OfType<ContainerMountAnnotation>().ToArray())
            storage.Annotations.Remove(mount);
        var capture = builder.Resources.Single(resource => resource.Name == "sharepoint");
        foreach (var resource in builder.Resources.Where(resource => resource == capture ||
                     resource.Annotations.OfType<ResourceRelationshipAnnotation>()
                         .Any(relationship => relationship.Type == "Parent" && relationship.Resource == capture)).ToArray())
            builder.Resources.Remove(resource);
        builder.CreateResourceBuilder<DotnetProjectResource>("webdemo")
            .WithEnvironment("Protocol__CaptureDirectory", "");
        return builder;
    }

    public async ValueTask DisposeAsync()
    {
        try { await application.DisposeAsync(); }
        finally { await builder.DisposeAsync(); }
    }
}
