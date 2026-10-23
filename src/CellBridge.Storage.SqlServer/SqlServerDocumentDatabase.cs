using CellBridge.Storage.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

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

    public static Task ConfigureTransactionAsync(SqlServerDocumentDbContext context, CancellationToken ct) =>
        context.Database.ExecuteSqlRawAsync("SET XACT_ABORT ON; SET LOCK_TIMEOUT 10000;", ct);

    public static Task LockAsync(SqlServerDocumentDbContext context, string resource, CancellationToken ct) =>
        context.Database.ExecuteSqlInterpolatedAsync($"""
            DECLARE @result int;
            EXEC @result = sys.sp_getapplock @Resource={resource}, @LockMode='Exclusive',
                @LockOwner='Transaction', @LockTimeout=10000;
            IF @result < 0 THROW 51000, 'CellBridge storage coordination timed out.', 1;
            """, ct);

    public static async Task<ContentUsage> LockUsageAsync(SqlServerDocumentDbContext context, CancellationToken ct) =>
        await context.Usage.FromSqlRaw("SELECT * FROM cellbridge_documents.usage WITH (UPDLOCK,HOLDLOCK) WHERE Id=1")
            .SingleOrDefaultAsync(ct) ?? throw new StorageCorruptionException("Missing content usage ledger.");
}
