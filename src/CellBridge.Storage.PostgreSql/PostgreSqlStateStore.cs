using System.Text.Json;
using CellBridge.Storage.Abstractions;
using Npgsql;

namespace CellBridge.Storage.PostgreSql;

/// <summary>Versioned immutable snapshots, coordinated by a row for each document.</summary>
public sealed partial class PostgreSqlStateStore(NpgsqlDataSource dataSource, StorageLimits? limits = null) : IDocumentStateStore, IDocumentLifecycleStore, IStorageBudgetParticipant, IAtomicDocumentRenameStore, IProviderRecoveryStore
{
    public object BudgetScope => dataSource;
    private readonly StorageLimits _limits = ValidateLimits(limits);
    private static StorageLimits ValidateLimits(StorageLimits? limits)
    {
        var result = limits ?? new StorageLimits(); result.Validate(); return result;
    }
    public bool Durable => true;
    public bool Shared => true;
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var durability = new NpgsqlCommand("SET LOCAL synchronous_commit=on", connection, transaction))
            await durability.ExecuteNonQueryAsync(cancellationToken);
        // Schema initialization is an explicit deployment operation, not request/startup behavior.
        await using (var gate = new NpgsqlCommand("SELECT pg_advisory_xact_lock(748219351)", connection, transaction))
            await gate.ExecuteNonQueryAsync(cancellationToken);
        await using (var existing = new NpgsqlCommand("SELECT to_regclass('cellbridge_schema')::text", connection, transaction))
            if (await existing.ExecuteScalarAsync(cancellationToken) is string)
            {
                // Drain old writers that already passed their version guard and block new guards during migration.
                await using (var schemaGate = new NpgsqlCommand("LOCK TABLE cellbridge_schema IN ACCESS EXCLUSIVE MODE", connection, transaction))
                    await schemaGate.ExecuteNonQueryAsync(cancellationToken);
                await using var version = new NpgsqlCommand("SELECT version FROM cellbridge_schema", connection, transaction);
                await using var versions = await version.ExecuteReaderAsync(cancellationToken);
                if (!await versions.ReadAsync(cancellationToken)) throw new StorageUnavailableException("Missing storage schema version.");
                var schema = versions.GetInt32(0);
                if (await versions.ReadAsync(cancellationToken)) throw new StorageUnavailableException("Multiple storage schema versions.");
                if (schema is not (3 or 4))
                    throw new StorageUnavailableException("Unsupported development storage schema. Recreate the development database; only schema 3 to 4 migration is supported.");
            }
        await using var source = typeof(PostgreSqlStateStore).Assembly.GetManifestResourceStream("CellBridge.Storage.PostgreSql.Schema.sql")!;
        using var reader = new StreamReader(source);
        await using var command = new NpgsqlCommand(await reader.ReadToEndAsync(cancellationToken), connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await InitializeRecoveryAsync(connection, transaction, cancellationToken);
        await ConfigureTransactionAsync(connection, transaction, cancellationToken);
        await using (var configure = new NpgsqlCommand("UPDATE cellbridge_usage SET stored_bytes=COALESCE((SELECT SUM(length) FROM cellbridge_objects),0)+COALESCE((SELECT SUM(octet_length(state_json)) FROM cellbridge_states),0)+COALESCE((SELECT SUM(octet_length(receipt_json)) FROM cellbridge_recovery_receipts),0),document_count=(SELECT COUNT(*) FROM cellbridge_documents),max_stored_bytes=$1,max_documents=$2 WHERE singleton=true", connection, transaction))
        {
            configure.Parameters.Add(new NpgsqlParameter { Value = _limits.MaxStoredBytes });
            configure.Parameters.Add(new NpgsqlParameter { Value = _limits.MaxDocuments });
            await configure.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        await CheckHealthAsync(cancellationToken);
    }
    public ValueTask<DocumentState?> FindByResourceIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        FindAsync("d.resource_id = $1 AND NOT d.is_deleted", id, cancellationToken);
    public ValueTask<DocumentState?> FindByPathKeyAsync(string key, CancellationToken cancellationToken = default) =>
        FindAsync("d.path_key = $1 AND NOT d.is_deleted", key, cancellationToken);
    private async ValueTask<DocumentState?> FindAsync(string predicate, object value, CancellationToken cancellationToken)
    {
        try
        {
            // The pointer and complete immutable state are acquired in one statement.
            await using var command = dataSource.CreateCommand($"SELECT s.state_json FROM cellbridge_documents d JOIN cellbridge_states s USING(resource_id,state_version) WHERE {predicate}");
            command.Parameters.Add(new NpgsqlParameter { Value = value });
            var json = await command.ExecuteScalarAsync(cancellationToken) as string;
            return json is null ? null : Decode(json);
        }
        catch (NpgsqlException ex) { throw new StorageUnavailableException("The document store is unavailable.", ex); }
    }
    public async ValueTask<IReadOnlyList<DocumentSummary>> ListAsync(int offset, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        await using var command = dataSource.CreateCommand("SELECT s.state_json,clock_timestamp() FROM cellbridge_documents d JOIN cellbridge_states s USING(resource_id,state_version) WHERE NOT d.is_deleted ORDER BY d.path_key OFFSET $1 LIMIT $2");
        command.Parameters.Add(new NpgsqlParameter { Value = offset });
        command.Parameters.Add(new NpgsqlParameter { Value = limit });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<DocumentSummary>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var state = Decode(reader.GetString(0));
            result.Add(new(state.ResourceId, state.Path, state.Content.Length, state.ContentVersion,
                state.ModifiedUtc, state.Editors.Count(x => x.ExpiresUtc > reader.GetDateTime(1)))
                { Security = state.Security });
        }
        return result;
    }
    public async ValueTask<bool> TryCreateAsync(DocumentState state, CancellationToken cancellationToken = default)
    {
        DocumentPathReservations.ValidateCreate(state);
        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await ConfigureTransactionAsync(connection, transaction, cancellationToken);
            await PostgreSqlNamespaceLock.AcquireAsync(connection, transaction, cancellationToken);
            await LockPathAsync(connection, transaction, state.PathKey, cancellationToken);
            await using (var path = new NpgsqlCommand("SELECT 1 FROM cellbridge_documents WHERE path_key=$1 LIMIT 1", connection, transaction))
            {
                path.Parameters.Add(new NpgsqlParameter { Value = state.PathKey });
                if (await path.ExecuteScalarAsync(cancellationToken) is not null) return false;
            }
            await using var insert = new NpgsqlCommand("INSERT INTO cellbridge_documents(resource_id,path_key,state_version) VALUES ($1,$2,0) ON CONFLICT DO NOTHING", connection, transaction);
            insert.Parameters.Add(new NpgsqlParameter { Value = state.ResourceId });
            insert.Parameters.Add(new NpgsqlParameter { Value = state.PathKey });
            if (await insert.ExecuteNonQueryAsync(cancellationToken) == 0) return false;
            var next = state with { StateVersion = 0 };
            _limits.CheckDocument(next);
            await PostgreSqlStorageBudget.AdjustAsync(connection, transaction, 0, 1, cancellationToken);
            if (await IsPathReservedAsync(connection, transaction, state.PathKey, state.ResourceId, cancellationToken)) return false;
            await InsertStateAsync(connection, transaction, next, cancellationToken);
            // Publication has begun. Cancellation cannot be interpreted as rollback.
            await transaction.CommitAsync(CancellationToken.None);
            return true;
        }
        catch (NpgsqlException ex) { throw new StorageUnavailableException("Document creation failed or its commit outcome is unknown.", ex); }
    }
    public ValueTask<T> TransitionAsync<T>(Guid id, Func<DocumentState, DateTime, StateTransition<T>> transition,
        CancellationToken cancellationToken = default) => TransitionCoreAsync(id, transition, false, false, cancellationToken);
    public ValueTask<T> RenameAsync<T>(Guid id, Func<DocumentState, DateTime, StateTransition<T>> transition,
        CancellationToken cancellationToken = default) => TransitionCoreAsync(id, transition, false, true, cancellationToken);
    private async ValueTask<T> TransitionCoreAsync<T>(Guid id, Func<DocumentState, DateTime, StateTransition<T>> transition,
        bool lifecycle, bool rename, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await ConfigureTransactionAsync(connection, transaction, cancellationToken);
            if (rename) await PostgreSqlNamespaceLock.AcquireAsync(connection, transaction, cancellationToken);
            long version;
            await using (var query = new NpgsqlCommand("SELECT state_version FROM cellbridge_documents WHERE resource_id=$1 FOR UPDATE", connection, transaction))
            {
                query.Parameters.Add(new NpgsqlParameter { Value = id });
                version = (long)(await query.ExecuteScalarAsync(cancellationToken) ?? throw new KeyNotFoundException("Document does not exist."));
            }
            // After waiting for a concurrent updater, use the locked pointer in
            // a new statement. Do not join an old statement snapshot to that pointer.
            await using var stateQuery = new NpgsqlCommand("SELECT state_json FROM cellbridge_states WHERE resource_id=$1 AND state_version=$2", connection, transaction);
            stateQuery.Parameters.Add(new NpgsqlParameter { Value = id });
            stateQuery.Parameters.Add(new NpgsqlParameter { Value = version });
            var current = Decode(await stateQuery.ExecuteScalarAsync(cancellationToken) as string ?? throw new StorageCorruptionException("The current state is missing."));
            // Separate statement: a time read before waiting for FOR UPDATE would be stale.
            await using var clock = new NpgsqlCommand("SELECT clock_timestamp()", connection, transaction);
            var now = (DateTime)(await clock.ExecuteScalarAsync(cancellationToken))!;
            if (current.IsDeleted && !lifecycle) throw new KeyNotFoundException("Document is deleted.");
            var result = transition(current, now);
            if (result.Next is { } next)
            {
                if (!lifecycle) DocumentPathReservations.ValidateTransition(current, next, rename);
                next = next with { StateVersion = checked(current.StateVersion + 1),
                    RetiredPathKeys = DocumentPathReservations.Capture(current, next, rename) };
                _limits.CheckDocument(next);
                // Content remains available to detached readers until quiescent maintenance.
                // Metadata history is independent of file versions and includes lease-only writes.
                await using (var prune = new NpgsqlCommand("WITH expired AS (DELETE FROM cellbridge_states WHERE resource_id=$1 AND state_version<=$2 RETURNING octet_length(state_json) AS bytes) SELECT COALESCE(SUM(bytes),0)::bigint FROM expired", connection, transaction))
                {
                    prune.Parameters.Add(new NpgsqlParameter { Value = id });
                    prune.Parameters.Add(new NpgsqlParameter { Value = next.StateVersion - _limits.MaxRetainedStateSnapshots });
                    var freed = (long)(await prune.ExecuteScalarAsync(cancellationToken))!;
                    if (freed != 0) await PostgreSqlStorageBudget.AdjustAsync(connection, transaction, -freed, 0, cancellationToken);
                }
                await InsertStateAsync(connection, transaction, next, cancellationToken);
                if (rename && next.PathKey != current.PathKey &&
                    await IsPathReservedAsync(connection, transaction, next.PathKey, id, cancellationToken))
                    throw new InvalidOperationException("The destination filename is reserved.");
                await using var update = new NpgsqlCommand("UPDATE cellbridge_documents SET state_version=$2,is_deleted=$3,path_key=$4 WHERE resource_id=$1", connection, transaction);
                update.Parameters.Add(new NpgsqlParameter { Value = id });
                update.Parameters.Add(new NpgsqlParameter { Value = next.StateVersion });
                update.Parameters.Add(new NpgsqlParameter { Value = next.IsDeleted });
                update.Parameters.Add(new NpgsqlParameter { Value = next.PathKey });
                await update.ExecuteNonQueryAsync(cancellationToken);
            }
            await transaction.CommitAsync(CancellationToken.None);
            return result.Result;
        }
        catch (PostgresException ex) when (rename && ex.SqlState == PostgresErrorCodes.UniqueViolation)
        { throw new InvalidOperationException("The destination filename already exists.", ex); }
        catch (NpgsqlException ex) { throw new StorageUnavailableException("Document publication failed or its commit outcome is unknown.", ex); }
    }
    public ValueTask<DocumentState?> FindLifecycleAsync(Guid id, CancellationToken cancellationToken = default) =>
        FindAsync("d.resource_id = $1", id, cancellationToken);
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
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await ConfigureTransactionAsync(connection, transaction, cancellationToken);
            await PostgreSqlNamespaceLock.AcquireAsync(connection, transaction, cancellationToken);
            await LockPathAsync(connection, transaction, replacement.PathKey, cancellationToken);
            await using var pointer = new NpgsqlCommand("SELECT state_version FROM cellbridge_documents WHERE resource_id=$1 FOR UPDATE", connection, transaction);
            pointer.Parameters.Add(new NpgsqlParameter { Value = id });
            if (await pointer.ExecuteScalarAsync(cancellationToken) is not long version) return false;
            await using var read = new NpgsqlCommand("SELECT state_json FROM cellbridge_states WHERE resource_id=$1 AND state_version=$2", connection, transaction);
            read.Parameters.Add(new NpgsqlParameter { Value = id });
            read.Parameters.Add(new NpgsqlParameter { Value = version });
            var retired = Decode((string)(await read.ExecuteScalarAsync(cancellationToken))!);
            if (retired.ReplacedBy == replacement.ResourceId && retired.LifecycleGeneration == expectedGeneration &&
                retired.StateVersion == expectedStateVersion + 1) return true;
            if (!DocumentLifecycle.CanRecreate(retired, expectedGeneration, expectedStateVersion, replacement)) return false;
            await using var insert = new NpgsqlCommand("INSERT INTO cellbridge_documents(resource_id,path_key,state_version) VALUES($1,$2,0) ON CONFLICT DO NOTHING", connection, transaction);
            insert.Parameters.Add(new NpgsqlParameter { Value = replacement.ResourceId });
            insert.Parameters.Add(new NpgsqlParameter { Value = replacement.PathKey });
            if (await insert.ExecuteNonQueryAsync(cancellationToken) != 1) return false;
            replacement = replacement with { StateVersion = 0 };
            _limits.CheckDocument(replacement);
            var next = retired with { ReplacedBy = replacement.ResourceId, StateVersion = checked(version + 1) };
            _limits.CheckDocument(next);
            await PostgreSqlStorageBudget.AdjustAsync(connection, transaction, 0, 1, cancellationToken);
            // Canonical ancestor tombstones are valid for later incarnations.
            // Foreign retired aliases remain reserved, including on tombstones.
            if (await IsRetiredPathReservedAsync(connection, transaction, replacement.PathKey, id, cancellationToken)) return false;
            await InsertStateAsync(connection, transaction, replacement, cancellationToken);
            await InsertStateAsync(connection, transaction, next, cancellationToken);
            await using var update = new NpgsqlCommand("UPDATE cellbridge_documents SET state_version=$2 WHERE resource_id=$1", connection, transaction);
            update.Parameters.Add(new NpgsqlParameter { Value = id });
            update.Parameters.Add(new NpgsqlParameter { Value = next.StateVersion });
            await update.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(CancellationToken.None);
            return true;
        }
        catch (NpgsqlException ex) { throw new StorageUnavailableException("Recreation failed or its commit outcome is unknown; retry the same replacement identity.", ex); }
    }
    private static async Task LockPathAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string path,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended($1,748219352))", connection, transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = path });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
    public static async Task<bool> IsPathReservedAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string pathKey, Guid resourceId, CancellationToken cancellationToken = default)
    {
        await using var command = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM cellbridge_documents d JOIN cellbridge_states s USING(resource_id,state_version) WHERE d.resource_id<>$2 AND (d.path_key=$1 OR jsonb_exists(COALESCE(s.state_json::jsonb->'RetiredPathKeys','[]'::jsonb),$1)))", connection, transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = pathKey });
        command.Parameters.Add(new NpgsqlParameter { Value = resourceId });
        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }
    private static async Task<bool> IsRetiredPathReservedAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string pathKey, Guid retiredResourceId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM cellbridge_documents d JOIN cellbridge_states s USING(resource_id,state_version) WHERE d.resource_id<>$2 AND jsonb_exists(COALESCE(s.state_json::jsonb->'RetiredPathKeys','[]'::jsonb),$1))", connection, transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = pathKey });
        command.Parameters.Add(new NpgsqlParameter { Value = retiredResourceId });
        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }
    private static async Task InsertStateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, DocumentState state, CancellationToken cancellationToken)
    {
        string json = JsonSerializer.Serialize(state);
        await PostgreSqlStorageBudget.AdjustAsync(connection, transaction, System.Text.Encoding.UTF8.GetByteCount(json), 0, cancellationToken);
        await using var command = new NpgsqlCommand("INSERT INTO cellbridge_states VALUES ($1,$2,$3)", connection, transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = state.ResourceId });
        command.Parameters.Add(new NpgsqlParameter { Value = state.StateVersion });
        command.Parameters.Add(new NpgsqlParameter { Value = json });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
    internal static async Task ConfigureTransactionAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        await using (var health = new NpgsqlCommand("SELECT current_setting('fsync'),version FROM cellbridge_schema", connection, transaction))
        await using (var reader = await health.ExecuteReaderAsync(cancellationToken))
            if (!await reader.ReadAsync(cancellationToken) || reader.GetString(0) != "on" || reader.GetInt32(1) != 4 || await reader.ReadAsync(cancellationToken))
                throw new StorageUnavailableException("Storage requires fsync=on and exactly schema version 4.");
        await using var command = new NpgsqlCommand("SET LOCAL synchronous_commit=on; SET LOCAL lock_timeout='5s'; SET LOCAL statement_timeout='30s'", connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
    public async ValueTask CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        await using var command = dataSource.CreateCommand("SELECT current_setting('fsync'),version FROM cellbridge_schema");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) || reader.GetString(0) != "on" || reader.GetInt32(1) != 4 || await reader.ReadAsync(cancellationToken))
            throw new StorageUnavailableException("Storage requires fsync=on and exactly schema version 4.");
    }
    private static DocumentState Decode(string json)
    {
        try
        {
            var state = JsonSerializer.Deserialize<DocumentState>(json) ?? throw new JsonException("Empty state.");
            if (state.FormatVersion != DocumentState.CurrentFormat)
                throw new StorageCorruptionException("Unsupported document storage format.");
            if (state.Security is null || state.Security.Grants is null)
                throw new StorageCorruptionException("Missing document security state.");
            return state;
        }
        catch (JsonException ex) { throw new StorageCorruptionException("Invalid persisted document state.", ex); }
    }
}
