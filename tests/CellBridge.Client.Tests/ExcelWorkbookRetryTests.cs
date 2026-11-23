using CellBridge.AspNetCore;
using CellBridge.Client.Excel;
using CellBridge.FssHttp;
using CellBridge.FssHttpB;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CellBridge.Client.Tests;

public sealed class ExcelWorkbookRetryTests
{
    [Fact]
    public async Task ReplaysUnresolvedExcelProposalWithIdenticalBytes()
    {
        await using var host = await StartAsync();
        var interceptor = new InterceptSaves(host.Transport, async (attempt, send) =>
        {
            var response = await send();
            if (attempt <= 2)
                throw new CellBridgeProtocolException("CellRequestFail");
            return response;
        });
        await using var document = await OpenAsync(interceptor, host.Uri);
        var workbook = new ExcelWorkbookSession(document);
        var summary = workbook.GetWorkbookSummary();
        var sheet = Assert.Single(summary.Sheets).SheetId;
        Assert.False(summary.Date1904);
        var before = workbook.ReadRangeAtRevision(sheet, "A1");
        Assert.Equal(summary.Revision, before.Revision);
        var initial = await host.Storage.Documents.ReadControlAsync(document.ResourceId);

        var error = await Assert.ThrowsAsync<IOException>(() =>
            workbook.SetCellsAsync(sheet, [new("A1", ExcelCellValue.Number(42))], before.Revision));
        Assert.Equal("CellRequestFail", Assert.IsType<CellBridgeProtocolException>(error.InnerException).ErrorCode);
        await Assert.ThrowsAsync<InvalidOperationException>(() => workbook.SyncAsync());

        var result = await workbook.RetryPendingCommitAsync();
        Assert.Equal(ExcelWriteStatus.Success, result.Status);
        Assert.Equal(result.Revision, workbook.GetWorkbookSummary().Revision);
        Assert.Equal(3, interceptor.Payloads.Count);
        Assert.All(interceptor.Payloads, payload => Assert.Equal(interceptor.Payloads[0], payload));
        Assert.Equal(ExcelCellValue.Number(42), Assert.Single(workbook.ReadRange(sheet, "A1")).Value);
        var head = await host.Storage.Documents.ReadControlAsync(document.ResourceId);
        Assert.Equal(initial!.Head.ContentVersion + 1, head!.Head.ContentVersion);
    }

    [Theory]
    [InlineData(1, ExcelWriteStatus.RetryExhausted)]
    [InlineData(2, ExcelWriteStatus.Success)]
    public async Task ConfirmedCoherencyFailureRebasesWithBoundedAttempts(int maxAttempts, ExcelWriteStatus status)
    {
        await using var host = await StartAsync();
        await using var peerDocument = await OpenAsync(host.Transport, host.Uri);
        var peer = new ExcelWorkbookSession(peerDocument);
        var sheet = Assert.Single(peer.ListSheets()).SheetId;
        var original = peer.ReadRangeAtRevision(sheet, "B1").Revision;
        var interceptor = new InterceptSaves(host.Transport, async (attempt, send) =>
        {
            if (attempt == 1)
            {
                var competing = await peer.SetCellsAsync(sheet, [new("B1", ExcelCellValue.Number(95))], original);
                Assert.Equal(ExcelWriteStatus.Success, competing.Status);
            }
            return await send();
        });
        await using var document = await OpenAsync(interceptor, host.Uri);
        var workbook = new ExcelWorkbookSession(document, new() { MaxCommitAttempts = maxAttempts });
        var result = await workbook.SetCellsAsync(sheet, [new("A1", ExcelCellValue.Number(42))], original);

        Assert.Equal(status, result.Status);
        Assert.Equal(maxAttempts, interceptor.Payloads.Count);
        var cells = workbook.ReadRange(sheet, "A1:B1");
        if (status == ExcelWriteStatus.Success)
        {
            Assert.Equal(ExcelCellValue.Number(42), cells[0].Value);
            Assert.NotEqual(interceptor.Payloads[0], interceptor.Payloads[1]);
        }
        else
        {
            Assert.Equal(ExcelCellValue.Blank, cells[0].Value);
        }
        Assert.Equal(ExcelCellValue.Number(95), cells[1].Value);
    }

    private static async Task<ExcelHost> StartAsync()
    {
        var content = new InMemoryContentStore();
        var storage = new StorageProvider(new InMemoryDocumentRepository(content), content);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddAuthentication();
        builder.Services.AddCellBridge(storage, requireDurability: false,
            configure: options => options.EnableOfficeRealTime = false);
        var app = builder.Build();
        try
        {
            var uri = new Uri("http://localhost/retry.xlsx");
            var actor = new CellBridgeActor(new("excel:test", "excel", "Excel"), CanCreate: true);
            await app.Services.GetRequiredService<CellBridgeDocumentService>().CreateAsync(uri.AbsolutePath,
                OfficeDocumentTemplates.CreateExcelWorkbook(), actor);
            var processor = app.Services.GetRequiredService<CellBridgeRequestProcessor>();
            return new ExcelHost(app, storage, uri, new InProcessCellBridgeTransport(processor, "http://localhost", actor));
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }
    }

    private static Task<CellBridgeDocumentSession> OpenAsync(ICellBridgeClientTransport transport, Uri uri) =>
        CellBridgeDocumentSession.OpenAsync(transport, uri, new() { Mode = CellBridgeDocumentSessionMode.EagerWriter });

    private sealed record ExcelHost(WebApplication App, StorageProvider Storage, Uri Uri, ICellBridgeClientTransport Transport)
        : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => App.DisposeAsync();
    }

    private sealed class InterceptSaves(ICellBridgeClientTransport inner,
        Func<int, Func<Task<CellStorageExecution>>, Task<CellStorageExecution>> intercept) : ICellBridgeClientTransport
    {
        public List<byte[]> Payloads { get; } = [];

        public Task<CellStorageExecution> ExecuteAsync(CellStorageRequest request, CancellationToken cancellationToken = default)
        {
            foreach (var subrequest in request.Requests.SelectMany(file => file.SubRequests).Where(item => item.Type == SubRequestType.Cell))
            {
                var payload = subrequest.SubRequestDataBinary
                    ?? Convert.FromBase64String(Assert.IsType<string>(subrequest.SubRequestDataXml));
                var cell = FsshttpbCellRequest.Deserialize(new BinaryReaderEx(payload));
                if (cell.SubRequests.Any(item => item.Data is PutChangesSubRequestData))
                {
                    Payloads.Add(payload);
                    return intercept(Payloads.Count, () => inner.ExecuteAsync(request, cancellationToken));
                }
            }
            return inner.ExecuteAsync(request, cancellationToken);
        }

        public Task<ICellBridgeRealTimeChannel?> TryConnectRealTimeAsync(RealTimeConnectionTarget target,
            CancellationToken cancellationToken = default) => inner.TryConnectRealTimeAsync(target, cancellationToken);
    }
}
