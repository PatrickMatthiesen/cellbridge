using System.Data;
using CellBridge.Storage.Abstractions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace CellBridge.Storage.SqlServer;

internal sealed partial class SqlServerDocumentDatabase
{
    public async Task<UsageRow> UpdateAtomicSaveUsageAsync(SqlServerDocumentDbContext db, long snapshotBytes,
        long contentBytes, CancellationToken ct)
    {
        var transaction = db.Database.CurrentTransaction?.GetDbTransaction() as SqlTransaction
            ?? throw new InvalidOperationException("The CellBridge database transaction is missing.");
        await using var command = new SqlCommand("""
            UPDATE cellbridge_documents.usage WITH (UPDLOCK,HOLDLOCK)
            SET StoredBytes = CONVERT(bigint, CASE
                WHEN CONVERT(decimal(38,0),StoredBytes) + CONVERT(decimal(38,0),@snapshot) +
                    CONVERT(decimal(38,0),@content) BETWEEN 0 AND 9223372036854775807
                THEN CONVERT(decimal(38,0),StoredBytes) + CONVERT(decimal(38,0),@snapshot) +
                    CONVERT(decimal(38,0),@content)
                ELSE CONVERT(decimal(38,0),StoredBytes) END)
            OUTPUT deleted.StoredBytes, deleted.DocumentCount, deleted.MaxStoredBytes, deleted.MaxDocuments
            WHERE Id=1
            """, (SqlConnection)db.Database.GetDbConnection(), transaction);
        command.Parameters.Add("@snapshot", SqlDbType.BigInt).Value = snapshotBytes;
        command.Parameters.Add("@content", SqlDbType.BigInt).Value = contentBytes;
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            throw new StorageCorruptionException("Missing content usage ledger.");
        return new UsageRow { Singleton = true, StoredBytes = reader.GetInt64(0), DocumentCount = reader.GetInt64(1),
            MaxStoredBytes = reader.GetInt64(2), MaxDocuments = reader.GetInt64(3) };
    }
}
