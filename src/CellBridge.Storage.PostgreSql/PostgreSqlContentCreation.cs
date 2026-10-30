using CellBridge.Storage.Abstractions;
using Npgsql;

namespace CellBridge.Storage.PostgreSql;

internal sealed partial class PostgreSqlDocumentDatabase
{
    public async Task<IReadOnlySet<string>> CreateObjectsAsync(CellBridgeDbContext db,
        IReadOnlyList<ContentHandle> handles, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var inserted = new HashSet<string>(StringComparer.Ordinal);
        if (handles.Count == 0) return inserted;
        var (connection, transaction) = PostgreSqlDatabase.GetTransaction(db);
        await using var batch = new NpgsqlBatch(connection, transaction);
        var inserts = new List<(string Key, NpgsqlBatchCommand Command)>(handles.Count);
        // The unique index coordinates duplicate creation. Sort keys to keep
        // overlapping multi-object insertions in the same order.
        foreach (var handle in handles)
        {
            var insert = new NpgsqlBatchCommand("INSERT INTO cellbridge_objects VALUES ($1,$2,$1) ON CONFLICT DO NOTHING");
            insert.Parameters.AddWithValue(handle.Key);
            insert.Parameters.AddWithValue(handle.Length);
            batch.BatchCommands.Add(insert);
            inserts.Add((handle.Key, insert));
        }
        await batch.ExecuteNonQueryAsync(ct);
        foreach (var (key, command) in inserts)
            if (command.Rows == 1) inserted.Add(key);
        return inserted;
    }
}
