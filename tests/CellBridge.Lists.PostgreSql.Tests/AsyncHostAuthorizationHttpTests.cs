using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json.Nodes;
using CellBridge.Lists;
using CellBridge.Lists.AspNetCore;
using CellBridge.Lists.PostgreSql;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CellBridge.Lists.PostgreSql.Tests;

[Collection("lists-postgresql")]
public sealed class AsyncHostAuthorizationHttpTests(ListDatabaseFixture database)
{
    [PostgreSqlFact]
    public async Task Host_policy_covers_http_odata_and_extract_paths()
    {
        var domain = "host-http:" + Guid.NewGuid().ToString("N");
        var workspace = Guid.NewGuid();
        var tenant = "tenant:" + Guid.NewGuid().ToString("N");
        var subject = "host:http:" + Guid.NewGuid().ToString("N");
        await PostgreSqlListSetup.RegisterHostWorkspaceAsync(database.AdminSource!, workspace,
            "HTTP host authorization", domain, tenant);
        var provider = new HttpProvider(domain);
        var engine = new PostgreSqlListEngine(database.RuntimeSource!, new ListLimits(), provider);
        await using var host = await HttpHost.CreateAsync(engine, subject, tenant);

        var field = new ListField(Guid.NewGuid(), "Title", ListFieldType.Text, MaxLength: 200);
        var listId = Guid.NewGuid();
        var listsPath = $"/api/workspaces/{workspace}/lists";
        Assert.Equal(HttpStatusCode.Created, (await host.Client.PostAsJsonAsync(listsPath,
            new CreateListRequest(listId, "HTTP list", [field]))).StatusCode);

        var recordId = Guid.NewGuid();
        var listPath = listsPath + "/" + listId;
        Assert.Equal(HttpStatusCode.Created, (await host.Client.PostAsJsonAsync(listPath + "/records",
            new CreateRecordRequest(recordId, 1,
                new JsonObject { [field.Id.ToString("D")] = "HTTP value" }))).StatusCode);

        Assert.Contains("Edm.String", await host.Client.GetStringAsync(listPath + "/odata/$metadata"));
        var odata = JsonNode.Parse(await host.Client.GetStringAsync(listPath + "/odata/Records"))!;
        Assert.Equal("HTTP value", Assert.Single(odata["value"]!.AsArray())!
            ["f_" + field.Id.ToString("N")]!.GetValue<string>());
        Assert.Contains("HTTP value", await host.Client.GetStringAsync(listPath + "/export.csv"));
        var xlsx = await host.Client.GetByteArrayAsync(listPath + "/export.xlsx");
        Assert.True(xlsx.Length > 4);
        Assert.Equal((byte)'P', xlsx[0]);
        Assert.Equal((byte)'K', xlsx[1]);

        provider.DenyReads = true;
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync(listPath)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await host.Client.GetAsync(listPath + "/odata/Records")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await host.Client.GetAsync(listPath + "/export.csv")).StatusCode);
    }

    private sealed class HttpProvider(string policyDomain) : IListAuthorizationProvider
    {
        public string PolicyDomain { get; } = policyDomain;
        public bool DenyReads { get; set; }

        public ValueTask<ListAuthorizationDecision> AuthorizeAsync(ListAuthorizationRequest request,
            CancellationToken ct = default) => ValueTask.FromResult(new ListAuthorizationDecision(
            !(DenyReads && request.Action == ListAuthorizationAction.Read)));
    }

    private sealed class HttpHost(WebApplication app, HttpClient client) : IAsyncDisposable
    {
        public HttpClient Client { get; } = client;

        public static async Task<HttpHost> CreateAsync(IListEngine engine, string subject, string tenant)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            builder.Services.AddSingleton(engine);
            builder.Services.AddAuthentication("integration")
                .AddScheme<AuthenticationSchemeOptions, BearerHandler>("integration", _ => { });
            builder.Services.AddCellBridgeLists(options =>
            {
                options.ActorResolver = context => context.User.FindFirst("sub") is { } claim
                    ? new ListActor(claim.Value, tenant)
                    : null;
                options.IntegrationAuthenticationScheme = "integration";
            });
            var app = builder.Build();
            app.UseAuthentication();
            app.MapCellBridgeLists();
            await app.StartAsync();
            var client = app.GetTestClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "valid");
            client.DefaultRequestHeaders.Add("X-Test-Actor", subject);
            return new HttpHost(app, client);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await app.DisposeAsync();
        }
    }

    private sealed class BearerHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (Request.Headers.Authorization != "Bearer valid")
                return Task.FromResult(AuthenticateResult.Fail("Invalid bearer token."));
            var identity = new ClaimsIdentity(
                [new Claim("sub", Request.Headers["X-Test-Actor"].ToString())], Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }
}
