using CellBridge.Storage.Abstractions;

namespace CellBridge.AspNetCore;

/// <summary>A live newest-first page. Concurrent publications can move offsets between calls.</summary>
public sealed record RevisionSummaryPage(IReadOnlyList<DocumentRevisionSummary> Revisions, int? NextOffset);

public sealed partial class CellBridgeDocumentService
{
    private IDocumentReadStore? SelectiveReads => _access is null && _policy is StoredGrantAuthorizationPolicy
        ? provider.State as IDocumentReadStore : null;

    /// <summary>Authorizes current metadata without loading graphs when the provider and policy support it.</summary>
    public async ValueTask<DocumentReadMetadata> GetDocumentMetadataAsync(Guid resourceId, CellBridgeActor actor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (SelectiveReads is not { } reads)
        {
            var state = await CurrentAsync(resourceId, cancellationToken);
            actor = await AuthorizeActorAsync(actor, state, DocumentAccess.Read, cancellationToken, fresh: true);
            state = await RevalidateReadStateAsync(state, cancellationToken) ?? throw MissingDocument();
            Demand(actor, state, DocumentAccess.Read);
            return DocumentReadModels.Metadata(state);
        }
        var observed = await reads.FindReadMetadataByResourceIdAsync(resourceId, cancellationToken) ?? throw MissingDocument();
        actor = await AuthorizeMetadataAsync(actor, observed, cancellationToken);
        var current = _externalAuthorization is null ? observed :
            await reads.FindReadMetadataByResourceIdAsync(resourceId, cancellationToken) ?? throw MissingDocument();
        DemandMetadata(actor, current);
        return current;
    }

    internal async ValueTask<DocumentReadMetadata?> FindDocumentMetadataByPathKeyAsync(string pathKey,
        CellBridgeActor actor, CancellationToken cancellationToken)
    {
        if (SelectiveReads is not { } reads)
        {
            var state = await provider.State.FindByPathKeyAsync(pathKey, cancellationToken);
            if (state is null) return null;
            actor = await AuthorizeActorAsync(actor, state, DocumentAccess.Read, cancellationToken, fresh: true);
            state = await RevalidateReadStateAsync(state, cancellationToken);
            if (state is null) return null;
            Demand(actor, state, DocumentAccess.Read);
            return DocumentReadModels.Metadata(state);
        }
        var observed = await reads.FindReadMetadataByPathKeyAsync(pathKey, cancellationToken);
        if (observed is null) return null;
        actor = await AuthorizeMetadataAsync(actor, observed, cancellationToken);
        var current = _externalAuthorization is null ? observed :
            await reads.FindReadMetadataByResourceIdAsync(observed.ResourceId, cancellationToken);
        if (current is null) return null;
        DemandMetadata(actor, current);
        return current;
    }

    /// <summary>Returns at most 256 revision headers, without partition graphs or content bytes.</summary>
    public async ValueTask<RevisionSummaryPage> ListRevisionSummariesAsync(Guid resourceId, int offset, int limit,
        CellBridgeActor actor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        DocumentReadModels.ValidatePage(offset, limit);
        if (SelectiveReads is not { } reads)
        {
            // Arbitrary host policies still receive the complete current state.
            var revisions = await ListRevisionsAsync(resourceId, actor, cancellationToken);
            var selected = revisions.Skip(offset).Take(limit).Select(DocumentReadModels.Summary).ToArray();
            return new(selected, (long)offset + selected.Length < revisions.Count ? checked(offset + selected.Length) : null);
        }
        var observed = await reads.FindReadMetadataByResourceIdAsync(resourceId, cancellationToken) ?? throw MissingDocument();
        actor = await AuthorizeMetadataAsync(actor, observed, cancellationToken);
        var page = await reads.ReadRevisionSummariesAsync(resourceId, offset, limit, cancellationToken) ?? throw MissingDocument();
        DemandMetadata(actor, page.Current);
        foreach (var revision in page.Revisions) ValidateRevision(page.Current, revision);
        return new(page.Revisions, page.NextOffset);
    }

    /// <summary>Returns one authorized historical content handle. A subsequent download must authorize again.</summary>
    public async ValueTask<DocumentRevisionSummary> GetRevisionSummaryAsync(Guid resourceId, ulong revisionNumber,
        CellBridgeActor actor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (SelectiveReads is not { } reads)
            return DocumentReadModels.Summary(await GetRevisionAsync(resourceId, revisionNumber, actor, cancellationToken));
        var observed = await reads.FindReadMetadataByResourceIdAsync(resourceId, cancellationToken) ?? throw MissingDocument();
        actor = await AuthorizeMetadataAsync(actor, observed, cancellationToken);
        var result = await reads.ReadRevisionSummaryAsync(resourceId, revisionNumber, cancellationToken) ?? throw MissingDocument();
        DemandMetadata(actor, result.Current);
        var revision = result.Revision ?? throw new KeyNotFoundException("The requested version does not exist.");
        ValidateRevision(result.Current, revision);
        if (revision.RevisionNumber != revisionNumber) throw new StorageCorruptionException("The selected revision number disagrees.");
        return revision;
    }

    private ValueTask<CellBridgeActor> AuthorizeMetadataAsync(CellBridgeActor actor, DocumentReadMetadata state,
        CancellationToken cancellationToken) => AuthorizeReadIdentityAsync(actor, state.ResourceId,
            state.LifecycleGeneration, state.IsDeleted, state.Security.AuthorizationPolicy, DocumentAccess.Read, cancellationToken, fresh: true);

    private void DemandMetadata(CellBridgeActor actor, DocumentReadMetadata state)
    {
        var access = DocumentAccess.None;
        if (!state.IsDeleted && !string.IsNullOrWhiteSpace(actor.Identity.Subject) &&
            (actor.AccessLimit is null || actor.AccessLimit.ResourceId == state.ResourceId))
        {
            if (_externalAuthorization is null)
                access = state.Security.AuthorizationPolicy is null ? NormalizeAccess(state.Security.AccessFor(actor.Identity.Subject)) : DocumentAccess.None;
            else if (actor.Authorization is { } admission && IsExternalBinding(state.Security.AuthorizationPolicy) &&
                admission.Subject == actor.Identity.Subject && admission.ResourceId == state.ResourceId &&
                admission.LifecycleGeneration == state.LifecycleGeneration && admission.Binding == state.Security.AuthorizationPolicy)
                access = admission.Access;
            if (actor.AccessLimit is { } ceiling) access &= ceiling.Access;
        }
        if (!access.HasFlag(DocumentAccess.Read)) throw new UnauthorizedAccessException("Document access denied.");
    }

    private static void ValidateRevision(DocumentReadMetadata current, DocumentRevisionSummary revision)
    {
        if (revision.ResourceId != current.ResourceId || revision.LifecycleGeneration != current.LifecycleGeneration)
            throw new StorageCorruptionException("The selected revision identity disagrees with the current document.");
    }

    private static KeyNotFoundException MissingDocument() => new("Document does not exist.");
}
