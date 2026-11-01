using CellBridge.Storage.Abstractions;

namespace CellBridge.AspNetCore;

/// <summary>Outcome of a permission-checked document deletion.</summary>
public enum DocumentDeletionStatus { Deleted, NotFound, Conflict }

public sealed partial class CellBridgeDocumentService
{
    /// <summary>
    /// Tombstones the exact document incarnation after fresh write authorization. Only its owner may
    /// delete a document. The expected versions prevent stale callers from deleting a changed file.
    /// </summary>
    public async ValueTask<DocumentDeletionStatus> DeleteDocumentAsync(Guid resourceId,
        long expectedLifecycleGeneration, long expectedStateVersion, CellBridgeActor actor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (resourceId == Guid.Empty || expectedLifecycleGeneration <= 0 || expectedStateVersion < 0)
            throw new ArgumentException("A resource identifier and valid lifecycle and state versions are required.");

        var observed = await provider.Documents.ReadControlAsync(resourceId, cancellationToken);
        if (observed is null || observed.Head.IsDeleted) return DocumentDeletionStatus.NotFound;
        var admitted = await AuthorizeActorAsync(actor, observed.Head, DocumentAccess.Write,
            cancellationToken, fresh: true);
        EnsureOwnerWriteAccess(admitted, observed.Head);

        // An asynchronous host policy may take time. Re-read all of the authorization and editor
        // generations before mutating, and make the storage command compare-and-swap them as well.
        var current = await provider.Documents.ReadControlAsync(resourceId, cancellationToken);
        if (current is null || current.Head.IsDeleted) return DocumentDeletionStatus.NotFound;

        // Re-evaluate the current head after the asynchronous authority call. Check current access
        // before reporting any stale-version conflict, so permission changes cannot expose versions.
        admitted = await AuthorizeActorAsync(actor, current.Head, DocumentAccess.Write,
            cancellationToken, fresh: true);
        EnsureOwnerWriteAccess(admitted, current.Head);

        if (current.Head.LifecycleGeneration != expectedLifecycleGeneration ||
            current.Head.StateVersion != expectedStateVersion ||
            current.Head.AuthorityGeneration != observed.Head.AuthorityGeneration ||
            current.Coordination.Generation != observed.Coordination.Generation)
            return DocumentDeletionStatus.Conflict;

        if (HasActiveEditorsOrLocks(current)) return DocumentDeletionStatus.Conflict;

        var result = await provider.Documents.ChangeLifecycleAsync(new DocumentLifecycleCommand.Delete(
            resourceId, expectedLifecycleGeneration, expectedStateVersion)
        {
            ExpectedAuthorityGeneration = current.Head.AuthorityGeneration,
            ExpectedCoordinationGeneration = current.Coordination.Generation,
        }, cancellationToken);
        if (result.Applied) return DocumentDeletionStatus.Deleted;
        return result.Current is null || result.Current.IsDeleted
            ? DocumentDeletionStatus.NotFound
            : DocumentDeletionStatus.Conflict;
    }

    private void EnsureOwnerWriteAccess(CellBridgeActor actor, DocumentHead state)
    {
        if (!string.Equals(state.Security.Owner, actor.Identity.Subject, StringComparison.Ordinal) ||
            !Access(actor, state).HasFlag(DocumentAccess.Write))
            throw new UnauthorizedAccessException("Only the document owner with current write access can delete it.");
    }

    private static bool HasActiveEditorsOrLocks(DocumentControlRead current)
    {
        var now = current.UtcNow;
        var coordination = current.Coordination;
        return current.Editors.Any(editor => editor.ExpiresUtc > now) ||
               coordination.SchemaOwners.Any(owner => owner.ExpiresUtc > now) ||
               coordination.Exclusive is { } exclusive && exclusive.ExpiresUtc > now ||
               coordination.HostLock is { } hostLock && hostLock.ExpiresUtc > now;
    }
}
