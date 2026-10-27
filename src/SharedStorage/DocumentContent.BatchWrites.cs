using System.Security.Cryptography;
using CellBridge.Storage.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace CellBridge.Storage.Shared;

internal sealed partial class DocumentContent<TContext> : IContentBatchWriter where TContext : DbContext
{
    public async ValueTask<IReadOnlyList<ContentHandle>> WriteManyAsync(
        IReadOnlyList<ReadOnlyMemory<byte>> contents, CancellationToken cancellationToken = default)
    {
        ValidateWriteBatch(contents, cancellationToken);
        if (contents.Count == 0) return Array.Empty<ContentHandle>();
        await _writers.WaitAsync(cancellationToken);
        try { return await WriteManyCoreAsync(contents, cancellationToken); }
        catch (Exception ex) when (database.IsDatabaseException(ex))
        { throw new StorageUnavailableException("Content batch write failed or its commit outcome is unknown.", ex); }
        finally { _writers.Release(); }
    }

    private async Task<IReadOnlyList<ContentHandle>> WriteManyCoreAsync(
        IReadOnlyList<ReadOnlyMemory<byte>> contents, CancellationToken cancellationToken)
    {
        var prepared = PrepareWriteBatch(contents, cancellationToken);
        await using var context = CreateContext();
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        await database.ConfigureAsync(context, cancellationToken);
        await database.CheckConfigurationAsync(context, cancellationToken);
        await database.Locks.PublicationAsync(context, false, cancellationToken);
        var added = await WriteManyInTransactionAsync(context, prepared, cancellationToken);
        if (added.InsertedAny)
        {
            // Admit all newly inserted objects atomically, holding the global
            // usage row only after all content has been uploaded.
            await DocumentBudget.AdjustAsync(database, context, added.Bytes, 0, cancellationToken);
            await context.SaveChangesAsync(cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
        await transaction.CommitAsync(CancellationToken.None);
        return prepared.Handles;
    }

    internal Task EnterWriterAsync(CancellationToken cancellationToken) => _writers.WaitAsync(cancellationToken);
    internal void ExitWriter() => _writers.Release();

    private void ValidateWriteBatch(IReadOnlyList<ReadOnlyMemory<byte>> contents, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(contents);
        cancellationToken.ThrowIfCancellationRequested();
        if (contents.Count > IContentBatchWriter.MaxBatchObjects)
            throw new ArgumentOutOfRangeException(nameof(contents), "A content batch admits at most 64 inputs.");
        long bytes = 0;
        foreach (var content in contents)
        {
            StorageLimits.Check("object bytes", content.Length, _maxObjectBytes);
            bytes = checked(bytes + content.Length);
            StorageLimits.Check("batch content bytes", bytes, IContentBatchWriter.MaxBatchBytes);
        }
    }

    internal PreparedWriteBatch PrepareWriteBatch(IReadOnlyList<ReadOnlyMemory<byte>> contents,
        CancellationToken cancellationToken)
    {
        ValidateWriteBatch(contents, cancellationToken);
        var handles = new ContentHandle[contents.Count];
        var unique = new Dictionary<string, (ContentHandle Handle, ReadOnlyMemory<byte> Bytes)>(StringComparer.Ordinal);
        for (var index = 0; index < contents.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var content = contents[index];
            var digest = Convert.ToHexStringLower(SHA256.HashData(content.Span));
            var handle = new ContentHandle(digest, content.Length, digest);
            handles[index] = handle;
            if (!unique.TryAdd(digest, (handle, content)) && unique[digest].Handle.Length != handle.Length)
                throw new StorageCorruptionException("Conflicting immutable content hashes.");
        }
        return new(handles, unique.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Value).ToArray());
    }

    // The caller owns transaction setup, quota admission and commit. Creation
    // locks for missing objects remain held until that transaction ends.
    internal async Task<(bool InsertedAny, long Bytes)> WriteManyInTransactionAsync(TContext context,
        PreparedWriteBatch prepared, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (prepared.Unique.Count == 0) return (false, 0);
        // Existing objects remain immutable under the caller's publication gate.
        // A missing object may still race with another writer; verify that winner below.
        var keys = prepared.Unique.Select(item => item.Handle.Key).ToArray();
        var existing = await database.ObjectsAtKeys(context, keys)
            .Select(row => new { row.ObjectKey, row.Length, row.Sha256 })
            .ToDictionaryAsync(row => row.ObjectKey,
                row => new ContentMetadata { Length = row.Length, Sha256 = row.Sha256 }, StringComparer.Ordinal, cancellationToken);
        var missing = new List<(ContentHandle Handle, ReadOnlyMemory<byte> Bytes)>();
        foreach (var item in prepared.Unique)
        {
            if (existing.TryGetValue(item.Handle.Key, out var metadata))
            {
                await VerifyExistingContentAsync(context, item.Handle, metadata, cancellationToken);
                continue;
            }
            missing.Add(item);
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (missing.Count == 0) return (false, 0);
        // Preserve the prepared key order when batching creation locks and inserts.
        var inserted = await database.CreateObjectsAsync(context, missing.Select(item => item.Handle).ToArray(), cancellationToken);
        long addedBytes = 0;
        var pending = new List<ChunkRow>(ChunksPerBatch);
        foreach (var item in missing)
        {
            if (!inserted.Contains(item.Handle.Key))
            {
                await VerifyExistingContentAsync(context, item.Handle, cancellationToken);
                continue;
            }
            addedBytes = checked(addedBytes + item.Handle.Length);
            var ordinal = 0;
            for (var offset = 0; offset < item.Bytes.Length;)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = Math.Min(ChunkSize, item.Bytes.Length - offset);
                pending.Add(new ChunkRow
                {
                    ObjectKey = item.Handle.Key,
                    Ordinal = ordinal++,
                    Bytes = item.Bytes.Slice(offset, count).ToArray(),
                });
                offset += count;
                if (pending.Count == ChunksPerBatch)
                    await SaveChunkBatchAsync(context, pending, cancellationToken);
            }
        }
        if (pending.Count != 0) await SaveChunkBatchAsync(context, pending, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return (inserted.Count != 0, addedBytes);
    }

    internal sealed record PreparedWriteBatch(ContentHandle[] Handles,
        IReadOnlyList<(ContentHandle Handle, ReadOnlyMemory<byte> Bytes)> Unique);
}
