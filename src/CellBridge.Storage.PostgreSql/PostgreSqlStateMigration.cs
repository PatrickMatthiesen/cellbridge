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
}
