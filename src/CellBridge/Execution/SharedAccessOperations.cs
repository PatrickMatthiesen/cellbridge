using CellBridge.Storage.Abstractions;

namespace CellBridge.AspNetCore;

public sealed partial class CellBridgeDocumentService
{
    /// <summary>
    /// Returns the state required by MS-SHDACCWS IsOnlyClient after current read
    /// authorization. A null result hides missing, deleted and unreadable documents.
    /// </summary>
    internal async ValueTask<bool?> IsOnlyClientAsync(Guid resourceId, CellBridgeActor actor,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var observed = await provider.Documents.ReadControlAsync(resourceId, cancellationToken);
            if (observed is null || observed.Head.IsDeleted) return null;

            var admitted = await AuthorizeActorAsync(actor, observed.Head, DocumentAccess.Read,
                cancellationToken, fresh: true);
            var current = await provider.Documents.ReadControlAsync(resourceId, cancellationToken);
            if (current is null || current.Head.IsDeleted) return null;
            if (current.Head.StateVersion != observed.Head.StateVersion ||
                current.Head.LifecycleGeneration != observed.Head.LifecycleGeneration ||
                current.Head.Security.AuthorizationPolicy != observed.Head.Security.AuthorizationPolicy)
                continue;
            if (!Access(admitted, current.Head).HasFlag(DocumentAccess.Read)) return null;

            // Expired editor/schema leases no longer represent a pending transition.
            // Use the repository's reducer so the projection follows the same clock
            // and expiry rules as publication without mutating control state here.
            var effective = DocumentControlReducer.ExpireForPublication(current).Current;
            return !effective.Coordination.CoauthorTransitionPending;
        }

        // A document that changes on every authorization retry is not safe to expose.
        return null;
    }
}
