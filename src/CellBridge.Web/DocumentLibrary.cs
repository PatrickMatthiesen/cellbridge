using Microsoft.AspNetCore.StaticFiles;
using CellBridge.Storage;

namespace CellBridge.Web;

/// <summary>The demo's catalog and startup loader. This is not a SharePoint API.</summary>
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

    /// <summary>Loads top-level files on startup; subsequent edits remain in memory.</summary>
    public static void Seed(DocumentStore store, IConfiguration configuration, string contentRoot)
    {
        if (configuration.GetValue("Documents:SeedExamples", true))
        {
            store.Put("/shared/test.docx", MinimalDocx.Create());
            store.Put("/shared/save-test.docx", MinimalDocx.Create("Save test for CellBridge"));
            store.Put("/shared/save-check.docx", MinimalDocx.Create("Save check for CellBridge"));
        }

        var configuredDirectory = configuration["Documents:Directory"];
        if (string.IsNullOrWhiteSpace(configuredDirectory)) return;
        var directory = Path.GetFullPath(configuredDirectory, contentRoot);
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException($"Documents:Directory does not exist: {directory}");

        foreach (var path in Directory.EnumerateFiles(directory).Order(StringComparer.OrdinalIgnoreCase))
        {
            // Office lock files are not documents. Keep the library flat to match /shared/{fileName}.
            if (Path.GetFileName(path).StartsWith("~$", StringComparison.Ordinal)) continue;
            store.Put("/shared/" + Uri.EscapeDataString(Path.GetFileName(path)), File.ReadAllBytes(path));
        }
    }
}

public sealed record DocumentListing(string Path, string Name, string ContentType, long Size,
    uint Version, DateTime LastModifiedUtc, int ActiveEditors);
