var builder = DistributedApplication.CreateBuilder(args);
var signingKey = builder.AddParameter("SigningKey", secret: true);
builder.AddProject<Projects.HostAuthorization>("authorization-consumer", launchProfileName: null)
    .WithEnvironment("Authentication__SigningKey", signingKey)
    .WithHttpEndpoint(name: "http")
    .WithHttpHealthCheck("/health");
builder.Build().Run();
