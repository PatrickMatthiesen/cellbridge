using CellBridge.Storage.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace CellBridge.Storage.PostgreSql;

internal static class PostgreSqlStorageBudget
{
    public static async Task AdjustAsync(CellBridgeDbContext context, long bytes, long documents,
        CancellationToken cancellationToken)
    {
        DocumentBudget.Adjust(await LockUsageAsync(context, cancellationToken), bytes, documents);
    }

    internal static async Task<UsageRow> LockUsageAsync(CellBridgeDbContext context, CancellationToken ct)
    {
        var previouslyTracked = DocumentBudget.FindTrackedUsage(context) is not null;
        var pool = await LockPoolAsync(context, ct);
        if (!previouslyTracked || pool.Allowances.Count != UsageAllowances.BucketCount ||
            pool.Allowances.Any(row => row.AvailableBytes != 0))
            UsageAllowances.Reclaim(pool.Usage, pool.Allowances);
        return pool.Usage;
    }

    internal static async Task<(UsageRow Usage, IReadOnlyList<UsageAllowanceRow> Allowances)> LockPoolAsync(
        CellBridgeDbContext context, CancellationToken ct)
    {
        var tracked = DocumentBudget.FindTrackedUsage(context);
        if (tracked is not null)
        {
            var detect = context.ChangeTracker.AutoDetectChangesEnabled;
            try
            {
                context.ChangeTracker.AutoDetectChangesEnabled = false;
                var rows = context.Set<UsageAllowanceRow>().Local.ToArray();
                if (rows.Length == UsageAllowances.BucketCount) return (tracked, rows);
            }
            finally { context.ChangeTracker.AutoDetectChangesEnabled = detect; }
        }
        var usage = tracked
            ?? await context.Usage.FromSqlRaw("SELECT * FROM cellbridge_usage WHERE singleton=true FOR UPDATE")
                .SingleOrDefaultAsync(ct)
            ?? throw new StorageCorruptionException("Missing storage usage ledger.");
        var allowances = await context.Set<UsageAllowanceRow>().FromSqlRaw(
            "SELECT * FROM cellbridge_usage_buckets ORDER BY bucket_id FOR UPDATE").ToListAsync(ct);
        return (usage, allowances);
    }
}
