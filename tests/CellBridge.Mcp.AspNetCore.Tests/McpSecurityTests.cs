using System.Net;
using System.Net.Http.Headers;
using CellBridge.Client;
using CellBridge.Storage.Abstractions;
using Microsoft.AspNetCore.Authorization;
using static CellBridge.Mcp.AspNetCore.Tests.McpWorkbookTests;

namespace CellBridge.Mcp.AspNetCore.Tests;

public sealed class McpSecurityTests
{
    [Fact]
    public async Task McpRejectsHostCookieAndUntrustedOrigin()
    {
        await using var host = await McpTestHost.StartAsync(configure: ConfigureOAuthDiscovery);
        await host.CreateWorkbookAsync();
        using var browser = host.CreateHttp();
        using var signedIn = await browser.PostAsync("/test/sign-in", content: null);
        signedIn.EnsureSuccessStatusCode();
        using var download = await browser.GetAsync(McpTestHost.WorkbookPath);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        var browserMcp = new McpHttpSession(browser);
        using var cookieOnly = await browserMcp.SendAsync(McpHttpSession.InitializeMethod, McpHttpSession.InitializeArguments);
        Assert.Equal(HttpStatusCode.Unauthorized, cookieOnly.StatusCode);
        Assert.Contains(cookieOnly.Headers.WwwAuthenticate, header => header.Scheme == "Bearer");
        browser.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "owner");
        using var mixedCredentials = await browserMcp.SendAsync(McpHttpSession.InitializeMethod, McpHttpSession.InitializeArguments);
        Assert.Equal(HttpStatusCode.Unauthorized, mixedCredentials.StatusCode);

        using var bearer = host.CreateHttp("owner");
        var mcp = new McpHttpSession(bearer);
        using var untrusted = await mcp.SendAsync(McpHttpSession.InitializeMethod, McpHttpSession.InitializeArguments,
            origin: "https://foreign.example");
        Assert.Equal(HttpStatusCode.Forbidden, untrusted.StatusCode);
        Assert.Empty(untrusted.Headers.WwwAuthenticate);
        using var sameOrigin = await mcp.SendAsync(McpHttpSession.InitializeMethod, McpHttpSession.InitializeArguments,
            origin: host.Origin.GetLeftPart(UriPartial.Authority));
        sameOrigin.EnsureSuccessStatusCode();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("invalid")]
    [InlineData("owner:0")]
    public async Task MissingInvalidAndExpiredCredentialsReceiveABearerChallenge(string? token)
    {
        await using var host = await McpTestHost.StartAsync();
        using var http = host.CreateHttp(token);
        using var response = await new McpHttpSession(http).SendAsync(McpHttpSession.InitializeMethod,
            McpHttpSession.InitializeArguments);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var challenge = Assert.Single(response.Headers.WwwAuthenticate);
        Assert.Equal("Bearer", challenge.Scheme);
        Assert.Null(challenge.Parameter);
    }

    [Theory]
    [InlineData(null, "https://foreign.example")]
    [InlineData("no-actor", null)]
    public async Task ForbiddenRequestsOutsidePolicyEvaluationDoNotReceiveABearerChallenge(string? token, string? origin)
    {
        await using var host = await McpTestHost.StartAsync(configure: ConfigureOAuthDiscovery);
        using var http = host.CreateHttp(token);
        using var response = await new McpHttpSession(http).SendAsync(McpHttpSession.InitializeMethod,
            McpHttpSession.InitializeArguments, origin: origin);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(response.Headers.WwwAuthenticate);
    }

    [Fact]
    public async Task WorkbookHandlesAndMcpSessionsAreBoundToTheAuthenticatedSubject()
    {
        await using var host = await McpTestHost.StartAsync();
        var document = await host.CreateWorkbookAsync();
        await host.SetPeerAccessAsync(document.ResourceId, DocumentAccess.Read);
        using var owner = host.CreateHttp("owner");
        using var peer = host.CreateHttp("peer");
        var mcp = new McpHttpSession(owner);
        await mcp.InitializeAsync();
        var workbookHandle = Handle(await OpenAsync(mcp, new { resourceId = document.ResourceId }));

        using var wrongSubject = await mcp.SendAsync("tools/list", requestClient: peer);
        Assert.Equal(HttpStatusCode.NotFound, wrongSubject.StatusCode);
        var peerMcp = new McpHttpSession(peer);
        await peerMcp.InitializeAsync();
        await ErrorAsync(peerMcp, "list_sheets", new { workbookHandle });
        var anotherOwnerSession = new McpHttpSession(owner);
        await anotherOwnerSession.InitializeAsync();
        await ErrorAsync(anotherOwnerSession, "list_sheets", new { workbookHandle });
        await SuccessAsync(mcp, "list_sheets", new { workbookHandle });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LookupConcealsUnauthorizedDocumentsAndNeverCreatesOnMiss(bool concealDownloads)
    {
        await using var host = await McpTestHost.StartAsync(
            configureProtocol: options => options.ConcealUnauthorizedDownloads = concealDownloads);
        var document = await host.CreateWorkbookAsync();
        using var peer = host.CreateHttp("peer");
        var mcp = new McpHttpSession(peer);
        await mcp.InitializeAsync();

        var missingId = await ErrorAsync(mcp, "open_workbook", new { resourceId = Guid.NewGuid() });
        var hiddenId = await ErrorAsync(mcp, "open_workbook", new { resourceId = document.ResourceId });
        Assert.Equal(missingId, hiddenId);
        const string missingPath = "/shared/missing.xlsx";
        var missing = await ErrorAsync(mcp, "open_workbook", new { path = missingPath });
        var hidden = await ErrorAsync(mcp, "open_workbook", new { path = McpTestHost.WorkbookPath });
        Assert.Equal(missing, hidden);
        await AssertReleasedAsync(host, document.ResourceId);
        await host.CreateWorkbookAsync(missingPath);
    }

    [Theory]
    [InlineData("sync_workbook")]
    [InlineData("read_range")]
    [InlineData("set_cells")]
    public async Task TheNextCallAfterRevocationCannotUseAnOpenWorkbook(string tool)
    {
        await using var host = await McpTestHost.StartAsync();
        var document = await host.CreateWorkbookAsync();
        await host.SetPeerAccessAsync(document.ResourceId, DocumentAccess.Read | DocumentAccess.Write);
        using var peer = host.CreateHttp("peer");
        var mcp = new McpHttpSession(peer);
        await mcp.InitializeAsync();
        var opened = await OpenAsync(mcp, new { resourceId = document.ResourceId });
        var workbookHandle = Handle(opened);
        var sheetId = Assert.Single((await SuccessAsync(mcp, "list_sheets", new { workbookHandle }))
            .GetProperty("sheets").EnumerateArray()).GetProperty("sheetId").GetUInt32();
        var allowed = await SetNumberAsync(mcp, workbookHandle, sheetId, "A1", "10", Revision(opened));
        AssertWriteStatus(allowed, "Success");
        await host.SetPeerAccessAsync(document.ResourceId, DocumentAccess.None);

        object arguments = tool switch
        {
            "sync_workbook" => new { workbookHandle },
            "read_range" => new { workbookHandle, sheetId, a1Range = "A1" },
            "set_cells" => SetNumberArguments(workbookHandle, sheetId, "A1", "20", Revision(allowed)),
            _ => throw new InvalidOperationException(tool),
        };
        await ErrorAsync(mcp, tool, arguments);
        await AssertReleasedAsync(host, document.ResourceId);

        using var owner = host.CreateHttp("owner");
        var ownerMcp = new McpHttpSession(owner);
        await ownerMcp.InitializeAsync();
        var saved = await OpenAsync(ownerMcp, new { resourceId = document.ResourceId });
        var persisted = await ReadAsync(ownerMcp, Handle(saved), sheetId, "A1");
        Assert.Equal("10", CellValue(persisted, "A1"));
    }

    [Fact]
    public async Task HostDefaultPolicyRequiresClaimEvenWithWorkbookGrant()
    {
        await using var host = await McpTestHost.StartAsync(
            configure: ConfigureOAuthDiscovery,
            configureAuthorization: options => options.DefaultPolicy = new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .RequireClaim("scope", McpTestHost.WorkbookScope)
                .Build());
        var document = await host.CreateWorkbookAsync();
        await host.SetPeerAccessAsync(document.ResourceId, DocumentAccess.Read);
        using var unscoped = host.CreateHttp("peer");
        using var scoped = host.CreateHttp("peer-mcp");

        using var denied = await new McpHttpSession(unscoped).SendAsync(McpHttpSession.InitializeMethod,
            McpHttpSession.InitializeArguments);
        AssertInsufficientScope(denied, host);

        var mcp = new McpHttpSession(scoped);
        await mcp.InitializeAsync();
        await OpenAsync(mcp, new { resourceId = document.ResourceId });
    }

    [Fact]
    public async Task ScopedUserBearerUsesUserGrantsAndJoinsWithASeparateClientId()
    {
        await using var host = await McpTestHost.StartAsync(configure: options =>
        {
            ConfigureOAuthDiscovery(options);
            options.AuthorizationPolicy = McpTestHost.WorkbookScopePolicy;
        });
        var document = await host.CreateWorkbookAsync();
        var privateDocument = await host.CreateWorkbookAsync("/shared/private.xlsx");
        await host.SetPeerAccessAsync(document.ResourceId, DocumentAccess.Read | DocumentAccess.Write);
        using var officeHttp = host.CreateHttp("peer");
        using var mcpHttp = host.CreateHttp("peer-mcp");

        using var missingScope = await new McpHttpSession(officeHttp).SendAsync(McpHttpSession.InitializeMethod,
            McpHttpSession.InitializeArguments);
        AssertInsufficientScope(missingScope, host);

        await using var office = await CellBridgeDocumentSession.OpenAsync(host.CreateTransport(officeHttp), host.WorkbookUrl);
        var mcp = new McpHttpSession(mcpHttp);
        await mcp.InitializeAsync();
        await ErrorAsync(mcp, "open_workbook", new { resourceId = privateDocument.ResourceId });
        var opened = await OpenAsync(mcp, new { resourceId = document.ResourceId });
        var workbookHandle = Handle(opened);

        using var unscopedCall = await mcp.SendAsync("tools/call",
            new { name = "list_sheets", arguments = new { workbookHandle } }, requestClient: officeHttp);
        AssertInsufficientScope(unscopedCall, host);

        var editors = (await host.ReadControlAsync(document.ResourceId)).Editors;
        Assert.Equal(2, editors.Length);
        Assert.All(editors, editor => Assert.Equal(McpTestHost.Peer.Identity.Subject, editor.Owner?.Subject));
        Assert.Single(editors, editor => editor.ClientId == office.ClientId);
        var mcpClientId = Assert.Single(editors, editor => editor.ClientId != office.ClientId).ClientId;

        var sheets = await SuccessAsync(mcp, "list_sheets", new { workbookHandle });
        var sheetId = Assert.Single(sheets.GetProperty("sheets").EnumerateArray()).GetProperty("sheetId").GetUInt32();
        var write = await SetNumberAsync(mcp, workbookHandle, sheetId, "A1", "20", Revision(opened));
        AssertWriteStatus(write, "Success");
        var lease = Assert.Single((await host.ReadControlAsync(document.ResourceId)).Coordination.SchemaOwners);
        Assert.Equal(McpTestHost.Peer.Identity.Subject, lease.OwnerSubject);
        Assert.Equal(mcpClientId, Guid.Parse(lease.Client!));

        await SuccessAsync(mcp, "close_workbook", new { workbookHandle });
        var remainingEditor = Assert.Single((await host.ReadControlAsync(document.ResourceId)).Editors);
        Assert.Equal(office.ClientId, remainingEditor.ClientId);
    }

    [Fact]
    public async Task DedicatedAgentUsesItsOwnWorkbookGrant()
    {
        await using var host = await McpTestHost.StartAsync();
        var document = await host.CreateWorkbookAsync();
        await host.SetPeerAccessAsync(document.ResourceId, DocumentAccess.Read | DocumentAccess.Write);
        using var agent = host.CreateHttp("agent");
        var mcp = new McpHttpSession(agent);
        await mcp.InitializeAsync();
        await ErrorAsync(mcp, "open_workbook", new { resourceId = document.ResourceId });

        await host.SetAccessAsync(document.ResourceId, McpTestHost.Agent, DocumentAccess.Read);
        var workbookHandle = Handle(await OpenAsync(mcp, new { resourceId = document.ResourceId }));
        var reader = Assert.Single((await host.ReadControlAsync(document.ResourceId)).Editors);
        Assert.Equal(McpTestHost.Agent.Identity.Subject, reader.Owner?.Subject);
        Assert.False(reader.AsEditor);

        var sheets = await SuccessAsync(mcp, "list_sheets", new { workbookHandle });
        var sheetId = Assert.Single(sheets.GetProperty("sheets").EnumerateArray()).GetProperty("sheetId").GetUInt32();
        await ReadAsync(mcp, workbookHandle, sheetId, "A1");
        await ErrorAsync(mcp, "set_cells", SetNumberArguments(workbookHandle, sheetId, "A1", "20", Revision(sheets)));
        Assert.Empty((await host.ReadControlAsync(document.ResourceId)).Coordination.SchemaOwners);
    }

    private static void ConfigureOAuthDiscovery(CellBridgeMcpOptions options)
    {
        options.AuthorizationServer = new Uri("https://issuer.example/tenant");
        options.ScopesSupported = [McpTestHost.WorkbookScope];
    }

    private static void AssertInsufficientScope(HttpResponseMessage response, McpTestHost host)
    {
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var challenge = Assert.Single(response.Headers.WwwAuthenticate);
        Assert.Equal("Bearer", challenge.Scheme);
        var parameters = Assert.IsType<string>(challenge.Parameter);
        var metadata = new Uri(host.Origin, "/.well-known/oauth-protected-resource/mcp");
        Assert.Contains("error=\"insufficient_scope\"", parameters);
        Assert.Contains($"scope=\"{McpTestHost.WorkbookScope}\"", parameters);
        Assert.Contains($"resource_metadata=\"{metadata.AbsoluteUri}\"", parameters);
    }
}
