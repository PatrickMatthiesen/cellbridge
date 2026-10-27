using System.Data;
using CellBridge.Storage.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Npgsql;

namespace CellBridge.Storage.PostgreSql;

internal sealed partial class PostgreSqlDocumentDatabase : IDocumentDatabase<CellBridgeDbContext>, IDocumentLocking<CellBridgeDbContext>
{
    private readonly DbContextOptions<CellBridgeDbContext> options;
    private readonly PooledDbContextFactory<CellBridgeDbContext>? factory;
    public PostgreSqlDocumentDatabase(DbContextOptions<CellBridgeDbContext> options)
    {
        this.options = options;
        factory = PostgreSqlDatabase.CreateRuntimeFactory(options);
    }
    public CellBridgeDbContext CreateContext() => factory?.CreateDbContext() ?? new(options);
    public bool ReadClockAfterStatement => false;
    public IDocumentLocking<CellBridgeDbContext> Locks => this;
    public Task<T> ReadAsync<T>(CellBridgeDbContext db, Func<Task<T>> action, bool coherent, CancellationToken ct) =>
        coherent ? ReadSnapshotAsync(db, action, ct) : action();

    private static async Task<T> ReadSnapshotAsync<T>(CellBridgeDbContext db, Func<Task<T>> action, CancellationToken ct)
    {
        // Only multi-statement health checks need a transaction. Ordinary reads
        // already have PostgreSQL's statement snapshot and issue no setup commands.
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        await ConfigureSettingsAsync(db, ct);
        var result = await action();
        await transaction.CommitAsync(ct);
        return result;
    }
    public Task ConfigureAsync(CellBridgeDbContext db, CancellationToken ct) => ConfigureSettingsAsync(db, ct);
    public async Task CheckConfigurationAsync(CellBridgeDbContext db, CancellationToken ct)
    {
        if (!await ValidateDatabaseConfigurationAsync(db, ct))
            throw new StorageUnavailableException("Storage requires fsync=on and exactly schema version 6.");
    }
    public Task CheckRecoveryAsync(CellBridgeDbContext db, CancellationToken ct) => CheckRecoverySchemaAsync(db, ct);
    public Task<DateTime> ClockAsync(CellBridgeDbContext db, CancellationToken ct) =>
        db.Database.SqlQueryRaw<DateTime>("SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct);
    public bool IsDatabaseException(Exception ex) => PostgreSqlDatabase.IsDatabaseException(ex);
    public bool IsUniqueViolation(Exception ex) => PostgreSqlDatabase.FindPostgresException(ex)?.SqlState == PostgresErrorCodes.UniqueViolation;
    public IQueryable<DocumentRow> AtPath(IQueryable<DocumentRow> rows, string key) => rows.Where(row => row.PathKey == key);
    public void SetNamespaceFields(DocumentRow row, DocumentState state) { }
    public void ValidatePointer(DocumentRow row, DocumentState state) { }
    public void ValidateNamespaceFields(DocumentRow row, DocumentNamespaceState state) { }
    public Task VerifyReservationsAsync(CellBridgeDbContext db, IReadOnlyDictionary<Guid, DocumentNamespaceState> states, CancellationToken ct) => Task.CompletedTask;
    public Task WriteReservationsAsync(CellBridgeDbContext db, DocumentState state, CancellationToken ct) => Task.CompletedTask;
    public Task<bool> HasReservationsAsync(CellBridgeDbContext db, CancellationToken ct) => Task.FromResult(false);

    public Task PublicationAsync(CellBridgeDbContext db, bool exclusive, CancellationToken ct) =>
        AcquirePublicationAsync(db, exclusive, ct);

    internal static async Task AcquirePublicationAsync(CellBridgeDbContext db, bool exclusive, CancellationToken ct)
    {
        // Advisory transaction locks coordinate publishers without requiring
        // content-only users to lock the document tables. The filesystem backend
        // takes this same lock before reserving an object in PostgreSQL.
        var (connection, transaction) = PostgreSqlDatabase.GetTransaction(db);
        await using var command = new NpgsqlCommand(exclusive
            ? "SELECT pg_advisory_xact_lock(748219354)"
            : "SELECT pg_advisory_xact_lock_shared(748219354)", connection, transaction);
        await command.ExecuteNonQueryAsync(ct);
    }
    public Task NamespaceAsync(CellBridgeDbContext db, CancellationToken ct)
    {
        var (connection, transaction) = PostgreSqlDatabase.GetTransaction(db);
        return PostgreSqlNamespaceLock.AcquireAsync(connection, transaction, ct);
    }
    public async Task DocumentAsync(CellBridgeDbContext db, Guid id, CancellationToken ct)
    {
        var (connection, transaction) = PostgreSqlDatabase.GetTransaction(db);
        await using var command = new NpgsqlCommand("SELECT state_version FROM cellbridge_documents WHERE resource_id=$1 FOR UPDATE", connection, transaction);
        command.Parameters.AddWithValue(id);
        await command.ExecuteScalarAsync(ct);
    }
    public async Task ContentAsync(CellBridgeDbContext db, string key, CancellationToken ct)
    {
        var (connection, transaction) = PostgreSqlDatabase.GetTransaction(db);
        await using var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended($1,748219353))", connection, transaction);
        command.Parameters.AddWithValue(key);
        await command.ExecuteNonQueryAsync(ct);
    }
    public async Task<bool> InsertObjectAsync(CellBridgeDbContext db, ContentHandle handle, CancellationToken ct)
    {
        var (connection, transaction) = PostgreSqlDatabase.GetTransaction(db);
        await using var command = new NpgsqlCommand("INSERT INTO cellbridge_objects VALUES ($1,$2,$1) ON CONFLICT DO NOTHING", connection, transaction);
        command.Parameters.AddWithValue(handle.Key);
        command.Parameters.AddWithValue(handle.Length);
        return await command.ExecuteNonQueryAsync(ct) != 0;
    }
    public async Task<UsageRow> UsageAsync(CellBridgeDbContext db, CancellationToken ct) =>
        db.Usage.Local.SingleOrDefault(row => row.Singleton) ??
        await db.Usage.FromSqlRaw("SELECT * FROM cellbridge_usage WHERE singleton=true FOR UPDATE").SingleOrDefaultAsync(ct) ??
        throw new StorageCorruptionException("Missing storage usage ledger.");

    internal static async Task ConfigureTransactionAsync(CellBridgeDbContext context, CancellationToken cancellationToken)
    {
        var valid = await ValidateDatabaseConfigurationAsync(context, cancellationToken);
        if (!valid)
            throw new StorageUnavailableException("Storage requires fsync=on and exactly schema version 6.");
        await ConfigureSettingsAsync(context, cancellationToken);
    }

    private static Task ConfigureSettingsAsync(CellBridgeDbContext context, CancellationToken cancellationToken)
    {
        // Every publication failure abandons the whole transaction.
        context.Database.AutoSavepointsEnabled = false;
        return context.Database.ExecuteSqlRawAsync("SET LOCAL synchronous_commit=on; SET LOCAL lock_timeout='5s'; SET LOCAL statement_timeout='30s'", cancellationToken);
    }

    private static Task<bool> ValidateDatabaseConfigurationAsync(CellBridgeDbContext context,
        CancellationToken cancellationToken) => context.Database.SqlQueryRaw<bool>("""
            SELECT count(*) = 1 AND min(version) = 6 AND max(version) = 6
                AND current_setting('fsync') = 'on' AS "Value"
            FROM cellbridge_schema
            """).SingleAsync(cancellationToken);
    private static async Task CheckRecoverySchemaAsync(CellBridgeDbContext context,
        CancellationToken cancellationToken)
    {
        var present = await context.Database.SqlQueryRaw<bool>("""
            SELECT to_regclass('cellbridge_recovery_schema') IS NOT NULL
                AND to_regclass('cellbridge_recovery_receipts') IS NOT NULL AS "Value"
            """).SingleAsync(cancellationToken);
        if (!present)
            throw new StorageUnavailableException(
                "Recovery schema is absent. Run explicit storage initialization with hosts stopped.");
        var versions = await context.RecoverySchema.AsNoTracking().Select(row => row.Version).ToListAsync(cancellationToken);
        if (versions.Count != 1 || versions[0] != 1)
            throw new StorageUnavailableException("Unsupported recovery schema; version 1 is required.");
    }

}
