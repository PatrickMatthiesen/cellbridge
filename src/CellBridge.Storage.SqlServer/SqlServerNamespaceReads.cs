using System.Text;
using Microsoft.EntityFrameworkCore;

namespace CellBridge.Storage.SqlServer;

internal sealed partial class SqlServerDocumentDatabase
{
    public async IAsyncEnumerable<(DocumentRow Pointer, string? Json)> ReadNamespaceRowsAsync(SqlServerDocumentDbContext db,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        // OPENJSON projects only namespace properties. Reconstructing these fields
        // preserves JSON types, long paths and duplicate keys for common validation.
        var rows = db.Database.SqlQueryRaw<NamespaceRow>("""
            SELECT d.ResourceId, d.PathKey, d.PathHash, d.RetiredPathCount, d.StateVersion, d.IsDeleted,
                (SELECT N'{' + STRING_AGG(CONVERT(nvarchar(max),N'"') + STRING_ESCAPE(field.[key],'json') + N'":' +
                    CASE field.[type] WHEN 0 THEN N'null'
                        WHEN 1 THEN N'"' + STRING_ESCAPE(field.[value],'json') + N'"'
                        ELSE field.[value] END, N',') + N'}'
                    FROM OPENJSON(CASE WHEN ISJSON(snapshot.Json)=1 THEN snapshot.Json ELSE N'{}' END) field
                    WHERE field.[key] COLLATE Latin1_General_100_BIN2 IN
                        ('FormatVersion','ResourceId','StateVersion','PathKey',
                         'IsDeleted','LifecycleGeneration','ReplacedBy','RetiredPathKeys')) AS NamespaceJson
            FROM cellbridge_documents.documents d LEFT JOIN cellbridge_documents.snapshots s
                ON d.ResourceId=s.ResourceId AND d.StateVersion=s.StateVersion
            CROSS APPLY (SELECT CONVERT(nvarchar(max),
                CONVERT(varchar(max),s.Data) COLLATE Latin1_General_100_BIN2_UTF8) AS Json) snapshot
            """).AsAsyncEnumerable();
        await foreach (var row in rows.WithCancellation(ct))
            yield return (new DocumentRow { ResourceId = row.ResourceId,
                PathKey = Encoding.BigEndianUnicode.GetString(row.PathKey), PathHash = row.PathHash,
                RetiredPathCount = row.RetiredPathCount, StateVersion = row.StateVersion, IsDeleted = row.IsDeleted }, row.NamespaceJson);
    }

    private sealed class NamespaceRow
    {
        public Guid ResourceId { get; set; }
        public required byte[] PathKey { get; set; }
        public required byte[] PathHash { get; set; }
        public int RetiredPathCount { get; set; }
        public long StateVersion { get; set; }
        public bool IsDeleted { get; set; }
        public string? NamespaceJson { get; set; }
    }
}
