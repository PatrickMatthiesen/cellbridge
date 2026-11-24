using CellBridge.Client;
using CellBridge.Client.Excel;
using CellBridge.Client.PowerPoint;
using CellBridge.Client.Word;
using ModelContextProtocol;
using System.Security.Cryptography;
using System.Text;

namespace CellBridge.Mcp.AspNetCore;

// Mutable fields belong to the entry gate. The registry never holds its own lock during document I/O.
internal sealed class McpWorkbookEntry(
    McpWorkbookRegistry.WorkbookKey key, McpDocumentFormat format, long lifecycleGeneration,
    DateTimeOffset? expiresAt, TimeSpan idleTimeout, DateTimeOffset openedAt, string handle, McpWorkbookTransport transport)
{
    private const int MaxRevisionTokens = 32;
    private const int MaxSheetOperations = 1024;
    private const int MaxOfficeOperations = 1024;

    private readonly byte[] _revisionKey = RandomNumberGenerator.GetBytes(32);
    private readonly OrderedDictionary<string, string> _revisions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SheetOperation> _sheetOperations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, OfficeOperation> _officeOperations = new(StringComparer.Ordinal);
    private string? _pendingWrite;
    private int _queuedRequests;

    public const string PendingWriteMessage = "A write outcome is unresolved. Retry that tool call with the same arguments.";
    private const string LostWriteMessage = "The write response was lost. Retry the tool call with the same arguments.";

    public McpWorkbookRegistry.WorkbookKey Key { get; } = key;
    public McpDocumentFormat Format { get; } = format;
    public long LifecycleGeneration { get; } = lifecycleGeneration;
    public DateTimeOffset? ExpiresAt { get; set; } = expiresAt;
    public string Handle { get; } = handle;
    public McpWorkbookTransport Transport { get; } = transport;
    public SemaphoreSlim Gate { get; } = new(1, 1);
    public volatile bool Closing;
    public bool Closed { get; set; }
    public bool Removed { get; set; }
    public DateTimeOffset RetryReleaseAt { get; set; }
    public DateTimeOffset LastUsed { get; set; } = openedAt;
    public CellBridgeDocumentSession? Document { get; set; }
    public ExcelWorkbookSession? Workbook { get; private set; }
    public WordDocumentSession? Word { get; private set; }
    public PowerPointPresentationSession? Presentation { get; private set; }
    public string Revision => Format switch
    {
        McpDocumentFormat.Excel => Workbook!.GetWorkbookSummary().Revision,
        McpDocumentFormat.Word => Word!.Revision,
        _ => Presentation!.Revision,
    };
    public bool HasPendingCommit => Format switch
    {
        McpDocumentFormat.Excel => Workbook!.HasPendingCommit,
        McpDocumentFormat.Word => Word!.HasPendingCommit,
        _ => Presentation!.HasPendingCommit,
    };

    public void Initialize(long maxPackageBytes, int maxReadCells)
    {
        switch (Format)
        {
            case McpDocumentFormat.Excel:
                Workbook = new ExcelWorkbookSession(Document!, new ExcelWorkbookLimits
                {
                    MaxPackageBytes = maxPackageBytes,
                    MaxSessionBytes = maxPackageBytes * 8,
                    MaxCellsPerOperation = Math.Max(10_000, maxReadCells),
                    TrackSyncChanges = true,
                });
                break;
            case McpDocumentFormat.Word:
                Word = new WordDocumentSession(Document!, maxPackageBytes * 8);
                break;
            case McpDocumentFormat.PowerPoint:
                Presentation = new PowerPointPresentationSession(Document!, maxPackageBytes * 8);
                break;
        }
    }

    public bool IsExpired(DateTimeOffset now) => ExpiresAt <= now || now - LastUsed >= idleTimeout;

    public async Task WaitForRequestAsync(int queueLimit, TimeSpan timeout, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Gate.Wait(0))
            return;

        var queued = Interlocked.Increment(ref _queuedRequests);
        try
        {
            if (queued > queueLimit)
                throw new McpException(Format == McpDocumentFormat.Excel
                    ? "The workbook is busy. Retry the request."
                    : "The document is busy. Retry the request.");
            if (!await Gate.WaitAsync(timeout, cancellationToken))
                throw new McpException(Format == McpDocumentFormat.Excel
                    ? "The request timed out waiting for the workbook."
                    : "The request timed out waiting for the document.");
        }
        finally
        {
            Interlocked.Decrement(ref _queuedRequests);
        }
    }

    public void ClearDocument()
    {
        Document = null;
        Workbook = null;
        Word = null;
        Presentation = null;
        _revisions.Clear();
        _sheetOperations.Clear();
        _officeOperations.Clear();
        _pendingWrite = null;
    }

    public string TokenFor(string revision)
    {
        var token = Convert.ToHexString(HMACSHA256.HashData(_revisionKey, Encoding.UTF8.GetBytes(revision)));
        if (_revisions.Count >= MaxRevisionTokens && !_revisions.ContainsKey(token))
            _revisions.RemoveAt(0);
        _revisions[token] = revision;
        return token;
    }

    public string ResolveRevision(string token)
    {
        if (token is not { Length: 64 } || !_revisions.TryGetValue(token, out var revision))
            throw new McpException("The revision is unavailable for this handle.");
        return revision;
    }

    public async Task<McpWriteResult> WriteAtRevisionAsync(string baseRevision, string mutation,
        Func<string, CancellationToken, Task<ExcelWriteResult>> write, CancellationToken cancellationToken)
    {
        if (_pendingWrite != mutation && !_revisions.ContainsKey(baseRevision))
            return Present(new ExcelWriteResult(ExcelWriteStatus.BaseRevisionUnavailable, Workbook!.GetWorkbookSummary().Revision, []));
        return await WriteWorkbookAsync(mutation, token => write(_revisions[baseRevision], token), cancellationToken);
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

        var result = await WriteWorkbookAsync("add_sheet:" + operationId, token => Workbook!.AddSheetAsync(sheetName, token),
            cancellationToken);
        if (!Workbook!.HasPendingCommit)
            operation.Result = result;
        return result;
    }

    public async Task<McpOfficeWriteResult> WriteOfficeAsync(string baseRevision, string mutation, string? operationId,
        Func<string, CancellationToken, Task<OfficeWriteResult>> write, CancellationToken cancellationToken)
    {
        if (operationId is null)
            return await WriteOfficeProposalAsync(baseRevision, mutation, write, cancellationToken);

        // Bind unresolved proposals to the operation ID as well as the tool arguments.
        var proposal = mutation + ":" + operationId;
        if (_officeOperations.TryGetValue(operationId, out var operation))
        {
            if (operation.Mutation != mutation)
                throw new McpException("operationId has already been used with a different tool or arguments.");
            if (operation.Result is { } replay)
                return replay;
        }
        else
        {
            EnsurePendingWriteMatches(proposal);
            if (_officeOperations.Count >= MaxOfficeOperations)
                throw new McpException("The Office operation limit for this handle was reached.");
            operation = new OfficeOperation(mutation);
            _officeOperations.Add(operationId, operation);
        }

        var result = await WriteOfficeProposalAsync(baseRevision, proposal, write, cancellationToken);
        // Store the exact result before the registry checks cancellation and returns it to MCP.
        if (!HasPendingCommit)
            operation.Result = result;
        return result;
    }

    private async Task<McpOfficeWriteResult> WriteOfficeProposalAsync(string baseRevision, string mutation,
        Func<string, CancellationToken, Task<OfficeWriteResult>> write, CancellationToken cancellationToken)
    {
        if (_pendingWrite is null && !_revisions.ContainsKey(baseRevision))
            return new(OfficeWriteStatus.BaseRevisionUnavailable.ToString(), TokenFor(Revision), [], null, null);

        var result = await WriteAsync(mutation, token => write(_revisions[baseRevision], token),
            token => Format == McpDocumentFormat.Word
                ? Word!.RetryPendingCommitAsync(token)
                : Presentation!.RetryPendingCommitAsync(token),
            () => new OfficeWriteResult(OfficeWriteStatus.OutcomeUnknown, Revision, [], Detail: LostWriteMessage),
            cancellationToken);
        var detail = result.Status == OfficeWriteStatus.OutcomeUnknown && HasPendingCommit
            ? LostWriteMessage
            : result.Detail;
        return new(result.Status.ToString(), TokenFor(result.Revision), result.Conflicts, detail, result.AddedSlideId);
    }

    private async Task<McpWriteResult> WriteWorkbookAsync(string mutation,
        Func<CancellationToken, Task<ExcelWriteResult>> write, CancellationToken cancellationToken)
    {
        var result = await WriteAsync(mutation, write, token => Workbook!.RetryPendingCommitAsync(token),
            () => new ExcelWriteResult(ExcelWriteStatus.OutcomeUnknown, Revision, [], LostWriteMessage),
            cancellationToken);
        return Present(result);
    }

    private async Task<T> WriteAsync<T>(string mutation, Func<CancellationToken, Task<T>> write,
        Func<CancellationToken, Task<T>> retry, Func<T> lostResponse, CancellationToken cancellationToken)
    {
        EnsurePendingWriteMatches(mutation);
        try
        {
            return _pendingWrite is null
                ? await write(cancellationToken)
                : await retry(cancellationToken);
        }
        catch (IOException) when (HasPendingCommit && !cancellationToken.IsCancellationRequested)
        {
            return lostResponse();
        }
        finally
        {
            _pendingWrite = HasPendingCommit ? mutation : null;
        }
    }

    private void EnsurePendingWriteMatches(string mutation)
    {
        if (_pendingWrite is not null && _pendingWrite != mutation)
            throw new McpException(PendingWriteMessage);
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

    private sealed class OfficeOperation(string mutation)
    {
        public string Mutation { get; } = mutation;
        public McpOfficeWriteResult? Result { get; set; }
    }
}

internal sealed record McpOfficeWriteResult(string Status, string Revision, IReadOnlyList<string> Conflicts,
    string? Detail, uint? AddedSlideId);
