using System.Collections.Concurrent;
using System.Collections.Immutable;
using CellBridge.AspNetCore;
using CellBridge.Storage.Abstractions;

// Application permission state lives outside CellBridge document state. This
// volatile example uses immutable local snapshots in place of a production host's
// permission database/cache. No CellBridge ownership or Grants are consulted.
public sealed class HostPermissionService : ICellBridgeAuthorizationPolicy
{
    private readonly ConcurrentDictionary<(Guid, DocumentAuthorizationBinding), Snapshot> _snapshots = new();
    public string PolicyDomain => "example:host-permissions";
    public DocumentAuthorizationBinding Binding(long revision) => new(PolicyDomain, 1, revision);

    public DocumentAuthorizationBinding BindNewDocument(Guid resourceId)
    {
        Install(resourceId, 1, DocumentAccess.Write);
        return Binding(1);
    }

    public void Install(Guid resourceId, long revision, DocumentAccess writerAccess)
    {
        if (revision <= 0 || (writerAccess & ~(DocumentAccess.Read | DocumentAccess.Write)) != 0)
            throw new ArgumentException("A positive revision and defined access are required.");
        var snapshot = new Snapshot(resourceId, Binding(revision), ImmutableDictionary<string, DocumentAccess>.Empty
            .Add("example:writer", writerAccess).Add("example:reader", DocumentAccess.Read));
        if (!_snapshots.TryAdd((resourceId, snapshot.Binding), snapshot) &&
            !_snapshots[(resourceId, snapshot.Binding)].Decisions.OrderBy(x => x.Key).SequenceEqual(snapshot.Decisions.OrderBy(x => x.Key)))
            throw new InvalidOperationException("An installed revision cannot change.");
    }

    public ICellBridgeAuthorizationSnapshot? Resolve(DocumentState state) =>
        state.Security.AuthorizationPolicy is { } binding ? _snapshots.GetValueOrDefault((state.ResourceId, binding)) : null;

    private sealed record Snapshot(Guid ResourceId, DocumentAuthorizationBinding Binding,
        ImmutableDictionary<string, DocumentAccess> Decisions) : ICellBridgeAuthorizationSnapshot
    {
        public DocumentAccess Evaluate(string subject) => Decisions.GetValueOrDefault(subject);
    }
}
