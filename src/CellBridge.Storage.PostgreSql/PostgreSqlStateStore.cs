using System.Text;
using CellBridge.Storage.Abstractions;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CellBridge.Storage.PostgreSql;

/// <summary>Versioned immutable snapshots, coordinated by a row for each document.</summary>
public sealed partial class PostgreSqlStateStore : IDocumentStateStore, IDocumentLifecycleStore,
    IStorageBudgetParticipant, IAtomicDocumentRenameStore, IProviderRecoveryStore, IDocumentReadStore
{
    private readonly DbContextOptions<CellBridgeDbContext> _options;
    private readonly object _budgetScope;
    private readonly StorageLimits _limits;

    public PostgreSqlStateStore(NpgsqlDataSource dataSource, StorageLimits? limits = null)
        : this(PostgreSqlDatabase.CreateOptions(dataSource), limits, dataSource) { }

    public PostgreSqlStateStore(DbContextOptions<CellBridgeDbContext> options, StorageLimits? limits = null)
        : this(options, limits, options) { }

    private PostgreSqlStateStore(DbContextOptions<CellBridgeDbContext> options, StorageLimits? limits, object budgetScope)
    {
        _options = PostgreSqlDatabase.ValidateOptions(options);
        _budgetScope = budgetScope;
        _limits = limits ?? new StorageLimits();
        _limits.Validate();
    }

    public object BudgetScope => _budgetScope;
    public bool Durable => true;
    public bool Shared => true;
    public Task InitializeAsync(CancellationToken cancellationToken = default) =>
        new PostgreSqlStorageMigrator(_options, _limits).MigrateAsync(cancellationToken);

    public ValueTask<DocumentState?> FindByResourceIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        FindAsync(id, null, false, cancellationToken);
    public ValueTask<DocumentState?> FindByPathKeyAsync(string key, CancellationToken cancellationToken = default) =>
        FindAsync(null, key, false, cancellationToken);
    public ValueTask<DocumentState?> FindLifecycleAsync(Guid id, CancellationToken cancellationToken = default) =>
        FindAsync(id, null, true, cancellationToken);

    private async ValueTask<DocumentState?> FindAsync(Guid? resourceId, string? pathKey, bool includeDeleted,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var context = new CellBridgeDbContext(_options);
            var query = from document in context.Documents.AsNoTracking()
                        join state in context.States.AsNoTracking()
                            on new { document.ResourceId, document.StateVersion }
                            equals new { state.ResourceId, state.StateVersion }
                        where (resourceId == null || document.ResourceId == resourceId) &&
                              (pathKey == null || document.PathKey == pathKey) &&
                              (includeDeleted || !document.IsDeleted)
                        select state.StateJson;
            var json = await query.SingleOrDefaultAsync(cancellationToken);
            return json is null ? null : Decode(json);
        }
        catch (Exception ex) when (PostgreSqlDatabase.IsDatabaseException(ex))
        { throw new StorageUnavailableException("The document store is unavailable.", ex); }
    }

    public async ValueTask<IReadOnlyList<DocumentSummary>> ListAsync(int offset, int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        await using var context = new CellBridgeDbContext(_options);
        var rows = await CurrentReadRows(context, null, null, false).OrderBy(row => row.PathKey)
            .Skip(offset).Take(limit).ToListAsync(cancellationToken);
        return rows.Select(row => DecodeReadMetadata(row).ToSummary(row.DatabaseUtc)).ToArray();
    }

    public async ValueTask<bool> TryCreateAsync(DocumentState state, CancellationToken cancellationToken = default)
    {
        DocumentPathReservations.ValidateCreate(state);
        try
        {
            await using var context = new CellBridgeDbContext(_options);
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
            await ConfigureTransactionAsync(context, cancellationToken);
            var (connection, npgsqlTransaction) = PostgreSqlDatabase.GetTransaction(context);
            await PostgreSqlNamespaceLock.AcquireAsync(connection, npgsqlTransaction, cancellationToken);
            await LockPathAsync(connection, npgsqlTransaction, state.PathKey, cancellationToken);
            // The live-path index is partial. Keep tombstoned identities reserved too.
            if (await context.Documents.AnyAsync(row => row.PathKey == state.PathKey, cancellationToken)) return false;
            // EF has no conflict-tolerant insert that preserves this cross-host admission race.
            await using (var insert = new NpgsqlCommand("INSERT INTO cellbridge_documents(resource_id,path_key,state_version) VALUES ($1,$2,0) ON CONFLICT DO NOTHING", connection, npgsqlTransaction))
            {
                insert.Parameters.AddWithValue(state.ResourceId);
                insert.Parameters.AddWithValue(state.PathKey);
                if (await insert.ExecuteNonQueryAsync(cancellationToken) == 0) return false;
            }
            var next = state with { StateVersion = 0 };
            _limits.CheckDocument(next);
            await PostgreSqlStorageBudget.AdjustAsync(context, 0, 1, cancellationToken);
            if (await IsPathReservedAsync(connection, npgsqlTransaction, state.PathKey, state.ResourceId, cancellationToken)) return false;
            await InsertStateAsync(context, next, cancellationToken);
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(CancellationToken.None);
            return true;
        }
        catch (Exception ex) when (PostgreSqlDatabase.IsDatabaseException(ex))
        { throw new StorageUnavailableException("Document creation failed or its commit outcome is unknown.", ex); }
    }

    public ValueTask<T> TransitionAsync<T>(Guid id, Func<DocumentState, DateTime, StateTransition<T>> transition,
        CancellationToken cancellationToken = default) => TransitionCoreAsync(id, transition, false, false, cancellationToken);
    public ValueTask<T> RenameAsync<T>(Guid id, Func<DocumentState, DateTime, StateTransition<T>> transition,
        CancellationToken cancellationToken = default) => TransitionCoreAsync(id, transition, false, true, cancellationToken);

    private async ValueTask<T> TransitionCoreAsync<T>(Guid id,
        Func<DocumentState, DateTime, StateTransition<T>> transition, bool lifecycle, bool rename,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var context = new CellBridgeDbContext(_options);
            await using var databaseTransaction = await context.Database.BeginTransactionAsync(cancellationToken);
            await ConfigureTransactionAsync(context, cancellationToken);
            var (connection, transaction) = PostgreSqlDatabase.GetTransaction(context);
            if (rename) await PostgreSqlNamespaceLock.AcquireAsync(connection, transaction, cancellationToken);
            var version = await LockDocumentAsync(connection, transaction, id, cancellationToken);
            var stateJson = await context.States.AsNoTracking().Where(row => row.ResourceId == id && row.StateVersion == version)
                .Select(row => row.StateJson).SingleOrDefaultAsync(cancellationToken)
                ?? throw new StorageCorruptionException("The current state is missing.");
            var current = Decode(stateJson);
            var now = await ReadDatabaseClockAsync(context, cancellationToken);
            if (current.IsDeleted && !lifecycle) throw new KeyNotFoundException("Document is deleted.");
            var result = transition(current, now);
            if (result.Next is { } next)
            {
                if (!lifecycle) DocumentPathReservations.ValidateTransition(current, next, rename);
                next = next with
                {
                    StateVersion = checked(current.StateVersion + 1),
                    RetiredPathKeys = DocumentPathReservations.Capture(current, next, rename),
                };
                _limits.CheckDocument(next);
                var expiredQuery = context.States.Where(row => row.ResourceId == id &&
                    row.StateVersion <= next.StateVersion - _limits.MaxRetainedStateSnapshots);
                var freed = await expiredQuery.SumAsync(
                    row => (long?)CellBridgeDbContext.OctetLength(row.StateJson), cancellationToken) ?? 0;
                if (freed != 0) await expiredQuery.ExecuteDeleteAsync(cancellationToken);
                if (freed != 0) await PostgreSqlStorageBudget.AdjustAsync(context, -freed, 0, cancellationToken);
                await InsertStateAsync(context, next, cancellationToken);
                if (rename && next.PathKey != current.PathKey &&
                    await IsPathReservedAsync(connection, transaction, next.PathKey, id, cancellationToken))
                    throw new InvalidOperationException("The destination filename is reserved.");
                var pointer = new DocumentRow
                {
                    ResourceId = id,
                    PathKey = current.PathKey,
                    StateVersion = version,
                    IsDeleted = current.IsDeleted,
                };
                context.Documents.Attach(pointer);
                pointer.StateVersion = next.StateVersion;
                pointer.IsDeleted = next.IsDeleted;
                pointer.PathKey = next.PathKey;
                await context.SaveChangesAsync(cancellationToken);
            }
            await databaseTransaction.CommitAsync(CancellationToken.None);
            return result.Result;
        }
        catch (Exception ex) when (rename && PostgreSqlDatabase.FindPostgresException(ex)?.SqlState == PostgresErrorCodes.UniqueViolation)
        { throw new InvalidOperationException("The destination filename already exists.", ex); }
        catch (Exception ex) when (PostgreSqlDatabase.IsDatabaseException(ex))
        { throw new StorageUnavailableException("Saving the document failed or the transaction result is unknown.", ex); }
    }

    public async ValueTask<bool> TryDeleteAsync(Guid id, long expectedGeneration, long expectedStateVersion,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await TransitionCoreAsync(id, (current, _) =>
            {
                var next = DocumentLifecycle.Delete(current, expectedGeneration, expectedStateVersion);
                return new StateTransition<bool>(current.IsDeleted ? null : next, next is not null);
            }, true, false, cancellationToken);
        }
        catch (KeyNotFoundException) { return false; }
    }

    public async ValueTask<bool> TryRecreateAsync(Guid id, long expectedGeneration, long expectedStateVersion,
        DocumentState replacement, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var context = new CellBridgeDbContext(_options);
            await using var databaseTransaction = await context.Database.BeginTransactionAsync(cancellationToken);
            await ConfigureTransactionAsync(context, cancellationToken);
            var (connection, transaction) = PostgreSqlDatabase.GetTransaction(context);
            await PostgreSqlNamespaceLock.AcquireAsync(connection, transaction, cancellationToken);
            await LockPathAsync(connection, transaction, replacement.PathKey, cancellationToken);
            var version = await TryLockDocumentAsync(connection, transaction, id, cancellationToken);
            if (version is null) return false;
            var json = await context.States.AsNoTracking().Where(row => row.ResourceId == id && row.StateVersion == version)
                .Select(row => row.StateJson).SingleAsync(cancellationToken);
            var retired = Decode(json);
            if (retired.ReplacedBy == replacement.ResourceId && retired.LifecycleGeneration == expectedGeneration &&
                retired.StateVersion == expectedStateVersion + 1) return true;
            if (!DocumentLifecycle.CanRecreate(retired, expectedGeneration, expectedStateVersion, replacement)) return false;
            // Conflict-tolerant insertion makes retry and competing recreation outcomes explicit.
            await using (var insert = new NpgsqlCommand("INSERT INTO cellbridge_documents(resource_id,path_key,state_version) VALUES($1,$2,0) ON CONFLICT DO NOTHING", connection, transaction))
            {
                insert.Parameters.AddWithValue(replacement.ResourceId);
                insert.Parameters.AddWithValue(replacement.PathKey);
                if (await insert.ExecuteNonQueryAsync(cancellationToken) != 1) return false;
            }
            replacement = replacement with { StateVersion = 0 };
            _limits.CheckDocument(replacement);
            var next = retired with { ReplacedBy = replacement.ResourceId, StateVersion = checked(version.Value + 1) };
            _limits.CheckDocument(next);
            await PostgreSqlStorageBudget.AdjustAsync(context, 0, 1, cancellationToken);
            if (await IsRetiredPathReservedAsync(connection, transaction, replacement.PathKey, id, cancellationToken)) return false;
            await InsertStateAsync(context, replacement, cancellationToken);
            await InsertStateAsync(context, next, cancellationToken);
            var pointer = new DocumentRow
            {
                ResourceId = id,
                PathKey = retired.PathKey,
                StateVersion = version.Value,
                IsDeleted = retired.IsDeleted,
            };
            context.Documents.Attach(pointer);
            pointer.StateVersion = next.StateVersion;
            await context.SaveChangesAsync(cancellationToken);
            await databaseTransaction.CommitAsync(CancellationToken.None);
            return true;
        }
        catch (Exception ex) when (PostgreSqlDatabase.IsDatabaseException(ex))
        { throw new StorageUnavailableException("Recreation failed or its commit outcome is unknown; retry the same replacement identity.", ex); }
    }

    private static async Task<long> LockDocumentAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid id, CancellationToken cancellationToken) =>
        await TryLockDocumentAsync(connection, transaction, id, cancellationToken)
        ?? throw new KeyNotFoundException("Document does not exist.");

    private static async Task<long?> TryLockDocumentAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid id, CancellationToken cancellationToken)
    {
        // EF has no LINQ representation for a pessimistic row lock.
        await using var command = new NpgsqlCommand("SELECT state_version FROM cellbridge_documents WHERE resource_id=$1 FOR UPDATE", connection, transaction);
        command.Parameters.AddWithValue(id);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is long version ? version : null;
    }

    private static async Task LockPathAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string path,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended($1,748219352))", connection, transaction);
        command.Parameters.AddWithValue(path);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public static async Task<bool> IsPathReservedAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string pathKey, Guid resourceId, CancellationToken cancellationToken = default)
    {
        // The reservation set lives inside authoritative JSON and participates in namespace locking.
        await using var command = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM cellbridge_documents d JOIN cellbridge_states s USING(resource_id,state_version) WHERE d.resource_id<>$2 AND (d.path_key=$1 OR jsonb_exists(COALESCE(s.state_json::jsonb->'RetiredPathKeys','[]'::jsonb),$1)))", connection, transaction);
        command.Parameters.AddWithValue(pathKey);
        command.Parameters.AddWithValue(resourceId);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static async Task<bool> IsRetiredPathReservedAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string pathKey, Guid retiredResourceId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM cellbridge_documents d JOIN cellbridge_states s USING(resource_id,state_version) WHERE d.resource_id<>$2 AND jsonb_exists(COALESCE(s.state_json::jsonb->'RetiredPathKeys','[]'::jsonb),$1))", connection, transaction);
        command.Parameters.AddWithValue(pathKey);
        command.Parameters.AddWithValue(retiredResourceId);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static async Task InsertStateAsync(CellBridgeDbContext context, DocumentState state,
        CancellationToken cancellationToken)
    {
        var json = DocumentStateJson.Serialize(state);
        await PostgreSqlStorageBudget.AdjustAsync(context, Encoding.UTF8.GetByteCount(json), 0, cancellationToken);
        context.States.Add(new StateRow { ResourceId = state.ResourceId, StateVersion = state.StateVersion, StateJson = json });
        await WriteSelectiveReadsAsync(context, state, cancellationToken);
    }

    internal static async Task ConfigureTransactionAsync(CellBridgeDbContext context, CancellationToken cancellationToken)
    {
        // Every caller owns the transaction and abandons it after a failed save.
        // Savepoints add work but cannot make these transactions reusable.
        context.Database.AutoSavepointsEnabled = false;
        var valid = await ValidateDatabaseConfigurationAsync(context, cancellationToken);
        if (!valid)
            throw new StorageUnavailableException("Storage requires fsync=on and exactly schema version 6.");
        await context.Database.ExecuteSqlRawAsync("SET LOCAL synchronous_commit=on; SET LOCAL lock_timeout='5s'; SET LOCAL statement_timeout='30s'", cancellationToken);
    }

    public async ValueTask CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var context = new CellBridgeDbContext(_options);
            if (!await ValidateDatabaseConfigurationAsync(context, cancellationToken))
                throw new StorageUnavailableException("Storage requires fsync=on and exactly schema version 6.");
        }
        catch (Exception ex) when (PostgreSqlDatabase.IsDatabaseException(ex))
        { throw new StorageUnavailableException("The document store is unavailable.", ex); }
    }

    private static Task<DateTime> ReadDatabaseClockAsync(CellBridgeDbContext context, CancellationToken cancellationToken) =>
        context.Database.SqlQueryRaw<DateTime>("SELECT clock_timestamp() AS \"Value\"").SingleAsync(cancellationToken);
    private static Task<bool> ValidateDatabaseConfigurationAsync(CellBridgeDbContext context,
        CancellationToken cancellationToken) => context.Database.SqlQueryRaw<bool>("""
            SELECT count(*) = 1 AND min(version) = 6 AND max(version) = 6
                AND current_setting('fsync') = 'on' AS "Value"
            FROM cellbridge_schema
            """).SingleAsync(cancellationToken);
    private DocumentState Decode(string json) => DocumentStateJson.Deserialize(json, limits: _limits);
}
