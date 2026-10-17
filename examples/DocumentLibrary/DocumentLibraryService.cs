using System.Collections.Immutable;
using CellBridge.AspNetCore;
using CellBridge.Storage.Abstractions;

namespace CellBridge.DocumentLibrary;

public sealed record LibraryDocument(
    Guid ResourceId,
    string FileName,
    string CellBridgePath,
    long Length,
    uint ContentVersion,
    DateTime ModifiedUtc,
    string DeliveryStatus,
    int PendingDeliveries,
    string? DeliveryBlock,
    string DestinationRevision);

public sealed class DocumentLibraryService(
    CellBridgeDocumentService documents,
    DocumentLibraryDestination destination,
    ExternalRevisionPublisher publisher,
    ExternalRevisionPublicationWorker publicationWorker,
    DocumentLibraryOptions options,
    CachedDocumentAuthorizationProvider? authorizationCache = null)
{
    private readonly SemaphoreSlim _permissionGate = new(1, 1);

    public const string OwnerSubject = "local:owner";
    public const string EditorSubject = "local:editor";
    public const string ReaderSubject = "local:reader";

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        foreach (var discovered in await destination.ListAsync(cancellationToken))
        {
            var manifest = discovered;
            var state = await documents.Provider.State.FindByResourceIdAsync(manifest.ResourceId, cancellationToken);
            if (state is null)
            {
                if (manifest.CellBridgeBound)
                    throw new InvalidOperationException($"Destination {manifest.FileName} was bound, but its CellBridge state is missing. Restore the PostgreSQL database and destination from one coordinated recovery point.");
                if (manifest.PendingCreation)
                    throw new InvalidOperationException($"Upload {manifest.FileName} did not complete its Create authorization and CellBridge commit. Recover its state or remove this unaccepted staged upload before restarting; it cannot be imported automatically.");
                if (!manifest.Receipts.IsEmpty)
                    throw new InvalidOperationException($"Unbound destination {manifest.FileName} unexpectedly has delivery receipts.");
                var bytes = await destination.ReadCurrentBytesAsync(manifest.ResourceId, cancellationToken);
                state = await documents.ImportExternalAsync(manifest.ResourceId, manifest.CellBridgePath, bytes,
                    DocumentLibraryPermissionProvider.Scope,
                    new(OwnerSubject, "owner", "Local owner"),
                    new(new("system:document-library-recovery", "recovery", "Document library recovery"), true),
                    cancellationToken)
                    ?? throw new InvalidOperationException($"Could not recover CellBridge state for {manifest.FileName}.");
            }

            RequireExternalAuthorization(state, manifest);

            if (state.Publication is null)
            {
                if (state.Content.Length != manifest.Length ||
                    !string.Equals(state.Content.Sha256, manifest.Sha256, StringComparison.Ordinal))
                    throw new InvalidOperationException($"CellBridge state for {manifest.FileName} no longer matches its unbound destination baseline.");
                if (!await publisher.BindAsync(state.ResourceId, state.LifecycleGeneration, state.StateVersion,
                    manifest.BindingId, manifest.DestinationId, manifest.CurrentRevision, cancellationToken))
                    throw new InvalidOperationException($"Could not bind {manifest.FileName} to its destination baseline.");
                manifest = await destination.MarkCellBridgeBoundAsync(manifest.ResourceId, cancellationToken);
                state = await RequireStateAsync(state.ResourceId, cancellationToken);
            }
            else if (state.Publication.BindingId != manifest.BindingId ||
                     !string.Equals(state.Publication.Destination, manifest.DestinationId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"CellBridge publication binding for {manifest.FileName} does not match the destination.");
            }

            if (!manifest.CellBridgeBound)
                await destination.MarkCellBridgeBoundAsync(manifest.ResourceId, cancellationToken);
        }
    }

    private static void RequireExternalAuthorization(DocumentState state, LibraryManifest manifest)
    {
        var binding = state.Security.AuthorizationPolicy;
        var scope = DocumentLibraryPermissionProvider.Scope;
        if (binding is { ContractVersion: 2, Revision: > 0 } &&
            binding.PolicyDomain == DocumentLibraryPermissionProvider.Domain &&
            binding.TenantId == scope.TenantId && binding.WorkspaceId == scope.WorkspaceId)
            return;

        if (binding is { ContractVersion: 1 } && binding.PolicyDomain == DocumentLibraryPermissionProvider.Domain)
            throw new InvalidOperationException($"Document {manifest.FileName} uses legacy authorization. This example does not migrate existing state. Use a fresh example database and destination root; preserve existing storage separately if it is needed for recovery.");
        throw new InvalidOperationException($"Permission binding for {manifest.FileName} does not match this host's authority. Use a fresh example database and destination root.");
    }

    public async Task<DocumentState> CreateBlankAsync(string? name, string? type, CellBridgeActor actor,
        CancellationToken cancellationToken = default)
    {
        if (!actor.CanCreate) throw new UnauthorizedAccessException("Document creation requires permission.");
        var fileName = DocumentCreation.FileName(name, type);
        await using var content = new MemoryStream(DocumentCreation.Content(fileName), writable: false);
        return await UploadAsync(fileName, content, actor, cancellationToken);
    }

    public async Task<DocumentState> UploadAsync(string fileName, Stream content, CellBridgeActor actor,
        CancellationToken cancellationToken = default)
    {
        if (!actor.CanCreate) throw new UnauthorizedAccessException("Document creation is restricted to the local owner.");
        if (await destination.FindByFileNameAsync(fileName, cancellationToken) is not null)
            throw new DocumentNameConflictException("A document with this file name already exists.");
        await using var buffer = new MemoryStream();
        await CopyBoundedAsync(content, buffer, options.MaxUploadBytes, cancellationToken);
        var bytes = buffer.ToArray();
        var resourceId = Guid.NewGuid();
        var bindingId = Guid.NewGuid();
        var path = "/shared/" + Uri.EscapeDataString(fileName);
        var initialPermissions = ImmutableDictionary<string, DocumentAccess>.Empty
            .Add(OwnerSubject, DocumentAccess.Write)
            .Add(EditorSubject, DocumentAccess.Write)
            .Add(ReaderSubject, DocumentAccess.Read)
            .SetItem(actor.Identity.Subject, DocumentAccess.Write);
        var manifest = await destination.CreateAsync(resourceId, bindingId, fileName, path,
            new MemoryStream(bytes, writable: false), initialPermissions, cancellationToken, pendingCreation: true);
        DocumentState? state;
        try
        {
            state = await documents.CreateExternalAsync(resourceId, path, bytes,
                DocumentLibraryPermissionProvider.Scope, actor, cancellationToken);
        }
        catch (UnauthorizedAccessException)
        {
            // This error means the engine rejected Create before committing state.
            // Unknown commit outcomes retain their staged manifest for explicit recovery.
            if (await documents.Provider.State.FindByResourceIdAsync(resourceId, CancellationToken.None) is null)
            {
                await destination.DiscardRejectedCreationAsync(resourceId, CancellationToken.None);
                authorizationCache?.InvalidateResource(resourceId);
            }
            throw;
        }
        if (state is null)
        {
            // A returned rejection is definitive. Exceptions may hide a successful commit and
            // must retain their staged upload for the recovery checks in InitializeAsync.
            await destination.DiscardRejectedCreationAsync(resourceId, CancellationToken.None);
            authorizationCache?.InvalidateResource(resourceId);
            throw new InvalidOperationException("CellBridge rejected the document path or resource ID.");
        }
        if (!await publisher.BindAsync(resourceId, state.LifecycleGeneration, state.StateVersion,
            bindingId, manifest.DestinationId, manifest.CurrentRevision, cancellationToken))
            throw new InvalidOperationException("CellBridge could not bind the imported document to its destination.");
        await destination.MarkCellBridgeBoundAsync(resourceId, cancellationToken);
        publicationWorker.Wake();
        return await RequireStateAsync(resourceId, cancellationToken);
    }

    public async Task<IReadOnlyList<LibraryDocument>> ListAsync(CellBridgeActor actor,
        CancellationToken cancellationToken = default)
    {
        var page = await AuthorizedDocumentCatalog.ReadAsync(documents, actor, 0, 1000, cancellationToken);
        var result = new List<LibraryDocument>(page.Documents.Count);
        foreach (var summary in page.Documents)
        {
            var state = await RequireStateAsync(summary.ResourceId, cancellationToken);
            var manifest = await destination.GetAsync(summary.ResourceId, cancellationToken);
            var publication = state.Publication;
            var pending = publication?.Pending.Length ?? 0;
            var status = publication?.BlockedReason is { } reason
                ? "Committed in CellBridge; destination blocked: " + reason
                : pending > 0
                    ? $"Committed in CellBridge; {pending} destination delivery item(s) pending"
                    : "Delivered to destination";
            result.Add(new(summary.ResourceId, manifest.FileName, manifest.CellBridgePath,
                summary.Length, summary.ContentVersion, summary.ModifiedUtc, status, pending,
                publication?.BlockedReason, manifest.CurrentRevision));
        }
        return result;
    }

    public async Task<Stream> OpenDeliveredAsync(Guid resourceId, CellBridgeActor actor,
        CancellationToken cancellationToken = default)
    {
        var state = await RequireStateAsync(resourceId, cancellationToken);
        if (!(await documents.GetAccessAsync(actor, state, cancellationToken)).HasFlag(DocumentAccess.Read))
            throw new UnauthorizedAccessException("Document access denied.");
        return await destination.OpenCurrentReadAsync(resourceId, cancellationToken);
    }

    public async Task<IReadOnlyList<DocumentRevision>> HistoryAsync(Guid resourceId, CellBridgeActor actor,
        CancellationToken cancellationToken = default) =>
        await documents.ListRevisionsAsync(resourceId, actor, cancellationToken);

    public async Task<long> SetPermissionAsync(Guid resourceId, string subject, DocumentAccess access,
        CancellationToken cancellationToken = default)
    {
        if (subject == OwnerSubject && !access.HasFlag(DocumentAccess.Write))
            throw new InvalidOperationException("The local owner must retain write access.");
        await _permissionGate.WaitAsync(cancellationToken);
        try
        {
            var currentManifest = await destination.GetAsync(resourceId, cancellationToken);
            var currentPermissions = currentManifest.Permissions;
            var nextPermissions = currentPermissions.SetItem(subject, Normalize(access));
            var committed = await destination.SetPermissionsAsync(resourceId, nextPermissions, cancellationToken);
            // Invalidation follows the durable host commit. Already admitted work may finish.
            authorizationCache?.InvalidateResource(resourceId);
            return committed.DesiredPermissionRevision;
        }
        finally
        {
            _permissionGate.Release();
        }
    }

    public Task<DocumentState> RequireStateAsync(Guid resourceId, CancellationToken cancellationToken = default) =>
        RequireStateCoreAsync(resourceId, cancellationToken);

    private async Task<DocumentState> RequireStateCoreAsync(Guid resourceId, CancellationToken cancellationToken)
    {
        return await documents.Provider.State.FindByResourceIdAsync(resourceId, cancellationToken)
            ?? throw new KeyNotFoundException("The document does not exist.");
    }

    private static DocumentAccess Normalize(DocumentAccess access)
    {
        if ((access & ~(DocumentAccess.Read | DocumentAccess.Write)) != 0)
            throw new ArgumentOutOfRangeException(nameof(access));
        return access.HasFlag(DocumentAccess.Write) ? access | DocumentAccess.Read : access;
    }

    private static async Task CopyBoundedAsync(Stream source, Stream destination, long maximum,
        CancellationToken cancellationToken)
    {
        if (maximum <= 0) throw new ArgumentOutOfRangeException(nameof(maximum));
        var buffer = new byte[65536];
        long length = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) != 0)
        {
            length = checked(length + read);
            if (length > maximum) throw new InvalidDataException("The upload exceeds the configured size limit.");
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }
}
