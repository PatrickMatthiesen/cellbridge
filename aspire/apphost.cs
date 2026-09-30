#:package Aspire.Hosting.Python@17.0.0-preview.1.26479.11
#:sdk Aspire.AppHost.Sdk@17.0.0-preview.1.26479.11
#:property AspireUseCliBundle=true

var builder = DistributedApplication.CreateBuilder(args);

#pragma warning disable ASPIRECSHARPAPPS001
var web = builder.AddCSharpApp("web", "../src/OfficeCollabServer.Web/OfficeCollabServer.Web.csproj")
    .WithHttpHealthCheck("/health")
    .WithExternalHttpEndpoints();

// Desktop Office uses Windows DNS, which does not resolve *.localhost here.
// Use localhost for this machine; configure a resolvable host for remote clients.
var collabPublicUrl = builder.AddParameter("collab-public-url", "https://localhost:7292")
    .WithDescription("Collaboration server HTTPS origin reachable by browsers and desktop Office.");

builder.AddCSharpApp("demo", "../demo/OfficeCollabServer.Demo/OfficeCollabServer.Demo.csproj")
    .WithEnvironment("CollabServer__BaseUrl", web.GetEndpoint("https"))
    .WithEnvironment("CollabServer__PublicBaseUrl", collabPublicUrl)
    .WithHttpHealthCheck("/health")
    .WaitFor(web)
    .WithExternalHttpEndpoints();

#pragma warning restore ASPIRECSHARPAPPS001

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

builder.Build().Run();
