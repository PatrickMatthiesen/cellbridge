#:package Aspire.Hosting.PostgreSQL@17.0.0-preview.1.26502.1
#:package Aspire.Hosting.Python@17.0.0-preview.1.26479.11
#:sdk Aspire.AppHost.Sdk@17.0.0-preview.1.26479.11
#:property AspireUseCliBundle=true

using Microsoft.Extensions.Configuration;

var builder = DistributedApplication.CreateBuilder(args);

// Automated runs get a disposable database, never the developer's document volume.
var testRun = builder.Configuration.GetValue("Testing:Enabled", false);
// Keep desktop capture configuration across shell exits and machine restarts.
// Disposable automated runs must not inherit the developer's capture directory.
if (!testRun)
    builder.Configuration.AddJsonFile(Path.Combine(builder.AppHostDirectory, "apphost.settings.json"), optional: true)
        .AddEnvironmentVariables();

// Local defaults still honor configuration/user secrets in Parameters:<name>.
IResourceBuilder<ParameterResource> Parameter(string name, string defaultValue) =>
    builder.AddParameter(name, () => builder.Configuration[$"Parameters:{name}"] ?? defaultValue);
var postgres = builder.AddPostgres("storage");
// A separate durable volume lets a laptop auth trial coexist with another checkout.
if (!testRun)
{
    var storageVolume = Parameter("StorageVolume", "cellbridge-storage-data")
        .WithDescription("Persistent PostgreSQL volume. Use a different name for each concurrent trial.");
    postgres.WithDataVolume((await storageVolume.Resource.GetValueAsync(default))!);
}
var database = postgres.AddDatabase("cellbridge");
var legacyOwner = Parameter("LegacyOwner", "")
    .WithDescription("Optional subject for upgrading anonymous document state. Empty for fresh/already migrated databases.");
var importOwner = Parameter("ImportOwner", testRun ? "local:integration-writer" : "")
    .WithDescription("Optional owner of sample startup imports. Ordinary authenticated creation needs no configured owner.");

#pragma warning disable ASPIRECSHARPAPPS001
var migration = builder.AddCSharpApp("storage-migration", "../tools/CellBridge.Storage.Migrate/CellBridge.Storage.Migrate.csproj")
    .WithReference(database)
    .WithEnvironment("Authentication__LegacyOwner", legacyOwner)
    .WaitFor(database);
#pragma warning restore ASPIRECSHARPAPPS001

IResourceBuilder<ProjectResource>? testAccount = null;
if (testRun)
{
    var password = builder.AddParameter("TestPassword", secret: true)
        .WithDescription("Temporary password for the disposable integration account.");
#pragma warning disable ASPIRECSHARPAPPS001
    testAccount = builder.AddCSharpApp("test-account", "../tools/CellBridge.Admin/CellBridge.Admin.csproj")
        .WithArgs("seed-test-user")
        .WithReference(database)
        .WithEnvironment("DOTNET_ENVIRONMENT", "Testing")
        .WithEnvironment("Seed__Password", password)
        .WaitForCompletion(migration)
        .WithExplicitStart();
#pragma warning restore ASPIRECSHARPAPPS001
}

var collabPublicUrl = Parameter("PublicOrigin", "https://localhost:7292")
    .WithDescription("Collaboration server HTTPS origin reachable by browsers and desktop Office.");

#pragma warning disable ASPIRECSHARPAPPS001
var web = builder.AddCSharpApp("web", "../src/CellBridge.Web/CellBridge.Web.csproj")
    .WithReference(database)
    .WithEnvironment("Storage__Provider", "PostgreSql")
    .WithEnvironment("Authentication__PublicOrigin", collabPublicUrl)
    .WithEnvironment("Authentication__ImportOwner", importOwner)
    .WaitForCompletion(migration)
    .WithHttpHealthCheck("/health")
    .WithExternalHttpEndpoints();

if (testAccount is not null) web.WaitForCompletion(testAccount);

var wireCapture = Parameter("WireCaptureDirectory", "")
    .WithDescription("Optional absolute directory for server request/response evidence. Empty disables capture.");
web.WithEnvironment("Protocol__CaptureDirectory", wireCapture);

// Opt-in second independent host for shared-storage/lease routing tests.
if (testRun || builder.Configuration.GetValue("AppHost:PeerEnabled", false))
{
    var peer = builder.AddCSharpApp("web-peer", "../src/CellBridge.Web/CellBridge.Web.csproj")
        .WithReference(database)
        .WithEnvironment("Storage__Provider", "PostgreSql")
        .WithEnvironment("Storage__MultipleInstances", "true")
        .WithEnvironment("Authentication__PublicOrigin", collabPublicUrl)
        .WithEndpoint("http", endpoint => endpoint.Port = null)
        .WithEndpoint("https", endpoint => endpoint.Port = null)
        .WaitForCompletion(migration)
        .WithHttpHealthCheck("/health")
        .WithExternalHttpEndpoints()
        .WithExplicitStart();
    if (testAccount is not null) peer.WaitForCompletion(testAccount);
}

// Optional integration suite. Builds must finish before starting Aspire.
if (builder.Configuration.GetValue("Testing:RunStorageTests", false))
{
    builder.AddExecutable("storage-tests", "dotnet", "..", "test", "tests/CellBridge.Storage.Tests", "--no-build", "--nologo", "--verbosity", "minimal")
        .WithReference(database)
        .WaitForCompletion(migration)
        .WithExplicitStart();
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

if (!testRun)
{
    var captureDirectory = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "../tools/capture"));
    var localUpstreamCa = Path.Combine(captureDirectory, "captures/sharepoint-preflight/upstream-cert.pem");

    // Values for the local capture experiment.
    var captureUpstream = Parameter("CaptureUpstream", "http://sharepoint.example.test:42292")
        .WithDescription("Fixed HTTP or HTTPS SharePoint origin.");
    var capturePort = Parameter("CapturePort", "8443")
        .WithDescription("Local HTTPS listener port. Restart AppHost after changing this value.");
    var captureOutput = Parameter("CaptureOutput", Path.Combine(captureDirectory, "captures"))
        .WithDescription("Local directory for raw capture evidence. Keep outside version control.");
    var captureUpstreamCa = Parameter("CaptureUpstreamCa", File.Exists(localUpstreamCa) ? localUpstreamCa : "")
        .WithDescription("PEM certificate or CA for HTTPS upstreams; ignored for HTTP. Empty uses default roots; Windows trust is unchanged.");

    // Mitmproxy owns TLS directly, avoiding another HTTP proxy hop for authentication.
    #pragma warning disable ASPIRECERTIFICATES001
    var capture = builder.AddPythonApp("sharepoint", captureDirectory, "run.py")
        .WithPip()
        .WithExplicitStart()
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
