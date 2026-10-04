using CellBridge.AspNetCore;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;

namespace CellBridge.Admin;

/// <summary>Explicit flat-directory import. Existing saved revisions always take precedence.</summary>
public static class DocumentImports
{
    public static async Task<(int Imported, int Skipped)> ImportDirectoryAsync(
        CellBridgeDocumentService service, string directory, SubjectIdentity owner,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException($"Import directory does not exist: {directory}");
        var importer = new CellBridgeActor(new SubjectIdentity("system:imports", "imports", "Document imports"), CanCreate: true);
        int imported = 0, skipped = 0;
        foreach (var file in Directory.EnumerateFiles(directory).Order(StringComparer.OrdinalIgnoreCase))
        {
            if (Path.GetFileName(file).StartsWith("~$", StringComparison.Ordinal)) continue;
            var path = "/shared/" + Uri.EscapeDataString(Path.GetFileName(file));
            if (await service.Provider.State.FindByPathKeyAsync(StorageIds.PathKey(DocumentStore.NormalizeUrl(path)), cancellationToken) is not null)
            {
                skipped++;
                continue;
            }
            var bytes = await File.ReadAllBytesAsync(file, cancellationToken);
            if (await service.ImportAsync(path, bytes, owner, importer, cancellationToken) is null) skipped++;
            else imported++;
        }
        return (imported, skipped);
    }
}
