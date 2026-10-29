using CellBridge.AspNetCore;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;

namespace CellBridge.WebDemo;

/// <summary>Sample document management shared by the library page and JSON endpoints.</summary>
public sealed class DocumentWorkspace(CellBridgeDocumentService service)
{
    public async Task<DocumentListingPage> ReadAsync(CellBridgeActor actor, int offset, int limit,
        CancellationToken cancellationToken)
    {
        var page = await AuthorizedDocumentCatalog.ReadAsync(service, actor, offset, limit, cancellationToken);
        return new(page.Documents.Select(document => new DocumentListing(document.Path,
            Path.GetFileName(document.Path), DocumentLibrary.ContentType(document.Path), document.Length,
            document.ContentVersion, document.ModifiedUtc, document.ActiveEditors)).ToArray(), page.NextOffset);
    }

    public async Task<DocumentWorkspaceCreation> CreateAsync(string? name, string? type, CellBridgeActor actor,
        CancellationToken cancellationToken)
    {
        if (!actor.CanCreate)
            return new(StatusCodes.Status403Forbidden, null, "You do not have permission to create documents.");
        var detached = new DocumentStore();
        var result = DocumentCreation.TryCreate(detached, name, type);
        if (!result.Created) return new(StatusCodes.Status400BadRequest, null, result.Error);
        var document = detached.List().Single();
        var escaped = string.Join('/', document.Url.Split('/').Select(Uri.EscapeDataString));
        DocumentHead? created;
        try { created = await service.CreateAsync(escaped, document.Content, actor, cancellationToken); }
        catch (StorageQuotaExceededException exception)
        {
            return new(StatusCodes.Status507InsufficientStorage, null, exception.Message, exception.Budget);
        }
        return created is null
            ? new(StatusCodes.Status409Conflict, null, "A document with that name already exists.")
            : new(StatusCodes.Status201Created, result.Document, null, Location: escaped);
    }
}

public sealed record DocumentListingPage(IReadOnlyList<DocumentListing> Documents, int? NextOffset);
public sealed record DocumentWorkspaceCreation(int StatusCode, DocumentListing? Document, string? Error,
    string? Budget = null, string? Location = null);
