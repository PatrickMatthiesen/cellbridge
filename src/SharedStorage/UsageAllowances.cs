using CellBridge.Storage.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace CellBridge.Storage.Shared;

// C is the global charged capacity, R each unused allowance. Exact inventory is
// C - SUM(R). All allowances are durable and transactionally consumed. Ordinary
// quota writers reclaim them while holding global first, then all bucket locks.
internal static class UsageAllowances
{
    internal const int BucketCount = 64;
    internal const long RefillBytes = 4L * 1024 * 1024;

    internal static int Bucket(Guid document)
    {
        Span<byte> bytes = stackalloc byte[16];
        document.TryWriteBytes(bytes);
        // Mix every byte: time-ordered and caller-supplied IDs may share their
        // leading bytes even when their documents are otherwise independent.
        uint hash = 2166136261;
        foreach (var value in bytes) hash = unchecked((hash ^ value) * 16777619);
        return (int)(hash % BucketCount);
    }

    internal static void Reclaim(UsageRow usage, IReadOnlyList<UsageAllowanceRow> allowances)
    {
        Validate(allowances);
        long available = 0;
        foreach (var row in allowances) available = checked(available + row.AvailableBytes);
        if (available > usage.ChargedBytes)
            throw new StorageCorruptionException("Unused storage allowances exceed charged capacity.");
        usage.ChargedBytes -= available;
        foreach (var row in allowances) row.AvailableBytes = 0;
    }

    internal static async Task AdmitAsync<TContext>(IDocumentDatabase<TContext> database, TContext db,
        Guid document, long snapshotBytes, long contentBytes, CancellationToken ct) where TContext : DbContext
    {
        var bucket = Bucket(document);
        var bytes = checked(snapshotBytes + contentBytes);
        // Signed/pruning and zero changes keep the original sequential checks.
        // In particular zero changes must still detect a missing global ledger.
        if (snapshotBytes >= 0 && contentBytes >= 0 && bytes > 0 &&
            await database.TryConsumeUsageAllowanceAsync(db, bucket, bytes, ct)) return;

        // A failed claim has rolled back its savepoint and released bucket locks.
        // Refills take global first and then buckets in their fixed order. Keep
        // other buckets' allowances unless capacity must be reclaimed for admission.
        var pool = await database.LockUsageAllowancePoolAsync(db, ct);
        var usage = pool.Usage;
        Validate(pool.Allowances);
        var allowance = pool.Allowances.Single(row => row.BucketId == bucket);
        var consumed = Math.Min(allowance.AvailableBytes, Math.Max(0, bytes));
        var charge = bytes - consumed;
        if (snapshotBytes >= 0 && contentBytes >= 0 && usage.ChargedBytes <= usage.MaxStoredBytes &&
            charge <= usage.MaxStoredBytes - usage.ChargedBytes)
        {
            usage.ChargedBytes = checked(usage.ChargedBytes + charge);
            allowance.AvailableBytes -= consumed;
        }
        else
        {
            Reclaim(usage, pool.Allowances);
            DocumentBudget.Adjust(usage, snapshotBytes, 0);
            DocumentBudget.Adjust(usage, contentBytes, 0);
        }
        // Another queued writer may already have refilled this bucket. Top it
        // up to the target instead of accumulating another full reservation.
        var refill = Math.Min(Math.Max(0, RefillBytes - allowance.AvailableBytes),
            Math.Max(0, usage.MaxStoredBytes - usage.ChargedBytes));
        allowance.AvailableBytes = checked(allowance.AvailableBytes + refill);
        usage.ChargedBytes = checked(usage.ChargedBytes + refill);
        await db.SaveChangesAsync(ct);
    }

    private static void Validate(IReadOnlyList<UsageAllowanceRow> allowances)
    {
        if (allowances.Count != BucketCount || allowances.Select(row => row.BucketId).Distinct().Count() != BucketCount ||
            allowances.Any(row => row.BucketId < 0 || row.BucketId >= BucketCount || row.AvailableBytes < 0))
            throw new StorageCorruptionException("The storage allowance inventory is incomplete or invalid.");
    }
}
