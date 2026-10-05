var builder = DistributedApplication.CreateBuilder(args);
var signingKey = builder.AddParameter("SigningKey", secret: true);
#pragma warning disable ASPIRECSHARPAPPS001
builder.AddCSharpApp("authorization-consumer", "../HostAuthorization.csproj")
    .WithEnvironment("Authentication__SigningKey", signingKey)
    .WithHttpEndpoint(name: "http")
    .WithHttpHealthCheck("/health");
#pragma warning restore ASPIRECSHARPAPPS001
builder.Build().Run();
