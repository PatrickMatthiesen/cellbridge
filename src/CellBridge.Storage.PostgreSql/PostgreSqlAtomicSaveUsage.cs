using CellBridge.Storage.Abstractions;
using Npgsql;

namespace CellBridge.Storage.PostgreSql;

internal sealed partial class PostgreSqlDocumentDatabase
{
    public async Task<UsageRow> UpdateAtomicSaveUsageAsync(CellBridgeDbContext db, long snapshotBytes,
        long contentBytes, CancellationToken ct)
    {
        var (connection, transaction) = PostgreSqlDatabase.GetTransaction(db);
        await using var command = new NpgsqlCommand("""
            WITH prior AS MATERIALIZED (
                SELECT singleton, stored_bytes, document_count, max_stored_bytes, max_documents
                FROM cellbridge_usage WHERE singleton=true FOR UPDATE
            )
            UPDATE cellbridge_usage AS usage
            SET stored_bytes = (CASE
                WHEN prior.stored_bytes::numeric + $1::numeric + $2::numeric BETWEEN 0 AND 9223372036854775807
                THEN prior.stored_bytes::numeric + $1::numeric + $2::numeric
                ELSE prior.stored_bytes::numeric END)::bigint
            FROM prior WHERE usage.singleton=prior.singleton
            RETURNING prior.stored_bytes, prior.document_count, prior.max_stored_bytes, prior.max_documents
            """, connection, transaction);
        command.Parameters.AddWithValue(snapshotBytes);
        command.Parameters.AddWithValue(contentBytes);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            throw new StorageCorruptionException("Missing storage usage ledger.");
        // The returned row is intentionally detached. Common .NET admission
        // checks preserve checked arithmetic and exception behavior before COMMIT.
        return new UsageRow { Singleton = true, StoredBytes = reader.GetInt64(0), DocumentCount = reader.GetInt64(1),
            MaxStoredBytes = reader.GetInt64(2), MaxDocuments = reader.GetInt64(3) };
    }
}
