using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.SqlClient;

namespace CellBridge.Storage.SqlServer;

internal sealed class SqlServerRepositoryDatabase(DbContextOptions<SqlServerDocumentDbContext> options) : IRepositoryDatabase<SqlServerDocumentDbContext>
{
    public SqlServerDocumentDbContext CreateContext() => new(options);
    public IsolationLevel ReadIsolation => IsolationLevel.Snapshot;
    public IQueryable<ObjectRow> ObjectsAtKeys(SqlServerDocumentDbContext db, IReadOnlyList<string> keys) =>
        SqlServerDocumentDatabase.QueryObjectsAtKeys(db, keys);
    public Task ConfigureAsync(SqlServerDocumentDbContext db, CancellationToken ct) => SqlServerDocumentDatabase.ConfigureTransactionAsync(db, ct);
    public async Task CheckHealthAsync(SqlServerDocumentDbContext db, CancellationToken ct)
    {
        await SqlServerDocumentDatabase.CheckConfigurationAsync(db, ct);
        if (await db.Database.SqlQueryRaw<int>("SELECT CAST(snapshot_isolation_state AS int) AS Value FROM sys.databases WHERE database_id=DB_ID()").SingleAsync(ct) != 1)
            throw new CellBridge.Storage.Abstractions.StorageUnavailableException("CellBridge document reads require ALLOW_SNAPSHOT_ISOLATION ON. Run explicit initialization with hosts stopped.");
    }
    public Task<RepositoryControlRow[]> ReadControlRowsAsync(SqlServerDocumentDbContext db, Guid id, long lifecycle,
        bool includePartitions, bool includeClock, CancellationToken ct)
    {
        var sql = """
            SELECT 1 AS Kind, CAST(NULL AS nvarchar(max)) AS Json, [Json] AS EncodedJson, CAST(NULL AS uniqueidentifier) AS ClientId, CAST(NULL AS int) AS [Partition], CAST(NULL AS bigint) AS Sequence, CAST(NULL AS datetime2) AS UtcNow
            FROM cellbridge_documents.repository_coordination WHERE ResourceId=@id
            UNION ALL
            SELECT 2, NULL, [Json], ClientId, NULL, NULL, NULL
            FROM cellbridge_documents.repository_editors WHERE ResourceId=@id
            """;
        if (includePartitions) sql += """

            UNION ALL
            SELECT 3, NULL, [Json], NULL, [Partition], Sequence, NULL
            FROM cellbridge_documents.repository_partitions WHERE ResourceId=@id AND Lifecycle=@lifecycle
            """;
        if (includeClock) sql += """

            UNION ALL
            SELECT 0, NULL, NULL, NULL, NULL, NULL, SYSUTCDATETIME()
            """;
        return db.Database.SqlQueryRaw<RepositoryControlRow>(sql, new SqlParameter("id", id),
            new SqlParameter("lifecycle", lifecycle)).TagWith("CellBridge coherent control batch")
            .OrderBy(row => row.Kind).ThenBy(row => row.ClientId).ThenBy(row => row.Partition).ToArrayAsync(ct);
    }
    public Task<DateTime> ClockAsync(SqlServerDocumentDbContext db, CancellationToken ct) => ClockCoreAsync(db, ct);
    private static async Task<DateTime> ClockCoreAsync(SqlServerDocumentDbContext db, CancellationToken ct) => DateTime.SpecifyKind(
        await db.Database.SqlQueryRaw<DateTime>("SELECT SYSUTCDATETIME() AS Value").SingleAsync(ct), DateTimeKind.Utc);
    public Task GuardAsync(SqlServerDocumentDbContext db, Guid id, CancellationToken ct) => db.Database.ExecuteSqlAsync($"""
        SELECT ResourceId FROM cellbridge_documents.repository_heads WITH (UPDLOCK,ROWLOCK) WHERE ResourceId={id}
        """, ct);
    public Task RecoveryGuardAsync(SqlServerDocumentDbContext db, CancellationToken ct) =>
        SqlServerDocumentDatabase.LockAsync(db, "CellBridge.Documents.NormalizedRecovery", ct);
    public bool IsDatabaseException(Exception error) => SqlServerDocumentDatabase.IsDatabaseException(error);
    public bool IsUniqueViolation(Exception error) => error is SqlException { Number: 2601 or 2627 } ||
        error.InnerException is { } inner && IsUniqueViolation(inner);
}
