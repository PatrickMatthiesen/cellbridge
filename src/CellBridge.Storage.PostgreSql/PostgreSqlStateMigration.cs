using CellBridge.Storage.Abstractions;
using Npgsql;

namespace CellBridge.Storage.PostgreSql;

public sealed partial class PostgreSqlStateStore
{
    // Called only by explicit initialization while the schema table excludes old writers.
    // The cursor bounds client memory to one retained snapshot. The entire migration,
    // version change and usage recount commit together, or all roll back.
    private async Task MigrateGraphMetadataAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
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
            var state = DocumentStateJson.Deserialize(json, strict: true, limits: _limits);
            if (state.ResourceId != resourceId || state.StateVersion != stateVersion)
                throw new StorageCorruptionException("Migration snapshot row identity disagrees with its state.");
            _limits.CheckDocument(state);
            await using var update = new NpgsqlCommand("UPDATE cellbridge_states SET state_json=$3 WHERE resource_id=$1 AND state_version=$2", connection, transaction);
            update.Parameters.AddWithValue(resourceId);
            update.Parameters.AddWithValue(stateVersion);
            update.Parameters.AddWithValue(DocumentStateJson.Serialize(state));
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
        await using var close = new NpgsqlCommand("CLOSE graph_metadata_migration", connection, transaction);
        await close.ExecuteNonQueryAsync(cancellationToken);
    }

    // Schema 5 already has the compact authoritative format. Build only derived
    // indexes from current heads; leave every retained state_json byte unchanged.
    private async Task MigrateSelectiveReadsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using (var clear = new NpgsqlCommand("TRUNCATE cellbridge_revision_summaries,cellbridge_read_heads", connection, transaction))
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
            var state = DocumentStateJson.Deserialize(json, strict: true, limits: _limits);
            if (state.ResourceId != resourceId || state.StateVersion != stateVersion ||
                state.PathKey != pathKey || state.IsDeleted != deleted)
                throw new StorageCorruptionException("Migration current pointer disagrees with its state.");
            await WriteSelectiveReadsAsync(connection, transaction, state, cancellationToken);
        }
        await using var close = new NpgsqlCommand("CLOSE selective_read_migration", connection, transaction);
        await close.ExecuteNonQueryAsync(cancellationToken);
    }
}
