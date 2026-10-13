#:package Aspire.Hosting.PostgreSQL@17.0.0-preview.1.26502.1
#:sdk Aspire.AppHost.Sdk@17.0.0-preview.1.26479.11
#:property AspireUseCliBundle=true

var builder = DistributedApplication.CreateBuilder(args);
var testDirectory = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, ".."));
var database = builder.AddPostgres("lists-postgres").AddDatabase("lists");
builder.AddExecutable("lists-tests", "dotnet", testDirectory,
        "test", "CellBridge.Lists.PostgreSql.Tests.csproj", "--no-build", "--nologo",
        "--logger", "trx", "--results-directory", "../../artifacts/lists-tests")
    .WithReference(database)
    .WithEnvironment("CELLBRIDGE_LISTS_REQUIRE_DATABASE", "true")
    .WaitFor(database)
    .WithExplicitStart();
builder.Build().Run();
