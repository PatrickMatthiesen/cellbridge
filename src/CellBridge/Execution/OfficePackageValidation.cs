using System.IO.Compression;
using CellBridge.Storage.Abstractions;

namespace CellBridge.AspNetCore;

/// <summary>Validates package structure and expanded size before publication.</summary>
internal static class OfficePackageValidation
{
    private static ZipArchive OpenDocumentArchive(Stream content, string documentUrl)
    {
        content.Position = 0;
        var archive = new ZipArchive(content, ZipArchiveMode.Read, leaveOpen: true);
        try
        {
            var mainPart = Path.GetExtension(documentUrl).ToLowerInvariant() switch
            {
                ".docx" => "word/document.xml",
                ".xlsx" => "xl/workbook.xml",
                ".pptx" => "ppt/presentation.xml",
                _ => throw new InvalidDataException("The proposed file type is not supported."),
            };
            if (archive.GetEntry("[Content_Types].xml") is null || archive.GetEntry(mainPart) is null)
                throw new InvalidDataException("The proposed package does not match the document's file type.");
            return archive;
        }
        catch
        {
            archive.Dispose();
            throw;
        }
    }

    internal static async ValueTask ValidateDocumentAsync(Stream content, string documentUrl,
        long maxExpandedBytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var archive = OpenDocumentArchive(content, documentUrl);
        long expanded = 0;
        var buffer = new byte[65536];
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.Length > maxExpandedBytes - expanded)
                throw new StorageQuotaExceededException("expanded package bytes", checked(expanded + entry.Length), maxExpandedBytes);
            await using var stream = entry.Open();
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken)) != 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                expanded = checked(expanded + read);
                StorageLimits.Check("expanded package bytes", expanded, maxExpandedBytes);
            }
        }
    }

}
