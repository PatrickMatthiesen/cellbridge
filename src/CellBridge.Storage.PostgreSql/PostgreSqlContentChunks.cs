using System.Text;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace CellBridge.Storage.PostgreSql;

internal sealed partial class PostgreSqlDocumentDatabase
{
    public async Task InsertChunksAsync(CellBridgeDbContext db, IReadOnlyList<ChunkRow> chunks, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (chunks.Count == 0) return;
        var sql = new StringBuilder("INSERT INTO cellbridge_chunks (object_key,ordinal,bytes) VALUES ");
        var parameters = new List<object>(chunks.Count * 3);
        for (var index = 0; index < chunks.Count; index++)
        {
            if (index != 0) sql.Append(',');
            sql.Append($"(@key{index},@ordinal{index},@bytes{index})");
            var chunk = chunks[index];
            parameters.Add(new NpgsqlParameter("key" + index, NpgsqlDbType.Text) { Value = chunk.ObjectKey });
            parameters.Add(new NpgsqlParameter("ordinal" + index, NpgsqlDbType.Integer) { Value = chunk.Ordinal });
            parameters.Add(new NpgsqlParameter("bytes" + index, NpgsqlDbType.Bytea) { Value = chunk.Bytes });
        }
        await db.Database.ExecuteSqlRawAsync(sql.ToString(), parameters, ct);
    }
}
