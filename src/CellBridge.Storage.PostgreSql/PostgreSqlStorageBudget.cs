using CellBridge.Storage.Abstractions;
using Npgsql;

namespace CellBridge.Storage.PostgreSql;

internal static class PostgreSqlStorageBudget
{
    public static async Task AdjustAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        long bytes, long documents, CancellationToken cancellationToken)
    {
        long used, count, maxBytes, maxCount;
        await using (var query = new NpgsqlCommand("SELECT stored_bytes,document_count,max_stored_bytes,max_documents FROM cellbridge_usage WHERE singleton=true FOR UPDATE", connection, transaction))
        await using (var reader = await query.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken)) throw new StorageCorruptionException("Missing storage usage ledger.");
            used = reader.GetInt64(0); count = reader.GetInt64(1);
            maxBytes = reader.GetInt64(2); maxCount = reader.GetInt64(3);
        }
        StorageLimits.Check("total stored bytes", checked(used + bytes), bytes > 0 ? maxBytes : long.MaxValue);
        StorageLimits.Check("document count", checked(count + documents), documents > 0 ? maxCount : long.MaxValue);
        await using var update = new NpgsqlCommand("UPDATE cellbridge_usage SET stored_bytes=stored_bytes+$1,document_count=document_count+$2 WHERE singleton=true", connection, transaction);
        update.Parameters.Add(new NpgsqlParameter { Value = bytes });
        update.Parameters.Add(new NpgsqlParameter { Value = documents });
        await update.ExecuteNonQueryAsync(cancellationToken);
    }
}
