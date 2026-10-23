using CellBridge.Storage.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace CellBridge.Storage.SqlServer;

public sealed record StorageMaintenanceReport(long StoredBytes, long Documents, long Snapshots,
    long ReferencedObjects, long OrphanObjects, long OrphanBytes, bool Applied);

/// <summary>Explicit orphan collection after every host, reader and writer has stopped.</summary>
public sealed class StorageMaintenance
{
    private readonly DbContextOptions<SqlServerDocumentDbContext> options;
    private readonly StorageLimits? limits;

    public StorageMaintenance(DbContextOptions<SqlServerDocumentDbContext> options, StorageLimits? limits = null)
    {
        this.options = SqlServerDocumentDatabase.Normalize(options);
        limits?.Validate();
        this.limits = limits;
    }

    public async Task<StorageMaintenanceReport> CollectOrphansAsync(bool apply = false, bool quiescent = false,
        CancellationToken cancellationToken = default)
    {
        if (apply && !quiescent)
            throw new InvalidOperationException("Applying maintenance requires all readers and writers to be stopped.");
        try
        {
            await using var db = new SqlServerDocumentDbContext(options);
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            await SqlServerDocumentDatabase.ConfigureTransactionAsync(db, cancellationToken);
            await SqlServerDocumentDatabase.CheckConfigurationAsync(db, cancellationToken);
            // Publishers take this lock before document/content locks or quota
            // admission. Recovery and other collectors use its exclusive form.
            await SqlServerDocumentDatabase.LockAsync(db, SqlServerDocumentDatabase.PublicationGate, cancellationToken);
            var usage = await SqlServerDocumentDatabase.LockUsageAsync(db, cancellationToken);
            var pointers = await db.Documents.AsNoTracking().ToDictionaryAsync(row => row.ResourceId, cancellationToken);
            var current = new HashSet<Guid>();
            var references = new Dictionary<string, ContentHandle>(StringComparer.Ordinal);
            long snapshots = 0, snapshotBytes = 0;
            await foreach (var row in db.Snapshots.AsNoTracking().AsAsyncEnumerable().WithCancellation(cancellationToken))
            {
                var state = SqlServerDocumentDatabase.DecodeSnapshot(row.Data, strict: true, limits);
                if (state.ResourceId != row.ResourceId || state.StateVersion != row.StateVersion ||
                    !pointers.TryGetValue(row.ResourceId, out var pointer) || row.StateVersion > pointer.StateVersion)
                    throw new StorageCorruptionException("Maintenance snapshot identity disagrees with its pointer.");
                if (row.StateVersion == pointer.StateVersion)
                {
                    SqlServerStateStore.ValidatePointer(state, pointer);
                    current.Add(row.ResourceId);
                }
                snapshots = checked(snapshots + 1);
                snapshotBytes = checked(snapshotBytes + row.Data.LongLength);
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
            await foreach (var row in db.Objects.AsNoTracking().AsAsyncEnumerable().WithCancellation(cancellationToken))
            {
                var handle = new ContentHandle(row.Key, row.Length, row.Sha256);
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
            var receiptBytes = await db.RecoveryReceipts.SumAsync(row => (long?)SqlServerDocumentDbContext.ByteLength(row.Data), cancellationToken) ?? 0;
            if (usage.StoredBytes != checked(objectBytes + snapshotBytes + receiptBytes) || usage.DocumentCount != pointers.Count)
                throw new StorageCorruptionException("The usage ledger disagrees with the maintenance inventory.");
            var orphanBytes = orphans.Sum(handle => handle.Length);
            if (apply)
            {
                foreach (var orphan in orphans)
                {
                    await db.Chunks.Where(row => row.Key == orphan.Key).ExecuteDeleteAsync(cancellationToken);
                    await db.Objects.Where(row => row.Key == orphan.Key).ExecuteDeleteAsync(cancellationToken);
                }
                await SqlServerDocumentDatabase.AdjustUsageAsync(db, -orphanBytes, 0, cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
            }
            await transaction.CommitAsync(CancellationToken.None);
            return new(usage.StoredBytes, usage.DocumentCount, snapshots, objectCount - orphans.Count,
                orphans.Count, orphanBytes, apply);
        }
        catch (Exception ex) when (SqlServerDocumentDatabase.IsDatabaseException(ex))
        { throw new StorageUnavailableException("Maintenance failed or its commit outcome is unknown. Inspect the store before retrying.", ex); }
    }
}
