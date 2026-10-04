using Microsoft.AspNetCore.StaticFiles;
using CellBridge.Storage;

namespace CellBridge.Web;

/// <summary>The demo's catalog formatting. This is not a SharePoint API.</summary>
public static class DocumentLibrary
{
    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    public static string ContentType(string path) =>
        ContentTypes.TryGetContentType(path, out var type) ? type : "application/octet-stream";

    public static DocumentListing[] List(DocumentStore store) => store.List()
        .Select(document =>
        {
            lock (document)
            {
                return new DocumentListing(document.Url, Path.GetFileName(document.Url),
                    ContentType(document.Url), document.ContentLength, document.ContentVersion,
                    document.LastModifiedUtc, document.Sessions.Count);
            }
        })
        .OrderBy(document => document.Path, StringComparer.OrdinalIgnoreCase)
        .ToArray();

}

public sealed record DocumentListing(string Path, string Name, string ContentType, long Size,
    uint Version, DateTime LastModifiedUtc, int ActiveEditors);
