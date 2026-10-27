using System.Data;
using System.Text;
using CellBridge.Storage.Abstractions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace CellBridge.Storage.SqlServer;

internal sealed partial class SqlServerDocumentDatabase
{
    public async Task<IReadOnlySet<string>> CreateObjectsAsync(SqlServerDocumentDbContext db,
        IReadOnlyList<ContentHandle> handles, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var inserted = new HashSet<string>(StringComparer.Ordinal);
        if (handles.Count == 0) return inserted;
        var transaction = db.Database.CurrentTransaction?.GetDbTransaction() as SqlTransaction
            ?? throw new InvalidOperationException("The CellBridge database transaction is missing.");
        await using var command = new SqlCommand { Connection = (SqlConnection)db.Database.GetDbConnection(), Transaction = transaction };
        command.Parameters.Add("@mode", SqlDbType.NVarChar, 32).Value = "Exclusive";
        var sql = new StringBuilder("""
            DECLARE @created TABLE ([Key] char(64) COLLATE Latin1_General_100_BIN2 PRIMARY KEY);
            DECLARE @result int;

            """);
        // Preserve the same sorted lock/insert pairs as the single-object path.
        for (var index = 0; index < handles.Count; index++)
        {
            var handle = handles[index];
            command.Parameters.Add("@resource" + index, SqlDbType.NVarChar, 255).Value = "CellBridge.Content." + handle.Key;
            command.Parameters.Add("@key" + index, SqlDbType.Char, 64).Value = handle.Key;
            command.Parameters.Add("@length" + index, SqlDbType.BigInt).Value = handle.Length;
            sql.AppendLine($"""
                EXEC @result = sys.sp_getapplock @Resource=@resource{index}, @LockMode=@mode,
                    @LockOwner='Transaction', @LockTimeout=10000;
                IF @result < 0 THROW 51000, 'CellBridge storage coordination timed out.', 1;
                IF NOT EXISTS (SELECT 1 FROM cellbridge_documents.objects WHERE [Key]=@key{index})
                    INSERT INTO cellbridge_documents.objects ([Key],[Length],[Sha256])
                    OUTPUT inserted.[Key] INTO @created
                    VALUES (@key{index},@length{index},@key{index});
                """);
        }
        sql.AppendLine("SELECT [Key] FROM @created;");
        command.CommandText = sql.ToString();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) inserted.Add(reader.GetString(0));
        return inserted;
    }
}
