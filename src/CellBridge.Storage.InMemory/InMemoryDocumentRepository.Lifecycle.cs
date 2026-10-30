using System.Collections.Immutable;
using System.Text.Json;
using System.Text;
using CellBridge.Storage.Abstractions;

namespace CellBridge.Storage.InMemory;

public sealed partial class InMemoryDocumentRepository
{
    public async ValueTask<PathChangeResult> ChangePathAsync(DocumentPathCommand command,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(command.NewPath) || string.IsNullOrEmpty(command.NewPathKey) || command.NewPathKey != StorageIds.PathKey(command.NewPath))
            throw new ArgumentException("The destination path must be nonempty.", nameof(command));
        if (!TryDocument(command.ResourceId, cancellationToken, out var entry)) throw new KeyNotFoundException("Document does not exist.");
        await entry.Guard.WaitAsync(cancellationToken);
        try
        {
            if (DocumentControlReducer.ValidateWrite(Control(entry), command.Admission, command.LockAttributes) is { } error)
                return new(entry.Head, false, error);
            if (entry.Head.PathKey == command.NewPathKey && entry.Head.Path == command.NewPath) return new(entry.Head, false);
            var retiring = entry.Head.PathKey != command.NewPathKey && !entry.RetiredPaths.Contains(entry.Head.PathKey);
            var reactivating = entry.Head.PathKey != command.NewPathKey && entry.RetiredPaths.Contains(command.NewPathKey);
            var metadataDelta = entry.OwnedPaths.Contains(command.NewPathKey) ? 0 : Encoding.UTF8.GetByteCount(command.NewPathKey);
            var next = entry.Head with
            {
                Path = command.NewPath, PathKey = command.NewPathKey, StateVersion = checked(entry.Head.StateVersion + 1),
                ModifiedUtc = UtcNow,
                Counters = entry.Head.Counters with
                {
                    RetiredPaths = checked(entry.Head.Counters.RetiredPaths + (retiring ? 1 : 0) - (reactivating ? 1 : 0)),
                },
            };
            next = AccountedHead(entry, next, metadataDelta);
            DocumentPublicationRules.CheckLimits(next, _limits);
            if (!_paths.TryAdd(command.NewPathKey, command.ResourceId) && _paths[command.NewPathKey] != command.ResourceId)
                return new(entry.Head, false, "FileAlreadyExists");
            if (retiring) entry.RetiredPaths.Add(entry.Head.PathKey);
            if (reactivating) entry.RetiredPaths.Remove(command.NewPathKey);
            entry.OwnedPaths.Add(command.NewPathKey);
            entry.MetadataBytes = checked(entry.MetadataBytes + metadataDelta);
            Volatile.Write(ref entry.Head, next);
            return new(next, true);
        }
        finally { entry.Guard.Release(); }
    }

    public async ValueTask<LifecycleResult> ChangeLifecycleAsync(DocumentLifecycleCommand command,
        CancellationToken cancellationToken = default)
    {
        // Preparation can include content I/O, and must precede either document guard.
        var replacement = command is DocumentLifecycleCommand.Recreate recreate
            ? await PrepareCreateAsync(recreate.Replacement, cancellationToken) : null;
        if (!TryDocument(command.ResourceId, cancellationToken, out var entry)) return new(false, null);
        await entry.Guard.WaitAsync(cancellationToken);
        try
        {
            var head = entry.Head;
            if (head.LifecycleGeneration != command.ExpectedGeneration) return new(false, head);
            if (command is DocumentLifecycleCommand.Delete)
            {
                if (head.IsDeleted) return new(head.DeletedFromStateVersion == command.ExpectedStateVersion, head);
                if (head.StateVersion != command.ExpectedStateVersion || entry.Deliveries.Count != 0) return new(false, head);
                var deleted = head with { IsDeleted = true, DeletedFromStateVersion = head.StateVersion,
                    StateVersion = checked(head.StateVersion + 1) };
                deleted = AccountedHead(entry, deleted);
                Volatile.Write(ref entry.Head, deleted);
                return new(true, deleted);
            }
            if (replacement is null) throw new ArgumentOutOfRangeException(nameof(command));
            if (head.ReplacedBy == replacement.Head.ResourceId && head.StateVersion == command.ExpectedStateVersion + 1)
                return new(true, head);
            if (!head.IsDeleted || head.StateVersion != command.ExpectedStateVersion || head.ReplacedBy is not null ||
                entry.Deliveries.Count != 0 || replacement.Head.ResourceId == head.ResourceId ||
                replacement.Head.PathKey != head.PathKey || replacement.Head.Path != head.Path ||
                replacement.Head.LifecycleGeneration != checked(head.LifecycleGeneration + 1) ||
                !EqualMetadata(replacement.Coordination, CoordinationState.Empty) || !replacement.Editors.IsEmpty)
                return new(false, head);
            var retiredMetadata = checked(-Encoding.UTF8.GetByteCount(head.PathKey));
            var retired = AccountedHead(entry, head with { ReplacedBy = replacement.Head.ResourceId,
                StateVersion = checked(head.StateVersion + 1) }, retiredMetadata);
            DocumentPublicationRules.CheckLimits(retired, _limits);
            // Reserve only the new resource and existing path. Other documents use neither guard.
            await replacement.Guard.WaitAsync(cancellationToken);
            try
            {
                if (!_documents.TryAdd(replacement.Head.ResourceId, replacement)) return new(false, head);
                if (!_paths.TryUpdate(head.PathKey, replacement.Head.ResourceId, head.ResourceId))
                {
                    _documents.TryRemove(new KeyValuePair<Guid, Entry>(replacement.Head.ResourceId, replacement));
                    return new(false, head);
                }
                entry.OwnedPaths.Remove(head.PathKey);
                entry.MetadataBytes = checked(entry.MetadataBytes + retiredMetadata);
                Volatile.Write(ref entry.Head, retired);
                return new(true, replacement.Head);
            }
            finally { replacement.Guard.Release(); }
        }
        finally { entry.Guard.Release(); }
    }

    public async ValueTask<DeliveryRead?> ReadNextDeliveryAsync(Guid id, CancellationToken cancellationToken = default)
    {
        if (!TryDocument(id, cancellationToken, out var entry)) return null;
        await entry.Guard.WaitAsync(cancellationToken);
        try { return NextDelivery(entry); }
        finally { entry.Guard.Release(); }
    }

    private static DeliveryRead? NextDelivery(Entry entry) => entry.Head is { IsDeleted: false, Delivery: { BlockedReason: null } binding } &&
        entry.Deliveries.Count != 0 ? new(entry.Head, binding, entry.Deliveries.Values[0]) : null;

    public ValueTask<IReadOnlyList<Guid>> ListPendingDeliveriesAsync(int limit, CancellationToken cancellationToken = default)
    {
        EnsureOnline(cancellationToken);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        IReadOnlyList<Guid> ids = _documents.Values.Select(e => Volatile.Read(ref e.Head))
            .Where(h => !h.IsDeleted && h.Counters.PendingDeliveries > 0 && h.Delivery is { BlockedReason: null })
            .OrderBy(h => h.ResourceId).Take(limit).Select(h => h.ResourceId).ToArray();
        return ValueTask.FromResult(ids);
    }

    public async ValueTask<DeliveryResult> UpdateDeliveryAsync(DocumentDeliveryCommand command,
        CancellationToken cancellationToken = default)
    {
        if (!TryDocument(command.ResourceId, cancellationToken, out var entry)) return new(false, null);
        await entry.Guard.WaitAsync(cancellationToken);
        try
        {
            var head = entry.Head;
            if (head.IsDeleted) return new(false, head);
            DocumentHead next;
            long metadataDelta = 0;
            switch (command)
            {
                case DocumentDeliveryCommand.Bind bind:
                    if (bind.Lifecycle != head.LifecycleGeneration || bind.StateVersion != head.StateVersion || head.Delivery is not null)
                        return new(false, head);
                    if (bind.Binding.BindingId == Guid.Empty || string.IsNullOrEmpty(bind.Binding.Destination) ||
                        string.IsNullOrEmpty(bind.Binding.ExpectedRevision) || bind.Binding.NextSequence != 1 || bind.Binding.BlockedReason is not null)
                        throw new ArgumentException("Invalid initial delivery binding.", nameof(command));
                    next = head with { Delivery = bind.Binding };
                    break;
                case DocumentDeliveryCommand.Retry retry:
                    if (head.Delivery is not { } retryBinding || retryBinding.BindingId != retry.BindingId ||
                        entry.Deliveries.Count == 0 || entry.Deliveries.Values[0].OperationId != retry.OperationId)
                        return new(false, head);
                    next = head with { Delivery = retryBinding with { BlockedReason = null } };
                    break;
                case DocumentDeliveryCommand.Complete complete:
                    if (!MatchesDelivery(entry, complete.Expected)) return new(false, head);
                    if (string.IsNullOrEmpty(complete.ExternalRevision)) throw new ArgumentException("An external revision is required.", nameof(command));
                    next = head with
                    {
                        Delivery = head.Delivery! with { ExpectedRevision = complete.ExternalRevision, BlockedReason = null },
                        Counters = head.Counters with
                        { PendingDeliveries = head.Counters.PendingDeliveries - 1,
                            PendingDeliveryBytes = head.Counters.PendingDeliveryBytes - complete.Expected.Revision.Package.Length },
                    };
                    metadataDelta = checked(-Bytes(complete.Expected.Revision));
                    break;
                case DocumentDeliveryCommand.Block block:
                    if (!MatchesDelivery(entry, block.Expected)) return new(false, head);
                    if (string.IsNullOrEmpty(block.Reason)) throw new ArgumentException("A blocking reason is required.", nameof(command));
                    next = head with { Delivery = head.Delivery! with { BlockedReason = block.Reason } };
                    break;
                default: throw new ArgumentOutOfRangeException(nameof(command));
            }
            next = next with { StateVersion = checked(head.StateVersion + 1) };
            next = AccountedHead(entry, next, metadataDelta);
            DocumentPublicationRules.CheckLimits(next, _limits);
            if (command is DocumentDeliveryCommand.Complete) entry.Deliveries.RemoveAt(0);
            entry.MetadataBytes = checked(entry.MetadataBytes + metadataDelta);
            Volatile.Write(ref entry.Head, next);
            return new(true, next);
        }
        finally { entry.Guard.Release(); }
    }

    private static bool MatchesDelivery(Entry entry, DeliveryRead expected) =>
        entry.Head.LifecycleGeneration == expected.Current.LifecycleGeneration && entry.Head.ResourceId == expected.Current.ResourceId &&
        entry.Head.Delivery is { } binding && binding.BindingId == expected.Binding.BindingId &&
        binding.Destination == expected.Binding.Destination && binding.ExpectedRevision == expected.Binding.ExpectedRevision &&
        binding.BlockedReason == expected.Binding.BlockedReason &&
        entry.Deliveries.Count != 0 && entry.Deliveries.Values[0] == expected.Revision;
}
