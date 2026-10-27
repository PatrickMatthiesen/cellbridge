using System.Security.Cryptography;
using CellBridge.Storage.Abstractions;

namespace CellBridge.Storage;

/// <summary>Owns a bounded set of provisional objects until an atomic save publishes them.</summary>
internal sealed class PendingSaveContent(long maxObjectBytes) : IContentStore, IDisposable
{
    internal const long MaxBytes = 8L * 1024 * 1024;
    private readonly Dictionary<string, byte[]> objects = new(StringComparer.Ordinal);
    private long bytes;
    public bool Durable => false;
    public bool Shared => false;
    internal IReadOnlyList<ReadOnlyMemory<byte>> Contents => objects.Values.Select(value => (ReadOnlyMemory<byte>)value).ToArray();

    public async ValueTask<ContentHandle> WriteAsync(Stream source, CancellationToken cancellationToken = default)
    {
        using var output = new MemoryStream();
        var buffer = new byte[65536];
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) != 0)
        {
            StorageLimits.Check("object bytes", output.Length + read, maxObjectBytes);
            StorageLimits.Check("pending save bytes", output.Length + read, MaxBytes);
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
        var value = output.ToArray();
        var hash = Convert.ToHexStringLower(SHA256.HashData(value));
        if (!objects.ContainsKey(hash))
        {
            StorageLimits.Check("pending save bytes", bytes + value.Length, MaxBytes);
            StorageLimits.Check("pending save objects", objects.Count + 1L, IContentBatchWriter.MaxBatchObjects);
            objects.Add(hash, value);
            bytes += value.Length;
        }
        return new(hash, value.Length, hash);
    }

    public ValueTask<Stream> OpenReadAsync(ContentHandle handle, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!objects.TryGetValue(handle.Key, out var value) || value.Length != handle.Length || handle.Key != handle.Sha256)
            throw new StorageCorruptionException("The provisional content handle is missing or inconsistent.");
        return ValueTask.FromResult<Stream>(new MemoryStream(value, writable: false));
    }

    public void Dispose() => objects.Clear();
}
