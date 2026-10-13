using System.Text;
using CellBridge.Storage.Abstractions;
using Microsoft.Extensions.Logging;

namespace CellBridge.AspNetCore;

internal sealed class ProtocolCaptureStore(CellBridgeOptions options, ILogger<ProtocolCaptureStore> logger)
{
    private readonly object _sync = new();
    private long? _used;

    public Task WriteTextAsync(string id, string suffix, string value, CancellationToken token) =>
        WriteAsync(id, suffix, Encoding.UTF8.GetBytes(value), token);

    public Task WriteAsync(string id, string suffix, ReadOnlyMemory<byte> bytes, CancellationToken token) =>
        WriteStreamingAsync(id, suffix, bytes.Length, stream => stream.WriteAsync(bytes, token).AsTask(), token);

    public async Task WriteStreamingAsync(string id, string suffix, long length,
        Func<Stream, Task> write, CancellationToken token)
    {
        if (options.CaptureDirectory is not { } directory) return;
        string? path = null;
        bool reserved = false, created = false, completed = false;
        long retained = 0;
        try
        {
            lock (_sync)
            {
                if (_used is null)
                {
                    ConfidentialFileSystem.CreateDirectory(directory);
                    _used = Directory.EnumerateFiles(directory).Sum(p => new FileInfo(p).Length);
                }
                if (length < 0 || length > options.MaxCaptureBytes - _used.Value)
                {
                    logger.LogWarning("Raw protocol capture skipped because its byte budget is exhausted.");
                    return;
                }
                _used += length;
                reserved = true;
                path = Path.Combine(directory, id + suffix);
            }
            await using (var output = ConfidentialFileSystem.CreateFile(path, FileAccess.Write))
            {
                created = true;
                token.ThrowIfCancellationRequested();
                await write(output);
                token.ThrowIfCancellationRequested();
                retained = output.Length;
                if (retained > length) throw new IOException("Capture exceeded its reserved length.");
            }
            completed = true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Diagnostic failures must not turn an accepted save into a failed HTTP request.
            logger.LogWarning("Raw protocol capture could not be written ({ErrorType}).", error.GetType().Name);
        }
        finally
        {
            if (reserved)
            {
                if (created && !completed)
                {
                    try { File.Delete(path!); retained = 0; }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                    {
                        // Account for evidence that could not be removed. If its size
                        // cannot be read, keep the reservation rather than undercount it.
                        try { retained = new FileInfo(path!).Length; }
                        catch (Exception sizeError) when (sizeError is IOException or UnauthorizedAccessException)
                        { retained = length; }
                        logger.LogWarning("Incomplete raw protocol capture could not be removed ({ErrorType}).", error.GetType().Name);
                    }
                }
                lock (_sync) _used -= length - retained;
            }
        }
    }
}
