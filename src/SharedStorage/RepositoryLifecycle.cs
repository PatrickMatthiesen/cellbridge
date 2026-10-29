using System.Text;
using CellBridge.Storage.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace CellBridge.Storage.Shared;

internal sealed partial class RelationalDocumentRepository<TContext> where TContext : DbContext
{
    public async ValueTask<PathChangeResult> ChangePathAsync(DocumentPathCommand command, CancellationToken cancellationToken = default)
    {
        try
        {
            return await MutateAsync(command.ResourceId, async (db, row, now) =>
            {
                var head = row is null || row.IsDeleted ? throw new KeyNotFoundException("Document does not exist.") : Head(row);
                var control = (await ControlAsync(db, head, cancellationToken)) with { UtcNow = now };
                if (DocumentControlReducer.ValidateWrite(control, command.Admission, command.LockAttributes) is { } error)
                    return new PathChangeResult(head, false, error);
                if (command.NewPathKey != StorageIds.PathKey(command.NewPath)) throw new ArgumentException("The filename key is not canonical.");
                if (command.NewPath == head.Path) return new(head, false);
                var renamed = head.PathKey != command.NewPathKey;
                var existingClaim = renamed ? await db.Set<RepositoryPathRow>().FindAsync([Hash(command.NewPathKey)], cancellationToken) : null;
                var newClaim = renamed && existingClaim is null;
                if (renamed && !await ClaimPathAsync(db, head.ResourceId, command.NewPathKey, true, cancellationToken))
                    return new(head, false, "FileAlreadyExists");
                var next = head with { Path = command.NewPath, PathKey = command.NewPathKey, StateVersion = checked(head.StateVersion + 1), ModifiedUtc = now,
                    Security = head.Security with { ModifiedBy = command.Admission.Actor },
                    Counters = head.Counters with { RetiredPaths = checked(head.Counters.RetiredPaths + (newClaim ? 1 : 0)),
                        StoredBytes = checked(head.Counters.StoredBytes + (newClaim ? Encoding.UTF8.GetByteCount(command.NewPathKey) : 0)) } };
                CheckCounters(next.Counters); next = SetHead(row!, next);
                return new PathChangeResult(next, true);
            }, cancellationToken);
        }
        catch (RepositoryConflictException)
        {
            var current = await FindAsync(new DocumentIdentity.Resource(command.ResourceId), cancellationToken)
                ?? throw new KeyNotFoundException("Document does not exist.");
            return new(current, false, "FileAlreadyExists");
        }
    }
    public async ValueTask<LifecycleResult> ChangeLifecycleAsync(DocumentLifecycleCommand command, CancellationToken cancellationToken = default)
    {
        if (command is DocumentLifecycleCommand.Recreate recreatePlan)
            await ValidatePayloadsAsync(recreatePlan.Replacement.Partitions, cancellationToken, initial: true);
        try
        {
            return await MutateAsync(command.ResourceId, async (db, row, now) =>
            {
                if (row is null) return new LifecycleResult(false, null);
                var head = Head(row);
                if (head.LifecycleGeneration != command.ExpectedGeneration || head.Counters.PendingDeliveries != 0) return new(false, head);
                if (command is DocumentLifecycleCommand.Delete)
                {
                    if (head.IsDeleted) return new(head.DeletedFromStateVersion == command.ExpectedStateVersion, head);
                    if (head.StateVersion != command.ExpectedStateVersion) return new(false, head);
                    var next = head with { IsDeleted = true, StateVersion = checked(head.StateVersion + 1), DeletedFromStateVersion = head.StateVersion, ModifiedUtc = now };
                    next = SetHead(row, next); return new(true, next);
                }
                var recreate = (DocumentLifecycleCommand.Recreate)command;
                if (head.ReplacedBy is { } existing) return new(existing == recreate.Replacement.Head.ResourceId,
                    await HeadAsync(db, existing, cancellationToken));
                if (!head.IsDeleted || head.StateVersion != command.ExpectedStateVersion) return new(false, head);
                var replacement = recreate.Replacement.Head;
                if (replacement.ResourceId == head.ResourceId || replacement.PathKey != head.PathKey || replacement.LifecycleGeneration != checked(head.LifecycleGeneration + 1))
                    return new(false, head);
                if (await HeadRowAsync(db, replacement.ResourceId, cancellationToken) is not null) return new(false, head);
                var claim = await db.Set<RepositoryPathRow>().FindAsync([Hash(head.PathKey)], cancellationToken)
                    ?? throw new StorageCorruptionException("The retired document's filename claim is absent.");
                if (claim.ResourceId != head.ResourceId || claim.PathKey != head.PathKey) return new(false, head);
                claim.ResourceId = replacement.ResourceId;
                var created = await CreateRowsAsync(db, recreate.Replacement, now, cancellationToken);
                SetHead(row, head with { ReplacedBy = created.ResourceId, StateVersion = checked(head.StateVersion + 1),
                    Counters = head.Counters with { StoredBytes = checked(head.Counters.StoredBytes - Encoding.UTF8.GetByteCount(head.PathKey)) } });
                return new LifecycleResult(true, created);
            }, cancellationToken);
        }
        catch (RepositoryConflictException)
        { return new(false, await FindAsync(new DocumentIdentity.Resource(command.ResourceId, true), cancellationToken)); }
    }
    private static async Task<DeliveryRead?> NextDeliveryAsync(TContext db, DocumentHead head, CancellationToken ct)
    {
        if (head.IsDeleted || head.Delivery is not { BlockedReason: null } binding || head.Counters.PendingDeliveries == 0) return null;
        var json = await db.Set<RepositoryDeliveryRow>().AsNoTracking().Where(row => row.ResourceId == head.ResourceId)
            .OrderBy(row => row.Sequence).Select(row => row.Json).FirstOrDefaultAsync(ct)
            ?? throw new StorageCorruptionException("The delivery counter has no corresponding outbox record.");
        var delivery = Decode<DocumentDelivery>(json);
        if (delivery.ResourceId != head.ResourceId || delivery.LifecycleGeneration != head.LifecycleGeneration)
            throw new StorageCorruptionException("An outbox entry belongs to another document incarnation.");
        return new(head, binding, delivery);
    }
    public ValueTask<DeliveryRead?> ReadNextDeliveryAsync(Guid id, CancellationToken cancellationToken = default) =>
        ReadAsync(async db => await HeadAsync(db, id, cancellationToken) is { } head ? await NextDeliveryAsync(db, head, cancellationToken) : null, cancellationToken);
    public ValueTask<IReadOnlyList<Guid>> ListPendingDeliveriesAsync(int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        return ReadAsync<IReadOnlyList<Guid>>(async db => await db.Set<RepositoryHeadRow>().AsNoTracking().Where(row => !row.IsDeleted && !row.DeliveryBlocked && row.PendingDeliveries > 0)
            .OrderBy(row => row.ResourceId).Take(limit).Select(row => row.ResourceId).ToArrayAsync(cancellationToken), cancellationToken);
    }
    public ValueTask<DeliveryResult> UpdateDeliveryAsync(DocumentDeliveryCommand command, CancellationToken cancellationToken = default) =>
        MutateAsync(command.ResourceId, async (db, row, now) =>
        {
            if (row is null || row.IsDeleted) return new DeliveryResult(false, null);
            var head = Head(row);
            if (command is DocumentDeliveryCommand.Bind bind)
            {
                if (head.LifecycleGeneration != bind.Lifecycle || head.StateVersion != bind.StateVersion) return new(false, head);
                if (head.Delivery == bind.Binding) return new(true, head);
                if (head.Delivery is not null || head.Counters.PendingDeliveries != 0 || bind.Binding.BindingId == Guid.Empty ||
                    string.IsNullOrWhiteSpace(bind.Binding.Destination) || string.IsNullOrWhiteSpace(bind.Binding.ExpectedRevision) || bind.Binding.NextSequence <= 0)
                    return new(false, head);
                var next = head with { Delivery = bind.Binding, StateVersion = checked(head.StateVersion + 1) };
                next = SetHead(row, next); return new(true, next);
            }
            if (head.Delivery is not { } binding) return new(false, head);
            if (command is DocumentDeliveryCommand.Retry retry)
            {
                if (binding.BindingId != retry.BindingId || !await db.Set<RepositoryDeliveryRow>().AnyAsync(entry => entry.ResourceId == head.ResourceId &&
                    entry.OperationId == retry.OperationId, cancellationToken)) return new(false, head);
                var next = head with { Delivery = binding with { BlockedReason = null }, StateVersion = checked(head.StateVersion + 1) };
                next = SetHead(row, next); return new(true, next);
            }
            var expected = command is DocumentDeliveryCommand.Complete complete ? complete.Expected : ((DocumentDeliveryCommand.Block)command).Expected;
            var first = await NextDeliveryAsync(db, head, cancellationToken);
            if (first is null || first.Revision != expected.Revision || binding.BindingId != expected.Binding.BindingId ||
                binding.Destination != expected.Binding.Destination || binding.ExpectedRevision != expected.Binding.ExpectedRevision)
                return new(false, head);
            if (command is DocumentDeliveryCommand.Block block)
            {
                var next = head with { Delivery = binding with { BlockedReason = block.Reason }, StateVersion = checked(head.StateVersion + 1) };
                next = SetHead(row, next); return new(true, next);
            }
            var accepted = (DocumentDeliveryCommand.Complete)command;
            if (string.IsNullOrWhiteSpace(accepted.ExternalRevision)) throw new ArgumentException("A destination revision is required.");
            var deliveryRow = await db.Set<RepositoryDeliveryRow>().FindAsync([head.ResourceId, first.Revision.OperationId], cancellationToken)
                ?? throw new StorageCorruptionException("The accepted delivery disappeared while guarded.");
            db.Set<RepositoryDeliveryRow>().Remove(deliveryRow);
            var completed = head with { Delivery = binding with { ExpectedRevision = accepted.ExternalRevision, BlockedReason = null },
                StateVersion = checked(head.StateVersion + 1), Counters = head.Counters with
                { PendingDeliveries = checked(head.Counters.PendingDeliveries - 1), PendingDeliveryBytes = checked(head.Counters.PendingDeliveryBytes - first.Revision.Package.Length),
                  StoredBytes = checked(head.Counters.StoredBytes - Encoding.UTF8.GetByteCount(deliveryRow.Json)) } };
            CheckCounters(completed.Counters); completed = SetHead(row, completed); return new(true, completed);
        }, cancellationToken);
}
