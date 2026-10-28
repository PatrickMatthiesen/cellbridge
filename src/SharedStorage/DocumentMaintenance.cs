using CellBridge.Storage.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace CellBridge.Storage.Shared;

internal sealed record DocumentMaintenanceReport(long StoredBytes, long Documents, long Snapshots,
    long ReferencedObjects, long OrphanObjects, long OrphanBytes, bool Applied);

internal sealed class DocumentMaintenance<TContext>(IDocumentDatabase<TContext> database, StorageLimits? limits)
    where TContext : DbContext
{
    public async Task<DocumentMaintenanceReport> CollectOrphansAsync(bool apply = false, bool quiescent = false, Action<string>? deleteFile = null,
        CancellationToken cancellationToken = default)
    {
        if (apply && !quiescent)
            throw new InvalidOperationException("Applying maintenance requires all readers and writers to be stopped.");
        try
        {
            await using var db = database.CreateContext();
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            await database.ConfigureAsync(db, cancellationToken);
            await database.CheckConfigurationAsync(db, cancellationToken);
            // Publishers take this lock before document/content locks or quota
            // admission. Recovery and other collectors use its exclusive form.
            await database.Locks.PublicationAsync(db, true, cancellationToken);
            var usage = await database.Locks.UsageAsync(db, cancellationToken);
            var pointers = await db.Set<DocumentRow>().AsNoTracking().ToDictionaryAsync(row => row.ResourceId, cancellationToken);
            var current = new HashSet<Guid>();
            var references = new Dictionary<string, ContentHandle>(StringComparer.Ordinal);
            long snapshots = 0, snapshotBytes = 0;
            await foreach (var row in db.Set<StateRow>().AsNoTracking().AsAsyncEnumerable().WithCancellation(cancellationToken))
            {
                var state = DocumentStateJson.Deserialize(row.StateJson, strict: true, limits);
                if (state.ResourceId != row.ResourceId || state.StateVersion != row.StateVersion ||
                    !pointers.TryGetValue(row.ResourceId, out var pointer) || row.StateVersion > pointer.StateVersion)
                    throw new StorageCorruptionException("Maintenance snapshot identity disagrees with its pointer.");
                if (row.StateVersion == pointer.StateVersion)
                {
                    if (state.ResourceId != pointer.ResourceId || state.StateVersion != pointer.StateVersion || state.PathKey != pointer.PathKey || state.IsDeleted != pointer.IsDeleted)
                        throw new StorageCorruptionException("Snapshot identity disagrees with its current pointer.");
                    database.ValidateNamespaceFields(pointer, DocumentNamespaceState.From(state));
                    current.Add(row.ResourceId);
                }
                snapshots = checked(snapshots + 1);
                snapshotBytes = checked(snapshotBytes + System.Text.Encoding.UTF8.GetByteCount(row.StateJson));
                foreach (var handle in StorageReferences.Handles(state))
                {
                    if (references.TryGetValue(handle.Key, out var other) && handle != other)
                        throw new StorageCorruptionException("Conflicting metadata for a referenced content key.");
                    references[handle.Key] = handle;
                }
            }
            if (current.Count != pointers.Count)
                throw new StorageCorruptionException("A current snapshot is missing from the maintenance inventory.");
            var orphans = new List<ContentHandle>();
            long objectBytes = 0, objectCount = 0;
            await foreach (var row in db.Set<ObjectRow>().AsNoTracking().AsAsyncEnumerable().WithCancellation(cancellationToken))
            {
                var handle = new ContentHandle(row.ObjectKey, row.Length, row.Sha256);
                objectBytes = checked(objectBytes + row.Length);
                objectCount = checked(objectCount + 1);
                if (!references.Remove(handle.Key, out var expected)) orphans.Add(handle);
                else if (expected != handle)
                    throw new StorageCorruptionException("Referenced object metadata is corrupt.");
            }
            if (references.Count != 0)
                throw new StorageCorruptionException("Referenced objects are absent from the usage inventory.");
            // Recovery receipts have no content handles. Preserve their rows and
            // charge their bytes together with every retained snapshot and object.
            var receiptBytes = await db.Set<RecoveryReceiptRow>().SumAsync(row => (long?)DocumentSql.ByteLength(row.ReceiptJson), cancellationToken) ?? 0;
            if (usage.ChargedBytes != checked(objectBytes + snapshotBytes + receiptBytes) || usage.DocumentCount != pointers.Count)
                throw new StorageCorruptionException("The usage ledger disagrees with the maintenance inventory.");
            var orphanBytes = orphans.Sum(handle => handle.Length);
            if (apply)
            {
                foreach (var orphan in orphans)
                {
                    deleteFile?.Invoke(orphan.Key);
                    await db.Set<ChunkRow>().Where(row => row.ObjectKey == orphan.Key).ExecuteDeleteAsync(cancellationToken);
                    await db.Set<ObjectRow>().Where(row => row.ObjectKey == orphan.Key).ExecuteDeleteAsync(cancellationToken);
                }
                await DocumentBudget.AdjustAsync(database, db, -orphanBytes, 0, cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
            }
            await transaction.CommitAsync(CancellationToken.None);
            return new(usage.ChargedBytes, usage.DocumentCount, snapshots, objectCount - orphans.Count,
                orphans.Count, orphanBytes, apply);
        }
        catch (Exception ex) when (database.IsDatabaseException(ex))
        { throw new StorageUnavailableException("Maintenance failed or its commit outcome is unknown. Inspect the store before retrying.", ex); }
    }
}
