using CellBridge.FssHttp;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;

namespace CellBridge.AspNetCore;

/// <summary>
/// Resolves an MS-FSSHTTP Request to the document it addresses.
///
/// ResourceID is the stable identity for a document. When the request supplies
/// that identity and enables its use, the URL is only an echoed hint and must not
/// be used as a fallback. If UseResourceID is true but ResourceID is omitted,
/// the URL identifies the initial request and the response can establish the ID.
/// This preserves document state when a client later sends a stale URL.
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
            if (!Guid.TryParse(request.ResourceId, out var resourceId))
            {
                return null;
            }

            return store.List().FirstOrDefault(document => document.TransitionId == resourceId);
        }

        return string.IsNullOrWhiteSpace(request.Url) ? null : store.Get(request.Url);
    }

    public static string CanonicalUrl(DocumentHead document, string publicOrigin)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(publicOrigin);
        return publicOrigin.TrimEnd('/') + string.Join('/', document.Path.Split('/', StringSplitOptions.None).Select(Uri.EscapeDataString));
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
