using System.Collections.Concurrent;
using System.Security.Cryptography;
using CellBridge.Storage.Abstractions;

namespace CellBridge.Storage.InMemory;

public sealed class InMemoryContentStore : IContentStore
{
    private readonly long _maxObjectBytes;
    public InMemoryContentStore(long maxObjectBytes = 512L * 1024 * 1024)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxObjectBytes);
        _maxObjectBytes = maxObjectBytes;
    }
    private readonly ConcurrentDictionary<string, byte[]> _objects = new(StringComparer.Ordinal);
    public bool Durable => false;
    public bool Shared => false;
    public async ValueTask<ContentHandle> WriteAsync(Stream source, CancellationToken cancellationToken = default)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[65536];
        int read;
        while ((read = await source.ReadAsync(chunk, cancellationToken)) != 0)
        {
            StorageLimits.Check("object bytes", checked(buffer.Length + read), _maxObjectBytes);
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }
        var bytes = buffer.ToArray();
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        _objects.TryAdd(hash, bytes);
        return new ContentHandle(hash, bytes.LongLength, hash);
    }
    public ValueTask<Stream> OpenReadAsync(ContentHandle handle, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var bytes = _objects.GetValueOrDefault(handle.Key) ?? throw new StorageCorruptionException("Referenced content is missing.");
        if (bytes.LongLength != handle.Length || Convert.ToHexStringLower(SHA256.HashData(bytes)) != handle.Sha256)
            throw new StorageCorruptionException("Referenced content does not match its integrity metadata.");
        return ValueTask.FromResult<Stream>(new MemoryStream(bytes, writable: false));
    }
}
