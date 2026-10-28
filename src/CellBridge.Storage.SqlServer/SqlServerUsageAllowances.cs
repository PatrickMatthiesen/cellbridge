using System.Data;
using CellBridge.Storage.Abstractions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace CellBridge.Storage.SqlServer;

internal sealed partial class SqlServerDocumentDatabase
{
    public async Task<bool> TryConsumeUsageAllowanceAsync(SqlServerDocumentDbContext db, int bucket,
        long bytes, CancellationToken ct)
    {
        if (bucket < 0 || bucket >= UsageAllowances.BucketCount)
            throw new ArgumentOutOfRangeException(nameof(bucket));
        if (bytes <= 0) throw new ArgumentOutOfRangeException(nameof(bytes));
        var transaction = db.Database.CurrentTransaction?.GetDbTransaction() as SqlTransaction
            ?? throw new InvalidOperationException("The CellBridge database transaction is missing.");
        // This transaction has not touched usage_buckets before this savepoint.
        // ROWLOCK and disabled table lock escalation keep a failed claim's locks
        // releasable by rollback, before the slow path takes the global lock.
        // Provider errors and cancellation propagate to the outer transaction.
        await using var command = new SqlCommand("""
            SAVE TRANSACTION quota_claim;
            DECLARE @claimed TABLE (BucketId int NOT NULL);
            UPDATE cellbridge_documents.usage_buckets WITH (ROWLOCK)
            SET AvailableBytes = AvailableBytes - @bytes
            OUTPUT inserted.BucketId INTO @claimed
            WHERE BucketId = @bucket AND AvailableBytes >= @bytes
                AND EXISTS (SELECT 1 FROM cellbridge_documents.usage
                    WHERE Id=1 AND StoredBytes <= MaxStoredBytes);
            IF @@ROWCOUNT = 0 ROLLBACK TRANSACTION quota_claim;
            SELECT CONVERT(bit, CASE WHEN EXISTS (SELECT 1 FROM @claimed) THEN 1 ELSE 0 END);
            """, (SqlConnection)db.Database.GetDbConnection(), transaction);
        if (db.Database.GetCommandTimeout() is { } timeout) command.CommandTimeout = timeout;
        command.Parameters.Add("@bucket", SqlDbType.Int).Value = bucket;
        command.Parameters.Add("@bytes", SqlDbType.BigInt).Value = bytes;
        return (bool)(await command.ExecuteScalarAsync(ct)
            ?? throw new StorageCorruptionException("The storage allowance claim returned no result."));
    }

    public Task<(UsageRow Usage, IReadOnlyList<UsageAllowanceRow> Allowances)> LockUsageAllowancePoolAsync(
        SqlServerDocumentDbContext db, CancellationToken ct) => LockUsageAllowancePoolCoreAsync(db, ct);

    private static async Task<(UsageRow Usage, IReadOnlyList<UsageAllowanceRow> Allowances)> LockUsageAllowancePoolCoreAsync(
        SqlServerDocumentDbContext db, CancellationToken ct)
    {
        var transaction = db.Database.CurrentTransaction
            ?? throw new InvalidOperationException("The CellBridge database transaction is missing.");
        if (ReferenceEquals(db.UsageAllowancePoolTransaction, transaction))
            return (DocumentBudget.FindTrackedUsage(db)
                ?? throw new StorageCorruptionException("Missing tracked content usage ledger."), db.LockedUsageAllowances!);

        var usage = await db.Usage.FromSqlRaw(
                "SELECT * FROM cellbridge_documents.usage WITH (UPDLOCK,HOLDLOCK) WHERE Id=1")
            .SingleOrDefaultAsync(ct) ?? throw new StorageCorruptionException("Missing content usage ledger.");
        var allowances = await db.UsageBuckets.FromSqlRaw(
                "SELECT * FROM cellbridge_documents.usage_buckets WITH (UPDLOCK,HOLDLOCK) ORDER BY BucketId")
            .ToListAsync(ct);
        db.LockedUsageAllowances = allowances;
        db.UsageAllowancePoolTransaction = transaction;
        return (usage, allowances);
    }
}
