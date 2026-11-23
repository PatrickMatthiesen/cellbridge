#nullable enable
using System.Net.Http.Json;
using CellBridge.AspNetCore;
using CellBridge.Client;
using CellBridge.Client.Excel;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;

namespace CellBridge.Interop.Tests;

[Collection("WebDemo integration")]
public sealed class ClientWorkbookInteropTests(WebDemoFixture fixture)
{
    [DistinctUserFact]
    public async Task DistinctUsersRebaseDisjointEditsAndAnOpenSessionLosesWriteAccess()
    {
        var endpoint = new Uri(Environment.GetEnvironmentVariable("OFFICECOLLABSERVER_INTEROP_ENDPOINT")!);
        // Send requests to the fixture's listener. SOAP carries the document's public URL.
        var origin = new Uri(endpoint.GetLeftPart(UriPartial.Authority));
        using var firstHttp = LiveInteropHttp.Create(endpoint);
        using var secondHttp = fixture.CreateSecondUserHttpClient();
        var name = "client-coauthors-" + Guid.NewGuid().ToString("N");
        using var created = await firstHttp.PostAsJsonAsync("/api/documents", new { name, type = "xlsx" });
        created.EnsureSuccessStatusCode();
        var path = "/shared/" + name + ".xlsx";
        await fixture.GrantSecondUserWriteAccessAsync(path);

        var documentUrl = new Uri(LiveInteropHttp.PublicOrigin(endpoint), path);
        var options = new CellBridgeDocumentSessionOptions
        {
            Mode = CellBridgeDocumentSessionMode.EagerWriter,
            EnableRealTime = false,
        };
        await using var first = await CellBridgeDocumentSession.OpenAsync(
            new HttpCellBridgeTransport(firstHttp, origin), documentUrl, options);
        await using var second = await CellBridgeDocumentSession.OpenAsync(
            new HttpCellBridgeTransport(secondHttp, origin), documentUrl, options);

        var joined = await fixture.ReadControlAsync(path);
        var subjects = new[] { "local:integration-writer", "local:integration-writer-1" };
        Assert.Equal(subjects, joined.Editors.Select(editor => editor.Owner!.Subject).Order(StringComparer.Ordinal));
        Assert.Equal(subjects, joined.Coordination.SchemaOwners.Select(lease => lease.OwnerSubject).Order(StringComparer.Ordinal));

        var firstWorkbook = new ExcelWorkbookSession(first);
        var secondWorkbook = new ExcelWorkbookSession(second);
        var sheet = Assert.Single(firstWorkbook.ListSheets()).SheetId;
        var baseRevision = first.GetSnapshot().Revision;
        Assert.Equal(baseRevision, second.GetSnapshot().Revision);
        var writes = await Task.WhenAll(
            firstWorkbook.SetCellsAsync(sheet, [new("A1", ExcelCellValue.Number(10))], baseRevision),
            secondWorkbook.SetCellsAsync(sheet, [new("B1", ExcelCellValue.String("second writer"))], baseRevision));
        Assert.All(writes, write => Assert.Equal(ExcelWriteStatus.Success, write.Status));

        var saved = await firstHttp.GetByteArrayAsync(path);
        using var workbook = SpreadsheetDocument.Open(new MemoryStream(saved), false);
        Assert.Empty(new OpenXmlValidator().Validate(workbook));
        await first.SyncAsync();
        Assert.Equal(saved, first.GetSnapshot().Package);
        Assert.Equal(new[] { "10", "second writer" },
            firstWorkbook.ReadRange(sheet, "A1:B1").Select(cell => cell.Value.RawValue));

        await fixture.RevokeSecondUserWriteAccessAsync(path);
        var afterRevocation = await fixture.ReadControlAsync(path);
        Assert.Equal(subjects[0], Assert.Single(afterRevocation.Coordination.SchemaOwners).OwnerSubject);
        Assert.False(Assert.Single(afterRevocation.Editors, editor => editor.Owner!.Subject == subjects[1]).AsEditor);
        var latest = await secondWorkbook.SyncAsync();
        var denied = await Assert.ThrowsAsync<CellBridgeProtocolException>(() =>
            secondWorkbook.SetCellsAsync(sheet, [new("A1", ExcelCellValue.Number(20))], latest));
        Assert.Equal($"HResult {CellBridgeAuthorization.AccessDeniedHResult}", denied.ErrorCode);
        await first.SyncAsync();
        Assert.Equal(saved, first.GetSnapshot().Package);
    }
}
