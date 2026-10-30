namespace CellBridge.Storage.Abstractions;

/// <summary>Writes bounded batches when the provider supports them.</summary>
public static class ContentStoreWriter
{
    public static async ValueTask<IReadOnlyList<ContentHandle>> WriteManyAsync(this IContentStore store,
        IReadOnlyList<ReadOnlyMemory<byte>> contents, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(contents);
        var handles = new List<ContentHandle>(contents.Count);
        // Keep provider commands and lock sets bounded even for large graphs.
        for (var offset = 0; offset < contents.Count;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = 0;
            long bytesInBatch = 0;
            while (offset + count < contents.Count && count < IContentBatchWriter.MaxBatchObjects)
            {
                var length = contents[offset + count].Length;
                StorageLimits.Check("batch content bytes", length, IContentBatchWriter.MaxBatchBytes);
                if (bytesInBatch + length > IContentBatchWriter.MaxBatchBytes) break;
                bytesInBatch += length;
                count++;
            }
            var batch = contents.Skip(offset).Take(count).ToArray();
            if (store is IContentBatchWriter writer)
            {
                var written = await writer.WriteManyAsync(batch, cancellationToken);
                if (written.Count != batch.Length)
                    throw new StorageCorruptionException("Content batch returned an incomplete handle list.");
                for (var i = 0; i < batch.Length; i++)
                {
                    if (written[i].Length != batch[i].Length || written[i].Sha256 !=
                        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(batch[i].Span)))
                        throw new StorageCorruptionException("Content batch returned a mismatched handle.");
                    handles.Add(written[i]);
                }
            }
            else
            {
                foreach (var bytes in batch)
                {
                    using var stream = new MemoryStream(bytes.ToArray(), writable: false);
                    handles.Add(await store.WriteAsync(stream, cancellationToken));
                }
            }
            offset += count;
        }
        cancellationToken.ThrowIfCancellationRequested();
        return handles;
    }
}
