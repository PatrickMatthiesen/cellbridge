using System.Collections.Immutable;
using System.Text.Json;
using CellBridge.Storage.Abstractions;

namespace CellBridge.Storage.InMemory;

public sealed partial class InMemoryStateStore
{
    private ImmutableArray<DocumentState> _importedSnapshots = [];
    private readonly Dictionary<Guid, RecoveryReceipt> _recoveryReceipts = [];
    private long UnpinnedBytes(DocumentState state) => _importedSnapshots.Any(s =>
        s.ResourceId == state.ResourceId && s.StateVersion == state.StateVersion)
        ? 0 : JsonSerializer.SerializeToUtf8Bytes(state).LongLength;

    private void AdjustRecoveryTransition(DocumentState current, DocumentState next, DocumentState? replacement = null)
    {
        // Match normal provider metadata retention. This removes old state rows, never live graph/history.
        var obsolete = _importedSnapshots.Where(s => s.ResourceId == next.ResourceId)
            .OrderByDescending(s => s.StateVersion).Skip(Budget.Limits.MaxRetainedStateSnapshots - 1).ToArray();
        var retained = _importedSnapshots.Except(obsolete).ToImmutableArray();
        long bytes = JsonSerializer.SerializeToUtf8Bytes(next).LongLength - UnpinnedBytes(current) -
            obsolete.Sum(s => JsonSerializer.SerializeToUtf8Bytes(s).LongLength);
        if (replacement is not null) bytes = checked(bytes + JsonSerializer.SerializeToUtf8Bytes(replacement).LongLength);
        Budget.Adjust(bytes, replacement is null ? 0 : 1);
        _importedSnapshots = retained;
    }

    public ValueTask CheckRecoveryAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    public ValueTask<ProviderSnapshot> CaptureRecoveryAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (Budget.SyncRoot)
        {
            var snapshots = _importedSnapshots.Concat(_documents.Values)
                .DistinctBy(s => (s.ResourceId, s.StateVersion)).OrderBy(s => s.ResourceId).ThenBy(s => s.StateVersion).ToImmutableArray();
            return ValueTask.FromResult(new ProviderSnapshot(_documents.Values.OrderBy(s => s.ResourceId)
                .Select(s => new RecoveryHead(s.ResourceId, s.StateVersion)).ToImmutableArray(), snapshots,
                _recoveryReceipts.Values.OrderBy(r => r.OperationId).ToImmutableArray()));
        }
    }

    public ValueTask<RecoveryReceipt?> FindRecoveryReceiptAsync(Guid operationId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (Budget.SyncRoot) return ValueTask.FromResult(_recoveryReceipts.GetValueOrDefault(operationId));
    }

    public ValueTask<RecoveryReceipt> ImportRecoveryAsync(ProviderSnapshot snapshot, RecoveryReceipt receipt,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (Budget.SyncRoot)
        lock (_paths)
        {
            if (_recoveryReceipts.TryGetValue(receipt.OperationId, out var previous))
            {
                ProviderRecovery.CheckReceipt(previous, receipt);
                return ValueTask.FromResult(previous);
            }
            if (!_documents.IsEmpty || _recoveryReceipts.Count != 0)
                throw new RecoveryConflictException("Recovery destination must have no live or retired document identities or prior recovery operations.");
            ProviderRecovery.CheckAdmission(snapshot, Budget.Limits, DateTime.UtcNow);
            if (snapshot.Receipts.Any(r => r.OperationId == receipt.OperationId) || snapshot.Receipts.Length >= 1000)
                throw new RecoveryConflictException("Recovery receipt identity collision or ledger limit.");
            long bytes = snapshot.Snapshots.Sum(s => JsonSerializer.SerializeToUtf8Bytes(s).LongLength) +
                snapshot.Receipts.Append(receipt).Sum(r => JsonSerializer.SerializeToUtf8Bytes(r).LongLength);
            Budget.Adjust(bytes, snapshot.Heads.Length);
            _importedSnapshots = snapshot.Snapshots;
            foreach (var head in snapshot.Heads)
            {
                var state = snapshot.Snapshots.Single(s => s.ResourceId == head.ResourceId && s.StateVersion == head.StateVersion);
                _documents[state.ResourceId] = state;
                // Recreated ancestors share canonical names; the latest incarnation owns the namespace entry.
                if (!_paths.TryGetValue(state.PathKey, out var owner) || _documents[owner].LifecycleGeneration < state.LifecycleGeneration)
                    _paths[state.PathKey] = state.ResourceId;
            }
            foreach (var retained in snapshot.Receipts.Append(receipt)) _recoveryReceipts.Add(retained.OperationId, retained);
            return ValueTask.FromResult(receipt);
        }
    }
}
