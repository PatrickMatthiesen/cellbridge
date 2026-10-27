using System.Net;
using System.Text.RegularExpressions;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Dotnet;
using Aspire.Hosting.Testing;

namespace CellBridge.TestInfrastructure;

/// <summary>Owns the application and databases used by HTTP and provider load scenarios.</summary>
public sealed class LoadTestEnvironment : IAsyncDisposable
{
    private readonly DistributedApplication application;
    private readonly IDistributedApplicationTestingBuilder builder;
    private readonly string password;
    public string Backend { get; }
    public Uri HttpEndpoint { get; }
    public string DocumentConnectionString { get; }
    public string FileSystemConnectionString { get; }
    public string ListsConnectionString { get; }
    public string ListsRuntimeConnectionString { get; }

    private LoadTestEnvironment(DistributedApplication application,
        IDistributedApplicationTestingBuilder builder, Uri endpoint, string documents, string filesystem,
        string lists, string runtime, string password, string backend)
    {
        this.application = application;
        this.builder = builder;
        this.password = password;
        Backend = backend;
        HttpEndpoint = endpoint;
        DocumentConnectionString = documents;
        FileSystemConnectionString = filesystem;
        ListsConnectionString = lists;
        ListsRuntimeConnectionString = runtime;
    }

    public static async Task<LoadTestEnvironment> StartAsync(CancellationToken cancellationToken = default,
        string backend = "postgresql")
    {
        if (backend is not ("postgresql" or "sqlserver")) throw new ArgumentException("Unknown load-test backend.", nameof(backend));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        var builder = await PostgreSqlEnvironment.CreateBuilderAsync(timeout.Token);
        DistributedApplication? application = null;
        try
        {
            var storage = builder.CreateResourceBuilder<PostgresServerResource>("storage");
            var initialization = builder.CreateResourceBuilder<DotnetProjectResource>("storage-init");
            var database = builder.CreateResourceBuilder<PostgresDatabaseResource>("cellbridge");
            var webdemo = builder.CreateResourceBuilder<DotnetProjectResource>("webdemo");
            webdemo.WithEnvironment("Logging__LogLevel__Default", "Warning")
                // One seeded account drives the load. Set explicit admission
                // limits so this test can compare concurrent storage work.
                .WithEnvironment("Protocol__MaxConcurrentRequests",
                    builder.Configuration["Parameters:LoadMaxConcurrentRequests"] ?? "16")
                .WithEnvironment("Protocol__MaxConcurrentRequestsPerSubject",
                    builder.Configuration["Parameters:LoadMaxConcurrentRequestsPerSubject"] ?? "16");
            var password = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24)) + "Aa1!";
            builder.Configuration["Parameters:LoadTestPassword"] = password;
            var appHost = webdemo.ApplicationBuilder;
            var temporaryDirectory = Path.GetFullPath(
                builder.Configuration["Parameters:TemporaryDirectory"] ?? "artifacts/tmp",
                Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "../..")));
            IResourceBuilder<ConnectionStringResource> runtime;
            var documentConnectionName = "cellbridge";
            if (backend == "sqlserver")
            {
                var sql = appHost.AddSqlServer("sqlserver")
                    .WithImageTag("2022-latest")
                    .WithEnvironment("MSSQL_MEMORY_LIMIT_MB", "2048")
                    .WithEnvironment("MSSQL_PID", "Developer");
                var documents = sql.AddDatabase("documents");
                var lists = sql.AddDatabase("lists");
                const string documentRole = "cellbridge_documents_runtime";
                const string listRole = "cellbridge_lists_runtime";
                builder.Configuration["Parameters:DocumentsRuntimePassword"] =
                    Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24)) + "Aa1!";
                builder.Configuration["Parameters:ListsRuntimePassword"] =
                    Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24)) + "Aa1!";
                var documentPassword = appHost.AddParameter("DocumentsRuntimePassword", secret: true);
                var listPassword = appHost.AddParameter("ListsRuntimePassword", secret: true);
                var documentRuntime = appHost.AddConnectionString("documents-runtime", ReferenceExpression.Create(
                    $"Server={sql.Resource.Host},{sql.Resource.Port};Initial Catalog={documents.Resource.DatabaseName};User ID={documentRole};Password={documentPassword};TrustServerCertificate=True;Encrypt=True"));
                runtime = appHost.AddConnectionString("lists-runtime", ReferenceExpression.Create(
                    $"Server={sql.Resource.Host},{sql.Resource.Port};Initial Catalog={lists.Resource.DatabaseName};User ID={listRole};Password={listPassword};TrustServerCertificate=True;Encrypt=True"));
                var documentInitialization = appHost.AddDotnetProject("documents-init",
                        Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "../../tools/CellBridge.Storage.Setup/CellBridge.Storage.Setup.csproj")),
                        options => options.ExcludeLaunchProfile = true)
                    .WithReference(documents)
                    .WithEnvironment("Storage__Provider", "SqlServer")
                    .WithEnvironment("Storage__RuntimeRole", documentRole)
                    .WithEnvironment("Storage__RuntimePassword", documentPassword)
                    .WithEnvironment("TMPDIR", temporaryDirectory)
                    .WithEnvironment("TMP", temporaryDirectory)
                    .WithEnvironment("TEMP", temporaryDirectory)
                    .WaitFor(documents);
                var listsInitialization = appHost.AddDotnetProject("lists-init",
                        Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "../../tools/CellBridge.Storage.Setup/CellBridge.Storage.Setup.csproj")),
                        options => options.ExcludeLaunchProfile = true)
                    .WithArgs("--initialize-lists")
                    .WithReference(lists)
                    .WithEnvironment("Lists__Provider", "SqlServer")
                    .WithEnvironment("Lists__RuntimeRole", listRole)
                    .WithEnvironment("Lists__RuntimePassword", listPassword)
                    .WithEnvironment("TMPDIR", temporaryDirectory)
                    .WithEnvironment("TMP", temporaryDirectory)
                    .WithEnvironment("TEMP", temporaryDirectory)
                    .WaitFor(lists);
                // PostgreSQL cellbridge remains the authentication database.
                webdemo.WithReference(documentRuntime, "documents")
                    .WithEnvironment("Storage__Provider", "SqlServer")
                    .WithEnvironment("Lists__Provider", "SqlServer")
                    .WaitForCompletion(documentInitialization)
                    .WaitForCompletion(listsInitialization);
                documentConnectionName = "documents-runtime";
            }
            else
            {
                var lists = storage.AddDatabase("lists");
                runtime = PostgreSqlEnvironment.AddListsRuntime(builder, storage, lists);
                webdemo.WaitForCompletion(builder.CreateResourceBuilder<DotnetProjectResource>("lists-init"));
            }
            var filesystem = storage.AddDatabase("filesystem");
            appHost.AddDotnetProject("filesystem-init",
                    Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "../../tools/CellBridge.Storage.Setup/CellBridge.Storage.Setup.csproj")),
                    options => options.ExcludeLaunchProfile = true)
                .WithReference(filesystem, "cellbridge")
                .WithEnvironment("TMPDIR", temporaryDirectory)
                .WithEnvironment("TMP", temporaryDirectory)
                .WithEnvironment("TEMP", temporaryDirectory)
                .WaitFor(filesystem);
            var account = appHost.AddDotnetProject("load-account",
                    Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "../../tools/CellBridge.Admin/CellBridge.Admin.csproj")),
                    options => options.ExcludeLaunchProfile = true)
                .WithArgs("seed-test-user")
                .WithEnvironment("DOTNET_ENVIRONMENT", "Testing")
                .WithEnvironment("Seed__Password", appHost.AddParameter("LoadTestPassword", secret: true))
                .WithEnvironment("TMPDIR", temporaryDirectory)
                .WithEnvironment("TMP", temporaryDirectory)
                .WithEnvironment("TEMP", temporaryDirectory)
                .WithReference(database)
                .WaitForCompletion(initialization);
            webdemo.WithReference(runtime, "lists")
                .WithEnvironment("Authentication__PublicOrigin", webdemo.GetEndpoint("https"))
                .WithEnvironment("Protocol__PublicOrigin", webdemo.GetEndpoint("https"))
                .WaitForCompletion(account);
            application = await builder.BuildAsync(timeout.Token);
            await application.StartAsync(timeout.Token);
            await PostgreSqlEnvironment.WaitForInitializerAsync(application, "storage-init", timeout.Token);
            await PostgreSqlEnvironment.WaitForInitializerAsync(application, "lists-init", timeout.Token);
            if (backend == "sqlserver")
                await PostgreSqlEnvironment.WaitForInitializerAsync(application, "documents-init", timeout.Token);
            await PostgreSqlEnvironment.WaitForInitializerAsync(application, "load-account", timeout.Token);
            await PostgreSqlEnvironment.WaitForInitializerAsync(application, "filesystem-init", timeout.Token);
            await application.ResourceNotifications.WaitForResourceHealthyAsync("webdemo", timeout.Token);
            return new(application, builder, application.GetEndpoint("webdemo", "https"),
                (await application.GetConnectionStringAsync(documentConnectionName, timeout.Token))!,
                (await application.GetConnectionStringAsync("filesystem", timeout.Token))!,
                (await application.GetConnectionStringAsync("lists", timeout.Token))!,
                (await application.GetConnectionStringAsync("lists-runtime", timeout.Token))!,
                password, backend);
        }
        catch
        {
            try { if (application is not null) await application.DisposeAsync(); }
            finally { await builder.DisposeAsync(); }
            throw;
        }
    }

    public async Task<HttpClient> CreateClientAsync(CancellationToken cancellationToken = default)
    {
        var client = new HttpClient(new HttpClientHandler
        {
            CookieContainer = new CookieContainer(),
            ServerCertificateCustomValidationCallback = (request, _, _, errors) =>
                errors == System.Net.Security.SslPolicyErrors.None || request.RequestUri?.IsLoopback == true,
        }) { BaseAddress = HttpEndpoint };
        try
        {
            var token = await LoginTokenAsync(client, cancellationToken);
            using var login = await client.PostAsync("/auth/login", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["login"] = "integration-writer", ["password"] = password,
                ["__RequestVerificationToken"] = token, ["returnUrl"] = "/auth/complete",
            }), cancellationToken);
            login.EnsureSuccessStatusCode();
            if (login.RequestMessage?.RequestUri?.AbsolutePath != "/auth/complete")
                throw new InvalidOperationException("The load-test account could not sign in.");
            client.DefaultRequestHeaders.Add("X-CellBridge-CSRF", await LoginTokenAsync(client, cancellationToken));
            return client;
        }
        catch { client.Dispose(); throw; }
    }

    private static async Task<string> LoginTokenAsync(HttpClient client, CancellationToken cancellationToken)
    {
        var html = await client.GetStringAsync("/auth/login", cancellationToken);
        var match = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        if (!match.Success) throw new InvalidOperationException("The login page did not issue an antiforgery token.");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    public async ValueTask DisposeAsync()
    {
        try { await application.DisposeAsync(); }
        finally { await builder.DisposeAsync(); }
    }
}
