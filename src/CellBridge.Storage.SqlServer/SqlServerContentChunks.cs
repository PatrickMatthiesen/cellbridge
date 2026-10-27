using System.Data;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace CellBridge.Storage.SqlServer;

internal sealed partial class SqlServerDocumentDatabase
{
    public async Task InsertChunksAsync(SqlServerDocumentDbContext db, IReadOnlyList<ChunkRow> chunks, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (chunks.Count == 0) return;
        var sql = new StringBuilder("INSERT INTO [cellbridge_documents].[chunks] ([Key],[Ordinal],[Bytes]) VALUES ");
        var parameters = new List<object>(chunks.Count * 3);
        for (var index = 0; index < chunks.Count; index++)
        {
            if (index != 0) sql.Append(',');
            sql.Append($"(@key{index},@ordinal{index},@bytes{index})");
            var chunk = chunks[index];
            parameters.Add(new SqlParameter("@key" + index, SqlDbType.Char, 64) { Value = chunk.ObjectKey });
            parameters.Add(new SqlParameter("@ordinal" + index, SqlDbType.Int) { Value = chunk.Ordinal });
            parameters.Add(new SqlParameter("@bytes" + index, SqlDbType.VarBinary, -1) { Value = chunk.Bytes });
        }
        await db.Database.ExecuteSqlRawAsync(sql.ToString(), parameters, ct);
    }
}
