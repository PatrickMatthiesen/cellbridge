using CellBridge.Storage;
using CellBridge.Storage.Abstractions;

namespace CellBridge.AspNetCore;

internal sealed record OfficeRealTimeAdmission(CellBridgeActor Actor, DocumentControlRead Control);
internal sealed record OfficeRealTimeAccessCheck(CellBridgeActor Actor, Guid? ClientId,
    DocumentAccess RequiredAccess, bool RequireOwnedClient);

internal sealed class OfficeRealTimeAdmissionBusyException()
    : InvalidOperationException("Office collaboration authorization could not obtain a stable document state.");

public sealed partial class CellBridgeDocumentService
{
    internal bool SupportsOfficeRealTimeSnapshots => _externalAuthorization is null && _access is null;

    internal async ValueTask<bool[]> AuthorizeOfficeRealTimeTogetherAsync(Guid resourceId, long generation,
        IReadOnlyList<OfficeRealTimeAccessCheck> checks, CancellationToken cancellationToken)
    {
        if (!SupportsOfficeRealTimeSnapshots)
            throw new NotSupportedException("Office RTC requires stored grants or an immutable versioned authorization policy without a legacy access evaluator.");
        if (checks.Count is < 1 or > 33) throw new ArgumentOutOfRangeException(nameof(checks));
        bool[] denied = new bool[checks.Count];
        if (resourceId == Guid.Empty || generation <= 0) return denied;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            var observed = await provider.Documents.ReadControlAsync(resourceId, cancellationToken).ConfigureAwait(false);
            if (observed is null || observed.Head.IsDeleted || observed.Head.LifecycleGeneration != generation) return denied;
            bool[] allowed = new bool[checks.Count];
            try
            {
                var snapshot = ResolvePolicy(observed.Head);
                if (snapshot is null) return denied;
                for (int i = 0; i < checks.Count; i++)
                {
                    var check = checks[i];
                    if (string.IsNullOrWhiteSpace(check.Actor.Identity.Subject) ||
                        check.RequiredAccess is not (DocumentAccess.Read or DocumentAccess.Write) ||
                        check.Actor.AccessLimit is { } limit && limit.ResourceId != resourceId) continue;
                    var access = NormalizeAccess(snapshot.Evaluate(check.Actor.Identity.Subject));
                    if (check.Actor.AccessLimit is { } ceiling) access &= ceiling.Access;
                    allowed[i] = access.HasFlag(check.RequiredAccess);
                }
            }
            catch (Exception) { return denied; }

            var current = await provider.Documents.ReadControlAsync(resourceId, cancellationToken).ConfigureAwait(false);
            if (current is null || current.Head.IsDeleted || current.Head.LifecycleGeneration != generation) return denied;
            if (!SameAuthorizationState(observed.Head, current.Head)) continue;
            var active = DocumentControlReducer.ExpireForPublication(current).Current;
            for (int i = 0; i < checks.Count; i++)
            {
                var check = checks[i];
                if (allowed[i] && (check.RequireOwnedClient || check.RequiredAccess == DocumentAccess.Write))
                    allowed[i] = check.ClientId is { } client && client != Guid.Empty &&
                        OwnsActiveClient(active, check.Actor.Identity.Subject, client,
                            check.RequiredAccess == DocumentAccess.Write);
            }
            return allowed;
        }
        throw new OfficeRealTimeAdmissionBusyException();
    }

    internal async ValueTask<OfficeRealTimeAdmission?> AuthorizeOfficeRealTimeAsync(Guid resourceId,
        long? expectedLifecycleGeneration, CellBridgeActor actor, Guid? clientId, DocumentAccess requiredAccess,
        bool requireOwnedClient, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var mustOwnClient = requireOwnedClient || requiredAccess == DocumentAccess.Write;
        if (resourceId == Guid.Empty || string.IsNullOrWhiteSpace(actor.Identity.Subject) ||
            (requiredAccess != DocumentAccess.Read && requiredAccess != DocumentAccess.Write) ||
            mustOwnClient && (clientId is null || clientId == Guid.Empty))
            return null;

        for (var attempt = 0; attempt < 4; attempt++)
        {
            var observed = await provider.Documents.ReadControlAsync(resourceId, cancellationToken).ConfigureAwait(false);
            if (observed is null || observed.Head.IsDeleted ||
                expectedLifecycleGeneration is { } generation && observed.Head.LifecycleGeneration != generation)
                return null;

            var authorized = await AuthorizeActorAsync(actor, observed.Head, requiredAccess, cancellationToken,
                fresh: true).ConfigureAwait(false);

            var current = await provider.Documents.ReadControlAsync(resourceId, cancellationToken).ConfigureAwait(false);
            if (current is null || current.Head.IsDeleted ||
                expectedLifecycleGeneration is { } expected && current.Head.LifecycleGeneration != expected)
                return null;
            if (!SameAuthorizationState(observed.Head, current.Head)) continue;

            if (!Access(authorized, current.Head).HasFlag(requiredAccess)) return null;

            var active = DocumentControlReducer.ExpireForPublication(current).Current;
            if (mustOwnClient && !OwnsActiveClient(active, actor.Identity.Subject, clientId!.Value,
                    requiredAccess == DocumentAccess.Write))
                return null;

            return new(authorized, active);
        }

        throw new OfficeRealTimeAdmissionBusyException();
    }

    private static bool SameAuthorizationState(DocumentHead left, DocumentHead right) =>
        left.ResourceId == right.ResourceId && left.StateVersion == right.StateVersion &&
        left.LifecycleGeneration == right.LifecycleGeneration && left.IsDeleted == right.IsDeleted &&
        left.AuthorityGeneration == right.AuthorityGeneration &&
        left.Security.AuthorizationPolicy == right.Security.AuthorizationPolicy;

    private static bool OwnsActiveClient(DocumentControlRead current, string subject, Guid clientId,
        bool requireLease)
    {
        bool OwnedLease(LeaseState lease) => lease.ExpiresUtc > current.UtcNow &&
            lease.OwnerSubject == subject && Guid.TryParse(lease.Client, out var leaseClient) &&
            leaseClient == clientId;

        if (current.Coordination.SchemaOwners.Any(OwnedLease) ||
            current.Coordination.Exclusive is { } exclusive && OwnedLease(exclusive))
            return true;
        return !requireLease && current.Editors.Any(editor => editor.ClientId == clientId &&
            editor.ExpiresUtc > current.UtcNow && editor.Owner?.Subject == subject);
    }
}
