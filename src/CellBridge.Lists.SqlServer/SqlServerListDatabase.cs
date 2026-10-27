using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace CellBridge.Lists.SqlServer;

internal static class SqlServerListDatabase
{
    internal static DbContextOptions<SqlServerListDbContext> Normalize(
        DbContextOptions<SqlServerListDbContext> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        using var check = new SqlServerListDbContext(options);
        if (!check.Database.IsSqlServer())
            throw new ArgumentException("Configure the SQL Server EF provider.", nameof(options));
        if (check.Database.CreateExecutionStrategy().RetriesOnFailure)
            throw new ArgumentException("CellBridge owns transactions and receipt retries. Do not enable EnableRetryOnFailure.", nameof(options));
        if (new SqlConnectionStringBuilder(check.Database.GetDbConnection().ConnectionString).MultipleActiveResultSets)
            throw new ArgumentException("CellBridge Lists requires MultipleActiveResultSets=false.", nameof(options));
        return new DbContextOptionsBuilder<SqlServerListDbContext>(options)
            .UseSqlServer(sql => sql.MigrationsHistoryTable(SqlServerListDbContext.MigrationsHistoryTable,
                SqlServerListDbContext.Schema))
            .UseQueryTrackingBehavior(QueryTrackingBehavior.TrackAll).Options;
    }

    // All participants take the workspace lock first. Reads hold Shared; registration
    // and mutations hold Exclusive until commit. Record identity locks come second.
    internal static Task LockAsync(SqlServerListDbContext db, string resource, bool exclusive,
        ListLimits limits, CancellationToken ct) => db.Database.ExecuteSqlAsync($"""
            DECLARE @result int;
            EXEC @result=sys.sp_getapplock @Resource={resource},
                @LockMode={(exclusive ? "Exclusive" : "Shared")}, @LockOwner='Transaction',
                @LockTimeout={(int)Math.Ceiling(limits.StatementTimeout.TotalMilliseconds)};
            IF @result < 0 THROW 51000, 'CellBridge Lists coordination failed.', 1;
            """, ct);

    internal static string WorkspaceLock(Guid workspaceId) => "CellBridge.Lists.Workspace:" + workspaceId.ToString("N");

    internal static Task<DateTimeOffset> ClockAsync(SqlServerListDbContext db, CancellationToken ct) =>
        db.Database.SqlQueryRaw<DateTimeOffset>(
            "SELECT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00') AS Value").SingleAsync(ct);

    internal static async Task<T> MapFailuresAsync<T>(Func<Task<T>> operation, CancellationToken ct)
    {
        try { return await operation(); }
        catch (Exception ex) when (IsStoreFailure(ex) && ct.IsCancellationRequested)
        { throw new OperationCanceledException("The list operation was canceled.", ex, ct); }
        catch (Exception ex) when (IsStoreFailure(ex))
        {
            // A failed commit can have an unknown outcome. Do not retry here. The
            // caller can resolve a create by repeating its original identity/payload.
            throw new ListException(ListFailure.Unavailable,
                $"The SQL Server list operation could not be confirmed: {ex.GetBaseException().Message} Retry creates with the same identity and payload.");
        }
    }

    internal static async Task SaveRequiredAsync(SqlServerListDbContext db, CancellationToken ct)
    {
        db.ChangeTracker.DetectChanges();
        await db.SaveChangesAsync(ct);
        // Suppression bypasses the EF state manager and acceptance of changes,
        // including when an interceptor returns a fake positive row count.
        // No database reads are needed to distinguish it from a completed save.
        db.ChangeTracker.DetectChanges();
        if (db.ChangeTracker.HasChanges())
            throw new ListException(ListFailure.Unavailable, "Required Lists publication was suppressed or left pending changes.");
    }

    private static bool IsStoreFailure(Exception ex) => ex is SqlException or DbUpdateException ||
        ex.InnerException is not null && IsStoreFailure(ex.InnerException);
}
