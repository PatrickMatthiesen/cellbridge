using CellBridge.Storage.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace CellBridge.Storage.SqlServer;

/// <summary>Explicit consumer initialization. Constructors and requests never change the schema.</summary>
public sealed class SqlServerStorageMigrator
{
    private readonly DbContextOptions<SqlServerDocumentDbContext> options;
    private readonly StorageLimits limits;

    public SqlServerStorageMigrator(DbContextOptions<SqlServerDocumentDbContext> options, StorageLimits? limits = null)
    {
        this.options = SqlServerDocumentDatabase.Normalize(options);
        this.limits = limits ?? new();
        this.limits.Validate();
    }

    public async Task MigrateAsync(CancellationToken cancellationToken = default)
    {
        await using var db = new SqlServerDocumentDbContext(options);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await SqlServerDocumentDatabase.ConfigureTransactionAsync(db, cancellationToken);
        await SqlServerDocumentDatabase.CheckDurabilityAsync(db, cancellationToken);
        await SqlServerDocumentDatabase.LockAsync(db, "CellBridge.Documents.Migrations", cancellationToken);
        var known = db.Database.GetMigrations().ToHashSet(StringComparer.Ordinal);
        if ((await db.Database.GetAppliedMigrationsAsync(cancellationToken)).Any(id => !known.Contains(id)))
            throw new StorageUnavailableException("Unsupported document migration history; persisted data was not changed.");
        await db.Database.MigrateAsync(cancellationToken);
        var usage = await SqlServerDocumentDatabase.LockUsageAsync(db, cancellationToken);
        var actual = await db.Objects.SumAsync(row => (long?)row.Length, cancellationToken) ?? 0;
        actual = checked(actual + (await db.Snapshots.SumAsync(row => (long?)SqlServerDocumentDbContext.ByteLength(row.Data), cancellationToken) ?? 0));
        actual = checked(actual + (await db.RecoveryReceipts.SumAsync(row => (long?)SqlServerDocumentDbContext.ByteLength(row.Data), cancellationToken) ?? 0));
        var documents = await db.Documents.LongCountAsync(cancellationToken);
        if (actual != usage.StoredBytes) throw new StorageCorruptionException("Content usage ledger does not match stored objects.");
        if (documents != usage.DocumentCount) throw new StorageCorruptionException("Document usage ledger does not match stored identities.");
        StorageLimits.Check("total stored bytes", actual, limits.MaxStoredBytes);
        usage.MaxStoredBytes = limits.MaxStoredBytes;
        StorageLimits.Check("document count", documents, limits.MaxDocuments);
        usage.MaxDocuments = limits.MaxDocuments;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
