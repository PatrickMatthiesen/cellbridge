using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace OfficeCollabServer.Demo.Pages;

public sealed class IndexModel(
    DocumentCatalogClient catalogClient,
    IOptions<CollabServerOptions> options,
    ILogger<IndexModel> logger) : PageModel
{
    public IReadOnlyList<DocumentCard> Documents { get; private set; } = [];
    public bool IsUnavailable { get; private set; }
    public string? ErrorMessage { get; private set; }
    public string? CreateError { get; private set; }
    [BindProperty] public string NewName { get; set; } = "";
    [BindProperty] public string NewType { get; set; } = "docx";
    [TempData] public string? CreatedMessage { get; set; }

    public async Task<IActionResult> OnPostCreateAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(NewName) || NewName.Length > 120 ||
            NewType is not ("docx" or "xlsx" or "pptx"))
        {
            CreateError = "Enter a name of up to 120 characters and choose a document type.";
        }
        else
        {
            CreateError = await catalogClient.CreateDocumentAsync(NewName.Trim(), NewType, cancellationToken);
        }
        if (CreateError is not null)
        {
            await OnGetAsync(cancellationToken);
            return Page();
        }
        CreatedMessage = "Document created. Use its Open button to launch Office.";
        return RedirectToPage();
    }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var result = await catalogClient.GetDocumentsAsync(cancellationToken);
        if (!result.IsAvailable)
        {
            IsUnavailable = true;
            ErrorMessage = "The collaboration server is unavailable. Check its address and try again.";
            return;
        }

        Uri publicBaseUri;
        try
        {
            publicBaseUri = options.Value.GetPublicBaseUri();
        }
        catch (InvalidOperationException exception)
        {
            logger.LogWarning(exception, "The collaboration server public URL is invalid.");
            IsUnavailable = true;
            ErrorMessage = "The collaboration server address is not configured correctly.";
            return;
        }

        Documents = result.Documents
            .Select(document => ToCard(document, publicBaseUri))
            .OrderBy(document => document.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static DocumentCard ToCard(CollabDocument document, Uri publicBaseUri)
    {
        var hasSafeUri = DocumentLinks.TryBuildDocumentUri(publicBaseUri, document.Path, out var documentUri);
        var application = DocumentLinks.GetApplication(document.Name, document.Path);
        var officeLaunchUrl = hasSafeUri ? DocumentLinks.BuildOfficeLaunchUrl(application, documentUri) : null;

        return new DocumentCard(
            string.IsNullOrWhiteSpace(document.Name) ? document.Path : document.Name,
            document.Path,
            document.ContentType,
            FormatSize(document.Size),
            FormatModified(document.LastModifiedUtc),
            document.Version,
            Math.Max(0, document.ActiveEditors),
            application,
            hasSafeUri ? DocumentLinks.BuildDownloadUri(documentUri) : null,
            officeLaunchUrl);
    }

    private static string FormatSize(long size) => size switch
    {
        < 0 => "Size unavailable",
        < 1024 => $"{size:N0} B",
        < 1024 * 1024 => $"{size / 1024d:N1} KB",
        _ => $"{size / (1024d * 1024d):N1} MB",
    };

    private static string FormatModified(DateTime value) => value == default
        ? "Modified date unavailable"
        : $"Updated {value.ToUniversalTime().ToString("dd MMM yyyy, HH:mm", CultureInfo.InvariantCulture)} UTC";
}
