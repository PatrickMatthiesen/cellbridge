using System.Collections.Concurrent;
using System.Collections.Immutable;
using CellBridge.AspNetCore;
using CellBridge.Storage.Abstractions;

namespace CellBridge.DocumentLibrary;

public sealed class DocumentLibraryPermissionPolicy(DocumentLibraryDestination destination)
    : ICellBridgeAuthorizationPolicy
{
    private readonly ConcurrentDictionary<(Guid ResourceId, long Revision), Snapshot> _snapshots = new();
    private readonly ConcurrentDictionary<Guid, long> _desired = new();

    public string PolicyDomain => "cellbridge:document-library-permissions";

    public DocumentAuthorizationBinding Binding(long revision) => new(PolicyDomain, 1, revision);

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        foreach (var manifest in await destination.ListAsync(cancellationToken)) Install(manifest);
    }

    public void Install(LibraryManifest manifest)
    {
        foreach (var permission in manifest.PermissionSnapshots.Values)
        {
            var decisions = permission.Decisions.ToImmutableDictionary(x => x.Key,
                x => Normalize(x.Value), StringComparer.Ordinal);
            var snapshot = new Snapshot(manifest.ResourceId, Binding(permission.Revision), decisions);
            var key = (manifest.ResourceId, permission.Revision);
            if (!_snapshots.TryAdd(key, snapshot))
            {
                var installed = _snapshots[key];
                if (installed.ResourceId != snapshot.ResourceId || installed.Binding != snapshot.Binding ||
                    !installed.Decisions.OrderBy(x => x.Key, StringComparer.Ordinal)
                        .SequenceEqual(snapshot.Decisions.OrderBy(x => x.Key, StringComparer.Ordinal)))
                    throw new InvalidOperationException("A permission revision cannot change after it is installed.");
            }
        }
        _desired[manifest.ResourceId] = manifest.DesiredPermissionRevision;
    }

    public DocumentAuthorizationBinding? BindNewDocument(Guid resourceId) =>
        _desired.TryGetValue(resourceId, out var revision) && _snapshots.ContainsKey((resourceId, revision))
            ? Binding(revision)
            : null;

    public ICellBridgeAuthorizationSnapshot? Resolve(DocumentState state) =>
        state.Security.AuthorizationPolicy is { Revision: var revision } binding &&
        string.Equals(binding.PolicyDomain, PolicyDomain, StringComparison.Ordinal) &&
        binding.ContractVersion == 1
            ? _snapshots.GetValueOrDefault((state.ResourceId, revision))
            : null;

    private static DocumentAccess Normalize(DocumentAccess access)
    {
        if ((access & ~(DocumentAccess.Read | DocumentAccess.Write)) != 0) return DocumentAccess.None;
        return access.HasFlag(DocumentAccess.Write) ? access | DocumentAccess.Read : access;
    }

    private sealed record Snapshot(Guid ResourceId, DocumentAuthorizationBinding Binding,
        ImmutableDictionary<string, DocumentAccess> Decisions) : ICellBridgeAuthorizationSnapshot
    {
        public DocumentAccess Evaluate(string subject) => Decisions.GetValueOrDefault(subject);
    }
}
