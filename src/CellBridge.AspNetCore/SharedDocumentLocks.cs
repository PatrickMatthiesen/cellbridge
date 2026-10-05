using CellBridge.Storage.Abstractions;

namespace CellBridge.AspNetCore;

public enum HostLockOperation { Acquire, Refresh, Release, Replace }
public sealed record HostWriteToken(Guid ResourceId, long LifecycleGeneration, long CoordinationGeneration,
    string LockToken, string OwnerSubject);
public sealed record HostLockResult(bool Success, HostWriteToken? WriteToken, string? CurrentLock);

/// <summary>Authoritative base WOPI lock integration. Hosts must route both protocols through the same provider.</summary>
public sealed class SharedDocumentLocks(CellBridgeDocumentService service)
{
    public ValueTask<HostLockResult> ApplyAsync(Guid resourceId, HostLockOperation operation, string token,
        CellBridgeActor actor, string? replacementToken = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        if (token.Length > 1024) throw new ArgumentException("Lock tokens are limited to 1024 characters.", nameof(token));
        if (operation == HostLockOperation.Replace && (string.IsNullOrWhiteSpace(replacementToken) || replacementToken.Length > 1024))
            throw new ArgumentException("A replacement lock token is required.", nameof(replacementToken));
        return service.Provider.State.TransitionAsync(resourceId, (current, now) =>
        {
            if (!service.Access(actor, current).HasFlag(DocumentAccess.Write)) throw new UnauthorizedAccessException("Write access denied.");
            var coordination = current.Coordination;
            var active = coordination.HostLock is { } host && host.ExpiresUtc > now ? host : null;
            if (coordination.Exclusive is { } exclusive && exclusive.ExpiresUtc > now || coordination.SchemaOwners.Any(l => l.ExpiresUtc > now))
                return new StateTransition<HostLockResult>(null, new(false, null, coordination.Exclusive?.Id ?? coordination.SchemaId));
            var owned = active is not null && active.Token == token && active.OwnerSubject == actor.Identity.Subject;
            if (operation == HostLockOperation.Acquire ? active is not null && !owned : !owned)
                return new StateTransition<HostLockResult>(null, new(false, null, active?.Token));
            var epoch = checked(coordination.Generation + 1);
            var nextHost = operation == HostLockOperation.Release ? null : new HostLease(
                operation == HostLockOperation.Replace ? replacementToken! : token, actor.Identity.Subject, now.AddMinutes(30));
            var next = current with { Coordination = coordination with
            { HostLock = nextHost, Exclusive = null, SchemaOwners = [], SchemaId = null,
                CoauthorClients = [], CoauthorTransitionPending = false, Generation = epoch } };
            var writeToken = nextHost is null ? null : new HostWriteToken(resourceId, current.LifecycleGeneration,
                epoch, nextHost.Token, actor.Identity.Subject);
            return new StateTransition<HostLockResult>(next, new(true, writeToken, nextHost?.Token));
        }, cancellationToken);
    }

    /// <summary>
    /// Atomically validates a prepared host write and commits its immutable state plus outbox.
    /// The trusted callback is bounded and performs no I/O. Preserve current-based independent state fields.
    /// Hosts stage package/graph handles before calling this method and publish externally through the journal.
    /// </summary>
    public ValueTask<bool> TryCommitAsync(HostWriteToken token, long expectedStateVersion, CellBridgeActor actor,
        Func<DocumentState, DateTime, DocumentState> commit, Guid operationId, CancellationToken cancellationToken = default) =>
        service.Provider.State.TransitionAsync(token.ResourceId, (current, now) =>
        {
            if (!service.Access(actor, current).HasFlag(DocumentAccess.Write) || token.OwnerSubject != actor.Identity.Subject ||
                current.LifecycleGeneration != token.LifecycleGeneration || current.StateVersion != expectedStateVersion ||
                current.Coordination.Generation != token.CoordinationGeneration || current.Coordination.HostLock is not { } lease ||
                lease.OwnerSubject != token.OwnerSubject || lease.Token != token.LockToken || lease.ExpiresUtc <= now)
                return new StateTransition<bool>(null, false);
            var next = commit(current, now);
            DocumentLifecycle.ValidateTransition(current, next);
            if (next.Coordination != current.Coordination) throw new InvalidOperationException("A prepared write cannot replace its authoritative lock state.");
            next = ExternalPublication.Append(current, next, operationId, service.Provider.Limits);
            return new StateTransition<bool>(next, true);
        }, cancellationToken);
}
