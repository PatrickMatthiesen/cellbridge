using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Encodings.Web;
using CellBridge.AspNetCore;
using CellBridge.Client;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CellBridge.Mcp.AspNetCore.Tests;

internal sealed class McpTestHost(WebApplication app, StorageProvider storage) : IAsyncDisposable
{
    private const string CookieScheme = "TestCookie";
    private const string BearerScheme = "TestBearer";
    public const string WorkbookScopePolicy = "WorkbookScope";
    private const string WorkbookScope = "workbooks.access";
    public const string WorkbookPath = "/shared/planning.xlsx";
    public static readonly CellBridgeActor Owner = CreateActor("owner", canCreate: true);
    public static readonly CellBridgeActor Peer = CreateActor("peer");
    public static readonly CellBridgeActor Agent = CreateActor("agent");
    private Task? stopping;

    public Uri Origin { get; } = new(app.Urls.Single());
    public Uri WorkbookUrl => new(Origin, WorkbookPath);

    public static async Task<McpTestHost> StartAsync(bool enableRealTime = false,
        Action<CellBridgeMcpOptions>? configure = null, Action<CellBridgeOptions>? configureProtocol = null)
    {
        var content = new InMemoryContentStore();
        var storage = new StorageProvider(new InMemoryDocumentRepository(content), content);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        builder.Services.AddAuthentication(CookieScheme)
            .AddScheme<AuthenticationSchemeOptions, TestBearerHandler>(BearerScheme, _ => { })
            .AddCookie(CookieScheme, options => options.ForwardDefaultSelector =
                context => context.Request.Headers.ContainsKey("Authorization") ? BearerScheme : null);
        builder.Services.AddAuthorization(options => options.AddPolicy(WorkbookScopePolicy,
            policy => policy.RequireClaim("scope", WorkbookScope)));
        builder.Services.AddCellBridge(storage, requireDurability: false,
            configure: options =>
            {
                options.EnableOfficeRealTime = enableRealTime;
                options.ConcealUnauthorizedDownloads = true;
                configureProtocol?.Invoke(options);
            });
        builder.Services.AddCellBridgeMcp(options =>
        {
            options.BearerScheme = BearerScheme;
            configure?.Invoke(options);
        });

        var app = builder.Build();
        try
        {
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapCellBridge();
            app.MapCellBridgeMcp();
            app.MapPost("/test/sign-in", (HttpContext context) =>
                context.SignInAsync(CookieScheme, Principal(Owner, CookieScheme)));
            await app.StartAsync();
            return new McpTestHost(app, storage);
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }
    }

    public HttpClient CreateHttp(string? token = null)
    {
        var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            BaseAddress = Origin,
            Timeout = TimeSpan.FromSeconds(15),
        };
        if (token is not null)
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return http;
    }

    public HttpCellBridgeTransport CreateTransport(HttpClient http) => new(http, Origin);

    public ICellBridgeRealTimeConnector CreateRealTimeConnector(CellBridgeActor actor) =>
        app.Services.GetRequiredService<CellBridgeInProcessHost>().CreateRealTimeConnector(actor);

    public async Task<DocumentHead> CreateWorkbookAsync(string path = WorkbookPath) =>
        Assert.IsType<DocumentHead>(await app.Services.GetRequiredService<CellBridgeDocumentService>()
            .CreateAsync(path, OfficeDocumentTemplates.CreateExcelWorkbook(), Owner));

    public async Task<DocumentControlRead> ReadControlAsync(Guid resourceId) =>
        Assert.IsType<DocumentControlRead>(await storage.Documents.ReadControlAsync(resourceId));

    public Task SetPeerAccessAsync(Guid resourceId, DocumentAccess access) =>
        SetAccessAsync(resourceId, Peer, access);

    public async Task SetAccessAsync(Guid resourceId, CellBridgeActor recipient, DocumentAccess access)
    {
        var head = (await ReadControlAsync(resourceId)).Head;
        var status = await app.Services.GetRequiredService<CellBridgeDocumentService>()
            .SetRecipientGrantAsync(resourceId, head.LifecycleGeneration, head.AuthorityGeneration,
                Owner, recipient.Identity, access);
        Assert.Equal(DocumentSharingUpdateStatus.Applied, status);
    }

    public Task StopAsync() => stopping ??= app.StopAsync().WaitAsync(TimeSpan.FromSeconds(15));

    public async ValueTask DisposeAsync()
    {
        try { await StopAsync(); }
        finally { await app.DisposeAsync(); }
    }

    private static CellBridgeActor CreateActor(string name, bool canCreate = false) =>
        new(new("mcp-tests:" + name, name, "MCP test " + name), CanCreate: canCreate);

    private static ClaimsPrincipal Principal(CellBridgeActor actor, string scheme) => new(new ClaimsIdentity(
    [
        new(CellBridgeActor.SubjectClaim, actor.Identity.Subject),
        new(ClaimTypes.Name, actor.Identity.Login),
        new(CellBridgeActor.DisplayNameClaim, actor.Identity.DisplayName),
    ], scheme));

    private sealed class TestBearerHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger, UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var token = Request.Headers.Authorization.ToString();
            var actor = token switch
            {
                "Bearer owner" => Owner,
                "Bearer peer" or "Bearer peer-mcp" => Peer,
                "Bearer agent" => Agent,
                _ => null,
            };
            if (actor is null) return Task.FromResult(AuthenticateResult.NoResult());

            var principal = Principal(actor, Scheme.Name);
            if (token == "Bearer peer-mcp")
                principal.Identities.Single().AddClaim(new("scope", WorkbookScope));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
        }
    }
}
