using CellBridge.Authorization.Internal;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;

namespace CellBridge.AspNetCore;

internal sealed record DocumentOperationAuthorization(string Subject, Guid ResourceId, long LifecycleGeneration,
    DocumentAuthorizationBinding? Binding, DocumentAccess Access);

public sealed partial class CellBridgeDocumentService
{
    private static IDocumentAuthorizationProvider? ValidateExternalProvider(IDocumentAuthorizationProvider? external,
        ICellBridgeAuthorizationPolicy? policy, ICellBridgeAccessEvaluator? evaluator)
    {
        if (external is null) return null;
        if (policy is not null && policy is not StoredGrantAuthorizationPolicy || evaluator is not null)
            throw new ArgumentException("Choose ordinary async authority or a versioned policy/evaluator, not competing authorities.");
        if (string.IsNullOrWhiteSpace(external.PolicyDomain) || external.PolicyDomain.Length > 256)
            throw new ArgumentException("A bounded, stable authorization domain is required.", nameof(external));
        return external;
    }

    private bool IsExternalBinding(DocumentAuthorizationBinding? binding) =>
        _externalAuthorization is not null && binding is { ContractVersion: 2, Revision: > 0 } &&
        binding.PolicyDomain == _externalAuthorization.PolicyDomain &&
        ValidScopeValue(binding.TenantId) && ValidScopeValue(binding.WorkspaceId);

    private static bool ValidScopeValue(string? value) => value is null ||
        value.Length is > 0 and <= 256 && !string.IsNullOrWhiteSpace(value) && !value.Any(char.IsControl);

    private DocumentAccess AdmittedAccess(CellBridgeActor actor, DocumentHead state)
    {
        var admission = actor.Authorization;
        return IsExternalBinding(state.Security.AuthorizationPolicy) && admission is not null &&
            admission.Subject == actor.Identity.Subject && admission.ResourceId == state.ResourceId &&
            admission.LifecycleGeneration == state.LifecycleGeneration &&
            admission.Binding == state.Security.AuthorizationPolicy ? admission.Access : DocumentAccess.None;
    }

    internal ValueTask<CellBridgeActor> AuthorizeResourceActorAsync(CellBridgeActor actor, Guid resourceId,
        DocumentAccess required, CancellationToken cancellationToken, bool fresh = false)
    {
        return _externalAuthorization is null ? ValueTask.FromResult(actor) : LoadAndAuthorize();
        async ValueTask<CellBridgeActor> LoadAndAuthorize() =>
            await AuthorizeActorAsync(actor, await CurrentAsync(resourceId, cancellationToken), required, cancellationToken, fresh);
    }

    internal ValueTask<CellBridgeActor> AuthorizeActorAsync(CellBridgeActor actor, DocumentHead state,
        DocumentAccess required, CancellationToken cancellationToken, bool fresh = false) =>
        AuthorizeReadIdentityAsync(actor, state.ResourceId, state.LifecycleGeneration, state.IsDeleted,
            state.Security.AuthorizationPolicy, required, cancellationToken, fresh);

    private async ValueTask<CellBridgeActor> AuthorizeReadIdentityAsync(CellBridgeActor actor, Guid resourceId,
        long lifecycleGeneration, bool isDeleted, DocumentAuthorizationBinding? observedBinding,
        DocumentAccess required, CancellationToken cancellationToken, bool fresh = false)
    {
        if (_externalAuthorization is null) return actor;
        // Nested helpers inherit denials too. A failed admission or changed binding cannot
        // trigger a second authority evaluation and turn the same operation into an allow.
        if (!fresh && actor.Authorization is not null)
            return actor;
        actor = actor with { Authorization = null };
        if (isDeleted || !IsExternalBinding(observedBinding) ||
            string.IsNullOrWhiteSpace(actor.Identity.Subject) || actor.Identity.Subject.Length > 1024)
            return actor with { Authorization = new(actor.Identity.Subject, resourceId, lifecycleGeneration,
                observedBinding, DocumentAccess.None) };
        var binding = observedBinding!;
        var access = DocumentAccess.None;
        // QueryAccess needs both capabilities. Ordinary write admissions imply read, as native grants do.
        if (required == DocumentAccess.None)
        {
            if (await Check(DocumentAction.Read)) access |= DocumentAccess.Read;
            if (await Check(DocumentAction.Write)) access |= DocumentAccess.Read | DocumentAccess.Write;
        }
        else if (await Check(required.HasFlag(DocumentAccess.Write) ? DocumentAction.Write : DocumentAction.Read))
            access = required.HasFlag(DocumentAccess.Write) ? DocumentAccess.Read | DocumentAccess.Write : DocumentAccess.Read;
        return actor with { Authorization = new(actor.Identity.Subject, resourceId, lifecycleGeneration, binding, access) };

        async ValueTask<bool> Check(DocumentAction action)
        {
            var start = _authorizationTime.GetUtcNow();
            try
            {
                var result = await _externalAuthorization.AuthorizeAsync(new(actor.Identity.Subject,
                    new(binding.TenantId, binding.WorkspaceId), resourceId, lifecycleGeneration, binding, action), cancellationToken);
                return result is { Allowed: true } && AuthorizationDecisionFreshness.IsValid(result.EvaluatedAtUtc,
                    result.FreshUntilUtc, start, _authorizationTime.GetUtcNow());
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception) { return false; }
        }
    }

    internal async ValueTask<DocumentHead?> RevalidateReadStateAsync(DocumentHead observed, CancellationToken cancellationToken)
    {
        return await provider.Documents.FindAsync(new DocumentIdentity.Resource(observed.ResourceId), cancellationToken);
    }

    /// <summary>Evaluates a fresh operation-time decision. Synchronous Access remains fail-closed for external actors.</summary>
    public async ValueTask<DocumentAccess> GetAccessAsync(CellBridgeActor actor, DocumentHead state,
        CancellationToken cancellationToken = default)
    {
        var admitted = await AuthorizeActorAsync(actor, state, DocumentAccess.None, cancellationToken, fresh: true);
        var current = await RevalidateReadStateAsync(state, cancellationToken);
        return current is null ? DocumentAccess.None : Access(admitted, current);
    }

    internal DocumentAccess ExecutionAccess(CellBridgeActor actor, CellExecution execution) =>
        _externalAuthorization is null ? Access(actor, execution.State) :
        execution.ResponseActor is { } admitted ? Access(admitted, execution.State) : DocumentAccess.None;

    /// <summary>Explicit trusted migration. It retires sessions and locks and fences all prepared writes.</summary>
    public async ValueTask<bool> BindExternalAuthorizationAsync(Guid resourceId, long expectedStateVersion,
        DocumentAuthorizationBinding? expectedBinding, DocumentAuthorizationScope scope,
        CancellationToken cancellationToken = default)
    {
        var next = ExternalBinding(scope, checked((expectedBinding?.Revision ?? 0) + 1));
        var current = await provider.Documents.ReadControlAsync(resourceId, cancellationToken) ?? throw MissingDocument();
        if (current.Head.StateVersion != expectedStateVersion || current.Head.Security.AuthorizationPolicy != expectedBinding)
            return false;
        var result = await provider.Documents.ExecuteControlAsync(new DocumentControlCommand.BindAuthorization(
            resourceId, expectedBinding, next, System.Collections.Immutable.ImmutableDictionary<string, DocumentAccess>.Empty) { ExpectedStateVersion = expectedStateVersion }, cancellationToken);
        return result.Changed;
    }

    private DocumentAuthorizationBinding ExternalBinding(DocumentAuthorizationScope scope, long revision = 1)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (_externalAuthorization is null) throw new InvalidOperationException("Configure an ordinary external document provider first.");
        var binding = new DocumentAuthorizationBinding(_externalAuthorization.PolicyDomain, 2, revision)
            { TenantId = scope.TenantId, WorkspaceId = scope.WorkspaceId };
        if (!IsExternalBinding(binding)) throw new ArgumentException("Scope values must contain 1 to 256 non-control characters, or be null.", nameof(scope));
        return binding;
    }

    /// <summary>Creates a resource under an explicit trusted external scope after checking Create.</summary>
    public async ValueTask<DocumentHead?> CreateExternalAsync(Guid resourceId, string escapedPath, byte[] bytes,
        DocumentAuthorizationScope scope, CellBridgeActor actor, CancellationToken cancellationToken = default)
    {
        if (resourceId == Guid.Empty) throw new ArgumentException("A resource identifier is required.", nameof(resourceId));
        ArgumentNullException.ThrowIfNull(actor);
        if (!actor.CanCreate || string.IsNullOrWhiteSpace(actor.Identity.Subject) || actor.Identity.Subject.Length > 1024)
            throw new UnauthorizedAccessException("Document creation denied.");
        var binding = ExternalBinding(scope);
        var start = _authorizationTime.GetUtcNow();
        bool allowed;
        try
        {
            var decision = await _externalAuthorization!.AuthorizeAsync(new(actor.Identity.Subject, scope,
                resourceId, 1, binding, DocumentAction.Create), cancellationToken);
            allowed = decision is { Allowed: true } && AuthorizationDecisionFreshness.IsValid(decision.EvaluatedAtUtc,
                decision.FreshUntilUtc, start, _authorizationTime.GetUtcNow());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { allowed = false; }
        if (!allowed)
            throw new UnauthorizedAccessException("Document creation denied.");
        return await CreateOwnedAsync(escapedPath, bytes, DocumentSecurity.Create(actor.Identity), cancellationToken, resourceId, binding);
    }

    /// <summary>Trusted import under a host-selected scope. CanCreate is an explicit host import capability.</summary>
    public ValueTask<DocumentHead?> ImportExternalAsync(Guid resourceId, string escapedPath, byte[] bytes,
        DocumentAuthorizationScope scope, SubjectIdentity owner, CellBridgeActor importer,
        CancellationToken cancellationToken = default)
    {
        if (resourceId == Guid.Empty) throw new ArgumentException("A resource identifier is required.", nameof(resourceId));
        ArgumentNullException.ThrowIfNull(importer);
        ArgumentNullException.ThrowIfNull(owner);
        if (!importer.CanCreate || string.IsNullOrWhiteSpace(importer.Identity.Subject) ||
            importer.Identity.Subject.Length > 1024 || string.IsNullOrWhiteSpace(owner.Subject) || owner.Subject.Length > 1024)
            throw new UnauthorizedAccessException("Document import denied.");
        return CreateOwnedAsync(escapedPath, bytes, DocumentSecurity.Create(owner, importer.Identity), cancellationToken,
            resourceId, ExternalBinding(scope));
    }
}
