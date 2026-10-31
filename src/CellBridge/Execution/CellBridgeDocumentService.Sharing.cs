using System.Collections.Immutable;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;

namespace CellBridge.AspNetCore;

/// <summary>Current stored grants visible to the document owner.</summary>
public sealed record DocumentSharingSnapshot(Guid ResourceId, long LifecycleGeneration,
    long AuthorityGeneration, string OwnerSubject, ImmutableDictionary<string, DocumentAccess> Grants);

public enum DocumentSharingUpdateStatus { Applied, Unchanged, Conflict, NotFound }

public sealed partial class CellBridgeDocumentService
{
    /// <summary>
    /// Returns the current stored grants for an exact resource incarnation. The actor must be the
    /// current owner and must have current write access.
    /// </summary>
    public async ValueTask<DocumentSharingSnapshot?> GetSharingAsync(Guid resourceId,
        long expectedLifecycleGeneration, CellBridgeActor actor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (resourceId == Guid.Empty || expectedLifecycleGeneration <= 0)
            throw new ArgumentException("A resource identifier and lifecycle generation are required.");
        var current = await provider.Documents.ReadControlAsync(resourceId, cancellationToken);
        if (current is null || current.Head.LifecycleGeneration != expectedLifecycleGeneration || current.Head.IsDeleted)
            return null;
        EnsureSharingSupported();
        EnsureStoredGrantDocument(current.Head);
        DemandOwnerWrite(actor, current.Head);

        return new(current.Head.ResourceId, current.Head.LifecycleGeneration,
            current.Head.AuthorityGeneration, current.Head.Security.Owner!, current.Head.Security.Grants);
    }

    /// <summary>
    /// Adds, changes, or removes one recipient grant on a stored-grant document. Recipient identity
    /// must come from the host's trusted directory. Concurrent authority changes return Conflict.
    /// </summary>
    public async ValueTask<DocumentSharingUpdateStatus> SetRecipientGrantAsync(Guid resourceId,
        long expectedLifecycleGeneration, long expectedAuthorityGeneration, CellBridgeActor actor,
        SubjectIdentity recipient, DocumentAccess access, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(recipient);
        if (resourceId == Guid.Empty || expectedLifecycleGeneration <= 0 || expectedAuthorityGeneration < 0)
            throw new ArgumentException("A resource identifier and valid lifecycle and authority generations are required.");
        if (string.IsNullOrWhiteSpace(recipient.Subject) || recipient.Subject.Length > 1024 ||
            recipient.Subject.Any(char.IsControl))
            throw new ArgumentException("A trusted recipient subject of at most 1024 non-control characters is required.", nameof(recipient));
        if (access is not (DocumentAccess.None or DocumentAccess.Read or (DocumentAccess.Read | DocumentAccess.Write)))
            throw new ArgumentOutOfRangeException(nameof(access), "Only no access, read, or read/write grants are supported.");
        EnsureSharingSupported();

        var current = await provider.Documents.ReadControlAsync(resourceId, cancellationToken);
        if (current is null || current.Head.IsDeleted) return DocumentSharingUpdateStatus.NotFound;
        if (current.Head.LifecycleGeneration != expectedLifecycleGeneration ||
            current.Head.AuthorityGeneration != expectedAuthorityGeneration)
            return DocumentSharingUpdateStatus.Conflict;
        EnsureStoredGrantDocument(current.Head);
        DemandOwnerWrite(actor, current.Head);
        if (recipient.Subject == current.Head.Security.Owner)
            throw new ArgumentException("The document owner cannot be changed through recipient grants.", nameof(recipient));

        var existing = current.Head.Security.Grants.GetValueOrDefault(recipient.Subject);
        if (access == DocumentAccess.None ? !current.Head.Security.Grants.ContainsKey(recipient.Subject) : existing == access)
            return DocumentSharingUpdateStatus.Unchanged;

        var grants = access == DocumentAccess.None
            ? current.Head.Security.Grants.Remove(recipient.Subject)
            : current.Head.Security.Grants.SetItem(recipient.Subject, access);
        // Preserve owner and document author metadata. A permission change is not a content edit.
        var nextSecurity = current.Head.Security with { Grants = grants };
        var admission = Admission(actor, current.Head);
        var result = await provider.Documents.ExecuteControlAsync(new DocumentControlCommand.SetPermissions(
            resourceId, admission, nextSecurity, expectedAuthorityGeneration), cancellationToken);
        if (result.Changed) return DocumentSharingUpdateStatus.Applied;
        if (result.ErrorCode is "CoherencyFailure" or "FileNotFound") return DocumentSharingUpdateStatus.Conflict;
        if (result.ErrorCode == "FileUnauthorizedAccess")
        {
            var latest = await provider.Documents.ReadControlAsync(resourceId, cancellationToken);
            if (latest is null || latest.Head.IsDeleted || latest.Head.LifecycleGeneration != expectedLifecycleGeneration)
                return DocumentSharingUpdateStatus.Conflict;
            DemandOwnerWrite(actor, latest.Head);
            // An active editor may advance state without changing owner authority.
            // The stale admission must not publish, but the owner can retry from a fresh snapshot.
            return DocumentSharingUpdateStatus.Conflict;
        }
        throw new InvalidOperationException("The stored grant update was rejected: " + result.ErrorCode);
    }

    private void EnsureSharingSupported()
    {
        if (_externalAuthorization is not null || _policy is not StoredGrantAuthorizationPolicy || _access is not null)
            throw new NotSupportedException("Stored-grant sharing requires stored grants to be the sole document authority. This host must manage sharing through its configured authority.");
    }

    private static void EnsureStoredGrantDocument(DocumentHead head)
    {
        if (head.Security.AuthorizationPolicy is not null)
            throw new NotSupportedException("Documents controlled by an external authorization policy cannot use stored-grant sharing.");
    }

    private void DemandOwnerWrite(CellBridgeActor actor, DocumentHead head)
    {
        if (string.IsNullOrWhiteSpace(head.Security.Owner) || actor.Identity.Subject != head.Security.Owner ||
            !Access(actor, head).HasFlag(DocumentAccess.Write))
            throw new UnauthorizedAccessException("Only the current document owner with write access can manage sharing.");
    }
}
