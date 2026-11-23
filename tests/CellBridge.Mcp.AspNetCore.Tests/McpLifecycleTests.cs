using System.Net;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.Tests;
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BusyWorkbookRejectsAnotherRequestWhenItsQueueIsDisabled(bool reopen)
    {
        var findEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumeFind = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        LifecycleRepository? repository = null;
        await using var host = await McpTestHost.StartAsync(configureProtocol: options =>
        {
            options.MaxConcurrentRequestsPerSubject = 2;
            options.MaxQueuedRequestsPerSubject = 0;
        }, wrapRepository: inner => repository = new(inner));
        var document = await host.CreateWorkbookAsync();
        using var http = host.CreateHttp("owner");
        var mcp = new McpHttpSession(http);
        await mcp.InitializeAsync();
        var workbookHandle = Handle(await OpenAsync(mcp, new { resourceId = document.ResourceId }));

        repository!.PauseNextFind = async token =>
        {
            findEntered.SetResult();
            await resumeFind.Task.WaitAsync(token);
        };
        var active = SuccessAsync(mcp, "list_sheets", new { workbookHandle });
        var tool = reopen ? "open_workbook" : "list_sheets";
        object arguments = reopen ? new { resourceId = document.ResourceId } : new { workbookHandle };
        try
        {
            await findEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var error = await ErrorAsync(mcp, tool, arguments).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Contains("The workbook is busy.", error);
        }
        finally
        {
            resumeFind.TrySetResult();
            await active;
        }
        await SuccessAsync(mcp, tool, arguments);
    }

    [Fact]
    public async Task FailedCloseRetainsItsSlotUntilParticipationReleaseSucceeds()
    {
        LifecycleRepository? repository = null;
        await using var host = await McpTestHost.StartAsync(configure: options =>
            options.MaxConcurrentSessionsPerActor = 2, wrapRepository: inner => repository = new(inner));
        var first = await host.CreateWorkbookAsync();
        var second = await host.CreateWorkbookAsync("/shared/second.xlsx");
        var third = await host.CreateWorkbookAsync("/shared/third.xlsx");
        using var http = host.CreateHttp("owner");
        var mcp = new McpHttpSession(http);
        var otherSession = new McpHttpSession(http);
        await mcp.InitializeAsync();
        await otherSession.InitializeAsync();
        var workbookHandle = await OpenWriterAsync(mcp, first.ResourceId);
        await OpenAsync(mcp, new { resourceId = second.ResourceId });

        var proxy = Assert.IsType<LifecycleRepository>(repository);
        proxy.FailRelease = true;
        try
        {
            var error = await ErrorAsync(mcp, "close_workbook", new { workbookHandle });
            Assert.Contains("Workbook participation release could not be confirmed.", error);
            var control = await host.ReadControlAsync(first.ResourceId);
            Assert.Single(control.Editors);
            Assert.Single(control.Coordination.SchemaOwners);

            Assert.Contains("Workbook unavailable.", await ErrorAsync(otherSession, "close_workbook", new { workbookHandle }));
            Assert.Contains("Workbook unavailable.", await ErrorAsync(mcp, "list_sheets", new { workbookHandle }));
            Assert.Contains("Workbook unavailable.", await ErrorAsync(mcp, "open_workbook", new { resourceId = first.ResourceId }));
            Assert.Contains("session limit was reached", await ErrorAsync(mcp, "open_workbook", new { resourceId = third.ResourceId }));
            Assert.Contains("Workbook participation release could not be confirmed.",
                await ErrorAsync(mcp, "close_workbook", new { workbookHandle }));
        }
        finally
        {
            proxy.FailRelease = false;
        }

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while ((await host.ReadControlAsync(first.ResourceId)).Editors.Length > 0)
            await Task.Delay(TimeSpan.FromMilliseconds(50), deadline.Token);
        await AssertReleasedAsync(host, first.ResourceId);
        // A repeated open waits for the same entry if the release is finishing after its last storage command.
        var reopened = await OpenAsync(mcp, new { resourceId = first.ResourceId });
        Assert.NotEqual(workbookHandle, Handle(reopened));
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

    private sealed class LifecycleRepository(IDocumentRepository inner) : RepositoryTestProxy(inner)
    {
        public Func<CancellationToken, Task>? PauseNextFind;
        public volatile bool FailRelease;

        public override async ValueTask<DocumentHead?> FindAsync(DocumentIdentity identity, CancellationToken ct = default)
        {
            if (Interlocked.Exchange(ref PauseNextFind, null) is { } pause)
                await pause(ct);
            return await base.FindAsync(identity, ct);
        }

        public override ValueTask<ControlResult> ExecuteControlAsync(DocumentControlCommand command,
            CancellationToken ct = default)
        {
            if (FailRelease && command is DocumentControlCommand.Office office &&
                (office.Request.SubRequestDataAttributes.GetValueOrDefault("CoauthRequestType") == "ExitCoauthoring" ||
                 office.Request.SubRequestDataAttributes.GetValueOrDefault("EditorsTableRequestType") == "LeaveEditingSession"))
                throw new IOException("Test storage release failure.");
            return base.ExecuteControlAsync(command, ct);
        }
    }
}
