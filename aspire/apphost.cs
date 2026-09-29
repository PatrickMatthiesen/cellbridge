#:sdk Aspire.AppHost.Sdk@13.6.0-preview.1.26427.6
#:property AspireUseCliBundle=true
#:project ../src/OfficeCollabServer.Web/OfficeCollabServer.Web.csproj
#:project ../demo/OfficeCollabServer.Demo/OfficeCollabServer.Demo.csproj

var builder = DistributedApplication.CreateBuilder(args);

var web = builder.AddProject<Projects.OfficeCollabServer_Web>("web")
    .WithHttpHealthCheck("/health")
    .WithExternalHttpEndpoints();

// Desktop Office uses Windows DNS, which does not resolve *.localhost here.
// Use localhost for this machine; configure a resolvable host for remote clients.
builder.Configuration["Parameters:collab-public-url"] ??= "https://localhost:7292";
var collabPublicUrl = builder.AddParameter("collab-public-url")
    .WithDescription("Collaboration server HTTPS origin reachable by browsers and desktop Office.");

builder.AddProject<Projects.OfficeCollabServer_Demo>("demo")
    .WithEnvironment("CollabServer__BaseUrl", web.GetEndpoint("https"))
    .WithEnvironment("CollabServer__PublicBaseUrl", collabPublicUrl)
    .WithHttpHealthCheck("/health")
    .WaitFor(web)
    .WithExternalHttpEndpoints();

var captureDirectory = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "../tools/capture"));
var localUpstreamCa = Path.Combine(captureDirectory, "captures/sharepoint-preflight/upstream-cert.pem");

// Defaults for the local capture experiment; override through Parameters configuration.
builder.Configuration["Parameters:capture-upstream"] ??= "http://sharepoint.example.test:42292";
builder.Configuration["Parameters:capture-port"] ??= "8443";
builder.Configuration["Parameters:capture-output"] ??= Path.Combine(captureDirectory, "captures");
builder.Configuration["Parameters:capture-upstream-ca"] ??= File.Exists(localUpstreamCa) ? localUpstreamCa : "";

var captureUpstream = builder.AddParameter("capture-upstream")
    .WithDescription("Fixed HTTP or HTTPS SharePoint origin.");
var capturePort = builder.AddParameter("capture-port")
    .WithDescription("Local HTTPS listener port. Restart AppHost after changing this value.");
var captureOutput = builder.AddParameter("capture-output")
    .WithDescription("Local directory for raw capture evidence. Keep outside version control.");
var captureUpstreamCa = builder.AddParameter("capture-upstream-ca")
    .WithDescription("PEM certificate or CA for HTTPS upstreams; ignored for HTTP. Empty uses default roots; Windows trust is unchanged.");

var python = Path.Combine(captureDirectory,
    OperatingSystem.IsWindows() ? ".venv/Scripts/python.exe" : ".venv/bin/python");
if (!File.Exists(python))
    throw new InvalidOperationException("Run tools/capture/setup.ps1 before starting Aspire.");

// Mitmproxy owns TLS directly, avoiding another HTTP proxy hop for authentication.
#pragma warning disable ASPIRECERTIFICATES001
var capture = builder.AddExecutable("sharepoint", python, captureDirectory, "run.py")
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

builder.Build().Run();
