#:package Aspire.Hosting.PostgreSQL@17.0.0-preview.1.26502.1
#:sdk Aspire.AppHost.Sdk@17.0.0-preview.1.26479.11
#:property AspireUseCliBundle=true

using Microsoft.Extensions.Configuration;

var builder = DistributedApplication.CreateBuilder(args);
var disposable = builder.Configuration.GetValue("Testing:Enabled", false);
var postgres = builder.AddPostgres("document-library-postgres");
if (!disposable) postgres.WithDataVolume("cellbridge-document-library-postgres");
var database = postgres.AddDatabase("cellbridge");
var restoreConfigFile = builder.Configuration["DocumentLibrary:RestoreConfigFile"];
var restorePackagesPath = builder.Configuration["DocumentLibrary:RestorePackagesPath"];

#pragma warning disable ASPIRECSHARPAPPS001
var setup = builder.AddCSharpApp("document-library-setup", "../Setup/CellBridge.DocumentLibrary.Setup.csproj")
    .WithReference(database)
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
    .WithReference(database)
    .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
    .WithEnvironment("DocumentLibrary__StorageProvider", "PostgreSql")
    .WithEnvironment("DocumentLibrary__DestinationRoot", destinationRoot)
    .WaitForCompletion(setup)
    .WithHttpsEndpoint()
    .WithHttpHealthCheck("/health")
    .WithExternalHttpEndpoints();
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
