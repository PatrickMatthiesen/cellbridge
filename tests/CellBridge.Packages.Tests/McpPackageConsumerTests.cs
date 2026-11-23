using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using CellBridge.AspNetCore;
using CellBridge.Mcp.AspNetCore;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using ModelContextProtocol.Client;

namespace CellBridge.Packages.Tests;

public sealed class McpPackageConsumerTests
{
    [Fact]
    public async Task PackagedMcpHostOpensAnAuthorizedWorkbookOverStreamableHttp()
    {
        const string issuer = "https://issuer.example";
        const string audience = "cellbridge-mcp-tests";
        const string scheme = "WorkbookBearer";
        const string origin = "https://localhost";
        var key = new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(32));

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthentication().AddJwtBearer(scheme, options =>
        {
            options.MapInboundClaims = false;
            options.TokenValidationParameters = new()
            {
                ValidIssuer = issuer,
                ValidAudience = audience,
                IssuerSigningKey = key,
            };
        });
        builder.Services.AddCellBridge(TestStorage.Memory(), requireDurability: false, configure: options =>
        {
            options.EnableOfficeRealTime = false;
            options.PublicOrigin = origin;
        });
        builder.Services.AddCellBridgeMcp(options => options.BearerScheme = scheme);
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapCellBridge();
        app.MapCellBridgeMcp("/workbook-tools");
        await app.StartAsync();

        var documents = app.Services.GetRequiredService<CellBridgeDocumentService>();
        var document = await documents.CreateAsync("/shared/package-mcp.xlsx",
            OfficeDocumentTemplates.CreateExcelWorkbook(), TestActor.Value);
        Assert.NotNull(document);

        using var http = app.GetTestClient();
        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            Subject = new ClaimsIdentity([new Claim(CellBridgeActor.SubjectClaim, TestActor.Value.Identity.Subject)]),
            Expires = DateTime.UtcNow.AddMinutes(5),
            SigningCredentials = new(key, SecurityAlgorithms.HmacSha256),
        });
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var transport = new HttpClientTransport(new()
        {
            Endpoint = new Uri(origin + "/workbook-tools"),
            TransportMode = HttpTransportMode.StreamableHttp,
        }, http, ownsHttpClient: false);

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await using var mcp = await McpClient.CreateAsync(transport, cancellationToken: timeout.Token);
        var opened = await mcp.CallToolAsync("open_workbook", new Dictionary<string, object?>
        {
            ["resourceId"] = document.ResourceId,
        }, cancellationToken: timeout.Token);
        Assert.False(opened.IsError ?? false);
        var workbook = Assert.IsType<JsonElement>(opened.StructuredContent);
        Assert.Equal(document.ResourceId, workbook.GetProperty("resourceId").GetGuid());

        var handle = new Dictionary<string, object?>
        {
            ["workbookHandle"] = workbook.GetProperty("workbookHandle").GetString(),
        };
        var listed = await mcp.CallToolAsync("list_sheets", handle, cancellationToken: timeout.Token);
        Assert.False(listed.IsError ?? false);
        var sheets = Assert.IsType<JsonElement>(listed.StructuredContent).GetProperty("sheets");
        Assert.Equal("Sheet1", Assert.Single(sheets.EnumerateArray()).GetProperty("name").GetString());

        var closed = await mcp.CallToolAsync("close_workbook", handle, cancellationToken: timeout.Token);
        Assert.False(closed.IsError ?? false);
        Assert.True(Assert.IsType<JsonElement>(closed.StructuredContent).GetProperty("closed").GetBoolean());
    }
}
