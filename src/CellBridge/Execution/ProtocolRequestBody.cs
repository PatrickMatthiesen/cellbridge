using CellBridge.Storage.Abstractions;

namespace CellBridge.AspNetCore;

internal static class ProtocolRequestBody
{
    // Large bodies spool privately before one exact-sized parsing allocation,
    // rather than retaining a growing MemoryStream alongside its final copy.
    public static async Task<byte[]> ReadAsync(Stream source, long maximum, CancellationToken cancellationToken)
    {
        const int memoryThreshold = 256 * 1024;
        using var memory = new MemoryStream(65536);
        FileStream? spool = null;
        var buffer = new byte[65536];
        long length = 0;
        try
        {
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) != 0)
            {
                if (read > maximum - length) throw new RequestBodyLimitException();
                length += read;
                if (spool is null && length > memoryThreshold)
                {
                    spool = ConfidentialFileSystem.CreateFile(Path.Combine(Path.GetTempPath(), "cellbridge-request-" + Guid.NewGuid().ToString("N")),
                        options: FileOptions.Asynchronous | FileOptions.DeleteOnClose);
                    memory.Position = 0;
                    await memory.CopyToAsync(spool, cancellationToken);
                }
                await (spool is null ? (Stream)memory : spool).WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
            if (spool is null) return memory.ToArray();
            await spool.FlushAsync(cancellationToken);
            spool.Position = 0;
            var bytes = GC.AllocateUninitializedArray<byte>(checked((int)length));
            await spool.ReadExactlyAsync(bytes, cancellationToken);
            return bytes;
        }
        finally { if (spool is not null) await spool.DisposeAsync(); }
    }

    internal sealed class RequestBodyLimitException : IOException;
}
