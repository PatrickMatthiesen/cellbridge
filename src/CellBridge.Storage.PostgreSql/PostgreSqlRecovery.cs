using System.Collections.Immutable;
using System.Data;
using System.Text;
using System.Text.Json;
using CellBridge.Storage.Abstractions;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CellBridge.Storage.PostgreSql;

public sealed partial class PostgreSqlStateStore
{
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

    public async ValueTask CheckRecoveryAsync(CancellationToken cancellationToken = default)
    {
        await CheckHealthAsync(cancellationToken);
        await using var context = new CellBridgeDbContext(_options);
        await CheckRecoverySchemaAsync(context, cancellationToken);
    }

    public async ValueTask<ProviderSnapshot> CaptureRecoveryAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var context = new CellBridgeDbContext(_options);
            await using var transaction = await context.Database.BeginTransactionAsync(
                IsolationLevel.RepeatableRead, cancellationToken);
            await ConfigureTransactionAsync(context, cancellationToken);
            await CheckRecoverySchemaAsync(context, cancellationToken);
            var heads = ImmutableArray.CreateBuilder<RecoveryHead>();
            var pointers = new Dictionary<(Guid, long), (string Path, bool Deleted)>();
            await foreach (var row in context.Documents.AsNoTracking().OrderBy(row => row.ResourceId)
                               .AsAsyncEnumerable().WithCancellation(cancellationToken))
            {
                StorageLimits.Check("recovery documents", heads.Count + 1, _limits.MaxDocuments);
                heads.Add(new(row.ResourceId, row.StateVersion));
                pointers.Add((row.ResourceId, row.StateVersion), (row.PathKey, row.IsDeleted));
            }
            var states = ImmutableArray.CreateBuilder<DocumentState>();
            long metadataBytes = 0;
            long decodedGraphMembershipBytes = 0;
            await foreach (var row in context.States.AsNoTracking()
                               .OrderBy(row => row.ResourceId).ThenBy(row => row.StateVersion)
                               .AsAsyncEnumerable().WithCancellation(cancellationToken))
            {
                var bytes = Encoding.UTF8.GetBytes(row.StateJson);
                metadataBytes = checked(metadataBytes + bytes.LongLength);
                StorageLimits.Check("recovery metadata bytes", metadataBytes, 128L * 1024 * 1024);
                var remainingGraphMembershipBytes = 128L * 1024 * 1024 - decodedGraphMembershipBytes;
                var decodeLimits = _limits with
                {
                    MaxDecodedGraphMembershipBytes = Math.Min(
                        _limits.MaxDecodedGraphMembershipBytes, remainingGraphMembershipBytes),
                };
                var state = DocumentStateJson.Deserialize(row.StateJson, strict: true, limits: decodeLimits);
                decodedGraphMembershipBytes = checked(decodedGraphMembershipBytes +
                    StorageLimits.GetDecodedGraphMembershipBytes(state));
                StorageLimits.Check("recovery decoded graph membership bytes", decodedGraphMembershipBytes,
                    128L * 1024 * 1024);
                if (state.ResourceId != row.ResourceId || state.StateVersion != row.StateVersion)
                    throw new StorageCorruptionException("Recovery snapshot row identity disagrees with JSON.");
                if (pointers.TryGetValue((state.ResourceId, state.StateVersion), out var pointer) &&
                    (pointer.Path != state.PathKey || pointer.Deleted != state.IsDeleted))
                    throw new StorageCorruptionException("Recovery head path/deletion flag disagrees with JSON.");
                states.Add(state);
            }
            var receipts = ImmutableArray.CreateBuilder<RecoveryReceipt>();
            await foreach (var row in context.RecoveryReceipts.AsNoTracking().OrderBy(row => row.OperationId)
                               .AsAsyncEnumerable().WithCancellation(cancellationToken))
            {
                StorageLimits.Check("recovery receipts", receipts.Count + 1, 1000);
                var receipt = RecoveryJson.Read<RecoveryReceipt>(Encoding.UTF8.GetBytes(row.ReceiptJson));
                if (receipt.OperationId != row.OperationId)
                    throw new StorageCorruptionException("Recovery receipt row identity disagrees with JSON.");
                receipts.Add(receipt);
            }
            await transaction.CommitAsync(cancellationToken);
            return new(heads.ToImmutable(), states.ToImmutable(), receipts.ToImmutable());
        }
        catch (Exception ex) when (PostgreSqlDatabase.IsDatabaseException(ex))
        { throw new StorageUnavailableException("Recovery snapshot could not be read.", ex); }
    }

    public async ValueTask<RecoveryReceipt?> FindRecoveryReceiptAsync(Guid operationId,
        CancellationToken cancellationToken = default)
    {
        await using var context = new CellBridgeDbContext(_options);
        var json = await context.RecoveryReceipts.AsNoTracking().Where(row => row.OperationId == operationId)
            .Select(row => row.ReceiptJson).SingleOrDefaultAsync(cancellationToken);
        return json is null ? null : RecoveryJson.Read<RecoveryReceipt>(Encoding.UTF8.GetBytes(json));
    }

    public async ValueTask<RecoveryReceipt> ImportRecoveryAsync(ProviderSnapshot snapshot, RecoveryReceipt receipt,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var context = new CellBridgeDbContext(_options);
            await using var databaseTransaction = await context.Database.BeginTransactionAsync(cancellationToken);
            await ConfigureTransactionAsync(context, cancellationToken);
            await CheckRecoverySchemaAsync(context, cancellationToken);
            var (connection, transaction) = PostgreSqlDatabase.GetTransaction(context);
            await PostgreSqlNamespaceLock.AcquireAsync(connection, transaction, cancellationToken);
            // Recovery needs a namespace-wide publication barrier that EF cannot express.
            await using (var gate = new NpgsqlCommand(
                "LOCK TABLE cellbridge_documents,cellbridge_states,cellbridge_recovery_receipts IN SHARE ROW EXCLUSIVE MODE",
                connection, transaction))
                await gate.ExecuteNonQueryAsync(cancellationToken);
            var replayJson = await context.RecoveryReceipts.AsNoTracking()
                .Where(row => row.OperationId == receipt.OperationId).Select(row => row.ReceiptJson)
                .SingleOrDefaultAsync(cancellationToken);
            if (replayJson is not null)
            {
                var accepted = RecoveryJson.Read<RecoveryReceipt>(Encoding.UTF8.GetBytes(replayJson));
                ProviderRecovery.CheckReceipt(accepted, receipt);
                return accepted;
            }
            var destinationRows = context.Documents.Select(_ => 1)
                .Concat(context.States.Select(_ => 1))
                .Concat(context.RecoveryReceipts.Select(_ => 1));
            if (await destinationRows.AnyAsync(cancellationToken))
                throw new RecoveryConflictException(
                    "Recovery destination must have no live or retired document identities or prior recovery operations.");
            var now = await ReadDatabaseClockAsync(context, cancellationToken);
            ProviderRecovery.CheckAdmission(snapshot, _limits, now);
            if (snapshot.Receipts.Any(candidate => candidate.OperationId == receipt.OperationId) ||
                snapshot.Receipts.Length >= 1000)
                throw new RecoveryConflictException("Recovery receipt identity collision or ledger limit.");
            await PostgreSqlStorageBudget.AdjustAsync(context, 0, snapshot.Heads.Length, cancellationToken);
            foreach (var state in snapshot.Snapshots)
                await InsertStateAsync(context, state, cancellationToken);
            foreach (var head in snapshot.Heads)
            {
                var state = snapshot.Snapshots.Single(candidate =>
                    candidate.ResourceId == head.ResourceId && candidate.StateVersion == head.StateVersion);
                context.Documents.Add(new DocumentRow
                {
                    ResourceId = state.ResourceId,
                    PathKey = state.PathKey,
                    StateVersion = state.StateVersion,
                    IsDeleted = state.IsDeleted,
                });
            }
            foreach (var retained in snapshot.Receipts.Append(receipt))
            {
                var json = JsonSerializer.Serialize(retained);
                await PostgreSqlStorageBudget.AdjustAsync(context, Encoding.UTF8.GetByteCount(json), 0,
                    cancellationToken);
                context.RecoveryReceipts.Add(new RecoveryReceiptRow
                {
                    OperationId = retained.OperationId,
                    ReceiptJson = json,
                });
            }
            cancellationToken.ThrowIfCancellationRequested();
            await context.SaveChangesAsync(cancellationToken);
            await databaseTransaction.CommitAsync(CancellationToken.None);
            return receipt;
        }
        catch (Exception ex) when (PostgreSqlDatabase.IsDatabaseException(ex))
        { throw new StorageUnavailableException(
            "Recovery failed or its commit outcome is unknown. Retry the same archive and context.", ex); }
    }
}
