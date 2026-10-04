using CellBridge.FssHttp;
using CellBridge.Storage;

namespace CellBridge.AspNetCore;

/// <summary>
/// Resolves an MS-FSSHTTP Request to the document it addresses.
///
/// ResourceID is the stable identity for a document. When the request supplies
/// that identity and enables its use, the URL is only an echoed hint and must not
/// be used as a fallback. This preserves the document's lock and coauthoring
/// state when a client sends a stale or differently escaped URL.
/// </summary>
public static class DocumentRequestResolver
{
    /// <summary>Resolves a request without creating a document for a miss.</summary>
    public static StoredDocument? Resolve(DocumentStore store, FssHttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(request);

        if (request.UseResourceId && request.ResourceId is not null)
        {
            if (string.IsNullOrWhiteSpace(request.ResourceId) ||
                !Guid.TryParse(request.ResourceId, out var resourceId))
            {
                return null;
            }

            return store.List().FirstOrDefault(document => document.TransitionId == resourceId);
        }

        return string.IsNullOrWhiteSpace(request.Url) ? null : store.Get(request.Url);
    }

    /// <summary>
    /// Builds the public URL for a stored document, escaping each path segment
    /// exactly once. In particular, a literal '%' in a stored filename becomes
    /// '%25', while a space becomes '%20'.
    /// </summary>
    public static string CanonicalUrl(StoredDocument document, string publicOrigin)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(publicOrigin);

        var path = document.Url.StartsWith('/') ? document.Url : "/" + document.Url;
        var escapedPath = string.Join('/',
            path.Split('/', StringSplitOptions.None).Select(Uri.EscapeDataString));
        return publicOrigin.TrimEnd('/') + escapedPath;
    }
}
