using System.Buffers;
using System.Security.Cryptography;
using CellBridge.Storage.Abstractions;

namespace CellBridge.Storage;

/// <summary>Owns a bounded set of provisional objects until an atomic save publishes them.</summary>
internal sealed class PendingSaveContent(long maxObjectBytes) : IContentStore, IContentBatchWriter, IDisposable
{
    internal const long MaxBytes = 8L * 1024 * 1024;
    private readonly Dictionary<string, byte[]> objects = new(StringComparer.Ordinal);
    private long bytes;
    public bool Durable => false;
    public bool Shared => false;
    internal IReadOnlyList<ReadOnlyMemory<byte>> ContentsAt(HashSet<string> keys) => objects.Where(item => keys.Contains(item.Key)).Select(item => (ReadOnlyMemory<byte>)item.Value).ToArray();
    internal bool Contains(string key) => objects.ContainsKey(key);
    internal IReadOnlyList<ReadOnlyMemory<byte>> Contents => objects.Values.Select(value => (ReadOnlyMemory<byte>)value).ToArray();

    public async ValueTask<ContentHandle> WriteAsync(Stream source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        cancellationToken.ThrowIfCancellationRequested();
        byte[] value;
        if (source.CanSeek)
        {
            var declaredLength = source.Length;
            var start = source.Position;
            if (declaredLength < 0 || start < 0 || start > declaredLength)
                throw new StorageCorruptionException("The provisional source has an invalid remaining length.");
            var length = declaredLength - start;
            CheckObjectLength(length);
            value = GC.AllocateUninitializedArray<byte>((int)length);
            var position = 0;
            while (position < value.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = await source.ReadAsync(value.AsMemory(position), cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (read == 0) throw new StorageCorruptionException("The provisional source is shorter than its declared length.");
                position += read;
            }
            if (await source.ReadAsync(new byte[1], cancellationToken) != 0)
                throw new StorageCorruptionException("The provisional source exceeds its declared length.");
        }
        else value = await ReadUnknownLengthAsync(source, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var handle = Handle(value);
        cancellationToken.ThrowIfCancellationRequested();
        if (!objects.ContainsKey(handle.Key))
        {
            CheckAdmission(bytes + value.Length, objects.Count + 1);
            objects.Add(handle.Key, value);
            bytes += value.Length;
        }
        return handle;
    }

    private async ValueTask<byte[]> ReadUnknownLengthAsync(Stream source, CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(65536);
        try
        {
            int read;
            while ((read = await source.ReadAsync(buffer.AsMemory(0, 65536), cancellationToken)) != 0)
            {
                CheckObjectLength(output.Length + read);
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
            return output.ToArray();
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }

    public ValueTask<IReadOnlyList<ContentHandle>> WriteManyAsync(IReadOnlyList<ReadOnlyMemory<byte>> contents,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contents);
        cancellationToken.ThrowIfCancellationRequested();
        if (contents.Count > IContentBatchWriter.MaxBatchObjects)
            throw new ArgumentOutOfRangeException(nameof(contents), "A content batch admits at most 64 inputs.");
        var handles = new ContentHandle[contents.Count];
        var added = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        long addedBytes = 0;
        long inputBytes = 0;
        for (var index = 0; index < contents.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var content = contents[index];
            CheckObjectLength(content.Length);
            inputBytes += content.Length;
            StorageLimits.Check("batch content bytes", inputBytes, IContentBatchWriter.MaxBatchBytes);
            var handle = handles[index] = Handle(content.Span);
            if (objects.ContainsKey(handle.Key) || added.ContainsKey(handle.Key)) continue;
            CheckAdmission(bytes + addedBytes + content.Length, objects.Count + added.Count + 1);
            cancellationToken.ThrowIfCancellationRequested();
            added.Add(handle.Key, content.ToArray());
            addedBytes += content.Length;
        }
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var (key, value) in added) objects.Add(key, value);
        bytes += addedBytes;
        return ValueTask.FromResult<IReadOnlyList<ContentHandle>>(handles);
    }

    private void CheckObjectLength(long length)
    {
        StorageLimits.Check("object bytes", length, maxObjectBytes);
        StorageLimits.Check("pending save bytes", length, MaxBytes);
    }

    private static void CheckAdmission(long totalBytes, int count)
    {
        StorageLimits.Check("pending save bytes", totalBytes, MaxBytes);
        StorageLimits.Check("pending save objects", count, IContentBatchWriter.MaxBatchObjects);
    }

    private static ContentHandle Handle(ReadOnlySpan<byte> value)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(value));
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
