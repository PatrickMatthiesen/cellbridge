using Microsoft.Extensions.Configuration;

var builder = DistributedApplication.CreateBuilder(args);
builder.Configuration.AddUserSecrets<Program>()
    .AddJsonFile(Path.Combine(builder.AppHostDirectory, "apphost.settings.json"), optional: true)
    .AddEnvironmentVariables();

var repositoryDirectory = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "../.."));
var temporaryDirectory = Path.GetFullPath(
    builder.Configuration["Parameters:TemporaryDirectory"] ?? "artifacts/tmp", repositoryDirectory);
if (OperatingSystem.IsWindows()) Directory.CreateDirectory(temporaryDirectory);
else Directory.CreateDirectory(temporaryDirectory,
    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

// Keep the existing development database when changing AppHost project paths.
// Concurrent checkouts can select their own volume; tests remove this mount.
var postgres = builder.AddPostgres("storage")
    .WithDataVolume(builder.Configuration["Parameters:StorageVolume"] ?? "cellbridge-storage-data");
var database = postgres.AddDatabase("cellbridge");
var initialization = builder.AddDotnetProject("storage-init", "../../tools/CellBridge.Storage.Setup/CellBridge.Storage.Setup.csproj")
    .WithEnvironment("TMPDIR", temporaryDirectory)
    .WithEnvironment("TMP", temporaryDirectory)
    .WithEnvironment("TEMP", temporaryDirectory)
    .WithReference(database)
    .WaitFor(database);

var webdemo = builder.AddDotnetProject("webdemo", "../CellBridge.WebDemo/CellBridge.WebDemo.csproj",
        options => options.ExcludeLaunchProfile = true)
    .WithHttpEndpoint()
    .WithHttpsEndpoint()
    .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
    .WithReference(database)
    .WithEnvironment("Storage__Provider", "PostgreSql")
    .WithEnvironment("TMPDIR", temporaryDirectory)
    .WithEnvironment("TMP", temporaryDirectory)
    .WithEnvironment("TEMP", temporaryDirectory)
    .WaitForCompletion(initialization)
    .WithHttpHealthCheck("/health", endpointName: "http")
    .WithExternalHttpEndpoints();

// A remote Office client may require a different externally supplied origin.
// Local runs use the allocated endpoint without constructing a URL or port.
webdemo.WithEnvironment(context =>
    context.EnvironmentVariables["Authentication__PublicOrigin"] =
        builder.Configuration["Parameters:PublicOrigin"] is { Length: > 0 } origin
            ? origin : webdemo.GetEndpoint("https"));
webdemo.WithEnvironment("Authentication__ApplicationName", builder.AddParameter("ApplicationName",
    () => builder.Configuration["Parameters:ApplicationName"] ?? "CellBridge"));
webdemo.WithEnvironment("Protocol__CaptureDirectory", builder.Configuration["Parameters:WireCaptureDirectory"] ?? "");
webdemo.WithEnvironment("Protocol__EnableOfficeRealTime", builder.Configuration["Parameters:EnableOfficeRealTime"] ?? "true");
if (builder.Configuration["Parameters:OfficeRealTimeTokenLifetime"] is { Length: > 0 } rtcLifetime)
    webdemo.WithEnvironment("Protocol__OfficeRealTimeTokenLifetime", rtcLifetime);
if (builder.Configuration["Parameters:DocumentLibraryId"] is { Length: > 0 } libraryId)
    webdemo.WithEnvironment("Protocol__DocumentLibraryId", libraryId);

// Capture is available on demand and keeps mitmproxy's direct TLS listener.
// Python is required by mitmproxy, rather than used to own .NET app lifecycles.
var captureDirectory = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "../../tools/capture"));
var captureUpstream = builder.AddParameter("CaptureUpstream",
    () => builder.Configuration["Parameters:CaptureUpstream"] ?? "http://sharepoint.example.test:42292");
var captureOutput = builder.AddParameter("CaptureOutput",
    () => builder.Configuration["Parameters:CaptureOutput"] ?? Path.Combine(captureDirectory, "captures"));
var captureUpstreamCa = builder.Configuration["Parameters:CaptureUpstreamCa"];
#pragma warning disable ASPIRECERTIFICATES001
builder.AddPythonApp("sharepoint", captureDirectory, "run.py")
    .WithPip()
    .WithExplicitStart()
    .WithEnvironment("CAPTURE_REVERSE_UPSTREAM", captureUpstream)
    .WithEnvironment("CAPTURE_OUTPUT_ROOT", captureOutput)
    .WithEnvironment(context =>
    {
        if (!string.IsNullOrWhiteSpace(captureUpstreamCa))
            context.EnvironmentVariables["CAPTURE_UPSTREAM_CA"] = captureUpstreamCa;
    })
    .WithHttpsEndpoint(name: "https", env: "CAPTURE_PORT", isProxied: false)
    .WithEndpoint("https", endpoint => endpoint.TargetHost = "sharepoint.dev.localhost")
    .WithHttpsDeveloperCertificate()
    .WithHttpsCertificateConfiguration(context =>
    {
        context.EnvironmentVariables["CAPTURE_TLS_CERT"] = context.CertificatePath;
        context.EnvironmentVariables["CAPTURE_TLS_KEY"] = context.KeyPath;
        return Task.CompletedTask;
    });
#pragma warning restore ASPIRECERTIFICATES001

builder.Build().Run();
