using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using CellBridge.AspNetCore;
using CellBridge.Mcp.AspNetCore;
using CellBridge.Storage.InMemory;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace CellBridge.WebDemo.Tests;

public sealed class McpIntegrationTests
{
    private const string Origin = "https://demo.example.test";
    private const string McpPath = "/mcp";
    private const string RootMetadataPath = "/.well-known/oauth-protected-resource";
    private const string MetadataPath = RootMetadataPath + McpPath;
    private const string TenantId = "6a51fd7e-2f82-4591-94b6-5dd6a388232f";
    private const string OfficeClientId = "283a622d-b301-45bf-8b9b-a3e5cce7e393";
    private const string McpClientId = "449bc6a1-79b9-4f88-8a78-361af05d1936";
    private const string ObjectId = "9d9bc3dd-708a-4265-a458-95e17ea9ec80";
    private const string Issuer = "https://login.microsoftonline.com/" + TenantId + "/v2.0";
    private const string AuthorizationServer = "https://login.microsoftonline.com/" + TenantId + "/oauth21/2026-08";
    private const string DefaultScope = "mcp.access";
    private const string DefaultAdvertisedScope = Origin + McpPath + "/" + DefaultScope;
    private const string RequiredScope = "workbooks.access";
    private const string AdvertisedScope = Origin + McpPath + "/" + RequiredScope;
    private const string KeyId = "webdemo-test-key";

    [Fact]
    public async Task UnconfiguredMcpReturnsUnavailableWithoutChallengeOrDiscovery()
    {
        using var key = RSA.Create(2048);
        await using var app = BuildApp(configureEntra: false, signingKey: key);
        await app.StartAsync();
        using var client = app.GetTestClient();

        using var health = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        using var get = await client.GetAsync(McpPath);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, get.StatusCode);
        using var initialize = await InitializeAsync(client, CreateToken(key, McpClientId, RequiredScope));
        await AssertUnavailableAsync(app, client, initialize, "Authentication:Entra:Enabled");
    }

    [Theory]
    [InlineData("Authentication:Entra:Enabled", "false", "Authentication:Entra:Enabled")]
    [InlineData("Authentication:Entra:TenantId", null, "Authentication:Entra:TenantId")]
    [InlineData("Authentication:Entra:ClientId", null, "Authentication:Entra:ClientId")]
    [InlineData("Mcp:ClientId", "", "Mcp:ClientId")]
    [InlineData("Mcp:AuthorizationServer", "http://example.test", "Mcp:AuthorizationServer")]
    [InlineData("Mcp:RequiredScope", "scope with spaces", "Mcp:RequiredScope")]
    [InlineData("Mcp:ScopesSupported:0", "https://example.test/mcp/scope with spaces", "Mcp:ScopesSupported")]
    public async Task IncompleteOrInvalidSettingsKeepMcpUnavailable(string key, string? value, string diagnostic)
    {
        await using var app = BuildApp(configure: settings => settings[key] = value);
        await app.StartAsync();
        using var client = app.GetTestClient();

        using var initialize = await InitializeAsync(client);
        await AssertUnavailableAsync(app, client, initialize, diagnostic);
    }

    private static async Task AssertUnavailableAsync(WebApplication app, HttpClient client,
        HttpResponseMessage initialize, string diagnostic)
    {
        Assert.Null(app.Services.GetService<CellBridgeMcpOptions>());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, initialize.StatusCode);
        Assert.Empty(initialize.Headers.WwwAuthenticate);
        using var problem = JsonDocument.Parse(await initialize.Content.ReadAsStringAsync());
        Assert.Equal("MCP is not configured.", problem.RootElement.GetProperty("title").GetString());
        Assert.Contains(diagnostic, problem.RootElement.GetProperty("detail").GetString());
        using var rootMetadata = await client.GetAsync(RootMetadataPath);
        Assert.Equal(HttpStatusCode.NotFound, rootMetadata.StatusCode);
        using var metadata = await client.GetAsync(MetadataPath);
        Assert.Equal(HttpStatusCode.NotFound, metadata.StatusCode);
    }

    [Fact]
    public async Task EntraDefaultsAdvertiseMcpDiscoveryAndScopeWithoutMcpSettings()
    {
        await using var app = BuildApp();
        await app.StartAsync();
        using var client = app.GetTestClient();
        await AssertDiscoveryAsync(client, AuthorizationServer, DefaultAdvertisedScope);
    }

    [Fact]
    public async Task DefaultScopeUsesTheSameOriginAsDiscovery()
    {
        await using var app = BuildApp(configure: settings =>
        {
            settings["Protocol:PublicOrigin"] = "https://protocol.example.test";
            settings["Authentication:PublicOrigin"] = Origin + "/";
        });
        await app.StartAsync();
        using var client = app.GetTestClient();
        await AssertDiscoveryAsync(client, AuthorizationServer, DefaultAdvertisedScope);
    }

    [Fact]
    public async Task WithoutPublicOriginDefaultScopeUsesApplicationIdUri()
    {
        await using var app = BuildApp(configure: settings => settings.Remove("Authentication:PublicOrigin"));
        await app.StartAsync();
        using var client = app.GetTestClient();
        const string loopbackOrigin = "https://localhost";
        client.BaseAddress = new Uri(loopbackOrigin);
        await AssertDiscoveryAsync(client, AuthorizationServer, "api://" + OfficeClientId + "/" + DefaultScope, loopbackOrigin);
    }

    private static async Task AssertDiscoveryAsync(HttpClient client, string authorizationServer, string advertisedScope,
        string origin = Origin)
    {
        using var initialize = await InitializeAsync(client);
        Assert.Equal(HttpStatusCode.Unauthorized, initialize.StatusCode);
        var challenge = Assert.Single(initialize.Headers.WwwAuthenticate);
        Assert.Equal("Bearer", challenge.Scheme);
        var parameters = Assert.IsType<string>(challenge.Parameter);
        Assert.Contains($"resource_metadata=\"{origin}{MetadataPath}\"", parameters);
        Assert.Contains($"scope=\"{advertisedScope}\"", parameters);

        foreach (var path in new[] { RootMetadataPath, MetadataPath })
        {
            using var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var metadata = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var resource = metadata.RootElement;
            Assert.Equal(origin + McpPath, resource.GetProperty("resource").GetString());
            Assert.Equal(authorizationServer, Assert.Single(resource.GetProperty("authorization_servers").EnumerateArray()).GetString());
            Assert.Equal(advertisedScope, Assert.Single(resource.GetProperty("scopes_supported").EnumerateArray()).GetString());
            Assert.Equal("header", Assert.Single(resource.GetProperty("bearer_methods_supported").EnumerateArray()).GetString());
        }
    }

    [Fact]
    public async Task EntraDefaultsRequireMcpScopeOnSharedAudience()
    {
        using var key = RSA.Create(2048);
        await using var app = BuildApp(signingKey: key);
        await app.StartAsync();
        using var client = app.GetTestClient();

        using var officeResponse = await InitializeAsync(client, CreateToken(key, OfficeClientId, EntraAuthentication.AccessScope));
        Assert.Equal(HttpStatusCode.Forbidden, officeResponse.StatusCode);
        Assert.Contains("error=\"insufficient_scope\"", Assert.Single(officeResponse.Headers.WwwAuthenticate).Parameter);
        using var mcpResponse = await InitializeAsync(client, CreateToken(key, OfficeClientId, DefaultScope));
        Assert.Equal(HttpStatusCode.OK, mcpResponse.StatusCode);
        Assert.False(string.IsNullOrWhiteSpace(Assert.Single(mcpResponse.Headers.GetValues("Mcp-Session-Id"))));
    }

    [Fact]
    public async Task DedicatedMcpOverridesDiscoveryAndAcceptsSignedTokenWithRequiredScope()
    {
        using var key = RSA.Create(2048);
        await using var app = BuildApp(signingKey: key, configure: ConfigureDedicatedMcp);
        await app.StartAsync();
        using var client = app.GetTestClient();
        await AssertDiscoveryAsync(client, Issuer, AdvertisedScope);

        var token = CreateToken(key, McpClientId, "profile " + RequiredScope);
        using var response = await InitializeAsync(client, token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(string.IsNullOrWhiteSpace(Assert.Single(response.Headers.GetValues("Mcp-Session-Id"))));
    }

    [Theory]
    [InlineData(OfficeClientId, RequiredScope, HttpStatusCode.Unauthorized)]
    [InlineData(McpClientId, "Workbooks.Access", HttpStatusCode.Forbidden)]
    [InlineData(McpClientId, null, HttpStatusCode.Forbidden)]
    public async Task SignedTokenRequiresMcpAudienceAndExactScope(string audience, string? scope, HttpStatusCode status)
    {
        using var key = RSA.Create(2048);
        await using var app = BuildApp(signingKey: key, configure: ConfigureDedicatedMcp);
        await app.StartAsync();
        using var client = app.GetTestClient();

        using var response = await InitializeAsync(client, CreateToken(key, audience, scope));
        Assert.Equal(status, response.StatusCode);
    }

    private static void ConfigureDedicatedMcp(Dictionary<string, string?> settings)
    {
        settings["Mcp:ClientId"] = McpClientId;
        settings["Mcp:AuthorizationServer"] = Issuer;
        settings["Mcp:RequiredScope"] = RequiredScope;
        settings["Mcp:ScopesSupported:0"] = AdvertisedScope;
    }

    private static WebApplication BuildApp(bool configureEntra = true, RSA? signingKey = null,
        Action<Dictionary<string, string?>>? configure = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddAuthentication("test-cookie").AddCookie("test-cookie");
        var settings = new Dictionary<string, string?>
        {
            ["Authentication:PublicOrigin"] = Origin,
        };
        if (configureEntra)
        {
            settings["Authentication:Entra:Enabled"] = "true";
            settings["Authentication:Entra:TenantId"] = TenantId;
            settings["Authentication:Entra:ClientId"] = OfficeClientId;
        }
        configure?.Invoke(settings);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var content = new InMemoryContentStore();
        builder.Services.AddCellBridge(new(new InMemoryDocumentRepository(content), content), requireDurability: false,
            configure: options =>
            {
                options.PublicOrigin = configuration["Protocol:PublicOrigin"] ?? configuration["Authentication:PublicOrigin"];
                options.EnableOfficeRealTime = false;
            });
        builder.Services.AddScoped<ICellBridgeExternalIdentityMapper, TestAccountMapper>();
        if (configuration["Authentication:PublicOrigin"] is { } publicOrigin)
            builder.Services.AddCellBridgeOfficeFormsAuthentication("test-cookie", options => options.PublicOrigin = publicOrigin);
        var configurationError = builder.Services.AddWebDemoMcp(configuration);

        var metadata = new OpenIdConnectConfiguration { Issuer = Issuer };
        if (signingKey is not null)
            metadata.SigningKeys.Add(new RsaSecurityKey(signingKey.ExportParameters(false)) { KeyId = KeyId });
        builder.Services.PostConfigureAll<JwtBearerOptions>(options =>
            options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(metadata));

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGet("/health", () => "Healthy");
        app.MapWebDemoMcp(configurationError);
        return app;
    }

    private static string CreateToken(RSA key, string audience, string? scope)
    {
        var claims = new List<Claim>
        {
            new("tid", TenantId),
            new("oid", ObjectId),
            new("ver", "2.0"),
        };
        if (scope is not null) claims.Add(new Claim("scp", scope));
        var now = DateTime.UtcNow;
        var signing = new SigningCredentials(new RsaSecurityKey(key) { KeyId = KeyId }, SecurityAlgorithms.RsaSha256);
        var token = new JwtSecurityToken(Issuer, audience, claims, now.AddMinutes(-1), now.AddMinutes(5), signing);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static async Task<HttpResponseMessage> InitializeAsync(HttpClient client, string? token = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, McpPath)
        {
            Content = JsonContent.Create(new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "initialize",
                @params = new
                {
                    protocolVersion = "2025-06-18",
                    capabilities = new { },
                    clientInfo = new { name = "webdemo-tests", version = "1.0" },
                },
            }),
        };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
    }

    private sealed class TestAccountMapper : ICellBridgeExternalIdentityMapper
    {
        public ValueTask<ClaimsPrincipal?> MapAsync(ValidatedExternalIdentity identity, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (identity.TenantId != Guid.Parse(TenantId) || identity.ObjectId != Guid.Parse(ObjectId))
                return ValueTask.FromResult<ClaimsPrincipal?>(null);

            return ValueTask.FromResult<ClaimsPrincipal?>(new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(CellBridgeActor.SubjectClaim, "webdemo-tests:linked-account"), new Claim("scp", DefaultScope),
                    new Claim("scp", RequiredScope)],
                "TestAccounts")));
        }
    }
}
