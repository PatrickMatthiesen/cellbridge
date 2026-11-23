using System.Net;
using System.Text.Json;
using CellBridge.AspNetCore;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace CellBridge.Mcp.AspNetCore.Tests;

public sealed class McpOAuthDiscoveryTests
{
    private const string Issuer = "https://issuer.example/tenant";
    private const string MetadataRoot = "/.well-known/oauth-protected-resource";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("read\"write")]
    [InlineData("read\\write")]
    [InlineData("read write")]
    [InlineData("read\twrite")]
    [InlineData("read\nwrite")]
    [InlineData("read\u007F")]
    [InlineData("réad")]
    public void RejectsInvalidOAuthScopeTokensAtRegistration(string? scope)
    {
        var services = new ServiceCollection();
        Assert.Throws<ArgumentException>(nameof(CellBridgeMcpOptions.ScopesSupported),
            () => services.AddCellBridgeMcp(options => options.ScopesSupported = [McpTestHost.WorkbookScope, scope!]));
    }

    [Fact]
    public async Task BothMetadataRoutesAdvertiseTheCanonicalResourceAndScopesUnderPathBase()
    {
        const string pathBase = "/tenant";
        const string mcpPath = "/tools/workbooks";
        const string endpointPath = pathBase + mcpPath;
        const string metadataPath = pathBase + MetadataRoot + mcpPath;
        string[] scopes = [McpTestHost.WorkbookScope, "!#[]~"];
        await using var host = await McpTestHost.StartAsync(pathBase: pathBase, mcpPath: mcpPath, configure: options =>
        {
            options.AuthorizationServer = new Uri(Issuer);
            options.ScopesSupported = scopes;
            options.MapRootProtectedResourceMetadata = true;
        });
        using var http = host.CreateHttp();

        foreach (var path in new[] { metadataPath, pathBase + MetadataRoot })
        {
            using var response = await http.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var metadata = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var resource = metadata.RootElement;
            Assert.Equal(new Uri(host.Origin, endpointPath).AbsoluteUri, resource.GetProperty("resource").GetString());
            Assert.Equal(Issuer, Assert.Single(resource.GetProperty("authorization_servers").EnumerateArray()).GetString());
            Assert.Equal("header", Assert.Single(resource.GetProperty("bearer_methods_supported").EnumerateArray()).GetString());
            Assert.Equal(scopes, resource.GetProperty("scopes_supported").EnumerateArray().Select(scope => scope.GetString()));
        }

        using var challenge = await new McpHttpSession(http, endpointPath).SendAsync(McpHttpSession.InitializeMethod,
            McpHttpSession.InitializeArguments);
        Assert.Equal(HttpStatusCode.Unauthorized, challenge.StatusCode);
        var header = Assert.Single(challenge.Headers.WwwAuthenticate);
        Assert.Equal("Bearer", header.Scheme);
        var parameters = Assert.IsType<string>(header.Parameter);
        Assert.Contains($"resource_metadata=\"{new Uri(host.Origin, metadataPath).AbsoluteUri}\"", parameters);
        Assert.Contains($"scope=\"{string.Join(" ", scopes)}\"", parameters);
    }

    [Fact]
    public async Task OmitsEmptyScopesAndLeavesRootMetadataUnmappedByDefault()
    {
        const string metadataPath = MetadataRoot + "/mcp";
        await using var host = await McpTestHost.StartAsync(configure: options => options.AuthorizationServer = new Uri(Issuer));
        using var http = host.CreateHttp();
        using var alias = await http.GetAsync(MetadataRoot);
        Assert.Equal(HttpStatusCode.NotFound, alias.StatusCode);

        using var response = await http.GetAsync(metadataPath);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var metadata = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(metadata.RootElement.TryGetProperty("scopes_supported", out _));

        using var challenge = await new McpHttpSession(http).SendAsync(McpHttpSession.InitializeMethod,
            McpHttpSession.InitializeArguments);
        Assert.Equal(HttpStatusCode.Unauthorized, challenge.StatusCode);
        var header = Assert.Single(challenge.Headers.WwwAuthenticate);
        Assert.Equal("Bearer", header.Scheme);
        Assert.Equal($"resource_metadata=\"{new Uri(host.Origin, metadataPath).AbsoluteUri}\"", header.Parameter);
    }

    [Theory]
    [InlineData("/Tools", "/Tools/mcp", true)]
    [InlineData("/Tools/{tenant}", "/Tools/acme/mcp", false)]
    [InlineData("/Tools/{tenant:tenantPrefix}", "/Tools/acme/mcp", false)]
    public async Task GroupedDiscoveryUsesTheConfiguredResourcePathAndCasing(
        string group, string resourcePath, bool mapRoot)
    {
        await using var host = await McpTestHost.StartAsync(routeGroup: group, configure: options =>
        {
            options.AuthorizationServer = new Uri(Issuer);
            options.MapRootProtectedResourceMetadata = mapRoot;
        }, configureServices: services => services.Configure<RouteOptions>(options =>
            options.ConstraintMap["tenantPrefix"] = typeof(TenantPrefixTransformer)));
        using var http = host.CreateHttp();
        var requestPath = resourcePath.ToLowerInvariant();
        var paths = mapRoot ? new[] { MetadataRoot + requestPath, MetadataRoot } : [MetadataRoot + requestPath];

        foreach (var path in paths)
        {
            using var response = await http.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var metadata = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(new Uri(host.Origin, resourcePath).AbsoluteUri,
                metadata.RootElement.GetProperty("resource").GetString());
        }

        using var challenge = await new McpHttpSession(http, requestPath).SendAsync(McpHttpSession.InitializeMethod,
            McpHttpSession.InitializeArguments);
        Assert.Equal(HttpStatusCode.Unauthorized, challenge.StatusCode);
        var expectedMetadata = new Uri(host.Origin, MetadataRoot + resourcePath);
        var metadataParameter = Assert.Single(challenge.Headers.WwwAuthenticate).Parameter;
        Assert.Equal($"resource_metadata=\"{expectedMetadata.AbsoluteUri}\"", metadataParameter);
        using var advertisedMetadata = await http.GetAsync(metadataParameter!["resource_metadata=\"".Length..^1]);
        Assert.Equal(HttpStatusCode.OK, advertisedMetadata.StatusCode);
        using var advertised = JsonDocument.Parse(await advertisedMetadata.Content.ReadAsStringAsync());
        Assert.Equal(new Uri(host.Origin, resourcePath).AbsoluteUri, advertised.RootElement.GetProperty("resource").GetString());
    }

    private sealed class TenantPrefixTransformer : IOutboundParameterTransformer
    {
        public string? TransformOutbound(object? value) => value is null ? null : $"tenant-{value}";
    }

    [Fact]
    public async Task RejectsASecondEndpointClaimingRootMetadata()
    {
        await using var app = BuildAppWithRootAlias();
        app.MapCellBridgeMcp("/first");
        Assert.Throws<InvalidOperationException>(() => app.MapCellBridgeMcp("/second"));
    }

    [Fact]
    public async Task RejectsAnAliasThatWouldOverlapTheGroupedTransport()
    {
        await using var app = BuildAppWithRootAlias();
        app.MapGroup("/.well-known").MapCellBridgeMcp("/OAuth-Protected-Resource");
        var routes = (IEndpointRouteBuilder)app;
        var error = Assert.Throws<InvalidOperationException>(() => routes.DataSources.SelectMany(source => source.Endpoints).ToArray());
        Assert.Contains("require different paths", error.Message);
    }

    private static WebApplication BuildAppWithRootAlias()
    {
        var builder = WebApplication.CreateSlimBuilder();
        var content = new InMemoryContentStore();
        builder.Services.AddCellBridge(new StorageProvider(new InMemoryDocumentRepository(content), content),
            requireDurability: false);
        builder.Services.AddCellBridgeMcp(options =>
        {
            options.AuthorizationServer = new Uri(Issuer);
            options.MapRootProtectedResourceMetadata = true;
        });
        return builder.Build();
    }
}
