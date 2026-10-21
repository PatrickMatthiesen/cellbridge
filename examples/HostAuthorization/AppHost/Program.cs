var builder = DistributedApplication.CreateBuilder(args);
var signingKey = builder.AddParameter("SigningKey", secret: true);
builder.AddDotnetProject("authorization-consumer", "../HostAuthorization.csproj", options => options.ExcludeLaunchProfile = true)
    .WithEnvironment("Authentication__SigningKey", signingKey)
    .WithHttpEndpoint(name: "http")
    .WithHttpHealthCheck("/health");
builder.Build().Run();
