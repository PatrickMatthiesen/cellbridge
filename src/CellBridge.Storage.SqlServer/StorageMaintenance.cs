using CellBridge.Storage.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace CellBridge.Storage.SqlServer;

public sealed record StorageMaintenanceReport(long StoredBytes, long Documents, long Snapshots,
    long ReferencedObjects, long OrphanObjects, long OrphanBytes, bool Applied);

/// <summary>Collects orphan objects after all readers and writers have stopped.</summary>
public sealed class StorageMaintenance
{
    private readonly DocumentMaintenance<SqlServerDocumentDbContext> maintenance;
    public StorageMaintenance(DbContextOptions<SqlServerDocumentDbContext> options, StorageLimits? limits = null)
    {
        limits?.Validate();
        options = SqlServerDocumentDatabase.Normalize(options);
        maintenance = new(new SqlServerDocumentDatabase(options), limits);
    }
    public async Task<StorageMaintenanceReport> CollectOrphansAsync(bool apply = false, bool quiescent = false,
        CancellationToken cancellationToken = default)
    {
        var report = await maintenance.CollectOrphansAsync(apply, quiescent, null, cancellationToken);
        return new(report.StoredBytes, report.Documents, report.Snapshots, report.ReferencedObjects,
            report.OrphanObjects, report.OrphanBytes, report.Applied);
    }
}
