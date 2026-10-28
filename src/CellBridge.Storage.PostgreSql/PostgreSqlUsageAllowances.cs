using CellBridge.Storage.Abstractions;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CellBridge.Storage.PostgreSql;

internal sealed partial class PostgreSqlDocumentDatabase
{
    public Task<(UsageRow Usage, IReadOnlyList<UsageAllowanceRow> Allowances)> LockUsageAllowancePoolAsync(
        CellBridgeDbContext db, CancellationToken ct) => PostgreSqlStorageBudget.LockPoolAsync(db, ct);

    public async Task<bool> TryConsumeUsageAllowanceAsync(CellBridgeDbContext db, int bucket, long bytes,
        CancellationToken ct)
    {
        var (connection, transaction) = PostgreSqlDatabase.GetTransaction(db);
        await using var batch = new NpgsqlBatch(connection, transaction);
        if (db.Database.GetCommandTimeout() is { } timeout) batch.Timeout = timeout;
        batch.BatchCommands.Add(new("SAVEPOINT cellbridge_quota_claim"));
        var claim = new NpgsqlBatchCommand("""
            UPDATE cellbridge_usage_buckets SET available_bytes=available_bytes-$2
            WHERE bucket_id=$1 AND available_bytes >= $2
                AND EXISTS (SELECT 1 FROM cellbridge_usage WHERE singleton=true AND stored_bytes <= max_stored_bytes)
            RETURNING available_bytes
            """);
        claim.Parameters.AddWithValue(bucket);
        claim.Parameters.AddWithValue(bytes);
        batch.BatchCommands.Add(claim);
        bool consumed;
        await using (var reader = await batch.ExecuteReaderAsync(ct)) consumed = await reader.ReadAsync(ct);
        if (consumed) return true;
        // Release every lock acquired by the failed claim before taking global
        // then bucket locks. SQL errors and cancellation abandon the whole save.
        await using var rollback = new NpgsqlCommand("ROLLBACK TO SAVEPOINT cellbridge_quota_claim", connection, transaction);
        if (db.Database.GetCommandTimeout() is { } rollbackTimeout) rollback.CommandTimeout = rollbackTimeout;
        await rollback.ExecuteNonQueryAsync(ct);
        return false;
    }
}
