using CellBridge.AspNetCore;
using CellBridge.Storage.Abstractions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CellBridge.WebDemo.Pages;

public sealed class DeleteModel(CellBridgeDocumentService service) : PageModel
{
    public string FileName { get; private set; } = "";
    public string? ErrorMessage { get; private set; }
    [BindProperty] public long LifecycleGeneration { get; set; }
    [BindProperty] public long StateVersion { get; set; }
    [BindProperty] public bool Confirmed { get; set; }
    [TempData] public string? CreatedMessage { get; set; }

    public async Task<IActionResult> OnGetAsync(Guid resourceId, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        return await LoadAsync(resourceId, cancellationToken) ? Page() : NotFound();
    }

    public async Task<IActionResult> OnPostAsync(Guid resourceId, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        var actor = CellBridgeActor.FromPrincipal(User);
        if (actor is null) return Unauthorized();
        if (!Confirmed)
        {
            if (!await LoadAsync(resourceId, cancellationToken)) return NotFound();
            ErrorMessage = "Confirm that you want to delete this document.";
            Response.StatusCode = StatusCodes.Status400BadRequest;
            return Page();
        }

        DocumentDeletionStatus result;
        try
        {
            result = await service.DeleteDocumentAsync(resourceId, LifecycleGeneration, StateVersion,
                actor, cancellationToken);
        }
        catch (UnauthorizedAccessException) { return NotFound(); }
        catch (ArgumentException) { return BadRequest(); }

        if (result == DocumentDeletionStatus.Deleted)
        {
            CreatedMessage = "Document deleted.";
            return RedirectToPage("/Index");
        }
        if (result == DocumentDeletionStatus.NotFound || !await LoadAsync(resourceId, cancellationToken))
            return NotFound();

        ErrorMessage = "The document changed or still has an active editor or lock. Close it in Office, refresh this page, and try again.";
        Response.StatusCode = StatusCodes.Status409Conflict;
        return Page();
    }

    private async Task<bool> LoadAsync(Guid resourceId, CancellationToken cancellationToken)
    {
        var actor = CellBridgeActor.FromPrincipal(User);
        if (actor is null) return false;
        DocumentHead document;
        try { document = await service.GetDocumentMetadataAsync(resourceId, actor, cancellationToken); }
        catch (UnauthorizedAccessException) { return false; }
        catch (KeyNotFoundException) { return false; }
        if (document.Security.Owner != actor.Identity.Subject) return false;
        FileName = Path.GetFileName(document.Path);
        LifecycleGeneration = document.LifecycleGeneration;
        StateVersion = document.StateVersion;
        return true;
    }
}
