using CellBridge.Storage.Abstractions;

namespace CellBridge.AspNetCore;

public enum HostLockOperation { Acquire, Refresh, Release, Replace }
public sealed record HostWriteToken(Guid ResourceId, long LifecycleGeneration, long CoordinationGeneration,
    string LockToken, string OwnerSubject);
public sealed record HostLockResult(bool Success, HostWriteToken? WriteToken, string? CurrentLock);

/// <summary>Authoritative base WOPI lock integration. Hosts must route both protocols through the same provider.</summary>
public sealed class SharedDocumentLocks(CellBridgeDocumentService service)
{
    public async ValueTask<HostLockResult> ApplyAsync(Guid resourceId, HostLockOperation operation, string token,
        CellBridgeActor actor, string? replacementToken = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        if (!Enum.IsDefined(operation)) throw new ArgumentOutOfRangeException(nameof(operation));
        if (token.Length > 1024) throw new ArgumentException("Lock tokens are limited to 1024 characters.", nameof(token));
        if (operation == HostLockOperation.Replace && (string.IsNullOrWhiteSpace(replacementToken) || replacementToken.Length > 1024))
            throw new ArgumentException("A replacement lock token is required.", nameof(replacementToken));
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var current = await service.Provider.Documents.ReadControlAsync(resourceId, cancellationToken)
                ?? throw new KeyNotFoundException("Document does not exist.");
            actor = await service.AuthorizeActorAsync(actor, current.Head, DocumentAccess.Write, cancellationToken, fresh: true);
            if (!service.Access(actor, current.Head).HasFlag(DocumentAccess.Write)) throw new UnauthorizedAccessException("Write access denied.");
            var now = current.UtcNow;
            var coordination = current.Coordination;
            var active = coordination.HostLock is { } host && host.ExpiresUtc > now ? host : null;
            if (coordination.Exclusive is { } exclusive && exclusive.ExpiresUtc > now || coordination.SchemaOwners.Any(l => l.ExpiresUtc > now))
                return new(false, null, coordination.Exclusive?.Id ?? coordination.SchemaId);
            var owned = active is not null && active.Token == token && active.OwnerSubject == actor.Identity.Subject;
            if (operation == HostLockOperation.Acquire ? active is not null && !owned : !owned)
                return new(false, null, active?.Token);
            var nextHost = operation == HostLockOperation.Release ? null : new HostLease(
                operation == HostLockOperation.Replace ? replacementToken! : token, actor.Identity.Subject, now.AddMinutes(30));
            var result = await service.Provider.Documents.ExecuteControlAsync(new DocumentControlCommand.SetHostLease(resourceId,
                service.Admission(actor, current.Head), nextHost, active?.Token), cancellationToken);
            if (result.ErrorCode is not null)
            {
                if (result.Current.Head.StateVersion != current.Head.StateVersion) continue;
                return new(false, null, result.Current.Coordination.HostLock?.Token ?? result.Current.Coordination.Exclusive?.Id ?? result.Current.Coordination.SchemaId);
            }
            var writeToken = nextHost is null ? null : new HostWriteToken(resourceId, current.Head.LifecycleGeneration,
                result.Current.Coordination.Generation, nextHost.Token, actor.Identity.Subject);
            return new(true, writeToken, nextHost?.Token);
        }
        throw new InvalidOperationException("The document changed while updating its host lock.");
    }

    /// <summary>
    /// Publishes a typed, durably staged host write through the same document fence used by Office.
    /// </summary>
    public async ValueTask<bool> TryCommitAsync(HostWriteToken token, PreparedDocumentPublication publication,
        CellBridgeActor actor, CancellationToken cancellationToken = default)
    {
        if (publication.ResourceId != token.ResourceId || publication.Receipt.Operation.Family != SaveOperationFamily.WholeFile ||
            publication.HostLockToken != token.LockToken || publication.Fence.LifecycleGeneration != token.LifecycleGeneration ||
            publication.Fence.CoordinationGeneration != token.CoordinationGeneration || token.OwnerSubject != actor.Identity.Subject)
            return false;
        var current = await service.Provider.Documents.ReadControlAsync(token.ResourceId, cancellationToken);
        if (current is null) return false;
        actor = await service.AuthorizeActorAsync(actor, current.Head, DocumentAccess.Write, cancellationToken, fresh: true);
        if (!service.Access(actor, current.Head).HasFlag(DocumentAccess.Write)) return false;
        var result = await service.Provider.Documents.PublishAsync(publication with
            { Admission = service.Admission(actor, current.Head) }, cancellationToken);
        return result.Status is PublicationStatus.Accepted or PublicationStatus.Replay;
    }
}
