using System.Security.Cryptography;
using CellBridge.Storage.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace CellBridge.Storage.Shared;

internal sealed partial class DocumentContent<TContext> : IContentBatchReader where TContext : DbContext
{
    public async ValueTask<IReadOnlyDictionary<string, byte[]>> ReadManyAsync(IReadOnlyCollection<ContentHandle> handles,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handles);
        cancellationToken.ThrowIfCancellationRequested();
        var unique = ContentStoreReader.UniqueHandles(handles).ToArray();
        foreach (var handle in unique)
        {
            if (handle.Key.Length != 64 || handle.Key.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')) ||
                handle.Key != handle.Sha256 || handle.Length < 0 || handle.Length > Array.MaxLength)
                throw new StorageCorruptionException("Invalid content handle.");
            StorageLimits.Check("object bytes", handle.Length, _maxObjectBytes);
        }
        var payloads = new Dictionary<string, byte[]>(unique.Length, StringComparer.Ordinal);
        if (unique.Length == 0) return payloads;
        try
        {
            await using var context = CreateContext();
            await context.Database.OpenConnectionAsync(cancellationToken);
            foreach (var batch in unique.Chunk(64))
            {
                var keys = batch.Select(handle => handle.Key).ToArray();
                var metadata = await database.ObjectsAtKeys(context, keys)
                    .Select(row => new { row.ObjectKey, row.Length, row.Sha256 })
                    .ToDictionaryAsync(row => row.ObjectKey, cancellationToken);
                var progress = new Dictionary<string, BatchReadProgress>(batch.Length, StringComparer.Ordinal);
                foreach (var handle in batch)
                {
                    if (!metadata.TryGetValue(handle.Key, out var stored) || stored.Length != handle.Length || stored.Sha256 != handle.Sha256)
                        throw new StorageCorruptionException("Referenced content is missing or has invalid metadata.");
                    progress.Add(handle.Key, new BatchReadProgress(GC.AllocateUninitializedArray<byte>((int)handle.Length)));
                }
                var chunks = context.Set<ChunkRow>().AsNoTracking().Where(row => keys.Contains(row.ObjectKey))
                    .OrderBy(row => row.ObjectKey).ThenBy(row => row.Ordinal)
                    .Select(row => new { row.ObjectKey, row.Ordinal, row.Bytes }).AsAsyncEnumerable();
                await foreach (var chunk in chunks.WithCancellation(cancellationToken))
                {
                    var target = progress[chunk.ObjectKey];
                    if (chunk.Ordinal != target.Ordinal++ || chunk.Bytes.Length is 0 or > ChunkSize)
                        throw new StorageCorruptionException("Invalid content chunk sequence.");
                    if (chunk.Bytes.Length > target.Bytes.Length - target.Position)
                        throw new StorageCorruptionException("Content exceeds its declared length.");
                    chunk.Bytes.CopyTo(target.Bytes, target.Position);
                    target.Position += chunk.Bytes.Length;
                }
                foreach (var handle in batch)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var target = progress[handle.Key];
                    if (target.Position != handle.Length || Convert.ToHexStringLower(SHA256.HashData(target.Bytes)) != handle.Sha256)
                        throw new StorageCorruptionException("Content chunks failed integrity verification.");
                    payloads.Add(handle.Key, target.Bytes);
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            return payloads;
        }
        catch (Exception ex) when (database.IsDatabaseException(ex))
        { throw new StorageUnavailableException("Content batch read failed.", ex); }
    }

    private sealed class BatchReadProgress(byte[] bytes)
    {
        public byte[] Bytes { get; } = bytes;
        public int Position { get; set; }
        public int Ordinal { get; set; }
    }
}
