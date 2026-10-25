using CellBridge.Storage.Abstractions;

namespace CellBridge.AspNetCore;

/// <summary>
/// Host-owned authority. All members are bounded, synchronous and perform no I/O.
/// External hosts use a non-null domain and immutable snapshots keyed by resource and complete binding.
/// Populate snapshots outside document transactions. A missing snapshot fails closed.
/// </summary>
public interface ICellBridgeAuthorizationPolicy
{
    string? PolicyDomain { get; }
    DocumentAuthorizationBinding? BindNewDocument(Guid resourceId);
    ICellBridgeAuthorizationSnapshot? Resolve(DocumentState state);
}

/// <summary>
/// Immutable permission decisions for one resource/revision. Undefined subjects return None.
/// Never use ambient claims, client IDs, login names or display names as authority.
/// </summary>
public interface ICellBridgeAuthorizationSnapshot
{
    Guid ResourceId { get; }
    DocumentAuthorizationBinding? Binding { get; }
    DocumentAccess Evaluate(string subject);
}

/// <summary>Default authority for unbound documents, using persisted ownership and grants.</summary>
public sealed class StoredGrantAuthorizationPolicy : ICellBridgeAuthorizationPolicy
{
    public string? PolicyDomain => null;
    public DocumentAuthorizationBinding? BindNewDocument(Guid resourceId) => null;
    public ICellBridgeAuthorizationSnapshot? Resolve(DocumentState state) =>
        state.Security.AuthorizationPolicy is null ? new Snapshot(state.ResourceId, state.Security) : null;

    private sealed record Snapshot(Guid ResourceId, DocumentSecurity Security) : ICellBridgeAuthorizationSnapshot
    {
        public DocumentAuthorizationBinding? Binding => null;
        public DocumentAccess Evaluate(string subject) => Security.AccessFor(subject);
    }
}
