using CellBridge.Storage.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace CellBridge.Storage.Shared;

internal sealed partial class DocumentOperations<TContext> where TContext : DbContext
{
    internal async ValueTask<T> TransitionWithContentAsync<T>(Guid id, DocumentContent<TContext> content,
        IReadOnlyList<ReadOnlyMemory<byte>> pending, Func<DocumentState, DateTime, StateTransition<T>> transition,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transition);
        if (!ReferenceEquals(content.BudgetScope, BudgetScope))
            throw new ArgumentException("Atomic saves require a shared content and state budget scope.", nameof(content));
        await content.EnterWriterAsync(cancellationToken);
        try
        {
            // Hashing and input admission precede database coordination. Package
            // materialization and validation remain the caller's responsibility.
            var prepared = content.PrepareWriteBatch(pending, cancellationToken);
            return await MutateAsync(async db =>
            {
                var added = await content.WriteManyInTransactionAsync(db, prepared, cancellationToken);
                await database.Locks.DocumentAsync(db, id, cancellationToken);
                var pointer = await db.Set<DocumentRow>().SingleOrDefaultAsync(row => row.ResourceId == id, cancellationToken)
                    ?? throw new KeyNotFoundException("Document does not exist.");
                var current = await LoadCurrentAsync(db, pointer, cancellationToken);
                if (current.IsDeleted) throw new KeyNotFoundException("Document is deleted.");
                var now = await ReadClockAsync(db, cancellationToken);
                var change = transition(current, now);
                long snapshotBytes = 0;
                if (change.Next is { } proposed)
                {
                    DocumentPathReservations.ValidateTransition(current, proposed, rename: false);
                    var version = checked(current.StateVersion + 1);
                    var retiredPaths = DocumentPathReservations.Capture(current, proposed, rename: false);
                    var next = proposed.StateVersion == version && proposed.RetiredPathKeys == retiredPaths
                        ? proposed : proposed with { StateVersion = version, RetiredPathKeys = retiredPaths };
                    limits.CheckDocument(next);
                    await WriteSelectiveReadsAsync(db, next, cancellationToken);
                    snapshotBytes = await StageSnapshotAsync(db, next, prune: true, cancellationToken);
                    UpdatePointer(pointer, next);
                }
                if (change.Next is not null || added.InsertedAny)
                {
                    // Flush state and projections while they are still private
                    // to this transaction, before serializing quota admission.
                    await db.SaveChangesAsync(cancellationToken);
                    // Spend this document's durable allowance or refill under
                    // the global lock. Signed and near-limit admission preserves
                    // snapshot-before-content checks. Rejection rolls back
                    // accounting and every earlier state write together.
                    await UsageAllowances.AdmitAsync(database, db, id, snapshotBytes, added.Bytes,
                        cancellationToken);
                }
                // Cancellation before COMMIT still has a known rollback,
                // including a callback that deliberately cancelled its request.
                cancellationToken.ThrowIfCancellationRequested();
                return change.Result;
            }, cancellationToken);
        }
        finally { content.ExitWriter(); }
    }
}
