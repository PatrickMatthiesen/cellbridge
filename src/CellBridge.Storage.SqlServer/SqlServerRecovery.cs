using System.Collections.Immutable;
using System.Text.Json;
using CellBridge.Storage.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace CellBridge.Storage.SqlServer;

public sealed partial class SqlServerStateStore
{
    public ValueTask CheckRecoveryAsync(CancellationToken cancellationToken = default) => CheckHealthAsync(cancellationToken);

    public async ValueTask<RecoveryReceipt?> FindRecoveryReceiptAsync(Guid operationId, CancellationToken cancellationToken = default) =>
        await ReadAsync(async db =>
        {
            var row = await db.RecoveryReceipts.AsNoTracking().SingleOrDefaultAsync(row => row.OperationId == operationId, cancellationToken);
            return row is null ? null : DecodeReceipt(row);
        }, cancellationToken);

    public async ValueTask<ProviderSnapshot> CaptureRecoveryAsync(CancellationToken cancellationToken = default) =>
        await MutateAsync(async db =>
        {
            // Every state publisher holds the shared publication lock. Its exclusive
            // form gives capture a stable namespace and retained snapshot inventory.
            var pointers = new Dictionary<Guid, DocumentPointer>();
            await foreach (var row in db.Documents.AsNoTracking().OrderBy(row => row.ResourceId).AsAsyncEnumerable().WithCancellation(cancellationToken))
            {
                StorageLimits.Check("recovery documents", pointers.Count + 1, limits.MaxDocuments);
                pointers.Add(row.ResourceId, row);
            }
            var metadataBytes = await db.Snapshots.SumAsync(row => (long?)SqlServerDocumentDbContext.ByteLength(row.Data), cancellationToken) ?? 0;
            StorageLimits.Check("recovery metadata bytes", metadataBytes, 128L * 1024 * 1024);
            var snapshots = ImmutableArray.CreateBuilder<DocumentState>();
            long membership = 0;
            var latest = new Dictionary<Guid, long>();
            var counts = new Dictionary<Guid, int>();
            await foreach (var row in db.Snapshots.AsNoTracking().OrderBy(row => row.ResourceId).ThenBy(row => row.StateVersion)
                .AsAsyncEnumerable().WithCancellation(cancellationToken))
            {
                var decodeLimits = limits with { MaxDecodedGraphMembershipBytes = Math.Min(limits.MaxDecodedGraphMembershipBytes, 128L * 1024 * 1024 - membership) };
                var state = Decode(row.Data, strict: true, decodeLimits: decodeLimits);
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
            var receipts = await db.RecoveryReceipts.AsNoTracking().OrderBy(row => row.OperationId).Take(1001).ToListAsync(cancellationToken);
            StorageLimits.Check("recovery receipts", receipts.Count, 1000);
            return new ProviderSnapshot(pointers.Values.Select(row => new RecoveryHead(row.ResourceId, row.StateVersion)).ToImmutableArray(),
                snapshots.ToImmutable(), receipts.Select(DecodeReceipt).ToImmutableArray());
        }, cancellationToken, exclusive: true);

    public async ValueTask<RecoveryReceipt> ImportRecoveryAsync(ProviderSnapshot snapshot, RecoveryReceipt receipt,
        CancellationToken cancellationToken = default) => await MutateAsync(async db =>
    {
        await SqlServerDocumentDatabase.LockAsync(db, NamespaceGate, cancellationToken);
        var accepted = await db.RecoveryReceipts.AsNoTracking().SingleOrDefaultAsync(row => row.OperationId == receipt.OperationId, cancellationToken);
        if (accepted is not null)
        {
            var replay = DecodeReceipt(accepted);
            ProviderRecovery.CheckReceipt(replay, receipt);
            return replay;
        }
        if (await db.Documents.AnyAsync(cancellationToken) || await db.Snapshots.AnyAsync(cancellationToken) ||
            await db.RecoveryReceipts.AnyAsync(cancellationToken) || await db.Reservations.AnyAsync(cancellationToken) ||
            await db.Metadata.AnyAsync(cancellationToken) || await db.Revisions.AnyAsync(cancellationToken))
            throw new RecoveryConflictException("Recovery requires an empty document namespace and recovery ledger.");
        var now = await ReadClockAsync(db, cancellationToken);
        ProviderRecovery.CheckAdmission(snapshot, limits, now);
        if (snapshot.Receipts.Any(row => row.OperationId == receipt.OperationId) || snapshot.Receipts.Length >= 1000)
            throw new RecoveryConflictException("Recovery receipt identity collision or ledger limit.");
        ProviderRecovery.CheckAdmission(snapshot with { Receipts = snapshot.Receipts.Add(receipt) }, limits, now);
        var heads = snapshot.Heads.Select(head => snapshot.Snapshots.Single(state => state.ResourceId == head.ResourceId && state.StateVersion == head.StateVersion)).ToArray();
        if (heads.Where(state => !state.IsDeleted).GroupBy(state => state.PathKey, StringComparer.Ordinal).Any(group => group.Count() > 1))
            throw new StorageCorruptionException("Recovery contains duplicate live document paths.");
        await SqlServerDocumentDatabase.AdjustUsageAsync(db, 0, heads.Length, cancellationToken);
        foreach (var state in snapshot.Snapshots)
            await WriteSnapshotAsync(db, state, false, cancellationToken);
        foreach (var state in heads)
        {
            db.Documents.Add(Pointer(state));
            await WriteIndexesAsync(db, state, cancellationToken);
        }
        foreach (var retained in snapshot.Receipts.Append(receipt))
        {
            var data = JsonSerializer.SerializeToUtf8Bytes(retained);
            await SqlServerDocumentDatabase.AdjustUsageAsync(db, data.LongLength, 0, cancellationToken);
            db.RecoveryReceipts.Add(new() { OperationId = retained.OperationId, Data = data });
        }
        await db.SaveChangesAsync(cancellationToken);
        return receipt;
    }, cancellationToken, exclusive: true);

    private static RecoveryReceipt DecodeReceipt(RecoveryRecord row)
    {
        var receipt = RecoveryJson.Read<RecoveryReceipt>(row.Data);
        if (receipt.OperationId != row.OperationId) throw new StorageCorruptionException("Recovery receipt identity disagrees with its row.");
        return receipt;
    }
}
