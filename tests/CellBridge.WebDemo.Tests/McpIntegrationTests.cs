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
    private const string RequiredScope = "workbooks.access";
    private const string AdvertisedScope = Origin + McpPath + "/" + RequiredScope;
    private const string KeyId = "webdemo-test-key";

    [Fact]
    public async Task McpAndDiscoveryAreUnmappedByDefault()
    {
        await using var app = BuildApp(enableMcp: false);
        await app.StartAsync();
        using var client = app.GetTestClient();

        Assert.Null(app.Services.GetService<CellBridgeMcpOptions>());
        using var initialize = await InitializeAsync(client);
        Assert.Equal(HttpStatusCode.NotFound, initialize.StatusCode);
        using var rootMetadata = await client.GetAsync(RootMetadataPath);
        Assert.Equal(HttpStatusCode.NotFound, rootMetadata.StatusCode);
        using var metadata = await client.GetAsync(MetadataPath);
        Assert.Equal(HttpStatusCode.NotFound, metadata.StatusCode);
    }

    [Fact]
    public async Task AnonymousInitializationPointsToPublicResourceMetadata()
    {
        await using var app = BuildApp();
        await app.StartAsync();
        using var client = app.GetTestClient();

        using var initialize = await InitializeAsync(client);
        Assert.Equal(HttpStatusCode.Unauthorized, initialize.StatusCode);
        var challenge = Assert.Single(initialize.Headers.WwwAuthenticate);
        Assert.Equal("Bearer", challenge.Scheme);
        var parameters = Assert.IsType<string>(challenge.Parameter);
        Assert.Contains($"resource_metadata=\"{Origin}{MetadataPath}\"", parameters);
        Assert.Contains($"scope=\"{AdvertisedScope}\"", parameters);

        foreach (var path in new[] { RootMetadataPath, MetadataPath })
        {
            using var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var metadata = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var resource = metadata.RootElement;
            Assert.Equal(Origin + McpPath, resource.GetProperty("resource").GetString());
            Assert.Equal(AuthorizationServer, Assert.Single(resource.GetProperty("authorization_servers").EnumerateArray()).GetString());
            Assert.Equal(AdvertisedScope, Assert.Single(resource.GetProperty("scopes_supported").EnumerateArray()).GetString());
            Assert.Equal("header", Assert.Single(resource.GetProperty("bearer_methods_supported").EnumerateArray()).GetString());
        }
    }

    [Fact]
    public async Task SignedMcpTokenWithRequiredScopeCanInitialize()
    {
        using var key = RSA.Create(2048);
        await using var app = BuildApp(signingKey: key);
        await app.StartAsync();
        using var client = app.GetTestClient();

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
        await using var app = BuildApp(signingKey: key);
        await app.StartAsync();
        using var client = app.GetTestClient();

        using var response = await InitializeAsync(client, CreateToken(key, audience, scope));
        Assert.Equal(status, response.StatusCode);
    }

    private static WebApplication BuildApp(bool enableMcp = true, RSA? signingKey = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddAuthentication("test-cookie").AddCookie("test-cookie");
        var content = new InMemoryContentStore();
        builder.Services.AddCellBridge(new(new InMemoryDocumentRepository(content), content), requireDurability: false,
            configure: options =>
            {
                options.PublicOrigin = Origin;
                options.EnableOfficeRealTime = false;
            });
        builder.Services.AddScoped<ICellBridgeExternalIdentityMapper, TestAccountMapper>();

        var settings = new Dictionary<string, string?>
        {
            ["Authentication:Entra:Enabled"] = "true",
            ["Authentication:Entra:TenantId"] = TenantId,
            ["Authentication:Entra:ClientId"] = OfficeClientId,
            ["Mcp:ClientId"] = McpClientId,
            ["Mcp:AuthorizationServer"] = AuthorizationServer,
            ["Mcp:RequiredScope"] = RequiredScope,
            ["Mcp:ScopesSupported:0"] = AdvertisedScope,
        };
        if (enableMcp) settings["Mcp:Enabled"] = "true";
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var mapMcp = builder.Services.AddWebDemoMcp(configuration);

        var metadata = new OpenIdConnectConfiguration { Issuer = Issuer };
        if (signingKey is not null)
            metadata.SigningKeys.Add(new RsaSecurityKey(signingKey.ExportParameters(false)) { KeyId = KeyId });
        builder.Services.PostConfigureAll<JwtBearerOptions>(options =>
            options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(metadata));

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        if (mapMcp) app.MapCellBridgeMcp(McpPath);
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
                [new Claim(CellBridgeActor.SubjectClaim, "webdemo-tests:linked-account"), new Claim("scp", RequiredScope)],
                "TestAccounts")));
        }
    }
}
