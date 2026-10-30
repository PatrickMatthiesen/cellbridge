using CellBridge.Storage.Abstractions;
using CellBridge.Diagnostics;
using Microsoft.EntityFrameworkCore;

namespace CellBridge.Storage.Shared;

internal sealed partial class RelationalDocumentRepository<TContext> where TContext : DbContext
{
    internal async ValueTask<PublicationResult> PublishWithContentAsync(PreparedDocumentPublication publication,
        IReadOnlyList<ReadOnlyMemory<byte>> contents, DocumentContent<TContext> writer, IContentStore persisted, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(contents);
        if (contents.Count > 64) throw new ArgumentOutOfRangeException(nameof(contents));
        long length = 0;
        foreach (var item in contents)
        {
            length = checked(length + item.Length);
            StorageLimits.Check("atomic publication bytes", length, 8L * 1024 * 1024);
            StorageLimits.Check("object bytes", item.Length, limits.MaxObjectBytes);
        }
        // The public input may still belong to its caller. Keep validation and
        // insertion bound to this owned copy, including across database awaits.
        var owned = contents.Select(item => (ReadOnlyMemory<byte>)item.ToArray()).ToArray();
        DocumentContent<TContext>.PreparedWriteBatch prepared;
        using (CellBridgeMetrics.Measure(CellBridgePhase.ContentHashing))
            prepared = writer.PrepareWriteBatch(owned, ct);
        var overlay = new ProvisionalPublicationContent(persisted, prepared.Unique);
        DocumentPublicationRules.ValidateShape(publication);
        await ValidatePayloadsAsync(publication.Partitions, ct, provisional: overlay);
        // Reject unrelated provisional bytes. Publication must account for every
        // physical object that this transaction could make durable.
        var references = publication.NewDocumentReferences.Concat(publication.Partitions.SelectMany(p => p.AddedElements).Select(e => e.Payload));
        if (publication.Receipt.Response is { } response) references = references.Append(response);
        var claimed = ContentStoreReader.UniqueHandles(references).ToDictionary(handle => handle.Key, StringComparer.Ordinal);
        foreach (var item in prepared.Unique)
            if (!claimed.TryGetValue(item.Handle.Key, out var handle) || handle != item.Handle)
                throw new InvalidDataException("Provisional content is not owned by this publication.");
        using (CellBridgeMetrics.Measure(CellBridgePhase.ContentWriterWait))
            await writer.EnterWriterAsync(ct);
        var holdsWriter = true;
        try
        {
            return await PublishCoreAsync(publication, ct, async db =>
            {
                try
                {
                    // The complete publication abandons its transaction on any
                    // failure. EF savepoints add subtransactions without recovery.
                    db.Database.AutoSavepointsEnabled = false;
                    await writer.CheckTransactionConfigurationAsync(db, ct);
                    using (CellBridgeMetrics.Measure(CellBridgePhase.ContentWrite))
                        await writer.WriteManyInTransactionAsync(db, prepared, ct);
                    await db.SaveChangesAsync(ct);
                }
                finally
                {
                    // Limit chunk buffers, without limiting complete publications.
                    writer.ExitWriter();
                    holdsWriter = false;
                }
            });
        }
        finally { if (holdsWriter) writer.ExitWriter(); }
    }

    private sealed class ProvisionalPublicationContent(IContentStore persisted,
        IReadOnlyList<(ContentHandle Handle, ReadOnlyMemory<byte> Bytes)> objects) : IContentStore, IContentBatchReader
    {
        private readonly Dictionary<string, (ContentHandle Handle, ReadOnlyMemory<byte> Bytes)> pending =
            objects.ToDictionary(item => item.Handle.Key, StringComparer.Ordinal);
        public bool Durable => false;
        public bool Shared => false;
        public ValueTask<ContentHandle> WriteAsync(Stream source, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public ValueTask<Stream> OpenReadAsync(ContentHandle handle, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!pending.TryGetValue(handle.Key, out var value)) return persisted.OpenReadAsync(handle, cancellationToken);
            if (handle != value.Handle) throw new StorageCorruptionException("Provisional content handle disagrees.");
            return ValueTask.FromResult<Stream>(new MemoryStream(value.Bytes.ToArray(), writable: false));
        }
        public async ValueTask<IReadOnlyDictionary<string, byte[]>> ReadManyAsync(IReadOnlyCollection<ContentHandle> handles,
            CancellationToken cancellationToken = default)
        {
            var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var (key, value) in await persisted.ReadManyVerifiedAsync(handles.Where(handle => !pending.ContainsKey(handle.Key)),
                cancellationToken: cancellationToken)) result.Add(key, value);
            foreach (var handle in ContentStoreReader.UniqueHandles(handles.Where(handle => pending.ContainsKey(handle.Key))))
            {
                var value = pending[handle.Key];
                if (value.Handle != handle) throw new StorageCorruptionException("Provisional content handle disagrees.");
                result.Add(handle.Key, value.Bytes.ToArray());
            }
            return result;
        }
    }
}
