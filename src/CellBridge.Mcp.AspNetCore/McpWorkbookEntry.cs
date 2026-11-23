using CellBridge.Client;
using CellBridge.Client.Excel;
using ModelContextProtocol;
using System.Security.Cryptography;
using System.Text;

namespace CellBridge.Mcp.AspNetCore;

// Mutable fields belong to the entry gate. The registry never holds its own lock during workbook I/O.
internal sealed class McpWorkbookEntry(
    McpWorkbookRegistry.WorkbookKey key, long lifecycleGeneration,
    DateTimeOffset? expiresAt, TimeSpan idleTimeout, DateTimeOffset openedAt, string handle, McpWorkbookTransport transport)
{
    private const int MaxRevisionTokens = 32;
    private const int MaxSheetOperations = 1024;

    private readonly byte[] _revisionKey = RandomNumberGenerator.GetBytes(32);
    private readonly Dictionary<string, string> _revisions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SheetOperation> _sheetOperations = new(StringComparer.Ordinal);
    private string? _pendingWrite;

    public McpWorkbookRegistry.WorkbookKey Key { get; } = key;
    public long LifecycleGeneration { get; } = lifecycleGeneration;
    public DateTimeOffset? ExpiresAt { get; } = expiresAt;
    public string Handle { get; } = handle;
    public McpWorkbookTransport Transport { get; } = transport;
    public SemaphoreSlim Gate { get; } = new(1, 1);
    public volatile bool Closing;
    public bool Closed { get; set; }
    public DateTimeOffset LastUsed { get; set; } = openedAt;
    public CellBridgeDocumentSession? Document { get; set; }
    public ExcelWorkbookSession? Workbook { get; set; }

    public bool IsExpired(DateTimeOffset now) => ExpiresAt <= now || now - LastUsed >= idleTimeout;

    public string TokenFor(string revision)
    {
        var token = Convert.ToHexString(HMACSHA256.HashData(_revisionKey, Encoding.UTF8.GetBytes(revision)));
        if (_revisions.Count >= MaxRevisionTokens && !_revisions.ContainsKey(token))
            _revisions.Remove(_revisions.Keys.First());
        _revisions[token] = revision;
        return token;
    }

    public async Task<McpWriteResult> WriteAtRevisionAsync(string baseRevision, string mutation,
        Func<string, CancellationToken, Task<ExcelWriteResult>> write, CancellationToken cancellationToken)
    {
        if (_pendingWrite != mutation && !_revisions.ContainsKey(baseRevision))
            return Present(new ExcelWriteResult(ExcelWriteStatus.BaseRevisionUnavailable, Workbook!.GetWorkbookSummary().Revision, []));
        return await WriteAsync(mutation, token => write(_revisions[baseRevision], token), cancellationToken);
    }

    public async Task<McpWriteResult> AddSheetAsync(string sheetName, string operationId,
        CancellationToken cancellationToken)
    {
        if (_sheetOperations.TryGetValue(operationId, out var operation))
        {
            if (operation.Name != sheetName)
                throw new McpException("operationId has already been used with a different sheetName.");
            if (operation.Result is { } replay)
                return replay;
        }
        else
        {
            if (_sheetOperations.Count >= MaxSheetOperations)
                throw new McpException("The add_sheet operation limit for this workbook handle was reached.");
            operation = new SheetOperation(sheetName);
            _sheetOperations.Add(operationId, operation);
        }

        var result = await WriteAsync("add_sheet:" + operationId, token => Workbook!.AddSheetAsync(sheetName, token),
            cancellationToken);
        if (!Workbook!.HasPendingCommit)
            operation.Result = result;
        return result;
    }

    private async Task<McpWriteResult> WriteAsync(string mutation, Func<CancellationToken, Task<ExcelWriteResult>> write,
        CancellationToken cancellationToken)
    {
        if (_pendingWrite is not null && _pendingWrite != mutation)
            throw new McpException("A write outcome is unresolved. Retry that tool call with the same arguments.");
        try
        {
            var result = _pendingWrite is null
                ? await write(cancellationToken)
                : await Workbook!.RetryPendingCommitAsync(cancellationToken);
            return Present(result);
        }
        catch (IOException) when (Workbook!.HasPendingCommit && !cancellationToken.IsCancellationRequested)
        {
            var revision = Workbook.GetWorkbookSummary().Revision;
            return Present(new ExcelWriteResult(ExcelWriteStatus.OutcomeUnknown, revision, [],
                "The write response was lost. Retry the tool call with the same arguments."));
        }
        finally
        {
            _pendingWrite = Workbook!.HasPendingCommit ? mutation : null;
        }
    }

    private McpWriteResult Present(ExcelWriteResult result) => new(result.Status.ToString(), TokenFor(result.Revision),
        result.Conflicts.Select(conflict => new McpCellConflict(conflict.Address,
            McpCellValue.From(conflict.Base), McpCellValue.From(conflict.Remote), McpCellValue.From(conflict.Local))).ToArray(),
        result.Detail, result.AddedSheetId);

    private sealed class SheetOperation(string name)
    {
        public string Name { get; } = name;
        public McpWriteResult? Result { get; set; }
    }
}
