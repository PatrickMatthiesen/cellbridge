using System.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace CellBridge.Storage.PostgreSql;

internal sealed class PostgreSqlRepositoryDatabase(DbContextOptions<CellBridgeDbContext> options) : IRepositoryDatabase<CellBridgeDbContext>,
    IRepositoryControlSnapshotDatabase<CellBridgeDbContext>
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
    public Task<RepositoryControlRow[]> ReadControlRowsAsync(CellBridgeDbContext db, Guid id, long lifecycle,
        bool includePartitions, bool includeClock, CancellationToken ct)
    {
        var sql = """
            SELECT 1 AS "Kind", "Json", NULL::bytea AS "EncodedJson", NULL::uuid AS "ClientId", NULL::integer AS "Partition", NULL::bigint AS "Sequence", NULL::timestamptz AS "UtcNow"
            FROM cellbridge_repository_coordination WHERE "ResourceId"=@id
            UNION ALL
            SELECT 2, "Json", NULL::bytea, "ClientId", NULL::integer, NULL::bigint, NULL::timestamptz
            FROM cellbridge_repository_editors WHERE "ResourceId"=@id
            """;
        if (includePartitions) sql += """

            UNION ALL
            SELECT 3, "Json", NULL::bytea, NULL::uuid, "Partition", "Sequence", NULL::timestamptz
            FROM cellbridge_repository_partitions WHERE "ResourceId"=@id AND "Lifecycle"=@lifecycle
            """;
        if (includeClock) sql += """

            UNION ALL
            SELECT 0, NULL::text, NULL::bytea, NULL::uuid, NULL::integer, NULL::bigint, clock_timestamp()
            """;
        return db.Database.SqlQueryRaw<RepositoryControlRow>(sql, new NpgsqlParameter("id", id),
            new NpgsqlParameter("lifecycle", lifecycle)).TagWith("CellBridge coherent control batch")
            .OrderBy(row => row.Kind).ThenBy(row => row.ClientId).ThenBy(row => row.Partition).ToArrayAsync(ct);
    }

    public async Task<RepositoryControlSnapshot?> ReadControlSnapshotAsync(CellBridgeDbContext db, Guid id, CancellationToken ct)
    {
        const string sql = """
            WITH head AS (
                SELECT * FROM cellbridge_repository_heads WHERE "ResourceId"=@id
            ), control AS (
                SELECT -1 AS "Kind", NULL::text AS "Json", NULL::bytea AS "EncodedJson", NULL::uuid AS "ClientId", NULL::integer AS "Partition", NULL::bigint AS "Sequence", NULL::timestamptz AS "UtcNow"
                UNION ALL
                SELECT 1, c."Json", NULL::bytea, NULL::uuid, NULL::integer, NULL::bigint, NULL::timestamptz
                FROM cellbridge_repository_coordination c JOIN head h ON c."ResourceId"=h."ResourceId"
                UNION ALL
                SELECT 2, e."Json", NULL::bytea, e."ClientId", NULL::integer, NULL::bigint, NULL::timestamptz
                FROM cellbridge_repository_editors e JOIN head h ON e."ResourceId"=h."ResourceId"
                UNION ALL
                SELECT 3, p."Json", NULL::bytea, NULL::uuid, p."Partition", p."Sequence", NULL::timestamptz
                FROM cellbridge_repository_partitions p JOIN head h ON p."ResourceId"=h."ResourceId" AND p."Lifecycle"=h."Lifecycle"
                UNION ALL
                SELECT 0, NULL::text, NULL::bytea, NULL::uuid, NULL::integer, NULL::bigint, clock_timestamp()
            )
            SELECT to_jsonb(h)::text AS "HeadJson",
                   COALESCE((SELECT json_agg(json_build_object(
                       'Kind', c."Kind", 'Json', c."Json", 'EncodedJson', c."EncodedJson",
                       'ClientId', c."ClientId", 'Partition', c."Partition", 'Sequence', c."Sequence", 'UtcNow', c."UtcNow")
                       ORDER BY c."Kind", c."ClientId", c."Partition") FROM control c WHERE c."Kind" >= 0), '[]'::json)::text AS "RowsJson"
            FROM head h
            """;
        var rows = await db.Database.SqlQueryRaw<RepositoryControlSnapshotRow>(sql, new NpgsqlParameter("id", id))
            .TagWith("CellBridge coherent control snapshot")
            .ToArrayAsync(ct);
        if (rows.Length == 0) return null;
        var head = System.Text.Json.JsonSerializer.Deserialize<RepositoryHeadRow>(rows[0].HeadJson)
            ?? throw new CellBridge.Storage.Abstractions.StorageCorruptionException("Missing normalized document head in control snapshot.");
        var controlRows = System.Text.Json.JsonSerializer.Deserialize<RepositoryControlRow[]>(rows[0].RowsJson)
            ?? throw new CellBridge.Storage.Abstractions.StorageCorruptionException("Missing normalized control rows in control snapshot.");
        // JSON timestamps with an explicit offset deserialize as local DateTime.
        // Restore the provider's UTC instant before the shared decoder labels it.
        foreach (var row in controlRows)
            if (row.UtcNow is { Kind: DateTimeKind.Local } instant) row.UtcNow = instant.ToUniversalTime();
        return new(head, controlRows);
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
