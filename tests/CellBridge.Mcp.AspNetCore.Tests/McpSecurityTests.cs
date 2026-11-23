using System.Net;
using System.Net.Http.Headers;
using CellBridge.Client;
using CellBridge.Storage.Abstractions;
using static CellBridge.Mcp.AspNetCore.Tests.McpWorkbookTests;

namespace CellBridge.Mcp.AspNetCore.Tests;

public sealed class McpSecurityTests
{
    [Fact]
    public async Task McpRejectsHostCookieAndUntrustedOrigin()
    {
        await using var host = await McpTestHost.StartAsync();
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
        using var sameOrigin = await mcp.SendAsync(McpHttpSession.InitializeMethod, McpHttpSession.InitializeArguments,
            origin: host.Origin.GetLeftPart(UriPartial.Authority));
        sameOrigin.EnsureSuccessStatusCode();
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
    public async Task ScopedUserBearerUsesUserGrantsAndJoinsWithASeparateClientId()
    {
        await using var host = await McpTestHost.StartAsync(
            configure: options => options.AuthorizationPolicy = McpTestHost.WorkbookScopePolicy);
        var document = await host.CreateWorkbookAsync();
        var privateDocument = await host.CreateWorkbookAsync("/shared/private.xlsx");
        await host.SetPeerAccessAsync(document.ResourceId, DocumentAccess.Read | DocumentAccess.Write);
        using var officeHttp = host.CreateHttp("peer");
        using var mcpHttp = host.CreateHttp("peer-mcp");

        using var missingScope = await new McpHttpSession(officeHttp).SendAsync(McpHttpSession.InitializeMethod,
            McpHttpSession.InitializeArguments);
        Assert.Equal(HttpStatusCode.Forbidden, missingScope.StatusCode);

        await using var office = await CellBridgeDocumentSession.OpenAsync(host.CreateTransport(officeHttp), host.WorkbookUrl);
        var mcp = new McpHttpSession(mcpHttp);
        await mcp.InitializeAsync();
        await ErrorAsync(mcp, "open_workbook", new { resourceId = privateDocument.ResourceId });
        var opened = await OpenAsync(mcp, new { resourceId = document.ResourceId });
        var workbookHandle = Handle(opened);

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
}
