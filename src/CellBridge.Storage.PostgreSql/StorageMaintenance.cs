using CellBridge.Storage.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace CellBridge.Storage.PostgreSql;

public sealed record StorageMaintenanceReport(long StoredBytes, long Documents, long Snapshots,
    long ReferencedObjects, long OrphanObjects, long OrphanBytes, bool Applied);

/// <summary>Collects orphan objects after all readers and writers have stopped.</summary>
public sealed class StorageMaintenance
{
    private readonly DocumentMaintenance<CellBridgeDbContext> maintenance;
    public StorageMaintenance(Npgsql.NpgsqlDataSource source) : this(source, null) { }
    public StorageMaintenance(Npgsql.NpgsqlDataSource source, StorageLimits? limits)
        : this(PostgreSqlDatabase.CreateOptions(source), limits) { }
    public StorageMaintenance(DbContextOptions<CellBridgeDbContext> options, StorageLimits? limits = null)
    {
        limits?.Validate();
        options = PostgreSqlDatabase.NormalizeOptions(options);
        maintenance = new(new PostgreSqlDocumentDatabase(options), new PostgreSqlRepositoryDatabase(options), limits);
    }
    public async Task<StorageMaintenanceReport> CollectOrphansAsync(bool apply = false, bool quiescent = false,
        string? fileSystemRoot = null, CancellationToken cancellationToken = default)
    {
        Action<string>? deleteFile = fileSystemRoot is null ? null : key =>
        {
            if (key.Length != 64 || key.Any(character => !char.IsAsciiHexDigit(character)))
                throw new StorageCorruptionException("Invalid orphan key.");
            CellBridge.Storage.FileSystem.FileSystemContentStore.DeleteQuiescentObject(fileSystemRoot, key);
        };
        var report = await maintenance.CollectOrphansAsync(apply, quiescent, deleteFile, cancellationToken);
        return new(report.StoredBytes, report.Documents, report.Snapshots, report.ReferencedObjects,
            report.OrphanObjects, report.OrphanBytes, report.Applied);
    }
}
