using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Dotnet;
using Aspire.Hosting.Testing;
using CellBridge.TestInfrastructure;

namespace CellBridge.Benchmarks.Tests;

public sealed class FixtureAuthenticationIsolationTests
{
    [Fact]
    public async Task TestingAppHostDoesNotRegisterOperatorEntraCredentialsBeforeFixtureOverrides()
    {
        await using var builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.CellBridge_AppHost>(
            ["--environment", "Testing", "--Parameters:Entra:Enabled=true",
                "--Parameters:EntraClientSecret=operator-secret-canary",
                "--Parameters:Entra:TenantId=11111111-1111-1111-1111-111111111111",
                "--Parameters:Entra:ClientId=22222222-2222-2222-2222-222222222222"]);
        Assert.Equal("true", builder.Configuration["Parameters:Entra:Enabled"]);
        Assert.DoesNotContain(builder.Resources, resource =>
            resource.Name.Equals("EntraClientSecret", StringComparison.OrdinalIgnoreCase));
        var host = builder.CreateResourceBuilder<DotnetProjectResource>("webdemo");
        var variables = new Dictionary<string, object>();
        var context = new EnvironmentCallbackContext(builder.ExecutionContext, host.Resource, variables);
        foreach (var callback in host.Resource.Annotations.OfType<EnvironmentCallbackAnnotation>())
            await callback.Callback(context);

        Assert.DoesNotContain("Authentication__Entra__Enabled", variables.Keys);
        Assert.DoesNotContain("Authentication__Entra__ClientSecret", variables.Keys);
        Assert.DoesNotContain(variables.Values.OfType<string>(), value => value.Contains("operator-secret-canary", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LocalFixtureOverridesOperatorAuthenticationOriginAndIngressWithoutStartingResources()
    {
        await using var builder = await PostgreSqlEnvironment.CreateBuilderAsync();
        Assert.Equal("false", builder.Configuration["Authentication:Entra:Enabled"]);
        var host = builder.CreateResourceBuilder<DotnetProjectResource>("webdemo");
        host.WithEnvironment("Authentication__Entra__Enabled", "true")
            .WithEnvironment("Authentication__Entra__ClientSecret", "operator-secret-canary")
            .WithEnvironment("Authentication__PublicOrigin", "https://operator.example")
            .WithEnvironment("Protocol__PublicOrigin", "https://operator.example")
            .WithEnvironment("Protocol__CaptureDirectory", "operator-capture-canary")
            .WithEndpoint("http", endpoint => endpoint.Port = 42233)
            .WithEndpoint("https", endpoint => endpoint.Port = 42234);

        PostgreSqlEnvironment.ConfigureLocalWebDemo(host);
        var variables = new Dictionary<string, object>();
        var context = new EnvironmentCallbackContext(builder.ExecutionContext, host.Resource, variables);
        foreach (var callback in host.Resource.Annotations.OfType<EnvironmentCallbackAnnotation>())
            await callback.Callback(context);

        Assert.Equal("false", variables["Authentication__Entra__Enabled"]);
        Assert.Equal("", variables["Authentication__Entra__ClientSecret"]);
        Assert.Equal("", variables["Authentication__Entra__TenantId"]);
        Assert.Equal("", variables["Authentication__Entra__ClientId"]);
        Assert.Equal("false", variables["Authentication__AllowOAuthToFormsFallback"]);
        Assert.Equal("", variables["Protocol__CaptureDirectory"]);
        Assert.IsAssignableFrom<IValueProvider>(variables["Authentication__PublicOrigin"]);
        Assert.IsAssignableFrom<IValueProvider>(variables["Protocol__PublicOrigin"]);
        Assert.Null(host.GetEndpoint("http").EndpointAnnotation.Port);
        Assert.Null(host.GetEndpoint("https").EndpointAnnotation.Port);
        Assert.DoesNotContain(variables.Values.OfType<string>(), value => value.Contains("canary", StringComparison.Ordinal));
    }
}
