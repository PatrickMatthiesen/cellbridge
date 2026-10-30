using CellBridge.Storage.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace CellBridge.Storage.SqlServer;

/// <summary>Explicit consumer initialization. Constructors and requests never change the schema.</summary>
public sealed class SqlServerStorageMigrator
{
    private readonly DbContextOptions<SqlServerDocumentDbContext> options;
    private readonly StorageLimits limits;
    private readonly IContentStore? migrationContent;

    public SqlServerStorageMigrator(DbContextOptions<SqlServerDocumentDbContext> options, StorageLimits? limits = null, IContentStore? migrationContent = null)
    {
        this.options = SqlServerDocumentDatabase.Normalize(options);
        this.migrationContent = migrationContent;
        this.limits = limits ?? new();
        this.limits.Validate();
    }

    public async Task MigrateAsync(CancellationToken cancellationToken = default)
    {
        await using var db = new SqlServerDocumentDbContext(options);
        if (await db.Database.SqlQueryRaw<int>("SELECT CAST(snapshot_isolation_state AS int) AS Value FROM sys.databases WHERE database_id=DB_ID()").SingleAsync(cancellationToken) != 1)
            await db.Database.ExecuteSqlRawAsync("ALTER DATABASE CURRENT SET ALLOW_SNAPSHOT_ISOLATION ON", cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await SqlServerDocumentDatabase.ConfigureTransactionAsync(db, cancellationToken);
        await SqlServerDocumentDatabase.CheckDurabilityAsync(db, cancellationToken);
        await SqlServerDocumentDatabase.LockAsync(db, SqlServerDocumentDatabase.MigrationGate, cancellationToken);
        await SqlServerDocumentDatabase.LockAsync(db, "CellBridge.Documents.Migrations", cancellationToken);
        var known = db.Database.GetMigrations().ToHashSet(StringComparer.Ordinal);
        if ((await db.Database.GetAppliedMigrationsAsync(cancellationToken)).Any(id => !known.Contains(id)))
            throw new StorageUnavailableException("Unsupported document migration history; persisted data was not changed.");
        await db.Database.MigrateAsync(cancellationToken);
        await new RelationalDocumentRepository<SqlServerDocumentDbContext>(new SqlServerRepositoryDatabase(options), limits)
            .BackfillAsync(db, migrationContent, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
