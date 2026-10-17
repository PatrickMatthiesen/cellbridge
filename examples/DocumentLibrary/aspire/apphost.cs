#:package Aspire.Hosting.PostgreSQL@17.0.0-preview.1.26502.1
#:sdk Aspire.AppHost.Sdk@17.0.0-preview.1.26479.11
#:property AspireUseCliBundle=true

using Microsoft.Extensions.Configuration;
using Aspire.Hosting.ApplicationModel;

var builder = DistributedApplication.CreateBuilder(args);
var disposable = builder.Configuration.GetValue("Testing:Enabled", false);
var postgres = builder.AddPostgres("document-library-postgres");
if (!disposable) postgres.WithDataVolume("cellbridge-document-library-postgres");
var database = postgres.AddDatabase("cellbridge");
var runtimeRole = "cellbridge_library_runtime";
var generatedRuntimePassword = Guid.NewGuid().ToString("N");
var runtimePassword = builder.AddParameter("LibraryRuntimePassword", () => generatedRuntimePassword, secret: true);
var tcp = postgres.GetEndpoint("tcp");
var runtimeConnection = ReferenceExpression.Create($"Host={tcp.Property(EndpointProperty.Host)};Port={tcp.Property(EndpointProperty.Port)};Database=cellbridge;Username={runtimeRole}");
var restoreConfigFile = builder.Configuration["DocumentLibrary:RestoreConfigFile"];
var restorePackagesPath = builder.Configuration["DocumentLibrary:RestorePackagesPath"];

#pragma warning disable ASPIRECSHARPAPPS001
var setup = builder.AddCSharpApp("document-library-setup", "../Setup/CellBridge.DocumentLibrary.Setup.csproj")
    .WithReference(database)
    .WithEnvironment("Lists__Enabled", "true")
    .WithEnvironment("Lists__RuntimeRole", runtimeRole)
    .WithEnvironment("Lists__RuntimePassword", runtimePassword)
    .WaitFor(database);
if (!string.IsNullOrWhiteSpace(restoreConfigFile))
{
    setup.WithEnvironment("RestoreConfigFile", restoreConfigFile);
}
if (!string.IsNullOrWhiteSpace(restorePackagesPath))
{
    setup.WithEnvironment("RestorePackagesPath", restorePackagesPath);
}

var destinationRoot = disposable
    ? Path.Combine(Path.GetTempPath(), "cellbridge-document-library-" + Guid.NewGuid().ToString("N"))
    : Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "../../../artifacts/document-library/destination"));

var app = builder.AddCSharpApp("document-library", "../CellBridge.DocumentLibrary.csproj")
    .WithEnvironment("ConnectionStrings__cellbridge", runtimeConnection)
    .WithEnvironment("ConnectionStrings__lists", runtimeConnection)
    .WithEnvironment("DocumentLibrary__RuntimePassword", runtimePassword)
    .WithEnvironment("Lists__RuntimePassword", runtimePassword)
    .WithEnvironment("Lists__Enabled", "true")
    .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
    .WithEnvironment("DocumentLibrary__StorageProvider", "PostgreSql")
    .WithEnvironment("DocumentLibrary__DestinationRoot", destinationRoot)
    .WaitForCompletion(setup)
    .WithHttpsEndpoint()
    .WithHttpHealthCheck("/health")
    .WithExternalHttpEndpoints();
// The collaborative browser does not trust this machine's developer certificate.
// Disposable local UI checks can use HTTP; the normal example keeps HTTPS only.
if (disposable) app.WithHttpEndpoint();
if (!string.IsNullOrWhiteSpace(restoreConfigFile))
{
    app.WithEnvironment("RestoreConfigFile", restoreConfigFile);
}
if (!string.IsNullOrWhiteSpace(restorePackagesPath))
{
    app.WithEnvironment("RestorePackagesPath", restorePackagesPath);
}
#pragma warning restore ASPIRECSHARPAPPS001

builder.Build().Run();
