using CellBridge.Storage.Abstractions;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CellBridge.Storage.PostgreSql;

public sealed record StorageMaintenanceReport(long StoredBytes, long Documents, long Snapshots,
    long ReferencedObjects, long OrphanObjects, long OrphanBytes, bool Applied);

/// <summary>Runs quiescent object collection after every host, reader and writer has stopped.</summary>
public sealed class StorageMaintenance
{
    private readonly DbContextOptions<CellBridgeDbContext> _options;
    private readonly StorageLimits? _limits;

    public StorageMaintenance(NpgsqlDataSource source) : this(source, null) { }
    public StorageMaintenance(NpgsqlDataSource source, StorageLimits? limits)
        : this(PostgreSqlDatabase.CreateOptions(source), limits) { }
    public StorageMaintenance(DbContextOptions<CellBridgeDbContext> options, StorageLimits? limits = null)
    {
        _options = PostgreSqlDatabase.ValidateOptions(options);
        _limits = limits;
    }

    public async Task<StorageMaintenanceReport> CollectOrphansAsync(bool apply = false,
        bool quiescent = false, string? fileSystemRoot = null, CancellationToken cancellationToken = default)
    {
        if (apply && !quiescent)
            throw new InvalidOperationException("Applying maintenance requires all readers and writers to be stopped.");
        await using var context = new CellBridgeDbContext(_options);
        await using var databaseTransaction = await context.Database.BeginTransactionAsync(cancellationToken);
        await PostgreSqlStateStore.ConfigureTransactionAsync(context, cancellationToken);
        var (connection, transaction) = PostgreSqlDatabase.GetTransaction(context);
        // The table barrier preserves the existing mark/sweep snapshot across all hosts.
        await using (var gate = new NpgsqlCommand(
            "LOCK TABLE cellbridge_documents,cellbridge_states,cellbridge_objects,cellbridge_chunks IN SHARE ROW EXCLUSIVE MODE",
            connection, transaction))
            await gate.ExecuteNonQueryAsync(cancellationToken);
        var references = new Dictionary<string, ContentHandle>(StringComparer.Ordinal);
        long snapshots = 0;
        await foreach (var json in context.States.AsNoTracking().Select(row => row.StateJson)
                           .AsAsyncEnumerable().WithCancellation(cancellationToken))
        {
            var state = DocumentStateJson.Deserialize(json, strict: true, limits: _limits);
            if (state is null || state.FormatVersion != DocumentState.CurrentFormat)
                throw new StorageCorruptionException("Unsupported state encountered during maintenance.");
            snapshots++;
            foreach (var handle in StorageReferences.Handles(state))
            {
                if (references.TryGetValue(handle.Key, out var other) && handle != other)
                    throw new StorageCorruptionException("Conflicting metadata for a referenced content key.");
                references[handle.Key] = handle;
            }
        }
        var orphans = new List<ContentHandle>();
        await foreach (var row in context.Objects.AsNoTracking().AsAsyncEnumerable().WithCancellation(cancellationToken))
        {
            var handle = new ContentHandle(row.ObjectKey, row.Length, row.Sha256);
            if (!references.Remove(handle.Key, out var expected)) orphans.Add(handle);
            else if (expected != handle)
                throw new StorageCorruptionException("Referenced object metadata is corrupt.");
        }
        if (references.Count != 0)
            throw new StorageCorruptionException("Referenced objects are absent from the usage inventory. Recreate the development store; importing untracked filesystem objects is not supported.");
        var orphanBytes = orphans.Sum(handle => handle.Length);
        if (apply)
        {
            foreach (var orphan in orphans)
            {
                if (fileSystemRoot is not null)
                {
                    if (orphan.Key.Length != 64 || orphan.Key.Any(character => !char.IsAsciiHexDigit(character)))
                        throw new StorageCorruptionException("Invalid orphan key.");
                    CellBridge.Storage.FileSystem.FileSystemContentStore.DeleteQuiescentObject(
                        fileSystemRoot, orphan.Key);
                }
                await context.Chunks.Where(row => row.ObjectKey == orphan.Key).ExecuteDeleteAsync(cancellationToken);
                await context.Objects.Where(row => row.ObjectKey == orphan.Key).ExecuteDeleteAsync(cancellationToken);
            }
            await PostgreSqlStorageBudget.AdjustAsync(context, -orphanBytes, 0, cancellationToken);
            await context.SaveChangesAsync(cancellationToken);
        }
        var usage = await context.Usage.AsNoTracking().SingleOrDefaultAsync(cancellationToken)
            ?? throw new StorageCorruptionException("Missing usage ledger.");
        var objectCount = await context.Objects.LongCountAsync(cancellationToken);
        await databaseTransaction.CommitAsync(CancellationToken.None);
        return new(usage.StoredBytes, usage.DocumentCount, snapshots,
            objectCount - (apply ? 0 : orphans.Count), orphans.Count, orphanBytes, apply);
    }
}
