using System.Text.Json;
using CellBridge.Client;
using CellBridge.Client.Excel;
using CellBridge.Storage.Abstractions;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;

namespace CellBridge.Mcp.AspNetCore.Tests;

public sealed class McpWorkbookTests
{
    [Fact]
    public async Task StreamableHttpCanEditAndCloseAWorkbook()
    {
        await using var host = await McpTestHost.StartAsync(configure: options => options.MaxReadCells = 2);
        var document = await host.CreateWorkbookAsync();
        using var http = host.CreateHttp("owner");
        var mcp = new McpHttpSession(http);
        await mcp.InitializeAsync();

        using var listing = await mcp.SendAsync("tools/list");
        var tools = McpHttpSession.AssertResult(await McpHttpSession.ReadMessageAsync(listing))
            .GetProperty("tools").EnumerateArray().ToDictionary(tool => tool.GetProperty("name").GetString()!);
        Assert.All(new[] { "open_workbook", "list_sheets", "read_range", "set_cells", "clear_range",
            "add_sheet", "sync_workbook", "get_editors", "close_workbook" }, name => Assert.Contains(name, tools));
        Assert.True(tools["read_range"].GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean());
        Assert.True(tools["clear_range"].GetProperty("annotations").GetProperty("destructiveHint").GetBoolean());

        var workbook = await OpenAsync(mcp, new { resourceId = document.ResourceId });
        Assert.Equal(document.ResourceId, workbook.GetProperty("resourceId").GetGuid());
        var workbookHandle = Handle(workbook);
        Assert.Equal(workbookHandle, Handle(await OpenAsync(mcp, new { path = McpTestHost.WorkbookPath })));
        var sheets = await SuccessAsync(mcp, "list_sheets", new { workbookHandle });
        var sheetId = Assert.Single(sheets.GetProperty("sheets").EnumerateArray()).GetProperty("sheetId").GetUInt32();
        var initial = await ReadAsync(mcp, workbookHandle, sheetId, "A1:B1");
        Assert.Equal(Revision(workbook), Revision(initial));
        AssertBlank(initial, "A1");
        await ErrorAsync(mcp, "read_range", new { workbookHandle, sheetId, a1Range = "A1:C1" });

        var editors = await SuccessAsync(mcp, "get_editors", new { workbookHandle });
        Assert.Equal(McpTestHost.Owner.Identity.Login,
            Assert.Single(editors.GetProperty("editors").EnumerateArray()).GetProperty("loginName").GetString());
        var control = await host.ReadControlAsync(document.ResourceId);
        Assert.False(Assert.Single(control.Editors).AsEditor);
        Assert.Empty(control.Coordination.SchemaOwners);

        var write = await SetNumberAsync(mcp, workbookHandle, sheetId, "A1", "12", Revision(initial));
        AssertWriteStatus(write, "Success");
        Assert.Equal("12", CellValue(await ReadAsync(mcp, workbookHandle, sheetId, "A1"), "A1"));
        Assert.Equal(McpTestHost.Owner.Identity.Subject,
            Assert.Single((await host.ReadControlAsync(document.ResourceId)).Coordination.SchemaOwners).OwnerSubject);
        var failedPrecondition = await SuccessAsync(mcp, "set_cells", new
        {
            workbookHandle, sheetId, baseRevision = Revision(write),
            edits = new[] { new { address = "A1", value = new { type = "Number", rawValue = "34" } } },
            expected = new { A1 = new { type = "Number", rawValue = "56" } },
        });
        AssertWriteStatus(failedPrecondition, "CellConflict");
        Assert.Equal(Revision(write), Revision(failedPrecondition));

        var clear = await SuccessAsync(mcp, "clear_range", new
        {
            workbookHandle, sheetId, a1Range = "A1", baseRevision = Revision(write),
        });
        AssertWriteStatus(clear, "Success");
        AssertBlank(await ReadAsync(mcp, workbookHandle, sheetId, "A1"), "A1");

        var addArguments = new { workbookHandle, sheetName = "Forecast", operationId = "forecast-2027" };
        var added = await SuccessAsync(mcp, "add_sheet", addArguments);
        AssertWriteStatus(added, "Success");
        var addedAgain = await SuccessAsync(mcp, "add_sheet", addArguments);
        Assert.Equal(Revision(added), Revision(addedAgain));
        await ErrorAsync(mcp, "add_sheet", new { workbookHandle, sheetName = "Other", addArguments.operationId });
        var latest = await SuccessAsync(mcp, "sync_workbook", new { workbookHandle });
        Assert.Equal(Revision(added), Revision(latest));

        var latestSheets = await SuccessAsync(mcp, "list_sheets", new { workbookHandle });
        var forecast = Assert.Single(latestSheets.GetProperty("sheets").EnumerateArray(),
            sheet => sheet.GetProperty("name").GetString() == "Forecast");
        Assert.Equal(added.GetProperty("addedSheetId").GetUInt32(), forecast.GetProperty("sheetId").GetUInt32());

        Assert.True((await SuccessAsync(mcp, "close_workbook", new { workbookHandle })).GetProperty("closed").GetBoolean());
        await ErrorAsync(mcp, "list_sheets", new { workbookHandle });
        await AssertReleasedAsync(host, document.ResourceId);
        await mcp.EndAsync();
    }

    [Fact]
    public async Task HttpPeerMergesDisjointEditsAndMcpReportsOverlappingCells()
    {
        await using var host = await McpTestHost.StartAsync();
        var document = await host.CreateWorkbookAsync();
        await host.SetPeerAccessAsync(document.ResourceId, DocumentAccess.Read | DocumentAccess.Write);
        using var ownerHttp = host.CreateHttp("owner");
        using var peerHttp = host.CreateHttp("peer");
        var mcp = new McpHttpSession(ownerHttp);
        await mcp.InitializeAsync();
        var opened = await OpenAsync(mcp, new { path = McpTestHost.WorkbookPath });
        Assert.Equal(document.ResourceId, opened.GetProperty("resourceId").GetGuid());
        var workbookHandle = Handle(opened);
        var baseRevision = Revision(opened);

        await using var peer = await CellBridgeDocumentSession.OpenAsync(host.CreateTransport(peerHttp), host.WorkbookUrl,
            new CellBridgeDocumentSessionOptions { Mode = CellBridgeDocumentSessionMode.EagerWriter, EnableRealTime = false });
        var workbook = new ExcelWorkbookSession(peer);
        var sheetId = Assert.Single(workbook.ListSheets()).SheetId;
        var peerBaseRevision = peer.GetSnapshot().Revision;

        var agentEdit = SetNumberAsync(mcp, workbookHandle, sheetId, "A1", "10", baseRevision);
        var peerEdit = workbook.SetCellsAsync(sheetId, [new("B1", ExcelCellValue.Number(20))], peerBaseRevision);
        await Task.WhenAll(agentEdit, peerEdit);
        AssertWriteStatus(await agentEdit, "Success");
        Assert.Equal(ExcelWriteStatus.Success, (await peerEdit).Status);
        var presence = await host.ReadControlAsync(document.ResourceId);
        Assert.Equal(new[] { McpTestHost.Owner.Identity.Subject, McpTestHost.Peer.Identity.Subject },
            presence.Coordination.SchemaOwners.Select(lease => lease.OwnerSubject).Order(StringComparer.Ordinal));

        var sync = await SuccessAsync(mcp, "sync_workbook", new { workbookHandle });
        var sharedRevision = Revision(sync);
        var change = Assert.Single(sync.GetProperty("changes").EnumerateArray(),
            entry => entry.GetProperty("address").GetString() == "B1");
        Assert.Equal("20", change.GetProperty("after").GetProperty("rawValue").GetString());
        var peerSharedRevision = await workbook.SyncAsync();
        var merged = await ReadAsync(mcp, workbookHandle, sheetId, "A1:B1");
        Assert.Equal("10", CellValue(merged, "A1"));
        Assert.Equal("20", CellValue(merged, "B1"));

        var first = await workbook.SetCellsAsync(sheetId, [new("A1", ExcelCellValue.Number(30))], peerSharedRevision);
        Assert.Equal(ExcelWriteStatus.Success, first.Status);
        var conflict = await SetNumberAsync(mcp, workbookHandle, sheetId, "A1", "40", sharedRevision);
        AssertWriteStatus(conflict, "CellConflict");
        Assert.NotEqual(sharedRevision, Revision(conflict));
        var cell = Assert.Single(conflict.GetProperty("conflicts").EnumerateArray());
        Assert.Equal("A1", cell.GetProperty("address").GetString());
        Assert.Equal("10", cell.GetProperty("base").GetProperty("rawValue").GetString());
        Assert.Equal("30", cell.GetProperty("remote").GetProperty("rawValue").GetString());
        Assert.Equal("40", cell.GetProperty("local").GetProperty("rawValue").GetString());

        var saved = await ownerHttp.GetByteArrayAsync(McpTestHost.WorkbookPath);
        using var package = SpreadsheetDocument.Open(new MemoryStream(saved), false);
        Assert.Empty(new OpenXmlValidator().Validate(package));
        Assert.Equal(new[] { "30", "20" }, ExcelWorkbook.ReadRange(saved, sheetId, "A1:B1")
            .Select(entry => entry.Value.RawValue));
    }

    internal static Task<JsonElement> OpenAsync(McpHttpSession mcp, object arguments) =>
        SuccessAsync(mcp, "open_workbook", arguments);

    internal static string Handle(JsonElement opened) =>
        Assert.IsType<string>(opened.GetProperty("workbookHandle").GetString());

    internal static string Revision(JsonElement result) =>
        Assert.IsType<string>(result.GetProperty("revision").GetString());

    internal static Task<JsonElement> ReadAsync(McpHttpSession mcp, string workbookHandle, uint sheetId, string a1Range) =>
        SuccessAsync(mcp, "read_range", new { workbookHandle, sheetId, a1Range });

    internal static object SetNumberArguments(string workbookHandle, uint sheetId, string address, string value, string baseRevision) =>
        new { workbookHandle, sheetId, baseRevision, edits = new[] { new { address, value = new { type = "Number", rawValue = value } } } };

    internal static Task<JsonElement> SetNumberAsync(McpHttpSession mcp, string workbookHandle, uint sheetId,
        string address, string value, string baseRevision) =>
        SuccessAsync(mcp, "set_cells", SetNumberArguments(workbookHandle, sheetId, address, value, baseRevision));

    internal static void AssertWriteStatus(JsonElement result, string status) =>
        Assert.Equal(status, result.GetProperty("status").GetString(), ignoreCase: true);

    internal static string? CellValue(JsonElement result, string address) =>
        FindValue(result, address).GetProperty("rawValue").GetString();

    private static void AssertBlank(JsonElement result, string address)
    {
        var value = FindValue(result, address);
        Assert.Equal("Blank", value.GetProperty("type").GetString());
        Assert.True(!value.TryGetProperty("rawValue", out var raw) || raw.ValueKind == JsonValueKind.Null);
    }

    private static JsonElement FindValue(JsonElement result, string address) =>
        Assert.Single(result.GetProperty("cells").EnumerateArray(), cell => cell.GetProperty("address").GetString() == address)
            .GetProperty("value");

    internal static async Task<JsonElement> SuccessAsync(McpHttpSession mcp, string tool, object arguments)
    {
        var result = await mcp.CallAsync(tool, arguments);
        Assert.False(result.TryGetProperty("isError", out var isError) && isError.GetBoolean(), result.ToString());
        return result.GetProperty("structuredContent");
    }

    internal static async Task<string> ErrorAsync(McpHttpSession mcp, string tool, object arguments)
    {
        var result = await mcp.CallAsync(tool, arguments);
        Assert.True(result.TryGetProperty("isError", out var isError) && isError.GetBoolean(), result.ToString());
        return Assert.IsType<string>(Assert.Single(result.GetProperty("content").EnumerateArray()).GetProperty("text").GetString());
    }

    internal static async Task AssertReleasedAsync(McpTestHost host, Guid resourceId)
    {
        var control = await host.ReadControlAsync(resourceId);
        Assert.Empty(control.Editors);
        Assert.Empty(control.Coordination.SchemaOwners);
    }
}
