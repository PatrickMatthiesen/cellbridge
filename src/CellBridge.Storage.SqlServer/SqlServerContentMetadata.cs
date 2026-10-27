using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace CellBridge.Storage.SqlServer;

internal sealed partial class SqlServerDocumentDatabase
{
    public IQueryable<ObjectRow> ObjectsAtKeys(SqlServerDocumentDbContext db, IReadOnlyList<string> keys)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(keys.Count, 64);
        if (keys.Count == 0) return db.Set<ObjectRow>().AsNoTracking().Where(_ => false);
        var parameters = keys.Select((key, index) => new SqlParameter("@key" + index, SqlDbType.Char, 64)
            { Value = key }).ToArray();
        var names = string.Join(",", parameters.Select(parameter => parameter.ParameterName));
        // A scan on a small objects table can wait on unrelated uncommitted
        // inserts. Seek only requested immutable keys under normal read locks.
        var query = $"""
            SELECT [Key], [Length], [Sha256]
            FROM [cellbridge_documents].[objects] WITH (FORCESEEK)
            WHERE [Key] IN ({names})
            """;
        return db.Set<ObjectRow>().FromSqlRaw(query, parameters).AsNoTracking();
    }
}
