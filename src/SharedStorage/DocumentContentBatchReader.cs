using System.Security.Cryptography;
using CellBridge.Storage.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace CellBridge.Storage.Shared;

// Shared physical verification. Callers own their EF context, connection and error
// translation; object queries retain the provider's existing mapping/seek policy.
internal static class DocumentContentBatchReader
{
    internal static ContentHandle[] ValidateHandles(IReadOnlyCollection<ContentHandle> handles, long maximum, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(handles);
        ct.ThrowIfCancellationRequested();
        var unique = ContentStoreReader.UniqueHandles(handles).ToArray();
        foreach (var handle in unique)
        {
            if (handle.Length < 0 || handle.Length > Array.MaxLength)
                throw new StorageCorruptionException("Invalid content handle length.");
            StorageLimits.Check("object bytes", handle.Length, maximum);
        }
        return unique;
    }

    internal static async Task ValidateMetadataAsync(Func<IReadOnlyList<string>, IQueryable<ObjectRow>> objects,
        IReadOnlyList<ContentHandle> handles, CancellationToken ct)
    {
        var keys = handles.Select(handle => handle.Key).ToArray();
        var metadata = await objects(keys).Select(row => new { row.ObjectKey, row.Length, row.Sha256 })
            .ToDictionaryAsync(row => row.ObjectKey, ct);
        foreach (var handle in handles)
            if (!metadata.TryGetValue(handle.Key, out var stored) || stored.Length != handle.Length || stored.Sha256 != handle.Sha256)
                throw new StorageCorruptionException("Referenced content is missing or has invalid metadata.");
    }

    internal static async Task<IReadOnlyDictionary<string, byte[]>> ReadAsync<TContext>(TContext db,
        Func<IReadOnlyList<string>, IQueryable<ObjectRow>> objects, IReadOnlyList<ContentHandle> handles, CancellationToken ct) where TContext : DbContext
    {
        var payloads = new Dictionary<string, byte[]>(handles.Count, StringComparer.Ordinal);
        foreach (var batch in handles.Chunk(64))
        {
            ct.ThrowIfCancellationRequested();
            await ValidateMetadataAsync(objects, batch, ct);
            var progress = batch.ToDictionary(handle => handle.Key, handle => new Progress(GC.AllocateUninitializedArray<byte>((int)handle.Length)), StringComparer.Ordinal);
            var keys = batch.Select(handle => handle.Key).ToArray();
            var chunks = db.Set<ChunkRow>().AsNoTracking().Where(row => keys.Contains(row.ObjectKey))
                .OrderBy(row => row.ObjectKey).ThenBy(row => row.Ordinal)
                .Select(row => new { row.ObjectKey, row.Ordinal, row.Bytes }).AsAsyncEnumerable();
            await foreach (var chunk in chunks.WithCancellation(ct))
            {
                if (!progress.TryGetValue(chunk.ObjectKey, out var target) || chunk.Ordinal != target.Ordinal++ || chunk.Bytes.Length is 0 or > 65536)
                    throw new StorageCorruptionException("Invalid content chunk sequence.");
                if (chunk.Bytes.Length > target.Bytes.Length - target.Position)
                    throw new StorageCorruptionException("Content exceeds its declared length.");
                chunk.Bytes.CopyTo(target.Bytes, target.Position);
                target.Position += chunk.Bytes.Length;
            }
            foreach (var handle in batch)
            {
                ct.ThrowIfCancellationRequested();
                var target = progress[handle.Key];
                if (target.Position != handle.Length || Convert.ToHexStringLower(SHA256.HashData(target.Bytes)) != handle.Sha256)
                    throw new StorageCorruptionException("Content chunks failed integrity verification.");
                payloads.Add(handle.Key, target.Bytes);
            }
        }
        ct.ThrowIfCancellationRequested();
        return payloads;
    }

    private sealed class Progress(byte[] bytes)
    {
        public byte[] Bytes { get; } = bytes;
        public int Position { get; set; }
        public int Ordinal { get; set; }
    }
}
