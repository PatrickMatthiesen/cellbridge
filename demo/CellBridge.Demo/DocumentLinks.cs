namespace CellBridge.Demo;

public enum OfficeApplication
{
    None,
    Word,
    Excel,
    PowerPoint,
}

public static class DocumentLinks
{
    private static readonly HashSet<string> WordExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".doc", ".docx", ".docm", ".dot", ".dotx", ".dotm",
    };

    private static readonly HashSet<string> ExcelExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".xls", ".xlsx", ".xlsm", ".xlsb", ".xlt", ".xltx", ".xltm", ".xlam",
    };

    private static readonly HashSet<string> PowerPointExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ppt", ".pptx", ".pptm", ".pot", ".potx", ".potm", ".pps", ".ppsx", ".ppsm",
    };

    public static OfficeApplication GetApplication(string name, string path)
    {
        var extension = Path.GetExtension(name);
        if (WordExtensions.Contains(extension)) return OfficeApplication.Word;
        if (ExcelExtensions.Contains(extension)) return OfficeApplication.Excel;
        if (PowerPointExtensions.Contains(extension)) return OfficeApplication.PowerPoint;

        extension = Path.GetExtension(path);
        if (WordExtensions.Contains(extension)) return OfficeApplication.Word;
        if (ExcelExtensions.Contains(extension)) return OfficeApplication.Excel;
        if (PowerPointExtensions.Contains(extension)) return OfficeApplication.PowerPoint;
        return OfficeApplication.None;
    }

    public static bool TryBuildDocumentUri(Uri baseUri, string path, out Uri documentUri)
    {
        documentUri = default!;
        if (baseUri is null || !baseUri.IsAbsoluteUri ||
            (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrWhiteSpace(path) || !path.StartsWith('/') || path.StartsWith("//") ||
            path.Contains('\\') || path.Any(char.IsControl))
        {
            return false;
        }

        var segments = path.Split('/', StringSplitOptions.None);
        if (segments.Length < 2 || segments.Any(segment => segment is "." or ".."))
        {
            return false;
        }

        var encodedPath = string.Join('/', segments.Skip(1).Select(Uri.EscapeDataString));
        documentUri = new Uri(baseUri, encodedPath);
        return documentUri.Scheme == baseUri.Scheme &&
               string.Equals(documentUri.Host, baseUri.Host, StringComparison.OrdinalIgnoreCase) &&
               documentUri.Port == baseUri.Port;
    }

    public static string? BuildOfficeLaunchUrl(OfficeApplication application, Uri documentUri) =>
        application switch
        {
            OfficeApplication.Word => $"ms-word:ofe|u|{documentUri.AbsoluteUri}",
            OfficeApplication.Excel => $"ms-excel:ofe|u|{documentUri.AbsoluteUri}",
            OfficeApplication.PowerPoint => $"ms-powerpoint:ofe|u|{documentUri.AbsoluteUri}",
            _ => null,
        };

    public static Uri BuildDownloadUri(Uri documentUri)
    {
        var builder = new UriBuilder(documentUri);
        var query = builder.Query.TrimStart('?');
        builder.Query = string.IsNullOrEmpty(query) ? "download=true" : $"{query}&download=true";
        return builder.Uri;
    }
}

public sealed record DocumentCard(
    string Name,
    string Path,
    string ContentType,
    string SizeLabel,
    string ModifiedLabel,
    uint Version,
    int ActiveEditors,
    OfficeApplication Application,
    Uri? DownloadUri,
    string? OfficeLaunchUrl)
{
    public string ApplicationLabel => Application switch
    {
        OfficeApplication.Word => "Word",
        OfficeApplication.Excel => "Excel",
        OfficeApplication.PowerPoint => "PowerPoint",
        _ => "File",
    };

    public string FileKind => string.IsNullOrWhiteSpace(ContentType)
        ? "Unknown format"
        : ContentType.Split(';', 2)[0];
}
