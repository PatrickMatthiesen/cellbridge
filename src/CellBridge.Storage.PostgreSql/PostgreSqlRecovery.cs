using System.Collections.Immutable;
using System.Data;
using System.Text;
using System.Text.Json;
using CellBridge.Storage.Abstractions;
using Npgsql;

namespace CellBridge.Storage.PostgreSql;

public sealed partial class PostgreSqlStateStore
{
    private static async Task InitializeRecoveryAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            CREATE TABLE IF NOT EXISTS cellbridge_recovery_schema(version integer PRIMARY KEY);
            INSERT INTO cellbridge_recovery_schema SELECT 1 WHERE NOT EXISTS(SELECT 1 FROM cellbridge_recovery_schema);
            CREATE TABLE IF NOT EXISTS cellbridge_recovery_receipts(operation_id uuid PRIMARY KEY,receipt_json text NOT NULL);
            """, connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await CheckRecoverySchemaAsync(connection, transaction, cancellationToken);
    }

    private static async Task CheckRecoverySchemaAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using (var exists = new NpgsqlCommand("SELECT to_regclass('cellbridge_recovery_schema') IS NOT NULL AND to_regclass('cellbridge_recovery_receipts') IS NOT NULL", connection, transaction))
            if (!(bool)(await exists.ExecuteScalarAsync(cancellationToken))!)
                throw new StorageUnavailableException("Recovery schema is absent. Run explicit storage initialization with hosts stopped.");
        await using var version = new NpgsqlCommand("SELECT version FROM cellbridge_recovery_schema", connection, transaction);
        await using var reader = await version.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) || reader.GetInt32(0) != 1 || await reader.ReadAsync(cancellationToken))
            throw new StorageUnavailableException("Unsupported recovery schema; version 1 is required.");
    }

    public async ValueTask CheckRecoveryAsync(CancellationToken cancellationToken = default)
    {
        await CheckHealthAsync(cancellationToken);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await CheckRecoverySchemaAsync(connection, null, cancellationToken);
    }

    public async ValueTask<ProviderSnapshot> CaptureRecoveryAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
            await ConfigureTransactionAsync(connection, transaction, cancellationToken);
            await CheckRecoverySchemaAsync(connection, transaction, cancellationToken);
            var heads = ImmutableArray.CreateBuilder<RecoveryHead>();
            var pointers = new Dictionary<(Guid, long), (string Path, bool Deleted)>();
            await using (var command = new NpgsqlCommand("SELECT resource_id,state_version,path_key,is_deleted FROM cellbridge_documents ORDER BY resource_id", connection, transaction))
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
                while (await reader.ReadAsync(cancellationToken))
                {
                    StorageLimits.Check("recovery documents", heads.Count + 1, _limits.MaxDocuments);
                    heads.Add(new(reader.GetGuid(0), reader.GetInt64(1)));
                    pointers.Add((reader.GetGuid(0), reader.GetInt64(1)), (reader.GetString(2), reader.GetBoolean(3)));
                }
            var states = ImmutableArray.CreateBuilder<DocumentState>();
            long metadataBytes = 0;
            await using (var command = new NpgsqlCommand("SELECT resource_id,state_version,state_json FROM cellbridge_states ORDER BY resource_id,state_version", connection, transaction))
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
                while (await reader.ReadAsync(cancellationToken))
                {
                    var bytes = Encoding.UTF8.GetBytes(reader.GetString(2));
                    metadataBytes = checked(metadataBytes + bytes.LongLength);
                    StorageLimits.Check("recovery metadata bytes", metadataBytes, 128L * 1024 * 1024);
                    var state = RecoveryJson.Read<DocumentState>(bytes);
                    if (state.ResourceId != reader.GetGuid(0) || state.StateVersion != reader.GetInt64(1))
                        throw new StorageCorruptionException("Recovery snapshot row identity disagrees with JSON.");
                    if (pointers.TryGetValue((state.ResourceId, state.StateVersion), out var pointer) &&
                        (pointer.Path != state.PathKey || pointer.Deleted != state.IsDeleted))
                        throw new StorageCorruptionException("Recovery head path/deletion flag disagrees with JSON.");
                    states.Add(state);
                }
            var receipts = ImmutableArray.CreateBuilder<RecoveryReceipt>();
            await using (var command = new NpgsqlCommand("SELECT operation_id,receipt_json FROM cellbridge_recovery_receipts ORDER BY operation_id", connection, transaction))
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
                while (await reader.ReadAsync(cancellationToken))
                {
                    StorageLimits.Check("recovery receipts", receipts.Count + 1, 1000);
                    var receipt = RecoveryJson.Read<RecoveryReceipt>(Encoding.UTF8.GetBytes(reader.GetString(1)));
                    if (receipt.OperationId != reader.GetGuid(0)) throw new StorageCorruptionException("Recovery receipt row identity disagrees with JSON.");
                    receipts.Add(receipt);
                }
            return new(heads.ToImmutable(), states.ToImmutable(), receipts.ToImmutable());
        }
        catch (NpgsqlException ex) { throw new StorageUnavailableException("Recovery snapshot could not be read.", ex); }
    }

    public async ValueTask<RecoveryReceipt?> FindRecoveryReceiptAsync(Guid operationId, CancellationToken cancellationToken = default)
    {
        await using var command = dataSource.CreateCommand("SELECT receipt_json FROM cellbridge_recovery_receipts WHERE operation_id=$1");
        command.Parameters.AddWithValue(operationId);
        var json = await command.ExecuteScalarAsync(cancellationToken) as string;
        return json is null ? null : RecoveryJson.Read<RecoveryReceipt>(Encoding.UTF8.GetBytes(json));
    }

    public async ValueTask<RecoveryReceipt> ImportRecoveryAsync(ProviderSnapshot snapshot, RecoveryReceipt receipt,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await ConfigureTransactionAsync(connection, transaction, cancellationToken);
            await CheckRecoverySchemaAsync(connection, transaction, cancellationToken);
            await PostgreSqlNamespaceLock.AcquireAsync(connection, transaction, cancellationToken);
            // Blocks every pointer writer; readers continue to observe the previous complete namespace.
            await using (var gate = new NpgsqlCommand("LOCK TABLE cellbridge_documents,cellbridge_states,cellbridge_recovery_receipts IN SHARE ROW EXCLUSIVE MODE", connection, transaction))
                await gate.ExecuteNonQueryAsync(cancellationToken);
            await using (var replay = new NpgsqlCommand("SELECT receipt_json FROM cellbridge_recovery_receipts WHERE operation_id=$1", connection, transaction))
            {
                replay.Parameters.AddWithValue(receipt.OperationId);
                if (await replay.ExecuteScalarAsync(cancellationToken) is string json)
                {
                    var accepted = RecoveryJson.Read<RecoveryReceipt>(Encoding.UTF8.GetBytes(json));
                    ProviderRecovery.CheckReceipt(accepted, receipt);
                    return accepted;
                }
            }
            await using (var occupied = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM cellbridge_documents) OR EXISTS(SELECT 1 FROM cellbridge_states) OR EXISTS(SELECT 1 FROM cellbridge_recovery_receipts)", connection, transaction))
                if ((bool)(await occupied.ExecuteScalarAsync(cancellationToken))!)
                    throw new RecoveryConflictException("Recovery destination must have no live or retired document identities or prior recovery operations.");
            DateTime now;
            await using (var clock = new NpgsqlCommand("SELECT clock_timestamp()", connection, transaction))
                now = (DateTime)(await clock.ExecuteScalarAsync(cancellationToken))!;
            ProviderRecovery.CheckAdmission(snapshot, _limits, now);
            if (snapshot.Receipts.Any(r => r.OperationId == receipt.OperationId) || snapshot.Receipts.Length >= 1000)
                throw new RecoveryConflictException("Recovery receipt identity collision or ledger limit.");
            await PostgreSqlStorageBudget.AdjustAsync(connection, transaction, 0, snapshot.Heads.Length, cancellationToken);
            foreach (var state in snapshot.Snapshots)
                await InsertStateAsync(connection, transaction, state, cancellationToken);
            foreach (var head in snapshot.Heads)
            {
                var state = snapshot.Snapshots.Single(s => s.ResourceId == head.ResourceId && s.StateVersion == head.StateVersion);
                await using var insert = new NpgsqlCommand("INSERT INTO cellbridge_documents VALUES($1,$2,$3,$4)", connection, transaction);
                insert.Parameters.AddWithValue(state.ResourceId); insert.Parameters.AddWithValue(state.PathKey);
                insert.Parameters.AddWithValue(state.StateVersion); insert.Parameters.AddWithValue(state.IsDeleted);
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }
            foreach (var retained in snapshot.Receipts.Append(receipt))
            {
                var json = JsonSerializer.Serialize(retained);
                await PostgreSqlStorageBudget.AdjustAsync(connection, transaction, Encoding.UTF8.GetByteCount(json), 0, cancellationToken);
                await using var insert = new NpgsqlCommand("INSERT INTO cellbridge_recovery_receipts VALUES($1,$2)", connection, transaction);
                insert.Parameters.AddWithValue(retained.OperationId); insert.Parameters.AddWithValue(json);
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            await transaction.CommitAsync(CancellationToken.None);
            return receipt;
        }
        catch (NpgsqlException ex)
        { throw new StorageUnavailableException("Recovery failed or its commit outcome is unknown. Retry the same archive and context.", ex); }
    }
}
