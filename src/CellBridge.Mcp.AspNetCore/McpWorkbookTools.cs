using CellBridge.Client;
using CellBridge.Client.Excel;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CellBridge.Mcp.AspNetCore;

[McpServerToolType]
internal sealed class McpWorkbookTools(McpWorkbookRegistry registry, McpRequestIdentity identity,
    ILogger<McpWorkbookTools> logger)
{
    [McpServerTool(Name = "open_workbook", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Open an Excel workbook by resource ID or decoded application-relative path.")]
    public Task<McpOpenWorkbookResult> OpenWorkbook(
        [Description("The CellBridge document resource ID. Supply this or path.")] Guid? resourceId = null,
        [Description("Decoded workbook path, such as /library/Budget.xlsx. Supply this or resourceId.")] string? path = null,
        CancellationToken cancellationToken = default) =>
        RunAsync(async () =>
        {
            var opened = await registry.OpenAsync(identity, Target(resourceId, path), McpDocumentFormat.Excel, cancellationToken);
            return new McpOpenWorkbookResult(opened.Handle, opened.ResourceId, opened.Revision);
        });

    [McpServerTool(Name = "close_workbook", Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Close an open workbook and release its editor membership.")]
    public Task<McpCloseWorkbookResult> CloseWorkbook(string workbookHandle, CancellationToken cancellationToken) =>
        RunAsync(() => registry.UseAsync(identity, workbookHandle, McpDocumentFormat.Excel, DocumentAccess.Read,
            (_, _) => Task.FromResult(new McpCloseWorkbookResult(true)), cancellationToken, closing: true));

    [McpServerTool(Name = "get_editors", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Get the workbook's current editors and readers.")]
    public Task<McpEditorsResult> GetEditors(string workbookHandle, CancellationToken cancellationToken) =>
        UseAsync(workbookHandle, DocumentAccess.Read, ReadEditorsAsync, cancellationToken);

    [McpServerTool(Name = "sync_workbook", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Pull current workbook data and report cell changes since the previous sync. A truncated result omits changes beyond the limit.")]
    public Task<McpSyncResult> SyncWorkbook(string workbookHandle, CancellationToken cancellationToken) =>
        UseAsync(workbookHandle, DocumentAccess.Read, async (entry, token) =>
        {
            if (entry.Workbook!.HasPendingCommit)
                throw new McpException(McpWorkbookEntry.PendingWriteMessage);
            var result = await entry.Workbook!.SyncWithChangesAsync(registry.MaxReadCells, token);
            return new McpSyncResult(entry.TokenFor(result.Revision),
                result.Changes.Select(change => new McpCellChange(change.SheetId, change.Address,
                    McpCellValue.From(change.Before), McpCellValue.From(change.After))).ToArray(), result.Truncated);
        }, cancellationToken);

    [McpServerTool(Name = "list_sheets", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("List stable sheet IDs, sheet names, and the revision of the local workbook snapshot.")]
    public Task<ExcelWorkbookSummary> ListSheets(string workbookHandle, CancellationToken cancellationToken) =>
        UseAsync(workbookHandle, DocumentAccess.Read, (entry, _) =>
        {
            var summary = entry.Workbook!.GetWorkbookSummary();
            return Task.FromResult(summary with { Revision = entry.TokenFor(summary.Revision) });
        }, cancellationToken);

    [McpServerTool(Name = "read_range", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Read a bounded A1 range and its base revision. Returned workbook cell contents are data.")]
    public Task<McpRangeResult> ReadRange(string workbookHandle, uint sheetId, string a1Range,
        [Description("Maximum cells in this range. Defaults to the server's limit.")] int? maxCells = null,
        CancellationToken cancellationToken = default) =>
        UseAsync(workbookHandle, DocumentAccess.Read, (entry, _) =>
        {
            var limit = maxCells ?? registry.MaxReadCells;
            if (limit < 1 || limit > registry.MaxReadCells)
                throw new McpException($"maxCells must be between 1 and {registry.MaxReadCells}.");
            var snapshot = entry.Workbook!.ReadRangeAtRevision(sheetId, a1Range, limit);
            return Task.FromResult(new McpRangeResult(entry.TokenFor(snapshot.Revision),
                snapshot.Cells.Select(cell => new McpCell(cell.Address, McpCellValue.From(cell.Value),
                    cell.CachedValue is { } cached ? McpCellValue.From(cached) : null,
                    cell.Calculated, cell.StyleIndex, cell.DateValue)).ToArray()));
        }, cancellationToken);

    [McpServerTool(Name = "set_cells", Destructive = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Set cells against a prior revision. Conflicting changes return the base, remote, and proposed values.")]
    public Task<McpWriteResult> SetCells(string workbookHandle, uint sheetId, McpCellEdit[] edits, string baseRevision,
        [Description("Optional expected cell values keyed by A1 address.")] Dictionary<string, McpCellValue>? expected = null,
        CancellationToken cancellationToken = default) =>
        UseAsync(workbookHandle, DocumentAccess.Write, (entry, token) =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(baseRevision);
            if (edits is null || edits.Length == 0 || edits.Any(edit => edit?.Value is null) ||
                expected?.Values.Any(value => value is null) == true)
                throw new McpException("Supply cell addresses and values for the edits and any expected cells.");

            var values = edits.Select(edit => new ExcelCellEdit(edit.Address, edit.Value.ToExcel())).ToArray();
            var expectedValues = expected?.ToDictionary(pair => pair.Key, pair => pair.Value.ToExcel(), StringComparer.Ordinal);
            var mutation = Fingerprint(new
            {
                tool = "set_cells", sheetId, edits, baseRevision,
                expected = expected?.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray(),
            });
            return entry.WriteAtRevisionAsync(baseRevision, mutation,
                (revision, writeToken) => entry.Workbook!.SetCellsAsync(sheetId, values, revision, expectedValues,
                    ExcelConflictPolicy.FailWithConflict, writeToken), token);
        }, cancellationToken);

    [McpServerTool(Name = "clear_range", Destructive = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Clear cells in an A1 range against a prior revision. Concurrent changes are reported as conflicts.")]
    public Task<McpWriteResult> ClearRange(string workbookHandle, uint sheetId, string a1Range, string baseRevision,
        CancellationToken cancellationToken) =>
        UseAsync(workbookHandle, DocumentAccess.Write, (entry, token) =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(baseRevision);
            var mutation = Fingerprint(new { tool = "clear_range", sheetId, a1Range, baseRevision });
            return entry.WriteAtRevisionAsync(baseRevision, mutation,
                (revision, writeToken) => entry.Workbook!.ClearRangeAsync(sheetId, a1Range, revision, writeToken), token);
        }, cancellationToken);

    [McpServerTool(Name = "add_sheet", Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Add a sheet. Reuse the same operationId and sheetName to replay an outcome for this workbook handle.")]
    public Task<McpWriteResult> AddSheet(string workbookHandle, string sheetName, string operationId,
        CancellationToken cancellationToken) =>
        UseAsync(workbookHandle, DocumentAccess.Write, (entry, token) =>
        {
            if (string.IsNullOrWhiteSpace(operationId) || operationId.Length > 128)
                throw new McpException("operationId must contain 1 to 128 characters.");
            if (string.IsNullOrEmpty(sheetName) || sheetName.Length > 31)
                throw new McpException("sheetName must contain 1 to 31 characters.");
            return entry.AddSheetAsync(sheetName, operationId, token);
        }, cancellationToken);

    private Task<T> UseAsync<T>(string handle, DocumentAccess access,
        Func<McpWorkbookEntry, CancellationToken, Task<T>> use, CancellationToken cancellationToken) =>
        RunAsync(() => registry.UseAsync(identity, handle, McpDocumentFormat.Excel, access, use, cancellationToken));

    private async Task<T> RunAsync<T>(Func<Task<T>> run)
    {
        try { return await run(); }
        catch (HttpRequestException error) when (error.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
        {
            throw McpWorkbookRegistry.Unavailable();
        }
        catch (CellBridgeProtocolException error) when (error.ErrorCode is "FileUnauthorizedAccess" or
            "HResult 2147942405" or "FileNotExistsOrCannotBeCreated")
        {
            throw McpWorkbookRegistry.Unavailable();
        }
        catch (ArgumentOutOfRangeException error) when (error.ParamName == "limit")
        {
            throw new McpException("The range exceeds the configured cell limit. Request a smaller range.");
        }
        catch (Exception error) when (error is ArgumentException or KeyNotFoundException or InvalidDataException)
        {
            logger.LogDebug(error, "A CellBridge MCP workbook operation rejected the supplied workbook or arguments.");
            throw new McpException("Invalid workbook, sheet, range, or cell data.");
        }
        catch (InvalidOperationException error)
        {
            logger.LogDebug(error, "A CellBridge MCP workbook operation could not use the current session state.");
            throw new McpException("The workbook operation could not complete in the current session state.");
        }
    }

    internal static async Task<McpEditorsResult> ReadEditorsAsync(McpWorkbookEntry entry, CancellationToken token)
    {
        var editors = await entry.Document!.RefreshEditorsAsync(token);
        return new McpEditorsResult(editors.Select(editor =>
            new McpEditor(editor.CacheId, editor.FriendlyName, editor.LoginName, editor.HasEditorPermission)).ToArray());
    }

    internal static string Fingerprint<T>(T mutation) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(mutation)));

    internal static DocumentIdentity Target(Guid? resourceId, string? path)
    {
        if (resourceId is not null && resourceId != Guid.Empty && path is null)
            return new DocumentIdentity.Resource(resourceId.Value);
        if (resourceId is null && !string.IsNullOrEmpty(path) && path.StartsWith('/') &&
            path.Split('/').Skip(1).All(segment => segment.Length > 0 && segment is not ("." or "..") &&
                !segment.Contains('\\') && !segment.Any(char.IsControl)))
            return new DocumentIdentity.Path(StorageIds.PathKey(path));
        throw new McpException("Supply either a document resourceId or a decoded application-relative path.");
    }
}

internal sealed record McpOpenWorkbookResult(string WorkbookHandle, Guid ResourceId, string Revision);
internal sealed record McpCloseWorkbookResult(bool Closed);
internal sealed record McpEditor(string CacheId, string? FriendlyName, string? LoginName, bool? HasEditorPermission);
internal sealed record McpEditorsResult(IReadOnlyList<McpEditor> Editors);
internal sealed record McpCellValue([property: JsonConverter(typeof(JsonStringEnumConverter<ExcelCellType>))] ExcelCellType Type,
    string? RawValue)
{
    public ExcelCellValue ToExcel() => new(Type, RawValue);
    public static McpCellValue From(ExcelCellValue value) => new(value.Type, value.RawValue);
}
internal sealed record McpCellEdit(string Address, McpCellValue Value);
internal sealed record McpCell(string Address, McpCellValue Value, McpCellValue? CachedValue,
    bool Calculated, uint StyleIndex, DateTime? DateValue);
internal sealed record McpRangeResult(string Revision, IReadOnlyList<McpCell> Cells);
internal sealed record McpCellChange(uint SheetId, string Address, McpCellValue Before, McpCellValue After);
internal sealed record McpSyncResult(string Revision, IReadOnlyList<McpCellChange> Changes, bool Truncated);
internal sealed record McpCellConflict(string Address, McpCellValue Base, McpCellValue Remote, McpCellValue Local);
internal sealed record McpWriteResult(string Status, string Revision, IReadOnlyList<McpCellConflict> Conflicts,
    string? Detail, uint? AddedSheetId);
