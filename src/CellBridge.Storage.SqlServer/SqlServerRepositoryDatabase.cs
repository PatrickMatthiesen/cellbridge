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
