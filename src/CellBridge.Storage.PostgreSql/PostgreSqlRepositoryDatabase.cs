using System.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace CellBridge.Storage.PostgreSql;

internal sealed class PostgreSqlRepositoryDatabase(DbContextOptions<CellBridgeDbContext> options) : IRepositoryDatabase<CellBridgeDbContext>
{
    private readonly PooledDbContextFactory<CellBridgeDbContext>? factory = PostgreSqlDatabase.CreateRuntimeFactory(options);
    public CellBridgeDbContext CreateContext() => factory?.CreateDbContext() ?? new(options);
    public IsolationLevel ReadIsolation => IsolationLevel.RepeatableRead;
    public IQueryable<ObjectRow> ObjectsAtKeys(CellBridgeDbContext db, IReadOnlyList<string> keys) =>
        PostgreSqlDocumentDatabase.QueryObjectsAtKeys(db, keys);
    public Task ConfigureAsync(CellBridgeDbContext db, CancellationToken ct) =>
        db.Database.ExecuteSqlRawAsync("SET LOCAL synchronous_commit=on; SET LOCAL lock_timeout='5s'; SET LOCAL statement_timeout='30s'", ct);
    public async Task CheckHealthAsync(CellBridgeDbContext db, CancellationToken ct)
    {
        await new PostgreSqlDocumentDatabase(options).CheckConfigurationAsync(db, ct);
        var (connection, transaction) = PostgreSqlDatabase.GetTransaction(db);
        await PostgreSqlStateMigration.ValidateRecoverySchemaAsync(connection, transaction, ct);
    }
    public Task<DateTime> ClockAsync(CellBridgeDbContext db, CancellationToken ct) =>
        db.Database.SqlQueryRaw<DateTime>("SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct);
    public async Task GuardAsync(CellBridgeDbContext db, Guid id, CancellationToken ct)
    {
        var (connection, transaction) = PostgreSqlDatabase.GetTransaction(db);
        await using var command = new NpgsqlCommand("SELECT \"ResourceId\" FROM cellbridge_repository_heads WHERE \"ResourceId\"=$1 FOR UPDATE", connection, transaction);
        command.Parameters.AddWithValue(id);
        await command.ExecuteScalarAsync(ct);
    }
    public Task RecoveryGuardAsync(CellBridgeDbContext db, CancellationToken ct) =>
        db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(748219355)", ct);
    public bool IsDatabaseException(Exception error) => PostgreSqlDatabase.IsDatabaseException(error);
    public bool IsUniqueViolation(Exception error) => PostgreSqlDatabase.FindPostgresException(error)?.SqlState == PostgresErrorCodes.UniqueViolation;
}
