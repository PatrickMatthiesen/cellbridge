using CellBridge.Storage;
using CellBridge.Storage.Abstractions;

namespace CellBridge.AspNetCore;

public sealed partial class CellBridgeDocumentService
{
    private static DocumentAccess NormalizeAccess(DocumentAccess access) =>
        (access & ~(DocumentAccess.Read | DocumentAccess.Write)) != 0 ? DocumentAccess.None
        : access.HasFlag(DocumentAccess.Write) ? access | DocumentAccess.Read : access;

    private bool ValidBinding(DocumentAuthorizationBinding? binding) =>
        _policy is StoredGrantAuthorizationPolicy ? binding is null
        : binding is { ContractVersion: 1, Revision: > 0 } &&
            !string.IsNullOrWhiteSpace(binding.PolicyDomain) &&
            string.Equals(binding.PolicyDomain, _policy.PolicyDomain, StringComparison.Ordinal);

    private ICellBridgeAuthorizationSnapshot? ResolvePolicy(DocumentState state)
    {
        if (!ValidBinding(state.Security.AuthorizationPolicy)) return null;
        var snapshot = _policy.Resolve(state);
        return snapshot?.ResourceId == state.ResourceId && snapshot.Binding == state.Security.AuthorizationPolicy
            ? snapshot : null;
    }

    private DocumentAccess SubjectAccess(CellBridgeActor actor, DocumentState state)
    {
        if (state.IsDeleted || string.IsNullOrWhiteSpace(actor.Identity.Subject)) return DocumentAccess.None;
        try
        {
            var snapshot = ResolvePolicy(state);
            if (snapshot is null) return DocumentAccess.None;
            // Preserve the beta evaluator only for unbound documents under the default policy.
            if (_policy is StoredGrantAuthorizationPolicy && _access is not null)
                return NormalizeAccess(_access.Evaluate(actor, state));
            return EvaluateBoundAccess(snapshot, actor.Identity.Subject, state);
        }
        catch (Exception)
        {
            // Authorization unavailability/undefined decisions must never fall back to grants.
            return DocumentAccess.None;
        }
    }

    private DocumentAccess EvaluateBoundAccess(ICellBridgeAuthorizationSnapshot snapshot, string subject, DocumentState state)
    {
        var access = NormalizeAccess(snapshot.Evaluate(subject));
        // External ceilings receive only the stable subject, including when reconciling lease owners.
        return _access is null ? access : access & NormalizeAccess(_access.Evaluate(new(new(subject, subject, subject)), state));
    }

    private DocumentState BindNewDocument(DocumentState state)
    {
        try
        {
            var binding = _policy.BindNewDocument(state.ResourceId);
            state = state with { Security = state.Security with { AuthorizationPolicy = binding } };
            if (ResolvePolicy(state) is null) throw new UnauthorizedAccessException();
            return state;
        }
        catch (Exception ex)
        {
            throw new UnauthorizedAccessException("Document authorization could not be initialized.", ex);
        }
    }

    /// <summary>
    /// Trusted host operation, not a document-writer privilege. Prepare the next immutable snapshot first.
    /// Successful CAS is the permission-change boundary; it reconciles recorded subjects and fences prepared writes.
    /// Cannot remove or migrate a binding to another policy domain.
    /// </summary>
    public async ValueTask<bool> UpdateAuthorizationAsync(Guid resourceId, DocumentAuthorizationBinding? expected,
        DocumentAuthorizationBinding next, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(next);
        if (!ValidBinding(next) || expected is not null &&
            (expected.PolicyDomain != next.PolicyDomain || expected.ContractVersion != next.ContractVersion || next.Revision <= expected.Revision))
            throw new ArgumentException("A supported binding with an increasing revision in the same policy domain is required.", nameof(next));
        // Resolve outside coordination too, so an unavailable snapshot cannot be mistaken for a successful update.
        var before = await CurrentAsync(resourceId, cancellationToken);
        _ = RequireNextSnapshot(before);
        return await provider.State.TransitionAsync(resourceId, (current, now) =>
        {
            if (current.IsDeleted || current.Security.AuthorizationPolicy != expected) return new StateTransition<bool>(null, false);
            var snapshot = RequireNextSnapshot(current);
            var security = current.Security with { AuthorizationPolicy = next };
            var nextState = current with { Security = security };
            // A throwing decision aborts the entire update. Missing subjects are explicit None decisions.
            var updated = DocumentPermissionUpdates.ApplyPolicy(current, now, security,
                subject => EvaluateBoundAccess(snapshot, subject, nextState));
            provider.Limits.CheckDocument(updated);
            return new StateTransition<bool>(updated, true);
        }, cancellationToken);

        ICellBridgeAuthorizationSnapshot RequireNextSnapshot(DocumentState current)
        {
            try
            {
                return ResolvePolicy(current with { Security = current.Security with { AuthorizationPolicy = next } })
                    ?? throw new UnauthorizedAccessException();
            }
            catch (Exception ex)
            {
                throw new UnauthorizedAccessException("The next authorization revision is unavailable.", ex);
            }
        }
    }
}
