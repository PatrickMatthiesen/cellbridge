using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
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
public sealed class HostAuthorizationHttpTests(ListDatabaseFixture database)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    [PostgreSqlFact]
    public async Task BearerHttpFlowUsesHostPolicyForNativeODataAndExtractEndpoints()
    {
        var domain = "host-http:" + Guid.NewGuid().ToString("N");
        var workspace = Guid.NewGuid();
        var actor = new ListActor("host:http:" + Guid.NewGuid().ToString("N"));
        await PostgreSqlListSetup.RegisterHostWorkspaceAsync(database.AdminSource!, workspace,
            "HTTP host authorization", domain);
        var policy = new HttpPolicy(new ListAuthorizationSnapshot(domain, actor.Subject, workspace,
            ListWorkspaceAccess.UseLists | ListWorkspaceAccess.CreateLists |
            ListWorkspaceAccess.ManagePermissions | ListWorkspaceAccess.ReadAudit,
            ListAccess.Read | ListAccess.Write | ListAccess.Design));
        var provider = new SingleProcessListAuthorizationProvider(domain, policy.LoadAsync);
        var engine = new PostgreSqlListEngine(database.RuntimeSource!, new ListLimits(), provider);
        await using var host = await HttpHost.CreateAsync(engine, actor.Subject);

        var field = new ListField(Guid.NewGuid(), "Title", ListFieldType.Text, MaxLength: 200);
        var listId = Guid.NewGuid();
        var listsPath = $"/api/workspaces/{workspace}/lists";
        var createList = await host.Client.PostAsJsonAsync(listsPath,
            new CreateListRequest(listId, "HTTP list", [field]));
        Assert.Equal(HttpStatusCode.Created, createList.StatusCode);

        var recordId = Guid.NewGuid();
        var listPath = listsPath + "/" + listId;
        var createRecord = await host.Client.PostAsJsonAsync(listPath + "/records",
            new CreateRecordRequest(recordId, 1,
                new JsonObject { [field.Id.ToString("D")] = "HTTP value" }));
        Assert.Equal(HttpStatusCode.Created, createRecord.StatusCode);

        var extra = new ListField(Guid.NewGuid(), "Active", ListFieldType.Boolean);
        var publish = await host.Client.PutAsJsonAsync(listPath + "/schema",
            new PublishSchemaRequest(1, "HTTP list v2", [field, extra]));
        Assert.Equal(HttpStatusCode.OK, publish.StatusCode);
        Assert.Equal(2, (await publish.Content.ReadFromJsonAsync<ListDefinition>(Json))!.SchemaVersion);

        var schema = await host.Client.GetFromJsonAsync<ListDefinition>(listPath, Json);
        Assert.Equal("HTTP list v2", schema!.Name);
        var query = await host.Client.PostAsJsonAsync(listPath + "/records/query",
            new ListQueryRequest(new ListQuery()));
        Assert.Equal(HttpStatusCode.OK, query.StatusCode);
        Assert.Equal(recordId,
            Assert.Single((await query.Content.ReadFromJsonAsync<ListQueryResponse>(Json))!.Records).Id);

        var metadata = await host.Client.GetStringAsync(listPath + "/odata/$metadata");
        Assert.Contains("Edm.String", metadata);
        Assert.Contains("f_" + field.Id.ToString("N"), metadata);
        var odata = JsonNode.Parse(await host.Client.GetStringAsync(listPath + "/odata/Records"))!;
        Assert.Equal("HTTP value", Assert.Single(odata["value"]!.AsArray())!
            ["f_" + field.Id.ToString("N")]!.GetValue<string>());
        var csv = await host.Client.GetStringAsync(listPath + "/export.csv");
        Assert.Contains("HTTP value", csv);
        var xlsx = await host.Client.GetByteArrayAsync(listPath + "/export.xlsx");
        Assert.True(xlsx.Length > 4);
        Assert.Equal((byte)'P', xlsx[0]);
        Assert.Equal((byte)'K', xlsx[1]);

        host.Client.DefaultRequestHeaders.Add("Cookie", "session=browser");
        var cookieGrant = await host.Client.PutAsJsonAsync(listPath + "/grants",
            new ListGrantRequest("another-subject", ListAccess.Read));
        Assert.Equal(HttpStatusCode.BadRequest, cookieGrant.StatusCode);
        Assert.Equal("antiforgery", (await cookieGrant.Content.ReadFromJsonAsync<JsonObject>())!["error"]!.GetValue<string>());
        host.Client.DefaultRequestHeaders.Remove("Cookie");

        var bearerGrant = await host.Client.PutAsJsonAsync(listPath + "/grants",
            new ListGrantRequest("another-subject", ListAccess.Read));
        Assert.Equal(HttpStatusCode.NotImplemented, bearerGrant.StatusCode);
        Assert.Equal("NotSupported", (await bearerGrant.Content.ReadFromJsonAsync<JsonObject>())!["error"]!.GetValue<string>());

        Assert.True(await engine.CoordinateGlobalAuthorizationChangeAsync(_ =>
        {
            policy.Set(new ListAuthorizationSnapshot(domain, actor.Subject, workspace,
                ListWorkspaceAccess.None, revision: "revoked"));
            return Task.FromResult(true);
        }));
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync(listPath)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await host.Client.PostAsJsonAsync(listPath + "/records/query",
                new ListQueryRequest(new ListQuery()))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await host.Client.GetAsync(listPath + "/odata/Records")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await host.Client.GetAsync(listPath + "/export.csv")).StatusCode);
    }

    private sealed class HttpPolicy(ListAuthorizationSnapshot initial)
    {
        private ListAuthorizationSnapshot _snapshot = initial;

        public void Set(ListAuthorizationSnapshot snapshot) => Volatile.Write(ref _snapshot, snapshot);

        public ValueTask<ListAuthorizationSnapshot?> LoadAsync(ListActor actor, Guid workspace,
            CancellationToken _) => ValueTask.FromResult<ListAuthorizationSnapshot?>(
            Volatile.Read(ref _snapshot) is { } snapshot && snapshot.Subject == actor.Subject &&
            snapshot.WorkspaceId == workspace ? snapshot : null);
    }

    private sealed class HttpHost(WebApplication app, HttpClient client) : IAsyncDisposable
    {
        public HttpClient Client { get; } = client;

        public static async Task<HttpHost> CreateAsync(IListEngine engine, string subject)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            builder.Services.AddSingleton<IListEngine>(engine);
            builder.Services.AddAuthentication("integration")
                .AddScheme<AuthenticationSchemeOptions, BearerHandler>("integration", _ => { });
            builder.Services.AddCellBridgeLists(options =>
            {
                options.ActorResolver = context => context.User.FindFirst("sub") is { } claim
                    ? new ListActor(claim.Value)
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
