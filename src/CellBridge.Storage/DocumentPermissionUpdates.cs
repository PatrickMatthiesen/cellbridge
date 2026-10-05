using System.Collections.Immutable;
using CellBridge.Storage.Abstractions;

namespace CellBridge.Storage;

/// <summary>Used by trusted operator tooling inside the document transaction.</summary>
public static class DocumentPermissionUpdates
{
    public static DocumentState Apply(DocumentState current, DateTime now, DocumentSecurity security)
    {
        if (current.Security.AuthorizationPolicy is not null || security.AuthorizationPolicy is not null)
            throw new InvalidOperationException("Bound documents require a host policy update.");
        return ApplyPolicy(current, now, security, security.AccessFor);
    }

    /// <summary>Trusted coordinated update. Subject evaluation must be bounded, immutable and perform no I/O.</summary>
    public static DocumentState ApplyPolicy(DocumentState current, DateTime now, DocumentSecurity security,
        Func<string, DocumentAccess> accessFor)
    {
        var document = StoredDocument.RestoreMetadata(current, now);
        foreach (var editor in current.Editors)
        {
            var access = editor.Owner is null ? DocumentAccess.None : accessFor(editor.Owner.Subject);
            if (!access.HasFlag(DocumentAccess.Read)) document.LeaveSession(editor.ClientId);
            else if (editor.AsEditor && !access.HasFlag(DocumentAccess.Write))
                document.SetEditorPermission(editor.ClientId, false);
        }
        // Revocation removes leases in the same transaction; it never extends them.
        var coordination = current.Coordination with
        {
            SchemaOwners = current.Coordination.SchemaOwners.Where(l => l.OwnerSubject is not null &&
                accessFor(l.OwnerSubject).HasFlag(DocumentAccess.Write)).ToImmutableArray(),
            Exclusive = current.Coordination.Exclusive is { OwnerSubject: { } subject } exclusive &&
                accessFor(subject).HasFlag(DocumentAccess.Write) ? exclusive : null,
            HostLock = current.Coordination.HostLock is { } host &&
                accessFor(host.OwnerSubject).HasFlag(DocumentAccess.Write) ? host : null,
            Generation = checked(current.Coordination.Generation + 1),
        };
        coordination = coordination with
        {
            CoauthorClients = (coordination.CoauthorClients.IsDefault ? [] : coordination.CoauthorClients)
                .Where(client => coordination.SchemaOwners.Any(lease => SameClient(lease.Client, client)))
                .ToImmutableArray(),
        };
        if (coordination.CoauthorClients.IsEmpty)
            coordination = coordination with { CoauthorTransitionPending = false };
        if (coordination.SchemaOwners.IsEmpty) coordination = coordination with { SchemaId = null };
        return document.CaptureCoordination(current, coordination) with { Security = security };
    }

    private static bool SameClient(string? left, string right) =>
        Guid.TryParse(left, out var leftId) && Guid.TryParse(right, out var rightId)
            ? leftId == rightId : string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
