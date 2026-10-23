using CellBridge.Storage.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Data.SqlClient;

namespace CellBridge.Storage.SqlServer;

internal static class SqlServerDocumentDatabase
{
    public static DbContextOptions<SqlServerDocumentDbContext> Normalize(
        DbContextOptions<SqlServerDocumentDbContext> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        using var check = new SqlServerDocumentDbContext(options);
        if (!check.Database.IsSqlServer()) throw new ArgumentException("Configure the SQL Server EF provider.", nameof(options));
        if (check.Database.CreateExecutionStrategy().RetriesOnFailure)
            throw new ArgumentException("CellBridge owns explicit transactions. Do not enable EnableRetryOnFailure.", nameof(options));
        return new DbContextOptionsBuilder<SqlServerDocumentDbContext>(options)
            .UseSqlServer(sql => sql.MigrationsHistoryTable(SqlServerDocumentDbContext.MigrationsHistoryTable,
                SqlServerDocumentDbContext.Schema))
            .UseQueryTrackingBehavior(QueryTrackingBehavior.TrackAll)
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.MigrationsUserTransactionWarning)).Options;
    }

    public static Task ConfigureTransactionAsync(SqlServerDocumentDbContext context, CancellationToken ct)
    {
        context.Database.AutoSavepointsEnabled = false;
        return context.Database.ExecuteSqlRawAsync("SET XACT_ABORT ON; SET LOCK_TIMEOUT 10000;", ct);
    }

    public static Task LockAsync(SqlServerDocumentDbContext context, string resource, CancellationToken ct, string mode = "Exclusive") =>
        context.Database.ExecuteSqlInterpolatedAsync($"""
            DECLARE @result int;
            EXEC @result = sys.sp_getapplock @Resource={resource}, @LockMode={mode},
                @LockOwner='Transaction', @LockTimeout=10000;
            IF @result < 0 THROW 51000, 'CellBridge storage coordination timed out.', 1;
            """, ct);

    public static async Task<ContentUsage> LockUsageAsync(SqlServerDocumentDbContext context, CancellationToken ct) =>
        context.Usage.Local.SingleOrDefault() ?? await context.Usage.FromSqlRaw("SELECT * FROM cellbridge_documents.usage WITH (UPDLOCK,HOLDLOCK) WHERE Id=1")
            .SingleOrDefaultAsync(ct) ?? throw new StorageCorruptionException("Missing content usage ledger.");

    public static async Task AdjustUsageAsync(SqlServerDocumentDbContext db, long bytes, long documents, CancellationToken ct)
    {
        var usage = await LockUsageAsync(db, ct);
        var total = checked(usage.StoredBytes + bytes);
        var count = checked(usage.DocumentCount + documents);
        StorageLimits.Check("total stored bytes", total, bytes > 0 ? usage.MaxStoredBytes : long.MaxValue);
        StorageLimits.Check("document count", count, documents > 0 ? usage.MaxDocuments : long.MaxValue);
        usage.StoredBytes = total;
        usage.DocumentCount = count;
    }

    public static async Task CheckConfigurationAsync(SqlServerDocumentDbContext db, CancellationToken ct)
    {
        var versions = await db.SchemaVersions.AsNoTracking().Select(row => row.Version).ToListAsync(ct);
        if (versions.Count != 1 || versions[0] != 1)
            throw new StorageUnavailableException("Document schema version 1 is required. Run explicit storage initialization.");
        await CheckDurabilityAsync(db, ct);
    }

    public static async Task CheckDurabilityAsync(SqlServerDocumentDbContext db, CancellationToken ct)
    {
        var delayed = await db.Database.SqlQueryRaw<string>(
            "SELECT delayed_durability_desc AS Value FROM sys.databases WHERE database_id=DB_ID()").SingleAsync(ct);
        if (delayed != "DISABLED")
            throw new StorageUnavailableException("CellBridge requires database delayed durability to be DISABLED.");
    }

    public static bool IsDatabaseException(Exception ex) => ex is SqlException or DbUpdateConcurrencyException ||
        ex.InnerException is not null && IsDatabaseException(ex.InnerException);
}
