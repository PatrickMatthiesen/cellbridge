using Aspire.Hosting.ApplicationModel;
using Npgsql;

var builder = DistributedApplication.CreateBuilder(args);
var postgres = builder.AddPostgres("document-library-postgres")
    .WithDataVolume("cellbridge-document-library-postgres");
var database = postgres.AddDatabase("cellbridge");
const string runtimeRole = "cellbridge_library_runtime";
var generatedRuntimePassword = Guid.NewGuid().ToString("N");
var runtimePassword = builder.AddParameter("LibraryRuntimePassword", () => generatedRuntimePassword, secret: true);

var setup = builder.AddDotnetProject("document-library-setup", "../Setup/CellBridge.DocumentLibrary.Setup.csproj")
    .WithReference(database)
    .WithEnvironment("Lists__Enabled", "true")
    .WithEnvironment("Lists__RuntimeRole", runtimeRole)
    .WithEnvironment("Lists__RuntimePassword", runtimePassword)
    .WaitFor(database);

var destinationRoot = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "../../../artifacts/document-library/destination"));
builder.AddDotnetProject("document-library", "../CellBridge.DocumentLibrary.csproj", options => options.ExcludeLaunchProfile = true)
    .WithReference(database)
    .WithReference(database, connectionName: "lists")
    .WithEnvironment(async context =>
    {
        // Resolve Aspire's connection before replacing only the credentials.
        // The web process receives its restricted role, never setup authority.
        var connectionString = await ((IResourceWithConnectionString)database.Resource).GetConnectionStringAsync(context.CancellationToken)
            ?? throw new InvalidOperationException("The document database connection is unavailable.");
        var password = await runtimePassword.Resource.GetValueAsync(context.CancellationToken);
        var runtimeConnection = new NpgsqlConnectionStringBuilder(connectionString)
        { Username = runtimeRole, Password = password }.ConnectionString;
        context.EnvironmentVariables["ConnectionStrings__cellbridge"] = runtimeConnection;
        context.EnvironmentVariables["ConnectionStrings__lists"] = runtimeConnection;
    })
    .WithEnvironment("Lists__Enabled", "true")
    .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
    .WithEnvironment("DocumentLibrary__StorageProvider", "PostgreSql")
    .WithEnvironment("DocumentLibrary__DestinationRoot", destinationRoot)
    .WithEnvironment("DocumentLibrary__AuthorizationCache__Enabled",
        builder.Configuration["DocumentLibrary:AuthorizationCache:Enabled"] ?? "false")
    .WithEnvironment("DocumentLibrary__AuthorizationCache__Capacity",
        builder.Configuration["DocumentLibrary:AuthorizationCache:Capacity"] ?? "1024")
    .WithEnvironment("DocumentLibrary__AuthorizationCache__DecisionTtl",
        builder.Configuration["DocumentLibrary:AuthorizationCache:DecisionTtl"] ?? "00:00:10")
    .WaitForCompletion(setup)
    .WithHttpsEndpoint()
    .WithHttpHealthCheck("/health")
    .WithExternalHttpEndpoints();

builder.Build().Run();
