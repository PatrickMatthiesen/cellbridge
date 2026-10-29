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
    public IDocumentLocking<CellBridgeDbContext> Locks => this;
    public Task ConfigureAsync(CellBridgeDbContext db, CancellationToken ct) => ConfigureSettingsAsync(db, ct);
    public async Task CheckConfigurationAsync(CellBridgeDbContext db, CancellationToken ct)
    {
        if (!await ValidateDatabaseConfigurationAsync(db, ct))
            throw new StorageUnavailableException("Storage requires fsync=on and exactly schema version 9.");
    }
    public bool IsDatabaseException(Exception ex) => PostgreSqlDatabase.IsDatabaseException(ex);
    public void ValidateNamespaceFields(DocumentRow row, DocumentNamespaceState state) { }
    // INSERT ON CONFLICT waits on the exact unique object key. No advisory creation lock is needed.
    public Task ContentAsync(CellBridgeDbContext db, string key, CancellationToken ct) => Task.CompletedTask;
    public async Task<bool> InsertObjectAsync(CellBridgeDbContext db, ContentHandle handle, CancellationToken ct)
    {
        var (connection, transaction) = PostgreSqlDatabase.GetTransaction(db);
        await using var command = new NpgsqlCommand("INSERT INTO cellbridge_objects VALUES ($1,$2,$1) ON CONFLICT DO NOTHING", connection, transaction);
        command.Parameters.AddWithValue(handle.Key);
        command.Parameters.AddWithValue(handle.Length);
        return await command.ExecuteNonQueryAsync(ct) != 0;
    }

    internal static async Task ConfigureTransactionAsync(CellBridgeDbContext context, CancellationToken cancellationToken)
    {
        var valid = await ValidateDatabaseConfigurationAsync(context, cancellationToken);
        if (!valid)
            throw new StorageUnavailableException("Storage requires fsync=on and exactly schema version 9.");
        await ConfigureSettingsAsync(context, cancellationToken);
    }

    private static Task ConfigureSettingsAsync(CellBridgeDbContext context, CancellationToken cancellationToken)
    {
        // Every caller owns the transaction and abandons it after a failed save.
        // Savepoints add work but cannot make these transactions reusable.
        context.Database.AutoSavepointsEnabled = false;
        return context.Database.ExecuteSqlRawAsync("SET LOCAL synchronous_commit=on; SET LOCAL lock_timeout='5s'; SET LOCAL statement_timeout='30s'", cancellationToken);
    }

    private static Task<bool> ValidateDatabaseConfigurationAsync(CellBridgeDbContext context,
        CancellationToken cancellationToken) => context.Database.SqlQueryRaw<bool>("""
            SELECT count(*) = 1 AND min(version) = 9 AND max(version) = 9
                AND current_setting('fsync') = 'on' AS "Value"
            FROM cellbridge_schema
            """).SingleAsync(cancellationToken);
}
