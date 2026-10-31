using CellBridge.Storage;

namespace CellBridge.WebDemo;

/// <summary>JSON body accepted by the demo's document creation endpoint.</summary>
public sealed record CreateDocumentRequest(string? Name, string? Type);

/// <summary>Outcome returned by <see cref="DocumentCreation.TryCreate"/>.</summary>
public sealed record DocumentCreationResult(
    bool Created,
    bool Conflict,
    string? Error,
    DocumentListing? Document)
{
    public static DocumentCreationResult Invalid(string error) => new(false, false, error, null);
    public static DocumentCreationResult AlreadyExists(string name) =>
        new(false, true, $"A document named '{name}' already exists.", null);
}

/// <summary>
/// Creates demo library documents using the shared blank Office templates.
/// </summary>
public static class DocumentCreation
{
    private static readonly HashSet<string> SupportedTypes =
        new(StringComparer.OrdinalIgnoreCase) { "docx", "xlsx", "pptx" };

    public static DocumentCreationResult TryCreate(DocumentStore store, string? name, string? type)
    {
        ArgumentNullException.ThrowIfNull(store);

        if (string.IsNullOrWhiteSpace(type) || !SupportedTypes.Contains(type.Trim().TrimStart('.')))
            return DocumentCreationResult.Invalid("Type must be one of: docx, xlsx, pptx.");

        var extension = type.Trim().TrimStart('.').ToLowerInvariant();
        if (!TryNormalizeName(name, extension, out var fileName, out var error))
            return DocumentCreationResult.Invalid(error!);

        var content = extension switch
        {
            "docx" => OfficeDocumentTemplates.CreateWordDocument(),
            "xlsx" => OfficeDocumentTemplates.CreateExcelWorkbook(),
            "pptx" => OfficeDocumentTemplates.CreatePowerPointPresentation(),
            _ => throw new InvalidOperationException("Unsupported document type."),
        };

        // Store URL-special characters escaped. DocumentStore normalizes the
        // key back to the display name, while HTTP callers still get a safe
        // URL for names containing spaces, %, #, or &.
        var url = "/shared/" + Uri.EscapeDataString(fileName);
        if (!store.TryAdd(url, content, out var document))
            return DocumentCreationResult.AlreadyExists(fileName);

        lock (document)
        {
            return new DocumentCreationResult(true, false, null,
                new DocumentListing(document.Url, fileName, DocumentLibrary.ContentType(fileName),
                    document.ContentLength, document.ContentVersion,
                    document.LastModifiedUtc, document.Sessions.Count));
        }
    }

    private static bool TryNormalizeName(string? requested, string extension,
        out string fileName, out string? error)
    {
        fileName = string.Empty;
        error = null;
        if (string.IsNullOrWhiteSpace(requested))
        {
            error = "Name is required.";
            return false;
        }

        var value = requested.Trim();
        if (value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            value.Contains('/') || value.Contains('\\') || value is "." or ".." ||
            value.Any(char.IsControl))
        {
            error = "Name must be a safe file name without path separators.";
            return false;
        }

        var existingExtension = Path.GetExtension(value);
        if (string.IsNullOrEmpty(existingExtension))
        {
            fileName = value + "." + extension;
        }
        else if (!string.Equals(existingExtension, "." + extension,
                     StringComparison.OrdinalIgnoreCase))
        {
            error = $"Name must use the .{extension} extension.";
            return false;
        }
        else
        {
            fileName = value;
        }

        if (fileName.Length > 180)
        {
            error = "Name is too long.";
            return false;
        }

        var stem = Path.GetFileNameWithoutExtension(fileName).TrimEnd('.', ' ');
        if (stem.Length == 0 || stem.EndsWith(' ') || fileName.EndsWith('.') || fileName.EndsWith(' '))
        {
            error = "Name must contain a file name and cannot end with a dot or space.";
            return false;
        }

        var reserved = stem.ToUpperInvariant();
        if (reserved is "CON" or "PRN" or "AUX" or "NUL" ||
            (reserved.Length == 4 && reserved[0] == 'C' && reserved[1] == 'O' && reserved[2] == 'M' && reserved[3] is >= '1' and <= '9') ||
            (reserved.Length == 4 && reserved[0] == 'L' && reserved[1] == 'P' && reserved[2] == 'T' && reserved[3] is >= '1' and <= '9'))
        {
            error = "That name is reserved by Windows.";
            return false;
        }

        return true;
    }
}
