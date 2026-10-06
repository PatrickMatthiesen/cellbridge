namespace CellBridge.DocumentLibrary;

public sealed class DocumentNameConflictException(string message) : InvalidOperationException(message);

internal static class DocumentCreation
{
    public static string FileName(string? name, string? type)
    {
        var extension = type?.Trim().TrimStart('.').ToLowerInvariant();
        if (extension is not ("docx" or "xlsx" or "pptx"))
            throw new ArgumentException("Choose Word, Excel or PowerPoint.");
        var value = name?.Trim();
        if (string.IsNullOrWhiteSpace(value) || value.Any(c => char.IsControl(c) || "<>:\"/\\|?*".Contains(c)) ||
            value.EndsWith('.') || value is "." or "..")
            throw new ArgumentException("Enter a file name without reserved characters or a trailing dot.");
        var existing = Path.GetExtension(value);
        if (existing.Length == 0) value += "." + extension;
        else if (!string.Equals(existing, "." + extension, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Use the .{extension} extension for this document type.");
        if (value.Length > 180) throw new ArgumentException("The file name must be 180 characters or fewer.");
        var stem = value.Split('.')[0].TrimEnd(' ', '.').ToUpperInvariant();
        if (stem.Length == 0 || stem is "CON" or "PRN" or "AUX" or "NUL" ||
            stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && stem[3] is >= '1' and <= '9')
            throw new ArgumentException("Choose a file name that is not reserved by Windows.");
        return value;
    }

    public static byte[] Content(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".docx" => MinimalDocx.Create(string.Empty),
        ".xlsx" => MinimalXlsx.Create(),
        ".pptx" => MinimalPptx.Create(),
        _ => throw new ArgumentException("Unsupported Office document type."),
    };
}
