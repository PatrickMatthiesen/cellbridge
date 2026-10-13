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
        string path;
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
                path = Path.Combine(directory, id + suffix);
            }
            await using var output = ConfidentialFileSystem.CreateFile(path, FileAccess.Write);
            token.ThrowIfCancellationRequested();
            await write(output);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Diagnostic failures must not turn an accepted save into a failed HTTP request.
            logger.LogWarning("Raw protocol capture could not be written ({ErrorType}).", error.GetType().Name);
        }
    }
}
