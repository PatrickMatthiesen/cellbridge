using System.Collections.Immutable;

namespace CellBridge.Storage.Abstractions;

public sealed record HostLease(string Token, string OwnerSubject, DateTime ExpiresUtc);

public sealed record ExternalRevision(Guid OperationId, Guid ResourceId, long LifecycleGeneration,
    long Sequence, uint ContentVersion, ContentHandle Content);

/// <summary>One immutable destination binding. Failed and unacknowledged entries remain content roots.</summary>
public sealed record ExternalPublicationState(Guid BindingId, string Destination, string ExpectedRevision,
    long NextSequence, ImmutableArray<ExternalRevision> Pending)
{
    public string? BlockedReason { get; init; }
}

public enum ExternalDeliveryStatus { Applied, Conflict, Stale }
public sealed record ExternalDeliveryResult(ExternalDeliveryStatus Status, string? Revision = null);
public sealed record ExternalDeliveryRequest(Guid BindingId, string Destination, string ExpectedRevision, ExternalRevision Revision);

/// <summary>
/// The destination must atomically persist bytes, a non-reusable revision token and a durable
/// receipt keyed by binding/operation, validating the complete immutable request fingerprint.
/// Retry returns the original resulting revision without writing again. Old compacted receipts
/// must return Stale, never reapply. Deduplication must survive deletion/recreation and rebinding.
/// Remote changes must advance the revision token. A content hash alone is insufficient.
/// </summary>
public interface IExternalRevisionDestination
{
    ValueTask<ExternalDeliveryResult> CompareExchangeAsync(ExternalDeliveryRequest request, Stream verifiedContent,
        CancellationToken cancellationToken = default);
}

public static class ExternalPublication
{
    /// <summary>Call inside the same atomic transition that publishes the committed file content.</summary>
    public static DocumentState Append(DocumentState current, DocumentState next, Guid operationId, StorageLimits limits)
    {
        if (next.Content != current.Content && next.ContentVersion != checked(current.ContentVersion + 1))
            throw new InvalidOperationException("Changed external file bytes require the next content version.");
        if (current.Publication is not { } publication || next.ContentVersion == current.ContentVersion) return next;
        if (operationId == Guid.Empty) throw new ArgumentException("A stable publication operation identity is required.", nameof(operationId));
        var entry = new ExternalRevision(operationId, next.ResourceId, next.LifecycleGeneration,
            publication.NextSequence, next.ContentVersion, next.Content);
        var pending = publication.Pending.Add(entry);
        StorageLimits.Check("pending external revisions", pending.Length, limits.MaxPendingExternalRevisions);
        StorageLimits.Check("pending external bytes", pending.Sum(r => r.Content.Length), limits.MaxPendingExternalBytes);
        return next with { Publication = publication with
        { Pending = pending, NextSequence = checked(publication.NextSequence + 1) } };
    }
}
