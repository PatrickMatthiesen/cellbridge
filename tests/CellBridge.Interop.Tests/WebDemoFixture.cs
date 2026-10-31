#nullable enable
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Dotnet;
using Aspire.Hosting.Testing;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.PostgreSql;
using CellBridge.TestInfrastructure;
using Npgsql;

namespace CellBridge.Interop.Tests;

[CollectionDefinition("WebDemo integration", DisableParallelization = true)]
public sealed class WebDemoCollection : ICollectionFixture<WebDemoFixture>;

/// <summary>Starts two local WebDemo processes and a disposable database for HTTP tests.</summary>
public sealed class WebDemoFixture : IAsyncLifetime
{
    private DistributedApplication? application;
    private IDistributedApplicationTestingBuilder? testingBuilder;
    private readonly Dictionary<string, string?> previous = new();
    private string? connectionString;

    private Uri? primaryOrigin;
    private string? secondUserCookie;
    private string? secondUserCsrf;

    public async Task InitializeAsync()
    {
        // A supplied endpoint remains available for focused external interoperability runs.
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OFFICECOLLABSERVER_INTEROP_ENDPOINT")))
            return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var testPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)) + "Aa1!";
        var builder = testingBuilder = await PostgreSqlEnvironment.CreateBuilderAsync(timeout.Token);
        builder.Configuration["Parameters:TestPassword"] = testPassword;
        var database = builder.CreateResourceBuilder<PostgresDatabaseResource>("cellbridge");
        var initialization = builder.CreateResourceBuilder<DotnetProjectResource>("storage-init");
        var primary = builder.CreateResourceBuilder<DotnetProjectResource>("webdemo");
        var temporaryDirectory = Path.GetFullPath(
            builder.Configuration["Parameters:TemporaryDirectory"] ?? "artifacts/tmp",
            Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "../..")));
        // Add path-based projects through the original builder so they share
        // the AppHost's coordinated build with its existing projects.
        var appHost = primary.ApplicationBuilder;
        var account = appHost.AddDotnetProject("test-account",
                Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "../../tools/CellBridge.Admin/CellBridge.Admin.csproj")),
                options => options.LaunchProfileName = null)
            .WithArgs("seed-test-user")
            .WithEnvironment("DOTNET_ENVIRONMENT", "Testing")
            .WithEnvironment("Seed__Password", builder.AddParameter("TestPassword", secret: true))
            .WithEnvironment("Seed__UserCount", "2")
            .WithEnvironment("TMPDIR", temporaryDirectory)
            .WithEnvironment("TMP", temporaryDirectory)
            .WithEnvironment("TEMP", temporaryDirectory)
            .WithReference(database)
            .WaitForCompletion(initialization);
        var peer = appHost.AddDotnetProject("webdemo-peer",
                Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "../CellBridge.WebDemo/CellBridge.WebDemo.csproj")),
                options => options.LaunchProfileName = null)
            .WithHttpEndpoint(name: "http")
            .WithHttpsEndpoint(name: "https")
            .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
            .WithEnvironment("Storage__Provider", "PostgreSql")
            .WithEnvironment("TMPDIR", temporaryDirectory)
            .WithEnvironment("TMP", temporaryDirectory)
            .WithEnvironment("TEMP", temporaryDirectory)
            .WithHttpHealthCheck("/health", endpointName: "http")
            .WithReference(database);
        foreach (var host in new[] { primary, peer })
        {
            host.WithEnvironment("Storage__MultipleInstances", "true")
                .WithEnvironment("Protocol__EnableOfficeRealTime", "false")
                .WithEnvironment("Protocol__DocumentLibraryId", "80548d11-8ec4-4f88-91fd-885ce8cad658")
                .WithEnvironment("Authentication__PublicOrigin", "https://cellbridge.test")
                .WithEnvironment("Protocol__PublicOrigin", "https://cellbridge.test")
                .WithEnvironment("Protocol__MaxConcurrentRequests", "32")
                .WithEnvironment("Protocol__MaxConcurrentRequestsPerSubject", "32")
                .WaitForCompletion(account);
        }
        application = await builder.BuildAsync(timeout.Token);
        try
        {
            await application.StartAsync(timeout.Token);
            await Task.WhenAll(application.ResourceNotifications.WaitForResourceHealthyAsync("webdemo", timeout.Token),
                application.ResourceNotifications.WaitForResourceHealthyAsync("webdemo-peer", timeout.Token));
            var origin = application.GetEndpoint("webdemo", "https");
            primaryOrigin = origin;
            connectionString = await application.GetConnectionStringAsync("cellbridge", timeout.Token)
                ?? throw new InvalidOperationException("The test PostgreSQL connection string is unavailable.");
            Set("OFFICECOLLABSERVER_INTEROP_ENDPOINT", new Uri(origin, "/_vti_bin/cellstorage.svc").ToString());
            Set("OFFICECOLLABSERVER_INTEROP_PEER", new Uri(application.GetEndpoint("webdemo-peer", "https"), "/_vti_bin/cellstorage.svc").ToString());
            Set("CELLBRIDGE_INTEROP_PUBLIC_ORIGIN", "https://cellbridge.test");
            var cookies = new CookieContainer();
            using var http = new HttpClient(new HttpClientHandler
            {
                CookieContainer = cookies,
                ServerCertificateCustomValidationCallback = (request, _, _, errors) =>
                    errors == System.Net.Security.SslPolicyErrors.None || request.RequestUri?.IsLoopback == true,
            }) { BaseAddress = origin };
            var token = await Token(http);
            using var login = await http.PostAsync("/auth/login", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["login"] = "integration-writer", ["password"] = testPassword,
                ["__RequestVerificationToken"] = token, ["returnUrl"] = "/auth/complete",
            }), timeout.Token);
            login.EnsureSuccessStatusCode();
            if (login.RequestMessage?.RequestUri?.AbsolutePath != "/auth/complete")
                throw new InvalidOperationException("The disposable integration account could not sign in.");
            token = await Token(http);
            Set("CELLBRIDGE_INTEROP_COOKIE", cookies.GetCookieHeader(origin));
            Set("CELLBRIDGE_INTEROP_CSRF", token);
            http.DefaultRequestHeaders.Add("X-CellBridge-CSRF", token);

            var secondCookies = new CookieContainer();
            using var secondHttp = new HttpClient(new HttpClientHandler
            {
                CookieContainer = secondCookies,
                ServerCertificateCustomValidationCallback = (request, _, _, errors) =>
                    errors == System.Net.Security.SslPolicyErrors.None || request.RequestUri?.IsLoopback == true,
            }) { BaseAddress = origin };
            var secondToken = await Token(secondHttp);
            using var secondLogin = await secondHttp.PostAsync("/auth/login", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["login"] = "integration-writer-1", ["password"] = testPassword,
                ["__RequestVerificationToken"] = secondToken, ["returnUrl"] = "/auth/complete",
            }), timeout.Token);
            secondLogin.EnsureSuccessStatusCode();
            if (secondLogin.RequestMessage?.RequestUri?.AbsolutePath != "/auth/complete")
                throw new InvalidOperationException("The second disposable integration account could not sign in.");
            secondToken = await Token(secondHttp);
            secondUserCookie = secondCookies.GetCookieHeader(origin);
            secondUserCsrf = secondToken;
            foreach (var name in new[] { "test", "save-test", "save-check" })
            {
                using var response = await http.PostAsJsonAsync("/api/documents", new { name, type = "docx" }, timeout.Token);
                response.EnsureSuccessStatusCode();
            }
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }

    public HttpClient CreateSecondUserHttpClient()
    {
        if (primaryOrigin is null || secondUserCookie is null || secondUserCsrf is null)
            throw new InvalidOperationException("The local fixture did not create its second authenticated user.");
        var client = new HttpClient(new HttpClientHandler
        {
            UseCookies = false,
            AllowAutoRedirect = false,
            ServerCertificateCustomValidationCallback = (request, _, _, errors) =>
                errors == System.Net.Security.SslPolicyErrors.None || request.RequestUri?.IsLoopback == true,
        })
        { BaseAddress = primaryOrigin, Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.Add("Cookie", secondUserCookie);
        client.DefaultRequestHeaders.Add("X-CellBridge-CSRF", secondUserCsrf);
        return client;
    }

    public async Task GrantSecondUserWriteAccessAsync(string path, CancellationToken cancellationToken = default)
    {
        if (connectionString is null || application is null)
            throw new InvalidOperationException("Write grants require the local fixture database.");
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        IDocumentRepository repository = new PostgreSqlStateStore(dataSource);
        var head = await repository.FindAsync(new DocumentIdentity.Path(StorageIds.PathKey(Uri.UnescapeDataString(path))), cancellationToken)
            ?? throw new KeyNotFoundException($"The test document '{path}' does not exist.");
        var current = await repository.ReadControlAsync(head.ResourceId, cancellationToken)
            ?? throw new KeyNotFoundException($"The test document '{path}' has no control state.");
        var owner = current.Head.Security.Owner ?? throw new InvalidOperationException("The test document has no owner.");
        var admission = new DocumentActorAdmission(new(owner, owner, owner), current.Head.ResourceId,
            current.Head.LifecycleGeneration, current.Head.Security.AuthorizationPolicy, DocumentAccess.Read | DocumentAccess.Write)
        { StateVersion = current.Head.StateVersion, AuthorityGeneration = current.Head.AuthorityGeneration };
        var security = current.Head.Security with
        {
            Grants = current.Head.Security.Grants.SetItem("local:integration-writer-1", DocumentAccess.Read | DocumentAccess.Write),
        };
        var result = await repository.ExecuteControlAsync(new DocumentControlCommand.SetPermissions(
            current.Head.ResourceId, admission, security, current.Head.AuthorityGeneration), cancellationToken);
        if (!result.Changed) throw new InvalidOperationException("The trusted test write grant failed: " + result.ErrorCode);
    }

    public async Task<DocumentControlRead> ReadControlAsync(string path, CancellationToken cancellationToken = default)
    {
        if (connectionString is null || application is null)
            throw new InvalidOperationException("Control reads require the local fixture database.");
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        IDocumentRepository repository = new PostgreSqlStateStore(dataSource);
        var head = await repository.FindAsync(new DocumentIdentity.Path(StorageIds.PathKey(Uri.UnescapeDataString(path))), cancellationToken)
            ?? throw new KeyNotFoundException($"The test document '{path}' does not exist.");
        return await repository.ReadControlAsync(head.ResourceId, cancellationToken)
            ?? throw new KeyNotFoundException($"The test document '{path}' has no control state.");
    }

    private static async Task<string> Token(HttpClient http)
    {
        var page = await http.GetStringAsync("/auth/login");
        var match = Regex.Match(page, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        if (!match.Success) throw new InvalidOperationException("The login page did not issue an antiforgery token.");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    private void Set(string name, string value)
    {
        previous.TryAdd(name, Environment.GetEnvironmentVariable(name));
        Environment.SetEnvironmentVariable(name, value);
    }

    public async Task DisposeAsync()
    {
        await using var ownedBuilder = testingBuilder;
        testingBuilder = null;
        foreach (var (name, value) in previous) Environment.SetEnvironmentVariable(name, value);
        if (application is not null) { await application.DisposeAsync(); application = null; }
    }
}
