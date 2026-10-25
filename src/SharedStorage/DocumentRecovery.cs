using System.Collections.Immutable;
using System.Text.Json;
using CellBridge.Storage.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace CellBridge.Storage.Shared;

internal sealed partial class DocumentOperations<TContext> where TContext : DbContext
{
    public async ValueTask CheckRecoveryAsync(CancellationToken cancellationToken = default)
    {
        await CheckHealthAsync(cancellationToken);
        await ReadAsync(async db => { await database.CheckRecoveryAsync(db, cancellationToken); return true; }, cancellationToken);
    }

    public async ValueTask<RecoveryReceipt?> FindRecoveryReceiptAsync(Guid operationId, CancellationToken cancellationToken = default) =>
        await ReadAsync(async db =>
        {
            var row = await db.Set<RecoveryReceiptRow>().AsNoTracking().SingleOrDefaultAsync(row => row.OperationId == operationId, cancellationToken);
            return row is null ? null : DecodeReceipt(row);
        }, cancellationToken);

    public async ValueTask<ProviderSnapshot> CaptureRecoveryAsync(CancellationToken cancellationToken = default) =>
        await MutateAsync(async db =>
        {
            await database.CheckRecoveryAsync(db, cancellationToken);
            // Every state publisher holds the shared publication lock. Its exclusive
            // form gives capture a stable namespace and retained snapshot inventory.
            var pointers = new Dictionary<Guid, DocumentRow>();
            await foreach (var row in db.Set<DocumentRow>().AsNoTracking().OrderBy(row => row.ResourceId).AsAsyncEnumerable().WithCancellation(cancellationToken))
            {
                StorageLimits.Check("recovery documents", pointers.Count + 1, limits.MaxDocuments);
                pointers.Add(row.ResourceId, row);
            }
            var metadataBytes = await db.Set<StateRow>().SumAsync(row => (long?)DocumentSql.ByteLength(row.StateJson), cancellationToken) ?? 0;
            StorageLimits.Check("recovery metadata bytes", metadataBytes, 128L * 1024 * 1024);
            var snapshots = ImmutableArray.CreateBuilder<DocumentState>();
            long membership = 0;
            var latest = new Dictionary<Guid, long>();
            var counts = new Dictionary<Guid, int>();
            await foreach (var row in db.Set<StateRow>().AsNoTracking().OrderBy(row => row.ResourceId).ThenBy(row => row.StateVersion)
                .AsAsyncEnumerable().WithCancellation(cancellationToken))
            {
                var decodeLimits = limits with { MaxDecodedGraphMembershipBytes = Math.Min(limits.MaxDecodedGraphMembershipBytes, 128L * 1024 * 1024 - membership) };
                var state = Decode(row.StateJson, strict: true, decodeLimits: decodeLimits);
                if (state.ResourceId != row.ResourceId || state.StateVersion != row.StateVersion || !pointers.TryGetValue(state.ResourceId, out var pointer))
                    throw new StorageCorruptionException("Recovery snapshot identity disagrees with its pointer.");
                if (state.StateVersion == pointer.StateVersion) ValidatePointer(state, pointer);
                membership = checked(membership + StorageLimits.GetDecodedGraphMembershipBytes(state));
                StorageLimits.Check("recovery decoded graph membership bytes", membership, 128L * 1024 * 1024);
                var count = counts.GetValueOrDefault(state.ResourceId) + 1;
                StorageLimits.Check("retained state snapshots", count, limits.MaxRetainedStateSnapshots);
                counts[state.ResourceId] = count;
                latest[state.ResourceId] = state.StateVersion;
                snapshots.Add(state);
            }
            if (latest.Count != pointers.Count || pointers.Values.Any(row => !latest.TryGetValue(row.ResourceId, out var version) || version != row.StateVersion))
                throw new StorageCorruptionException("Recovery inventory has missing or stale heads.");
            var receipts = await db.Set<RecoveryReceiptRow>().AsNoTracking().OrderBy(row => row.OperationId).Take(1001).ToListAsync(cancellationToken);
            StorageLimits.Check("recovery receipts", receipts.Count, 1000);
            return new ProviderSnapshot(pointers.Values.Select(row => new RecoveryHead(row.ResourceId, row.StateVersion)).ToImmutableArray(),
                snapshots.ToImmutable(), receipts.Select(DecodeReceipt).ToImmutableArray());
        }, cancellationToken, exclusive: true);

    public async ValueTask<RecoveryReceipt> ImportRecoveryAsync(ProviderSnapshot snapshot, RecoveryReceipt receipt,
        CancellationToken cancellationToken = default) => await MutateAsync(async db =>
    {
        await database.CheckRecoveryAsync(db, cancellationToken);
        await database.Locks.NamespaceAsync(db, cancellationToken);
        var accepted = await db.Set<RecoveryReceiptRow>().AsNoTracking().SingleOrDefaultAsync(row => row.OperationId == receipt.OperationId, cancellationToken);
        if (accepted is not null)
        {
            var replay = DecodeReceipt(accepted);
            ProviderRecovery.CheckReceipt(replay, receipt);
            return replay;
        }
        if (await db.Set<DocumentRow>().AnyAsync(cancellationToken) || await db.Set<StateRow>().AnyAsync(cancellationToken) ||
            await db.Set<RecoveryReceiptRow>().AnyAsync(cancellationToken) || await database.HasReservationsAsync(db, cancellationToken) ||
            await db.Set<ReadHeadRow>().AnyAsync(cancellationToken) || await db.Set<RevisionSummaryRow>().AnyAsync(cancellationToken))
            throw new RecoveryConflictException("Recovery requires an empty document namespace and recovery ledger.");
        var now = await ReadClockAsync(db, cancellationToken);
        ProviderRecovery.CheckAdmission(snapshot, limits, now);
        if (snapshot.Receipts.Any(row => row.OperationId == receipt.OperationId) || snapshot.Receipts.Length >= 1000)
            throw new RecoveryConflictException("Recovery receipt identity collision or ledger limit.");
        ProviderRecovery.CheckAdmission(snapshot with { Receipts = snapshot.Receipts.Add(receipt) }, limits, now);
        var heads = snapshot.Heads.Select(head => snapshot.Snapshots.Single(state => state.ResourceId == head.ResourceId && state.StateVersion == head.StateVersion)).ToArray();
        if (heads.Where(state => !state.IsDeleted).GroupBy(state => state.PathKey, StringComparer.Ordinal).Any(group => group.Count() > 1))
            throw new StorageCorruptionException("Recovery contains duplicate live document paths.");
        await DocumentBudget.AdjustAsync(database, db, 0, heads.Length, cancellationToken);
        foreach (var state in snapshot.Snapshots)
            await WriteSnapshotAsync(db, state, false, cancellationToken);
        foreach (var state in heads)
        {
            db.Set<DocumentRow>().Add(Pointer(state));
            await WriteSelectiveReadsAsync(db, state, cancellationToken);
        }
        foreach (var retained in snapshot.Receipts.Append(receipt))
        {
            var data = JsonSerializer.Serialize(retained);
            await DocumentBudget.AdjustAsync(database, db, System.Text.Encoding.UTF8.GetByteCount(data), 0, cancellationToken);
            db.Set<RecoveryReceiptRow>().Add(new() { OperationId = retained.OperationId, ReceiptJson = data });
        }
        await db.SaveChangesAsync(cancellationToken);
        return receipt;
    }, cancellationToken, exclusive: true);

    private static RecoveryReceipt DecodeReceipt(RecoveryReceiptRow row)
    {
        var receipt = RecoveryJson.Read<RecoveryReceipt>(System.Text.Encoding.UTF8.GetBytes(row.ReceiptJson));
        if (receipt.OperationId != row.OperationId) throw new StorageCorruptionException("Recovery receipt identity disagrees with its row.");
        return receipt;
    }
}
