using CellBridge.AspNetCore;
using CellBridge.Client;
using CellBridge.Client.Excel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace CellBridge.Packages.Tests;

public sealed class ClientPackageConsumerTests
{
    [Fact]
    public async Task PackagedClientCanOpenSaveAndSyncFromPackagedHost()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        TestActor.Register(builder.Services);
        builder.Services.AddCellBridge(TestStorage.Memory(), requireDurability: false,
            configure: options => options.EnableOfficeRealTime = false);
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapCellBridge();
        await app.StartAsync();

        const string path = "/shared/package-consumer.xlsx";
        var package = OfficeDocumentTemplates.CreateExcelWorkbook();
        var documents = app.Services.GetRequiredService<CellBridgeDocumentService>();
        Assert.NotNull(await documents.CreateAsync(path, package, TestActor.Value));

        using var http = TestActor.CreateClient(app.GetTestServer(), "writer");
        var transport = new HttpCellBridgeTransport(http, http.BaseAddress!);
        await using var session = await CellBridgeDocumentSession.OpenAsync(transport, new Uri(http.BaseAddress!, path));
        var initial = session.GetSnapshot();
        Assert.Equal(package, initial.Package);
        var workbook = new ExcelWorkbookSession(session);
        var sheet = Assert.Single(workbook.ListSheets());
        Assert.Equal("Sheet1", sheet.Name);
        Assert.Equal(ExcelCellValue.Blank, Assert.Single(workbook.ReadRange(sheet.SheetId, "A1")).Value);

        await session.SyncAsync();
        Assert.Equal(initial.Revision, session.GetSnapshot().Revision);
        Assert.Equal(package, session.GetSnapshot().Package);

        await using var writer = await CellBridgeDocumentSession.OpenAsync(transport, new Uri(http.BaseAddress!, path),
            new() { Mode = CellBridgeDocumentSessionMode.LazyWriter });
        var editor = new ExcelWorkbookSession(writer);
        var savedValue = ExcelCellValue.Number(42);
        var result = await editor.SetCellsAsync(sheet.SheetId, [new("A1", savedValue)], writer.GetSnapshot().Revision);
        Assert.Equal(ExcelWriteStatus.Success, result.Status);

        await session.SyncAsync();
        Assert.NotEqual(initial.Revision, session.GetSnapshot().Revision);
        Assert.Equal(savedValue, Assert.Single(workbook.ReadRange(sheet.SheetId, "A1")).Value);
    }
}
