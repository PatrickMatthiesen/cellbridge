using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using CellBridge.AspNetCore;
using CellBridge.Authentication;

namespace CellBridge.WebDemo.Pages;

public sealed class IndexModel(
    DocumentWorkspace workspace,
    AuthenticationOrigin origin,
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
        if (!User.HasClaim("cellbridge:create", "true")) return Forbid();
        if (string.IsNullOrWhiteSpace(NewName) || NewName.Length > 120 ||
            NewType is not ("docx" or "xlsx" or "pptx"))
        {
            CreateError = "Enter a name of up to 120 characters and choose a document type.";
        }
        else
        {
            var actor = CellBridgeActor.FromPrincipal(User);
            if (actor is null) return Unauthorized();
            try
            {
                var result = await workspace.CreateAsync(NewName.Trim(), NewType, actor, cancellationToken);
                CreateError = result.Error;
            }
            catch (CellBridge.Storage.Abstractions.StorageUnavailableException exception)
            {
                logger.LogWarning(exception, "Unable to create a document.");
                CreateError = "The document store is unavailable. Refresh the library before retrying to check whether the file was created.";
            }
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
        Response.Headers.CacheControl = "no-store";
        var actor = CellBridgeActor.FromPrincipal(User);
        if (actor is null)
        {
            IsUnavailable = true;
            ErrorMessage = "Your sign-in does not identify a document user.";
            return;
        }
        var documents = new List<DocumentListing>();
        int offset = 0;
        try
        {
            while (true)
            {
                var page = await workspace.ReadAsync(actor, offset, 1000, cancellationToken);
                documents.AddRange(page.Documents);
                if (page.NextOffset is not { } next) break;
                offset = next;
            }
        }
        catch (CellBridge.Storage.Abstractions.StorageUnavailableException exception)
        {
            logger.LogWarning(exception, "The document library is unavailable.");
            IsUnavailable = true;
            ErrorMessage = "The document library is unavailable. Try again.";
            return;
        }
        var publicBaseUri = new Uri(origin.Value + "/");

        Documents = documents
            .Select(document => ToCard(document, publicBaseUri))
            .OrderBy(document => document.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static DocumentCard ToCard(DocumentListing document, Uri publicBaseUri)
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
