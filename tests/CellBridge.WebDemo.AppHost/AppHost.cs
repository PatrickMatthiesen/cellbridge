var builder = DistributedApplication.CreateBuilder(args);
var password = builder.AddParameter("TestPassword", secret: true);
var database = builder.AddPostgres("test-storage").WithImageTag("18.3").AddDatabase("cellbridge");
var initialization = builder.AddProject<Projects.CellBridge_Storage_Setup>("storage-init")
    .WithReference(database).WaitFor(database);
var account = builder.AddProject<Projects.CellBridge_Admin>("test-account")
    .WithArgs("seed-test-user")
    .WithEnvironment("DOTNET_ENVIRONMENT", "Testing")
    .WithEnvironment("Seed__Password", password)
    .WithReference(database).WaitForCompletion(initialization);
foreach (var name in new[] { "webdemo", "webdemo-peer" })
{
    builder.AddProject<Projects.CellBridge_WebDemo>(name, options => options.LaunchProfileName = null)
        .WithHttpEndpoint(name: "http")
        .WithHttpsEndpoint(name: "https")
        .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
        .WithEnvironment("Storage__Provider", "PostgreSql")
        .WithEnvironment("Storage__MultipleInstances", "true")
        .WithEnvironment("Authentication__PublicOrigin", "https://cellbridge.test")
        .WithEnvironment("Protocol__PublicOrigin", "https://cellbridge.test")
        .WithEnvironment("Protocol__MaxConcurrentRequests", "32")
        .WithEnvironment("Protocol__MaxConcurrentRequestsPerSubject", "32")
        .WithHttpHealthCheck("/health")
        .WithReference(database).WaitForCompletion(account);
}
builder.Build().Run();
