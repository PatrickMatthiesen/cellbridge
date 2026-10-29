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
public sealed record ExternalDeliveryRequest(Guid BindingId, string Destination, string ExpectedRevision, DocumentDelivery Revision);

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
