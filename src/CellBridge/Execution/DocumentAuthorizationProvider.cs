using CellBridge.Storage.Abstractions;

namespace CellBridge.AspNetCore;

/// <summary>Identifies the tenant and workspace that contain a document.</summary>
public sealed record DocumentAuthorizationScope(string? TenantId, string? WorkspaceId);

/// <summary>Identifies the document operation that requires authorization.</summary>
public enum DocumentAction
{
    Read,
    Write,
    Create
}

/// <summary>
/// Describes one authorization check. The lifecycle generation and policy binding prevent a
/// decision for an older document or policy revision from authorizing a newer one.
/// </summary>
public sealed record DocumentAuthorizationRequest(
    string Subject,
    DocumentAuthorizationScope Scope,
    Guid ResourceId,
    long LifecycleGeneration,
    DocumentAuthorizationBinding Binding,
    DocumentAction Action);

/// <summary>
/// Records an authority's decision and optional source freshness metadata. <paramref name="EvaluatedAtUtc"/>
/// is the authority's original evaluation time. <paramref name="FreshUntilUtc"/> is its exclusive
/// freshness deadline. Consumers reject future evaluation times with zero clock-skew tolerance,
/// inconsistent deadlines, and decisions that expire before admission. When the authority omits
/// both values, the consumer treats the decision as fresh at the start of the provider call.
/// </summary>
public sealed record DocumentAuthorizationDecision(
    bool Allowed,
    DateTimeOffset? EvaluatedAtUtc = null,
    DateTimeOffset? FreshUntilUtc = null);

/// <summary>Asynchronously decides whether a subject may perform a document operation.</summary>
public interface IDocumentAuthorizationProvider
{
    /// <summary>Gets the stable domain that owns the policy bindings evaluated by this provider.</summary>
    string PolicyDomain { get; }

    /// <summary>Evaluates one request without granting access when the authority is unavailable.</summary>
    ValueTask<DocumentAuthorizationDecision> AuthorizeAsync(
        DocumentAuthorizationRequest request,
        CancellationToken cancellationToken = default);
}
