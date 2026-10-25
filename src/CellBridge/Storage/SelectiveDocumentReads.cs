using System.Collections.Immutable;

namespace CellBridge.Storage.Abstractions;

/// <summary>Current authorization, identity and content metadata, without graphs, receipts or revision history.</summary>
public sealed record DocumentReadMetadata(Guid ResourceId, string Path, string PathKey,
    DateTime CreatedUtc, DateTime ModifiedUtc, uint ContentVersion, long StateVersion,
    long LifecycleGeneration, bool IsDeleted, ContentHandle Content, DocumentSecurity Security,
    ulong LatestRevisionNumber, int RevisionCount, ImmutableArray<DateTime> EditorExpiries)
{
    public int PendingDeliveryCount { get; init; }
    public string? DeliveryBlock { get; init; }
    public string Etag => $"\"{{{ResourceId.ToString("D").ToUpperInvariant()}}},{ContentVersion}\"";
    public DocumentSummary ToSummary(DateTime now) => new(ResourceId, Path, Content.Length,
        ContentVersion, ModifiedUtc, EditorExpiries.Count(expiry => expiry > now)) { Security = Security, PendingDeliveryCount = PendingDeliveryCount, DeliveryBlock = DeliveryBlock };
}

/// <summary>One immutable publication's download metadata. It does not contain partition graphs.</summary>
public sealed record DocumentRevisionSummary(Guid ResourceId, long LifecycleGeneration, ulong RevisionNumber,
    uint ContentVersion, DateTime CreatedUtc, SubjectIdentity Author, ContentHandle Content);

/// <summary>The current authorization fence and selected revisions must come from one coherent store view.</summary>
public sealed record DocumentRevisionReadPage(DocumentReadMetadata Current,
    ImmutableArray<DocumentRevisionSummary> Revisions, int? NextOffset);
public sealed record DocumentRevisionRead(DocumentReadMetadata Current, DocumentRevisionSummary? Revision);

/// <summary>
/// Optional selective reads. Each call observes one current document pointer and
/// returns metadata for that pointer; revision rows belong to that same current
/// lifecycle/history. Missing or stale derived records are corruption, not a reason
/// to return another document. Reads perform no package or graph payload I/O.
/// Existing state-store and full graph-returning APIs remain available.
/// </summary>
public interface IDocumentReadStore
{
    ValueTask<DocumentReadMetadata?> FindReadMetadataByResourceIdAsync(Guid resourceId, CancellationToken cancellationToken = default);
    ValueTask<DocumentReadMetadata?> FindReadMetadataByPathKeyAsync(string pathKey, CancellationToken cancellationToken = default);
    ValueTask<DocumentRevisionReadPage?> ReadRevisionSummariesAsync(Guid resourceId, int offset, int limit,
        CancellationToken cancellationToken = default);
    ValueTask<DocumentRevisionRead?> ReadRevisionSummaryAsync(Guid resourceId, ulong revisionNumber,
        CancellationToken cancellationToken = default);
}

/// <summary>Pure projections shared by providers and compatibility fallbacks.</summary>
public static class DocumentReadModels
{
    public const int MaxPageSize = 256;

    public static void ValidatePage(int offset, int limit)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (limit is < 1 or > MaxPageSize)
            throw new ArgumentOutOfRangeException(nameof(limit), $"Page size must be between 1 and {MaxPageSize}.");
    }

    public static DocumentReadMetadata Metadata(DocumentState state) => new(state.ResourceId, state.Path, state.PathKey,
        state.CreatedUtc, state.ModifiedUtc, state.ContentVersion, state.StateVersion, state.LifecycleGeneration,
        state.IsDeleted, state.Content, state.Security,
        state.Revisions.IsEmpty ? Math.Max(1UL, state.ContentVersion) : state.Revisions[^1].RevisionNumber,
        state.Revisions.IsEmpty ? 1 : state.Revisions.Length,
        state.Editors.Select(editor => editor.ExpiresUtc).ToImmutableArray())
    { PendingDeliveryCount = state.Publication?.Pending.Length ?? 0, DeliveryBlock = state.Publication?.BlockedReason };

    public static DocumentRevisionSummary Summary(DocumentRevision revision) => new(revision.ResourceId,
        revision.LifecycleGeneration, revision.RevisionNumber, revision.ContentVersion, revision.CreatedUtc,
        revision.Author, revision.Content);

    public static IEnumerable<DocumentRevisionSummary> Revisions(DocumentState state) => state.Revisions.IsEmpty
        ? [new(state.ResourceId, state.LifecycleGeneration, Math.Max(1UL, state.ContentVersion), state.ContentVersion,
            state.ModifiedUtc, state.Security.ModifiedBy ?? state.Security.CreatedBy ?? new("unknown", "unknown", "unknown"), state.Content)]
        : state.Revisions.Select(Summary);
}
