#:package Aspire.Hosting.PostgreSQL@17.0.0-preview.1.26502.1
#:package Aspire.Hosting.Python@17.0.0-preview.1.26479.11
#:sdk Aspire.AppHost.Sdk@17.0.0-preview.1.26479.11
#:property AspireUseCliBundle=true

using Microsoft.Extensions.Configuration;

var builder = DistributedApplication.CreateBuilder(args);

// Automated runs get a disposable database, never the developer's document volume.
var testRun = Environment.GetEnvironmentVariable("CELLBRIDGE_TEST_RUN") == "1";
// Keep desktop capture configuration across shell exits and machine restarts.
// Disposable automated runs must not inherit the developer's capture directory.
if (!testRun)
    builder.Configuration.AddJsonFile(Path.Combine(builder.AppHostDirectory, "apphost.settings.json"), optional: true);
var postgres = builder.AddPostgres("storage");
// A separate durable volume lets a laptop auth trial coexist with another checkout.
if (!testRun) postgres.WithDataVolume(
    Environment.GetEnvironmentVariable("CELLBRIDGE_STORAGE_VOLUME") ?? "cellbridge-storage-data");
var database = postgres.AddDatabase("cellbridge");
var legacyOwner = Environment.GetEnvironmentVariable("CELLBRIDGE_LEGACY_OWNER");
var importOwner = Environment.GetEnvironmentVariable("CELLBRIDGE_IMPORT_OWNER");
if (testRun) importOwner = "local:integration-writer";

#pragma warning disable ASPIRECSHARPAPPS001
var migration = builder.AddCSharpApp("storage-migration", "../tools/CellBridge.Storage.Migrate/CellBridge.Storage.Migrate.csproj")
    .WithReference(database)
    .WaitFor(database);
if (!string.IsNullOrWhiteSpace(legacyOwner)) migration.WithEnvironment("Authentication__LegacyOwner", legacyOwner);
#pragma warning restore ASPIRECSHARPAPPS001

IResourceBuilder<ExecutableResource>? testAccount = null;
if (testRun)
{
    var password = Environment.GetEnvironmentVariable("CELLBRIDGE_TEST_PASSWORD")
        ?? throw new InvalidOperationException("Disposable test runs require CELLBRIDGE_TEST_PASSWORD.");
    testAccount = builder.AddExecutable("test-account", OperatingSystem.IsWindows() ? "python" : "python3", "..", "tools/testing/bootstrap.py")
        .WithReference(database)
        .WithEnvironment("CELLBRIDGE_TEST_PASSWORD", password)
        .WaitForCompletion(migration);
}

var collabPublicUrl = builder.AddParameter("collab-public-url",
    Environment.GetEnvironmentVariable("CELLBRIDGE_PUBLIC_ORIGIN") ?? "https://localhost:7292")
    .WithDescription("Collaboration server HTTPS origin reachable by browsers and desktop Office.");

#pragma warning disable ASPIRECSHARPAPPS001
var web = builder.AddCSharpApp("web", "../src/CellBridge.Web/CellBridge.Web.csproj")
    .WithReference(database)
    .WithEnvironment("Storage__Provider", "PostgreSql")
    .WithEnvironment("Authentication__PublicOrigin", collabPublicUrl)
    .WaitForCompletion(migration)
    .WithHttpHealthCheck("/health")
    .WithExternalHttpEndpoints();

if (!string.IsNullOrWhiteSpace(importOwner)) web.WithEnvironment("Authentication__ImportOwner", importOwner);
if (testAccount is not null) web.WaitForCompletion(testAccount);

var wireCapture = Environment.GetEnvironmentVariable("CELLBRIDGE_WIRE_CAPTURE")
    ?? builder.Configuration["Protocol:CaptureDirectory"];
if (!string.IsNullOrWhiteSpace(wireCapture))
    web.WithEnvironment("Protocol__CaptureDirectory", Path.GetFullPath(wireCapture));

// Opt-in second independent host for shared-storage/lease routing tests.
if (Environment.GetEnvironmentVariable("CELLBRIDGE_RUN_TWO_INSTANCES") == "1")
{
    builder.AddCSharpApp("web-peer", "../src/CellBridge.Web/CellBridge.Web.csproj")
        .WithReference(database)
        .WithEnvironment("Storage__Provider", "PostgreSql")
        .WithEnvironment("Storage__MultipleInstances", "true")
        .WithEnvironment("Authentication__PublicOrigin", collabPublicUrl)
        .WithEndpoint("http", endpoint => endpoint.Port = 5182)
        .WithEndpoint("https", endpoint => endpoint.Port = 7293)
        .WaitForCompletion(migration)
        .WithHttpHealthCheck("/health")
        .WithExternalHttpEndpoints();
}

// Optional integration suite. Builds must finish before starting Aspire.
if (Environment.GetEnvironmentVariable("CELLBRIDGE_RUN_STORAGE_TESTS") == "1")
{
    builder.AddExecutable("storage-tests", "dotnet", "..", "test", "tests/CellBridge.Storage.Tests", "--no-build", "--nologo", "--verbosity", "minimal")
        .WithReference(database)
        .WaitForCompletion(migration);
}

// Both Office and the library use the public web origin.
var demo = builder.AddCSharpApp("demo", "../demo/CellBridge.Demo/CellBridge.Demo.csproj")
    .WithReference(database)
    .WithEnvironment("Authentication__PublicOrigin", collabPublicUrl)
    .WithEnvironment("CollabServer__BaseUrl", web.GetEndpoint("https"))
    .WithEnvironment("CollabServer__PublicBaseUrl", collabPublicUrl)
    .WithHttpHealthCheck("/health")
    .WaitFor(web);
web.WithEnvironment("Demo__BaseUrl", demo.GetEndpoint("http"));

#pragma warning restore ASPIRECSHARPAPPS001

if (!testRun && Environment.GetEnvironmentVariable("CELLBRIDGE_SKIP_SHAREPOINT_CAPTURE") != "1")
{
var captureDirectory = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "../tools/capture"));
var localUpstreamCa = Path.Combine(captureDirectory, "captures/sharepoint-preflight/upstream-cert.pem");

// Values for the local capture experiment.
var captureUpstream = builder.AddParameter("capture-upstream", "http://sharepoint.example.test:42292")
    .WithDescription("Fixed HTTP or HTTPS SharePoint origin.");
var capturePort = builder.AddParameter("capture-port", "8443")
    .WithDescription("Local HTTPS listener port. Restart AppHost after changing this value.");
var captureOutput = builder.AddParameter("capture-output", Path.Combine(captureDirectory, "captures"))
    .WithDescription("Local directory for raw capture evidence. Keep outside version control.");
var captureUpstreamCa = builder.AddParameter("capture-upstream-ca", File.Exists(localUpstreamCa) ? localUpstreamCa : "")
    .WithDescription("PEM certificate or CA for HTTPS upstreams; ignored for HTTP. Empty uses default roots; Windows trust is unchanged.");

// Mitmproxy owns TLS directly, avoiding another HTTP proxy hop for authentication.
#pragma warning disable ASPIRECERTIFICATES001
var capture = builder.AddPythonApp("sharepoint", captureDirectory, "run.py")
    .WithPip()
    .WithEnvironment("CAPTURE_REVERSE_UPSTREAM", captureUpstream)
    .WithEnvironment("CAPTURE_PORT", capturePort)
    .WithEnvironment("CAPTURE_OUTPUT_ROOT", captureOutput)
    .WithHttpsEndpoint(port: int.Parse((await capturePort.Resource.GetValueAsync(default))!),
        name: "https", isProxied: false)
    .WithEndpoint("https", endpoint => endpoint.TargetHost = "sharepoint.dev.localhost")
    .WithHttpsDeveloperCertificate()
    .WithHttpsCertificateConfiguration(ctx =>
    {
        ctx.EnvironmentVariables["CAPTURE_TLS_CERT"] = ctx.CertificatePath;
        ctx.EnvironmentVariables["CAPTURE_TLS_KEY"] = ctx.KeyPath;
        return Task.CompletedTask;
    });
#pragma warning restore ASPIRECERTIFICATES001
if (!string.IsNullOrWhiteSpace(await captureUpstreamCa.Resource.GetValueAsync(default)))
    capture.WithEnvironment("CAPTURE_UPSTREAM_CA", captureUpstreamCa);
}

builder.Build().Run();
