using System.Text.Json;
using CellBridge.Storage.Abstractions;
using Npgsql;

namespace CellBridge.Storage.PostgreSql;

/// <summary>Versioned immutable snapshots, coordinated by a row for each document.</summary>
public sealed class PostgreSqlStateStore(NpgsqlDataSource dataSource, StorageLimits? limits = null) : IDocumentStateStore, IStorageBudgetParticipant
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
        // Schema initialization is an explicit deployment operation, not request/startup behavior.
        await using (var gate = new NpgsqlCommand("SELECT pg_advisory_xact_lock(748219351)", connection, transaction))
            await gate.ExecuteNonQueryAsync(cancellationToken);
        await using (var existing = new NpgsqlCommand("SELECT to_regclass('cellbridge_schema')::text", connection, transaction))
            if (await existing.ExecuteScalarAsync(cancellationToken) is string)
            {
                await using var version = new NpgsqlCommand("SELECT version FROM cellbridge_schema", connection, transaction);
                await using var versions = await version.ExecuteReaderAsync(cancellationToken);
                if (!await versions.ReadAsync(cancellationToken) || versions.GetInt32(0) is not (1 or 2) || await versions.ReadAsync(cancellationToken))
                    throw new StorageUnavailableException("Cannot migrate this storage schema.");
            }
        await using var source = typeof(PostgreSqlStateStore).Assembly.GetManifestResourceStream("CellBridge.Storage.PostgreSql.Schema.sql")!;
        using var reader = new StreamReader(source);
        await using var command = new NpgsqlCommand(await reader.ReadToEndAsync(cancellationToken), connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await ConfigureTransactionAsync(connection, transaction, cancellationToken);
        await using (var configure = new NpgsqlCommand("UPDATE cellbridge_usage SET max_stored_bytes=$1,max_documents=$2 WHERE singleton=true", connection, transaction))
        {
            configure.Parameters.Add(new NpgsqlParameter { Value = _limits.MaxStoredBytes });
            configure.Parameters.Add(new NpgsqlParameter { Value = _limits.MaxDocuments });
            await configure.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        await CheckHealthAsync(cancellationToken);
    }
    public ValueTask<DocumentState?> FindByResourceIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        FindAsync("d.resource_id = $1", id, cancellationToken);
    public ValueTask<DocumentState?> FindByPathKeyAsync(string key, CancellationToken cancellationToken = default) =>
        FindAsync("d.path_key = $1", key, cancellationToken);
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
        await using var command = dataSource.CreateCommand("SELECT s.state_json,clock_timestamp() FROM cellbridge_documents d JOIN cellbridge_states s USING(resource_id,state_version) ORDER BY d.path_key OFFSET $1 LIMIT $2");
        command.Parameters.Add(new NpgsqlParameter { Value = offset });
        command.Parameters.Add(new NpgsqlParameter { Value = limit });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<DocumentSummary>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var state = Decode(reader.GetString(0));
            result.Add(new(state.ResourceId, state.Path, state.Content.Length, state.ContentVersion,
                state.ModifiedUtc, state.Editors.Count(x => x.ExpiresUtc > reader.GetDateTime(1))));
        }
        return result;
    }
    public async ValueTask<bool> TryCreateAsync(DocumentState state, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await ConfigureTransactionAsync(connection, transaction, cancellationToken);
            await using var insert = new NpgsqlCommand("INSERT INTO cellbridge_documents VALUES ($1,$2,0) ON CONFLICT DO NOTHING", connection, transaction);
            insert.Parameters.Add(new NpgsqlParameter { Value = state.ResourceId });
            insert.Parameters.Add(new NpgsqlParameter { Value = state.PathKey });
            if (await insert.ExecuteNonQueryAsync(cancellationToken) == 0) return false;
            var next = state with { StateVersion = 0 };
            _limits.CheckDocument(next);
            await PostgreSqlStorageBudget.AdjustAsync(connection, transaction, 0, 1, cancellationToken);
            await InsertStateAsync(connection, transaction, next, cancellationToken);
            // Publication has begun. Cancellation cannot be interpreted as rollback.
            await transaction.CommitAsync(CancellationToken.None);
            return true;
        }
        catch (NpgsqlException ex) { throw new StorageUnavailableException("Document creation failed or its commit outcome is unknown.", ex); }
    }
    public async ValueTask<T> TransitionAsync<T>(Guid id, Func<DocumentState, DateTime, StateTransition<T>> transition,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await ConfigureTransactionAsync(connection, transaction, cancellationToken);
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
            var result = transition(current, now);
            if (result.Next is { } next)
            {
                if (next.ResourceId != id || next.PathKey != current.PathKey || next.Path != current.Path)
                    throw new InvalidOperationException("A transition cannot change document identity or path.");
                next = next with { StateVersion = checked(current.StateVersion + 1) };
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
                await using var update = new NpgsqlCommand("UPDATE cellbridge_documents SET state_version=$2 WHERE resource_id=$1", connection, transaction);
                update.Parameters.Add(new NpgsqlParameter { Value = id });
                update.Parameters.Add(new NpgsqlParameter { Value = next.StateVersion });
                await update.ExecuteNonQueryAsync(cancellationToken);
            }
            await transaction.CommitAsync(CancellationToken.None);
            return result.Result;
        }
        catch (NpgsqlException ex) { throw new StorageUnavailableException("Document publication failed or its commit outcome is unknown.", ex); }
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
            if (!await reader.ReadAsync(cancellationToken) || reader.GetString(0) != "on" || reader.GetInt32(1) != 2 || await reader.ReadAsync(cancellationToken))
                throw new StorageUnavailableException("Storage requires fsync=on and exactly schema version 2.");
        await using var command = new NpgsqlCommand("SET LOCAL synchronous_commit=on; SET LOCAL lock_timeout='5s'; SET LOCAL statement_timeout='30s'", connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
    public async ValueTask CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        await using var command = dataSource.CreateCommand("SELECT current_setting('fsync'),version FROM cellbridge_schema");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) || reader.GetString(0) != "on" || reader.GetInt32(1) != 2 || await reader.ReadAsync(cancellationToken))
            throw new StorageUnavailableException("Storage requires fsync=on and exactly schema version 2.");
    }
    private static DocumentState Decode(string json)
    {
        try
        {
            var state = JsonSerializer.Deserialize<DocumentState>(json) ?? throw new JsonException("Empty state.");
            if (state.FormatVersion != DocumentState.CurrentFormat)
                throw new StorageCorruptionException("Unsupported document storage format.");
            return state;
        }
        catch (JsonException ex) { throw new StorageCorruptionException("Invalid persisted document state.", ex); }
    }
}
