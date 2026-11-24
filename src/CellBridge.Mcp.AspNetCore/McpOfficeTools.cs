using System.Buffers;
using System.Net;
using System.Text;
using System.Text.Json;
using CellBridge.Client;
using CellBridge.Storage.Abstractions;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace CellBridge.Mcp.AspNetCore;

[McpServerToolType]
internal sealed partial class McpOfficeTools(McpWorkbookRegistry registry, McpRequestIdentity identity,
    ILogger<McpOfficeTools> logger)
{
    private const int MaxTextBytes = 64 * 1024;
    private const int MaxResponseBytes = 256 * 1024;

    private Task<McpOpenDocument> OpenAsync(McpDocumentFormat format, Guid? resourceId, string? path,
        CancellationToken cancellationToken) => RunAsync(format, () =>
    {
        if (path?.Length > 4096)
            throw new McpException("path must contain at most 4096 characters.");
        return registry.OpenAsync(identity, McpWorkbookTools.Target(resourceId, path), format, cancellationToken);
    });

    private Task<McpCloseWorkbookResult> CloseAsync(McpDocumentFormat format, string handle,
        CancellationToken cancellationToken) => RunAsync(format, () => registry.UseAsync(identity, handle, format,
            DocumentAccess.Read, (_, _) => Task.FromResult(new McpCloseWorkbookResult(true)), cancellationToken, closing: true));

    private Task<T> ReadAsync<T>(McpDocumentFormat format, string handle, Func<McpWorkbookEntry, T> read,
        CancellationToken cancellationToken) => ReadAsync(format, handle, (entry, _) => Task.FromResult(read(entry)),
            cancellationToken);

    private Task<T> ReadAsync<T>(McpDocumentFormat format, string handle,
        Func<McpWorkbookEntry, CancellationToken, Task<T>> read, CancellationToken cancellationToken) =>
        RunAsync(format, () => registry.UseAsync(identity, handle, format, DocumentAccess.Read,
            async (entry, token) => CheckResponse(await read(entry, token)), cancellationToken));

    private Task<McpOfficeRevision> SyncAsync(McpDocumentFormat format, string handle,
        CancellationToken cancellationToken) => ReadAsync(format, handle, async (entry, token) =>
    {
        if (entry.HasPendingCommit)
            throw new McpException(McpWorkbookEntry.PendingWriteMessage);
        var revision = format == McpDocumentFormat.Word
            ? await entry.Word!.SyncAsync(token)
            : await entry.Presentation!.SyncAsync(token);
        return new McpOfficeRevision(entry.TokenFor(revision));
    }, cancellationToken);

    private Task<McpOfficeWriteResult> WriteAsync<TEdit>(McpDocumentFormat format, string handle, string baseRevision,
        string tool, TEdit edit, Func<McpWorkbookEntry, string, CancellationToken, Task<OfficeWriteResult>> write,
        CancellationToken cancellationToken, string? operationId) =>
        RunAsync(format, () => registry.UseAsync(identity, handle, format, DocumentAccess.Write, async (entry, token) =>
        {
            if (string.IsNullOrWhiteSpace(baseRevision) || baseRevision.Length > 128)
                throw new McpException("Supply the baseRevision returned by an Office tool for this handle.");
            var mutation = McpWorkbookTools.Fingerprint(new { tool, baseRevision, edit });
            var result = await entry.WriteOfficeAsync(baseRevision, mutation, operationId,
                (revision, writeToken) => write(entry, revision, writeToken), token);
            return CheckResponse(result);
        }, cancellationToken));

    private async Task<T> RunAsync<T>(McpDocumentFormat format, Func<Task<T>> run)
    {
        try { return await run(); }
        catch (HttpRequestException error) when (error.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
        {
            throw McpWorkbookRegistry.Unavailable(format);
        }
        catch (CellBridgeProtocolException error) when (error.ErrorCode is "FileUnauthorizedAccess" or
            "HResult 2147942405" or "FileNotExistsOrCannotBeCreated")
        {
            throw McpWorkbookRegistry.Unavailable(format);
        }
        catch (KeyNotFoundException error)
        {
            logger.LogDebug(error, "A CellBridge MCP operation could not find a revision or document item.");
            throw new McpException("The requested revision or document item is unavailable.");
        }
        catch (Exception error) when (error is ArgumentException or InvalidDataException or
            DocumentFormat.OpenXml.Packaging.OpenXmlPackageException or System.Xml.XmlException)
        {
            logger.LogDebug(error, "A CellBridge MCP operation rejected the supplied document or arguments.");
            throw new McpException("Invalid Office document or arguments.");
        }
        catch (InvalidOperationException error)
        {
            logger.LogDebug(error, "A CellBridge MCP operation could not use the current session state.");
            throw new McpException("The Office operation could not complete in the current session state.");
        }
    }

    private int ReadLimit(int? requested, string argument)
    {
        var limit = requested ?? registry.MaxOfficeReadItems;
        if (limit < 1 || limit > registry.MaxOfficeReadItems)
            throw new McpException($"{argument} must be between 1 and {registry.MaxOfficeReadItems}.");
        return limit;
    }

    private static void CheckText(string? text)
    {
        if (text is null || Encoding.UTF8.GetByteCount(text) > MaxTextBytes)
            throw new McpException($"text must contain at most {MaxTextBytes} UTF-8 bytes.");
    }

    private static string RequireOperationId(string? operationId)
    {
        if (string.IsNullOrWhiteSpace(operationId) || operationId.Length > 128)
            throw new McpException("operationId must contain 1 to 128 characters.");
        return operationId;
    }

    private static int? NextIndex(int startIndex, int limit, int count) =>
        count > limit ? startIndex + limit : null;

    private static T CheckResponse<T>(T result)
    {
        // Bound the structured JSON before the transport encodes it into the tool response.
        using var writer = new Utf8JsonWriter(new ResponseBuffer());
        JsonSerializer.Serialize(writer, result, JsonSerializerOptions.Web);
        return result;
    }

    private sealed class ResponseBuffer : IBufferWriter<byte>
    {
        private readonly ArrayBufferWriter<byte> _buffer = new();

        public void Advance(int count) => _buffer.Advance(count);

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            var remaining = MaxResponseBytes - _buffer.WrittenCount;
            if (Math.Max(sizeHint, 1) > remaining)
                throw new McpException("The Office tool response is too large. Request less document content.");
            return _buffer.GetMemory(sizeHint)[..Math.Min(remaining, _buffer.FreeCapacity)];
        }

        public Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;
    }
}

internal sealed record McpOfficeRevision(string Revision);
