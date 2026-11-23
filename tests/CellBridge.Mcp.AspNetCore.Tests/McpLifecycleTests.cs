using System.Net;
using CellBridge.Storage.Abstractions;
using static CellBridge.Mcp.AspNetCore.Tests.McpWorkbookTests;

namespace CellBridge.Mcp.AspNetCore.Tests;

public sealed class McpLifecycleTests
{
    [Fact]
    public async Task IdleWorkbookIsEvictedAndLeavesItsCoauthoringSession()
    {
        await using var host = await McpTestHost.StartAsync(configure: options =>
            options.IdleSessionTimeout = TimeSpan.FromSeconds(2));
        var document = await host.CreateWorkbookAsync();
        using var http = host.CreateHttp("owner");
        var mcp = new McpHttpSession(http);
        await mcp.InitializeAsync();
        var workbookHandle = await OpenWriterAsync(mcp, document.ResourceId);
        Assert.Single((await host.ReadControlAsync(document.ResourceId)).Coordination.SchemaOwners);

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while ((await host.ReadControlAsync(document.ResourceId)).Editors.Length > 0)
            await Task.Delay(TimeSpan.FromMilliseconds(50), deadline.Token);
        await AssertReleasedAsync(host, document.ResourceId);

        var newMcp = new McpHttpSession(http);
        await newMcp.InitializeAsync();
        var reopened = await OpenAsync(newMcp, new { resourceId = document.ResourceId });
        Assert.NotEqual(workbookHandle, Handle(reopened));
    }

    [Fact]
    public async Task SessionCapsApplyPerSubjectAndAcrossSubjects()
    {
        await using var host = await McpTestHost.StartAsync(configure: options =>
        {
            options.MaxConcurrentSessionsPerActor = 1;
            options.MaxTotalSessions = 2;
        });
        using var owner = host.CreateHttp("owner");
        using var peer = host.CreateHttp("peer");
        using var agent = host.CreateHttp("agent");
        var ownerMcp = new McpHttpSession(owner);
        var peerMcp = new McpHttpSession(peer);
        var agentMcp = new McpHttpSession(agent);
        await ownerMcp.InitializeAsync();

        using var perSubject = await new McpHttpSession(owner).SendAsync(McpHttpSession.InitializeMethod,
            McpHttpSession.InitializeArguments);
        Assert.Equal(HttpStatusCode.TooManyRequests, perSubject.StatusCode);

        await peerMcp.InitializeAsync();
        using var total = await agentMcp.SendAsync(McpHttpSession.InitializeMethod, McpHttpSession.InitializeArguments);
        Assert.Equal(HttpStatusCode.TooManyRequests, total.StatusCode);
        await ownerMcp.EndAsync();
        await agentMcp.InitializeAsync();
    }

    [Fact]
    public async Task GracefulShutdownReleasesSameSubjectWritersAcrossDocuments()
    {
        await using var host = await McpTestHost.StartAsync(enableRealTime: true, configureProtocol: options =>
        {
            options.MaxConcurrentRequestsPerSubject = 1;
            options.MaxQueuedRequestsPerSubject = 0;
            options.MaxQueuedRequests = 0;
        });
        var first = await host.CreateWorkbookAsync();
        var second = await host.CreateWorkbookAsync("/shared/second.xlsx");
        using var http = host.CreateHttp("owner");
        var resourceIds = new[] { first.ResourceId, second.ResourceId };
        foreach (var resourceId in resourceIds)
        {
            var mcp = new McpHttpSession(http);
            await mcp.InitializeAsync();
            await OpenWriterAsync(mcp, resourceId);
            var control = await host.ReadControlAsync(resourceId);
            Assert.True(Assert.Single(control.Editors).AsEditor);
            Assert.Equal(McpTestHost.Owner.Identity.Subject, Assert.Single(control.Coordination.SchemaOwners).OwnerSubject);
        }

        await host.StopAsync();

        foreach (var resourceId in resourceIds)
            await AssertReleasedAsync(host, resourceId);
    }

    [Fact]
    public async Task WorkbookCapsApplyAcrossDocumentsAndClosingReclaimsCapacity()
    {
        await using var host = await McpTestHost.StartAsync(configure: options =>
        {
            options.MaxConcurrentSessionsPerActor = 2;
            options.MaxTotalSessions = 3;
        });
        var first = await host.CreateWorkbookAsync();
        var second = await host.CreateWorkbookAsync("/shared/second.xlsx");
        var third = await host.CreateWorkbookAsync("/shared/third.xlsx");
        foreach (var document in new[] { first, second })
            await host.SetPeerAccessAsync(document.ResourceId, DocumentAccess.Read);
        using var owner = host.CreateHttp("owner");
        using var peer = host.CreateHttp("peer");
        var ownerMcp = new McpHttpSession(owner);
        var peerMcp = new McpHttpSession(peer);
        await ownerMcp.InitializeAsync();
        await peerMcp.InitializeAsync();

        var workbookHandle = Handle(await OpenAsync(ownerMcp, new { resourceId = first.ResourceId }));
        await OpenAsync(ownerMcp, new { resourceId = second.ResourceId });
        await ErrorAsync(ownerMcp, "open_workbook", new { resourceId = third.ResourceId });

        await OpenAsync(peerMcp, new { resourceId = second.ResourceId });
        await ErrorAsync(peerMcp, "open_workbook", new { resourceId = first.ResourceId });
        await SuccessAsync(ownerMcp, "close_workbook", new { workbookHandle });
        await AssertReleasedAsync(host, first.ResourceId);
        await OpenAsync(peerMcp, new { resourceId = first.ResourceId });
    }

    private static async Task<string> OpenWriterAsync(McpHttpSession mcp, Guid resourceId)
    {
        var opened = await OpenAsync(mcp, new { resourceId });
        var handle = Handle(opened);
        var sheets = await SuccessAsync(mcp, "list_sheets", new { workbookHandle = handle });
        var sheetId = Assert.Single(sheets.GetProperty("sheets").EnumerateArray()).GetProperty("sheetId").GetUInt32();
        AssertWriteStatus(await SetNumberAsync(mcp, handle, sheetId, "A1", "10", Revision(opened)), "Success");
        return handle;
    }
}
