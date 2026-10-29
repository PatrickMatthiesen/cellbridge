using System.Net;
using System.Text.RegularExpressions;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Dotnet;
using Aspire.Hosting.Testing;
using CellBridge.Storage.Abstractions;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace CellBridge.TestInfrastructure;

/// <summary>Owns the application and databases used by HTTP and provider load scenarios.</summary>
public sealed class LoadTestEnvironment : IAsyncDisposable
{
    private readonly DistributedApplication application;
    private readonly IDistributedApplicationTestingBuilder builder;
    private readonly string password;
    private readonly LoadIdentitySessions? sessions;
    public int UserCount { get; }
    public int ProtocolConcurrency { get; }
    public int ProtocolConcurrencyPerSubject { get; }
    public bool QueryStatisticsEnabled { get; }
    public int? PostgreSqlPoolSize { get; }
    public int? PostgreSqlMaxAutoPrepare { get; }
    public string Backend { get; }
    public Uri HttpEndpoint { get; }
    public string DocumentConnectionString { get; }
    public string PostgreSqlDocumentConnectionString { get; }
    public string FileSystemConnectionString { get; }
    public string ListsConnectionString { get; }
    public string ListsRuntimeConnectionString { get; }

    public OwnedWebDemoProcessProbe CreateWebDemoProcessProbe()
    {
        // Aspire.Hosting 17's KnownProperties.Executable.Pid is the snapshot key executable.pid.
        if (!application.ResourceNotifications.TryGetCurrentState("webdemo", out var resource))
            throw new InvalidOperationException("The owned WebDemo resource has no process snapshot.");
        var value = resource.Snapshot.Properties.SingleOrDefault(property =>
            string.Equals(property.Name, "executable.pid", StringComparison.OrdinalIgnoreCase))?.Value;
        if (!int.TryParse(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture), out var pid) || pid <= 0)
            throw new InvalidOperationException("The owned WebDemo resource has no executable process ID.");
        return new OwnedWebDemoProcessProbe(pid);
    }

    private LoadTestEnvironment(DistributedApplication application,
        IDistributedApplicationTestingBuilder builder, Uri endpoint, string documents, string postgreSqlDocuments, string filesystem,
        string lists, string runtime, string password, string backend, int userCount, LoadIdentitySessions? sessions,
        int protocolConcurrency, int protocolConcurrencyPerSubject, bool queryStatisticsEnabled, int? postgreSqlPoolSize,
        int? postgreSqlMaxAutoPrepare)
    {
        this.application = application;
        this.builder = builder;
        this.password = password;
        this.sessions = sessions;
        Backend = backend;
        UserCount = userCount;
        ProtocolConcurrency = protocolConcurrency;
        ProtocolConcurrencyPerSubject = protocolConcurrencyPerSubject;
        QueryStatisticsEnabled = queryStatisticsEnabled;
        PostgreSqlPoolSize = postgreSqlPoolSize;
        PostgreSqlMaxAutoPrepare = postgreSqlMaxAutoPrepare;
        HttpEndpoint = endpoint;
        DocumentConnectionString = documents;
        PostgreSqlDocumentConnectionString = postgreSqlDocuments;
        FileSystemConnectionString = filesystem;
        ListsConnectionString = lists;
        ListsRuntimeConnectionString = runtime;
    }

    public static async Task<LoadTestEnvironment> StartAsync(CancellationToken cancellationToken = default,
        string backend = "postgresql", int userCount = 1, bool useDefaultAdmissionLimits = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(userCount);
        if (backend is not ("postgresql" or "sqlserver")) throw new ArgumentException("Unknown load-test backend.", nameof(backend));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        var builder = await PostgreSqlEnvironment.CreateBuilderAsync(timeout.Token);
        DistributedApplication? application = null;
        LoadIdentitySessions? sessions = null;
        try
        {
            var storage = builder.CreateResourceBuilder<PostgresServerResource>("storage");
            var queryStatisticsRequested = builder.Configuration.GetValue("Parameters:LoadQueryStatistics", false);
            var queryStatisticsEnabled = queryStatisticsRequested && backend == "postgresql";
            if (queryStatisticsRequested && !queryStatisticsEnabled)
                Console.WriteLine("PostgreSQL query statistics are unavailable for the SQL Server document workload.");
            if (queryStatisticsEnabled)
                storage.WithArgs("-c", "shared_preload_libraries=pg_stat_statements", "-c", "track_io_timing=on",
                    "-c", "track_wal_io_timing=on");
            if (builder.Configuration.GetValue<int?>("Parameters:LoadPostgreSqlMaxWalSizeMiB") is { } walSize)
            {
                ArgumentOutOfRangeException.ThrowIfNegativeOrZero(walSize);
                storage.WithArgs("-c", "max_wal_size=" + walSize.ToString(System.Globalization.CultureInfo.InvariantCulture) + "MB");
            }
            var initialization = builder.CreateResourceBuilder<DotnetProjectResource>("storage-init");
            var database = builder.CreateResourceBuilder<PostgresDatabaseResource>("cellbridge");
            var webdemo = builder.CreateResourceBuilder<DotnetProjectResource>("webdemo");
            var configuredPoolSize = builder.Configuration.GetValue<int?>("Parameters:LoadPostgreSqlPoolSize");
            if (configuredPoolSize is { } poolSize) ArgumentOutOfRangeException.ThrowIfNegativeOrZero(poolSize);
            var postgreSqlPoolSize = backend == "postgresql" ? configuredPoolSize : null;
            var configuredAutoPrepare = builder.Configuration.GetValue<int?>("Parameters:LoadPostgreSqlMaxAutoPrepare");
            if (configuredAutoPrepare is { } autoPrepare) ArgumentOutOfRangeException.ThrowIfNegative(autoPrepare);
            var postgreSqlMaxAutoPrepare = backend == "postgresql" ? configuredAutoPrepare : null;
            var postgreSqlOverrides = new List<string>();
            if (postgreSqlPoolSize is { } webPoolSize)
                postgreSqlOverrides.Add("Maximum Pool Size=" + webPoolSize.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (postgreSqlMaxAutoPrepare is { } webAutoPrepare)
                postgreSqlOverrides.Add("Max Auto Prepare=" + webAutoPrepare.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (postgreSqlOverrides.Count != 0)
                webdemo.WithEnvironment("ConnectionStrings__cellbridge", ReferenceExpression.Create(
                    $"{database.Resource.ConnectionStringExpression};{string.Join(';', postgreSqlOverrides)}"));
            var protocolConcurrency = builder.Configuration.GetValue("Parameters:LoadMaxConcurrentRequests", useDefaultAdmissionLimits ? 8 : 16);
            var protocolConcurrencyPerSubject = builder.Configuration.GetValue("Parameters:LoadMaxConcurrentRequestsPerSubject", useDefaultAdmissionLimits ? 2 : 16);
            // Anchor configured paths before launching a process with another
            // working directory. Explicit blank settings select the library's
            // shared local default and override per-WebDemo configuration.
            const string certificateKey = "Authentication:KeyProtection:CertificatePath";
            var certificatePath = builder.Configuration[certificateKey];
            builder.Configuration[certificateKey] = string.IsNullOrWhiteSpace(certificatePath)
                ? "" : Path.GetFullPath(certificatePath);
            const string certificatePasswordKey = "Authentication:KeyProtection:Password";
            builder.Configuration[certificatePasswordKey] ??= "";
            webdemo.WithEnvironment("Authentication__KeyProtection__CertificatePath", builder.Configuration[certificateKey])
                .WithEnvironment("Authentication__KeyProtection__Password", builder.Configuration[certificatePasswordKey]);
            webdemo.WithEnvironment("Logging__LogLevel__Default", "Warning")
                // Set explicit admission limits so scenarios can compare
                // concurrent storage work with their configured accounts.
                .WithEnvironment("Protocol__MaxConcurrentRequests",
                    protocolConcurrency.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .WithEnvironment("Protocol__MaxConcurrentRequestsPerSubject",
                    protocolConcurrencyPerSubject.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .WithEnvironment("Logging__LogLevel__Microsoft.AspNetCore.Antiforgery", "Error");
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
                .WithEnvironment("Logging__LogLevel__Default", "Warning")
                .WithEnvironment("DOTNET_ENVIRONMENT", "Testing")
                .WithEnvironment("Seed__Password", appHost.AddParameter("LoadTestPassword", secret: true))
                .WithEnvironment("Seed__UserCount", userCount.ToString(System.Globalization.CultureInfo.InvariantCulture))
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
            var endpoint = application.GetEndpoint("webdemo", "https");
            var identityConnection = (await application.GetConnectionStringAsync("cellbridge", timeout.Token))!;
            if (queryStatisticsEnabled)
            {
                // This connection comes only from this fixture's disposable Aspire database.
                // An explicitly requested but unavailable extension is a setup failure.
                await using var statistics = NpgsqlDataSource.Create(identityConnection);
                await using var extension = statistics.CreateCommand("CREATE EXTENSION IF NOT EXISTS pg_stat_statements");
                await extension.ExecuteNonQueryAsync(timeout.Token);
            }
            if (userCount > 1)
                sessions = await LoadIdentitySessions.CreateAsync(identityConnection, endpoint, builder.Configuration,
                    userCount, timeout.Token);
            return new(application, builder, endpoint,
                (await application.GetConnectionStringAsync(documentConnectionName, timeout.Token))!,
                (await application.GetConnectionStringAsync("cellbridge", timeout.Token))!,
                (await application.GetConnectionStringAsync("filesystem", timeout.Token))!,
                (await application.GetConnectionStringAsync("lists", timeout.Token))!,
                (await application.GetConnectionStringAsync("lists-runtime", timeout.Token))!,
                password, backend, userCount, sessions, protocolConcurrency, protocolConcurrencyPerSubject,
                queryStatisticsEnabled, postgreSqlPoolSize, postgreSqlMaxAutoPrepare);
        }
        catch
        {
            try { if (sessions is not null) await sessions.DisposeAsync(); }
            finally
            {
                try { if (application is not null) await application.DisposeAsync(); }
                finally { await builder.DisposeAsync(); }
            }
            throw;
        }
    }

    public SubjectIdentity GetUserIdentity(int userIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(userIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(userIndex, UserCount);
        var suffix = userIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var login = userIndex == 0 ? "integration-writer" : "integration-writer-" + suffix;
        return new("local:" + login, login, userIndex == 0 ? "Integration writer" : "Integration writer " + suffix);
    }

    public Task<HttpClient> CreateClientAsync(CancellationToken cancellationToken = default) =>
        CreateClientAsync(0, cancellationToken);

    public async Task<HttpClient> CreateClientAsync(int userIndex, CancellationToken cancellationToken = default)
    {
        _ = GetUserIdentity(userIndex);
        if (sessions is null) return await CreateLoginClientAsync(userIndex, cancellationToken);
        var cookies = new CookieContainer();
        cookies.Add(HttpEndpoint, new Cookie(sessions.CookieName, sessions[userIndex], sessions.CookiePath)
        { Secure = true, HttpOnly = true });
        var client = CreateHttpClient(cookies);
        try
        {
            // The server still decrypts and validates the normal Identity ticket.
            using var accepted = await client.GetAsync("/auth/complete", cancellationToken);
            accepted.EnsureSuccessStatusCode();
            client.DefaultRequestHeaders.Add("X-CellBridge-CSRF", await LoginTokenAsync(client, cancellationToken));
            return client;
        }
        catch { client.Dispose(); throw; }
    }

    /// <summary>Measures the actual password login path when a test explicitly needs it.</summary>
    public async Task<HttpClient> CreateLoginClientAsync(int userIndex = 0, CancellationToken cancellationToken = default)
    {
        var identity = GetUserIdentity(userIndex);
        var client = CreateHttpClient(new CookieContainer());
        try
        {
            var token = await LoginTokenAsync(client, cancellationToken);
            using var login = await client.PostAsync("/auth/login", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["login"] = identity.Login, ["password"] = password,
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

    private HttpClient CreateHttpClient(CookieContainer cookies) =>
        new(new HttpClientHandler
        {
            CookieContainer = cookies,
            ServerCertificateCustomValidationCallback = (request, _, _, errors) =>
                errors == System.Net.Security.SslPolicyErrors.None || request.RequestUri?.IsLoopback == true,
        }) { BaseAddress = HttpEndpoint };

    private static async Task<string> LoginTokenAsync(HttpClient client, CancellationToken cancellationToken)
    {
        var html = await client.GetStringAsync("/auth/login", cancellationToken);
        var match = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        if (!match.Success) throw new InvalidOperationException("The login page did not issue an antiforgery token.");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    public async ValueTask DisposeAsync()
    {
        try { if (sessions is not null) await sessions.DisposeAsync(); }
        finally
        {
            try { await application.DisposeAsync(); }
            finally { await builder.DisposeAsync(); }
        }
    }
}
