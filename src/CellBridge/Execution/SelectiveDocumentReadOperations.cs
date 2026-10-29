using CellBridge.Storage.Abstractions;

namespace CellBridge.AspNetCore;

public sealed record DocumentDownload(DocumentHead Current, DocumentPackage Package, Stream Content, DateTime ModifiedUtc) : IAsyncDisposable
{
    public ValueTask DisposeAsync() => Content.DisposeAsync();
}

public sealed record RevisionSummaryPage(IReadOnlyList<DocumentRevisionRecord> Revisions, int? NextOffset);

public sealed partial class CellBridgeDocumentService
{
    /// <summary>Authorizes current metadata without reading graphs, receipts or history, for every policy type.</summary>
    public async ValueTask<DocumentHead> GetDocumentMetadataAsync(Guid resourceId, CellBridgeActor actor,
        CancellationToken cancellationToken = default)
    {
        var observed = await CurrentAsync(resourceId, cancellationToken);
        actor = await AuthorizeActorAsync(actor, observed, DocumentAccess.Read, cancellationToken, fresh: actor.Authorization is null);
        var current = await RevalidateReadStateAsync(observed, cancellationToken) ?? throw MissingDocument();
        Demand(actor, current, DocumentAccess.Read);
        return await RefreshPresenceAsync(current, actor, cancellationToken);
    }

    internal async ValueTask<DocumentHead?> FindDocumentMetadataByPathKeyAsync(string pathKey,
        CellBridgeActor actor, CancellationToken cancellationToken)
    {
        var observed = await provider.Documents.FindAsync(new DocumentIdentity.Path(pathKey), cancellationToken);
        if (observed is null) return null;
        actor = await AuthorizeActorAsync(actor, observed, DocumentAccess.Read, cancellationToken, fresh: actor.Authorization is null);
        var current = await RevalidateReadStateAsync(observed, cancellationToken);
        if (current is null) return null;
        Demand(actor, current, DocumentAccess.Read);
        return await RefreshPresenceAsync(current, actor, cancellationToken);
    }

    private async ValueTask<DocumentHead> RefreshPresenceAsync(DocumentHead head, CellBridgeActor actor,
        CancellationToken cancellationToken)
    {
        if (head.NextEditorExpiryUtc is null) return head;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            // Presence expiry uses the provider clock. Documents without participants
            // keep the metadata-only read path; active participants are bounded controls.
            var control = await provider.Documents.ReadControlAsync(head.ResourceId, cancellationToken) ?? throw MissingDocument();
            Demand(actor, control.Head, DocumentAccess.Read);
            if (control.Head.NextEditorExpiryUtc is not { } expiry || expiry > control.UtcNow) return control.Head;
            var result = await provider.Documents.ExecuteControlAsync(new DocumentControlCommand.ExpireEditors(
                head.ResourceId, Admission(actor, control.Head)), cancellationToken);
            Demand(actor, result.Current.Head, DocumentAccess.Read);
            if (!result.Changed && result.Current.Head.StateVersion != control.Head.StateVersion) continue;
            if (result.ErrorCode is not null) throw new UnauthorizedAccessException("Current presence access validation failed.");
            return result.Current.Head;
        }
        throw new InvalidOperationException("The document changed while expiring editor presence.");
    }

    public async ValueTask<RevisionSummaryPage> ListRevisionSummariesAsync(Guid resourceId, int offset, int limit,
        CellBridgeActor actor, CancellationToken cancellationToken = default)
    {
        DocumentReadModels.ValidatePage(offset, limit);
        actor = await AuthorizeResourceActorAsync(actor, resourceId, DocumentAccess.Read, cancellationToken, fresh: actor.Authorization is null);
        var page = await provider.Documents.ReadRevisionsAsync(resourceId, offset, limit, cancellationToken) ?? throw MissingDocument();
        Demand(actor, page.Current, DocumentAccess.Read);
        foreach (var revision in page.Revisions) ValidateRevision(page.Current, revision);
        return new(page.Revisions, page.NextOffset);
    }

    public async ValueTask<DocumentRevisionRecord> GetRevisionSummaryAsync(Guid resourceId, ulong revisionNumber,
        CellBridgeActor actor, CancellationToken cancellationToken = default)
    {
        actor = await AuthorizeResourceActorAsync(actor, resourceId, DocumentAccess.Read, cancellationToken, fresh: actor.Authorization is null);
        var result = await provider.Documents.ReadRevisionAsync(resourceId, revisionNumber, cancellationToken) ?? throw MissingDocument();
        if (result.Current.IsDeleted) throw MissingDocument();
        Demand(actor, result.Current, DocumentAccess.Read);
        var revision = result.Revision ?? throw new KeyNotFoundException("The requested version does not exist.");
        ValidateRevision(result.Current, revision);
        if (revision.RevisionNumber != revisionNumber) throw new StorageCorruptionException("The selected revision number disagrees.");
        return revision;
    }

    /// <summary>Returns headers and verified canonical bytes from one immutable selection.</summary>
    public async ValueTask<DocumentDownload> OpenDocumentAsync(DocumentIdentity identity, ulong? revisionNumber, CellBridgeActor actor,
        CancellationToken cancellationToken = default)
    {
        var head = await provider.Documents.FindAsync(identity, cancellationToken) ?? throw MissingDocument();
        actor = await AuthorizeActorAsync(actor, head, DocumentAccess.Read, cancellationToken, fresh: actor.Authorization is null);
        Demand(actor, head, DocumentAccess.Read);
        DocumentPackage package;
        var modifiedUtc = head.ModifiedUtc;
        if (revisionNumber is { } number)
        {
            var selected = await provider.Documents.ReadRevisionAsync(head.ResourceId, number, cancellationToken) ?? throw MissingDocument();
            Demand(actor, selected.Current, DocumentAccess.Read);
            var revision = selected.Revision ?? throw new KeyNotFoundException("The requested version does not exist.");
            ValidateRevision(selected.Current, revision);
            head = selected.Current;
            package = revision.Package;
            modifiedUtc = revision.CreatedUtc;
        }
        else package = head.Package;
        var stream = await provider.Packages.OpenReadAsync(package, cancellationToken);
        try
        {
            var current = await CurrentAsync(head.ResourceId, cancellationToken);
            Demand(actor, current, DocumentAccess.Read);
            if (current.LifecycleGeneration != package.LifecycleGeneration) throw new UnauthorizedAccessException("The document lifecycle changed.");
            return new(head, package, stream, modifiedUtc);
        }
        catch { await stream.DisposeAsync(); throw; }
    }

    private static void ValidateRevision(DocumentHead current, DocumentRevisionRecord revision)
    {
        if (revision.ResourceId != current.ResourceId || revision.LifecycleGeneration != current.LifecycleGeneration)
            throw new StorageCorruptionException("The selected revision identity disagrees with the current document.");
    }

    private static KeyNotFoundException MissingDocument() => new("Document does not exist.");
}
