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
    public string? ListsRuntimeConnectionString { get; }

    private PostgreSqlEnvironment(DistributedApplication application, IDistributedApplicationTestingBuilder builder,
        string connectionString, string listsConnectionString, string? listsRuntimeConnectionString = null)
    {
        this.application = application;
        this.builder = builder;
        ConnectionString = connectionString;
        ListsConnectionString = listsConnectionString;
        ListsRuntimeConnectionString = listsRuntimeConnectionString;
    }

    public static async Task<PostgreSqlEnvironment> StartAsync(CancellationToken cancellationToken = default,
        bool initializeListsRuntime = false)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        var builder = await CreateBuilderAsync(timeout.Token);
        DistributedApplication? application = null;
        try
        {
            builder.Resources.Remove(builder.Resources.Single(resource => resource.Name == "webdemo"));
            var storage = builder.CreateResourceBuilder<PostgresServerResource>("storage");
            var lists = storage.AddDatabase("lists");
            if (initializeListsRuntime) AddListsRuntime(builder, storage, lists);
            application = await builder.BuildAsync(timeout.Token);
            await application.StartAsync(timeout.Token);
            await WaitForInitializerAsync(application, "storage-init", timeout.Token);
            await application.ResourceNotifications.WaitForResourceHealthyAsync("lists", timeout.Token);
            if (initializeListsRuntime)
                await WaitForInitializerAsync(application, "lists-init", timeout.Token);
            return new(application, builder, (await application.GetConnectionStringAsync("cellbridge", timeout.Token))!,
                (await application.GetConnectionStringAsync("lists", timeout.Token))!,
                initializeListsRuntime ? await application.GetConnectionStringAsync("lists-runtime", timeout.Token) : null);
        }
        catch
        {
            try { if (application is not null) await application.DisposeAsync(); }
            finally { await builder.DisposeAsync(); }
            throw;
        }
    }

    internal static IResourceBuilder<ConnectionStringResource> AddListsRuntime(
        IDistributedApplicationTestingBuilder builder, IResourceBuilder<PostgresServerResource> storage,
        IResourceBuilder<PostgresDatabaseResource> lists)
    {
        const string role = "cellbridge_lists_runtime";
        builder.Configuration["Parameters:ListsRuntimePassword"] =
            Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var appHost = storage.ApplicationBuilder;
        var password = appHost.AddParameter("ListsRuntimePassword", secret: true);
        var runtime = appHost.AddConnectionString("lists-runtime", ReferenceExpression.Create(
            $"Host={storage.Resource.Host};Port={storage.Resource.Port};Database={lists.Resource.DatabaseName};Username={role};Password={password}"));
        var temporaryDirectory = Path.GetFullPath(
            builder.Configuration["Parameters:TemporaryDirectory"] ?? "artifacts/tmp",
            Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "../..")));
        appHost.AddDotnetProject("lists-init",
                Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "../../tools/CellBridge.Storage.Setup/CellBridge.Storage.Setup.csproj")))
            .WithArgs("--initialize-lists")
            .WithReference(lists)
            .WithEnvironment("Lists__RuntimeRole", role)
            .WithEnvironment("Lists__RuntimePassword", password)
            .WithEnvironment("TMPDIR", temporaryDirectory)
            .WithEnvironment("TMP", temporaryDirectory)
            .WithEnvironment("TEMP", temporaryDirectory)
            .WaitFor(lists);
        return runtime;
    }

    internal static async Task WaitForInitializerAsync(DistributedApplication application, string name,
        CancellationToken cancellationToken)
    {
        await application.ResourceNotifications.WaitForResourceAsync(name, KnownResourceStates.Finished, cancellationToken);
        if (!application.ResourceNotifications.TryGetCurrentState(name, out var initialized) ||
            initialized.Snapshot.ExitCode != 0)
            throw new InvalidOperationException($"The {name} setup resource did not complete successfully.");
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
