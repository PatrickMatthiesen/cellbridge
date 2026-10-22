using System.Text.Json;
using CellBridge.Storage.Abstractions;
using Npgsql;

namespace CellBridge.Storage.PostgreSql;

// SQL here is limited to importing the supported pre-EF schemas. Runtime data access uses EF.
internal static class PostgreSqlStateMigration
{
    internal static async Task InitializeRecoveryAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            CREATE TABLE IF NOT EXISTS cellbridge_recovery_schema(version integer PRIMARY KEY);
            INSERT INTO cellbridge_recovery_schema SELECT 1 WHERE NOT EXISTS(SELECT 1 FROM cellbridge_recovery_schema);
            CREATE TABLE IF NOT EXISTS cellbridge_recovery_receipts(operation_id uuid PRIMARY KEY,receipt_json text NOT NULL);
            """, connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await ValidateRecoverySchemaAsync(connection, transaction, cancellationToken);
    }

    internal static async Task ValidateRecoverySchemaAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT count(*) = 1 AND min(version) = 1 AND max(version) = 1
            FROM cellbridge_recovery_schema
            """, connection, transaction);
        if (await command.ExecuteScalarAsync(cancellationToken) is not true)
            throw new StorageUnavailableException("Unsupported recovery schema; version 1 is required.");
    }

    internal static async Task MigrateGraphMetadataAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        StorageLimits limits, CancellationToken cancellationToken)
    {
        await using (var declare = new NpgsqlCommand("DECLARE graph_metadata_migration NO SCROLL CURSOR FOR SELECT resource_id,state_version,state_json FROM cellbridge_states ORDER BY resource_id,state_version", connection, transaction))
            await declare.ExecuteNonQueryAsync(cancellationToken);
        while (true)
        {
            Guid resourceId;
            long stateVersion;
            string json;
            await using (var fetch = new NpgsqlCommand("FETCH FORWARD 1 FROM graph_metadata_migration", connection, transaction))
            await using (var reader = await fetch.ExecuteReaderAsync(cancellationToken))
            {
                if (!await reader.ReadAsync(cancellationToken)) break;
                resourceId = reader.GetGuid(0);
                stateVersion = reader.GetInt64(1);
                json = reader.GetString(2);
            }
            var state = DocumentStateJson.Deserialize(json, strict: true, limits: limits);
            if (state.ResourceId != resourceId || state.StateVersion != stateVersion)
                throw new StorageCorruptionException("Migration snapshot row identity disagrees with its state.");
            limits.CheckDocument(state);
            await using var update = new NpgsqlCommand(
                "UPDATE cellbridge_states SET state_json=$3 WHERE resource_id=$1 AND state_version=$2",
                connection, transaction);
            update.Parameters.AddWithValue(resourceId);
            update.Parameters.AddWithValue(stateVersion);
            update.Parameters.AddWithValue(DocumentStateJson.Serialize(state));
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
        await using var close = new NpgsqlCommand("CLOSE graph_metadata_migration", connection, transaction);
        await close.ExecuteNonQueryAsync(cancellationToken);
    }

    internal static async Task MigrateSelectiveReadsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        StorageLimits limits, CancellationToken cancellationToken)
    {
        await using (var clear = new NpgsqlCommand(
            "TRUNCATE cellbridge_revision_summaries,cellbridge_read_heads", connection, transaction))
            await clear.ExecuteNonQueryAsync(cancellationToken);
        await using (var declare = new NpgsqlCommand("DECLARE selective_read_migration NO SCROLL CURSOR FOR SELECT d.resource_id,d.state_version,d.path_key,d.is_deleted,s.state_json FROM cellbridge_documents d LEFT JOIN cellbridge_states s USING(resource_id,state_version) ORDER BY d.resource_id", connection, transaction))
            await declare.ExecuteNonQueryAsync(cancellationToken);
        while (true)
        {
            Guid resourceId;
            long stateVersion;
            string pathKey;
            bool deleted;
            string json;
            await using (var fetch = new NpgsqlCommand("FETCH FORWARD 1 FROM selective_read_migration", connection, transaction))
            await using (var reader = await fetch.ExecuteReaderAsync(cancellationToken))
            {
                if (!await reader.ReadAsync(cancellationToken)) break;
                resourceId = reader.GetGuid(0);
                stateVersion = reader.GetInt64(1);
                pathKey = reader.GetString(2);
                deleted = reader.GetBoolean(3);
                if (reader.IsDBNull(4)) throw new StorageCorruptionException("Migration current state is missing.");
                json = reader.GetString(4);
            }
            var state = DocumentStateJson.Deserialize(json, strict: true, limits: limits);
            if (state.ResourceId != resourceId || state.StateVersion != stateVersion ||
                state.PathKey != pathKey || state.IsDeleted != deleted)
                throw new StorageCorruptionException("Migration current pointer disagrees with its state.");
            await WriteSelectiveReadsAsync(connection, transaction, state, cancellationToken);
        }
        await using var close = new NpgsqlCommand("CLOSE selective_read_migration", connection, transaction);
        await close.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task WriteSelectiveReadsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        DocumentState state, CancellationToken cancellationToken)
    {
        var metadata = DocumentReadModels.Metadata(state);
        var revisions = DocumentReadModels.Revisions(state).ToArray();
        if (revisions.Length == 0 || revisions.Length != metadata.RevisionCount ||
            revisions.Any(revision => revision.ResourceId != state.ResourceId ||
                revision.LifecycleGeneration != state.LifecycleGeneration || revision.RevisionNumber == 0) ||
            revisions.Select(revision => revision.RevisionNumber).Distinct().Count() != revisions.Length ||
            revisions.Max(revision => revision.RevisionNumber) != metadata.LatestRevisionNumber)
            throw new StorageCorruptionException("Cannot index inconsistent revision history.");
        var revisionJson = JsonSerializer.Serialize(revisions);
        await using (var sync = new NpgsqlCommand("""
            WITH removed AS (DELETE FROM cellbridge_revision_summaries r WHERE r.resource_id=$1 AND NOT EXISTS (
                SELECT 1 FROM jsonb_array_elements($2::jsonb) item
                WHERE (item->>'LifecycleGeneration')::bigint=r.lifecycle_generation
                    AND (item->>'RevisionNumber')::numeric=r.revision_number))
            INSERT INTO cellbridge_revision_summaries(resource_id,lifecycle_generation,revision_number,summary_json)
                SELECT $1,(item->>'LifecycleGeneration')::bigint,(item->>'RevisionNumber')::numeric,item::text
                FROM jsonb_array_elements($2::jsonb) item
                ON CONFLICT(resource_id,lifecycle_generation,revision_number) DO UPDATE
                    SET summary_json=excluded.summary_json
                    WHERE cellbridge_revision_summaries.summary_json<>excluded.summary_json
            """, connection, transaction))
        {
            sync.Parameters.AddWithValue(state.ResourceId);
            sync.Parameters.AddWithValue(revisionJson);
            await sync.ExecuteNonQueryAsync(cancellationToken);
        }
        await using var head = new NpgsqlCommand("""
            INSERT INTO cellbridge_read_heads(resource_id,state_version,lifecycle_generation,revision_count,metadata_json)
                VALUES($1,$2,$3,$4,$5)
                ON CONFLICT(resource_id) DO UPDATE SET state_version=excluded.state_version,
                    lifecycle_generation=excluded.lifecycle_generation,revision_count=excluded.revision_count,
                    metadata_json=excluded.metadata_json
            """, connection, transaction);
        head.Parameters.AddWithValue(state.ResourceId);
        head.Parameters.AddWithValue(state.StateVersion);
        head.Parameters.AddWithValue(state.LifecycleGeneration);
        head.Parameters.AddWithValue(revisions.Length);
        head.Parameters.AddWithValue(JsonSerializer.Serialize(metadata));
        await head.ExecuteNonQueryAsync(cancellationToken);
    }
}
