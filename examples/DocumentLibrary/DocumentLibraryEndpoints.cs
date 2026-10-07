using CellBridge.AspNetCore;
using CellBridge.Storage.Abstractions;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace CellBridge.DocumentLibrary;

public static class DocumentLibraryEndpoints
{
    public static void Map(WebApplication app, IHostEnvironment environment)
    {
        app.MapGet("/local-login", (HttpContext context) =>
            Results.Redirect("/auth/login" + context.Request.QueryString)).AllowAnonymous();

        app.MapPost("/local-logout", async (HttpContext context, IAntiforgery antiforgery) =>
        {
            await antiforgery.ValidateRequestAsync(context);
            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Redirect("/auth/login");
        }).RequireAuthorization();

        app.MapGet("/", async (HttpContext context, DocumentLibraryService library, IAntiforgery antiforgery) =>
        {
            var actor = RequireActor(context);
            var documents = await library.ListAsync(actor, context.RequestAborted);
            var token = antiforgery.GetAndStoreTokens(context).RequestToken!;
            return Html(DocumentLibraryPage.Library(context, documents, token,
                actor.CanCreate,
                context.User.HasClaim("document-library:permission-admin", "true")));
        }).RequireAuthorization();

        app.MapPost("/library/create", async (HttpContext context, DocumentLibraryService library,
            IAntiforgery antiforgery) =>
        {
            try { await antiforgery.ValidateRequestAsync(context); }
            catch (AntiforgeryValidationException) { return CreationError("Refresh the page and try again.", 400); }
            var actor = RequireActor(context);
            if (!actor.CanCreate) return Results.StatusCode(403);
            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            try
            {
                await library.CreateBlankAsync(form["name"], form["type"], actor, context.RequestAborted);
                return Results.Redirect("/");
            }
            catch (ArgumentException error) { return CreationError(error.Message, 400); }
            catch (DocumentNameConflictException error) { return CreationError(error.Message, 409); }
        }).RequireAuthorization();

        app.MapPost("/library/upload", async (HttpContext context, DocumentLibraryService library,
            IAntiforgery antiforgery) =>
        {
            await antiforgery.ValidateRequestAsync(context);
            var actor = RequireActor(context);
            if (!actor.CanCreate) return Results.StatusCode(403);
            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            var file = form.Files.GetFile("file");
            if (file is null || file.Length == 0) return Results.BadRequest("Choose a non-empty Office file.");
            await using var stream = file.OpenReadStream();
            await library.UploadAsync(file.FileName, stream, actor, context.RequestAborted);
            return Results.Redirect("/");
        }).RequireAuthorization();

        app.MapGet("/library/files/{resourceId:guid}/download", async (Guid resourceId, HttpContext context,
            DocumentLibraryService library) =>
        {
            try
            {
                var actor = RequireActor(context);
                var manifest = await context.RequestServices.GetRequiredService<DocumentLibraryDestination>()
                    .GetAsync(resourceId, context.RequestAborted);
                var stream = await library.OpenDeliveredAsync(resourceId, actor, context.RequestAborted);
                return Results.Stream(stream, ContentType(manifest.FileName), manifest.FileName,
                    enableRangeProcessing: true);
            }
            catch (UnauthorizedAccessException) { return Results.StatusCode(403); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (FileNotFoundException) { return Results.NotFound(); }
        }).RequireAuthorization();

        app.MapGet("/library/files/{resourceId:guid}/history", async (Guid resourceId, HttpContext context,
            DocumentLibraryService library, IAntiforgery antiforgery) =>
        {
            try
            {
                var revisions = await library.HistoryAsync(resourceId, RequireActor(context), context.RequestAborted);
                var token = antiforgery.GetAndStoreTokens(context).RequestToken!;
                return Html(DocumentLibraryPage.History(context, resourceId, revisions, token));
            }
            catch (UnauthorizedAccessException) { return Results.StatusCode(403); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
        }).RequireAuthorization();

        app.MapPost("/library/files/{resourceId:guid}/permissions", async (Guid resourceId, HttpContext context,
            DocumentLibraryService library, IAntiforgery antiforgery) =>
        {
            await antiforgery.ValidateRequestAsync(context);
            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            var subject = form["subject"].ToString();
            var access = form["access"].ToString() switch
            {
                "none" => DocumentAccess.None,
                "read" => DocumentAccess.Read,
                "write" => DocumentAccess.Write,
                _ => (DocumentAccess)(-1),
            };
            if (subject is not (DocumentLibraryService.EditorSubject or DocumentLibraryService.ReaderSubject) ||
                access == (DocumentAccess)(-1)) return Results.BadRequest("Invalid permission update.");
            await library.SetPermissionAsync(resourceId, subject, access, context.RequestAborted);
            return Results.Redirect("/");
        }).RequireAuthorization("permission-admin");
    }

    private static CellBridgeActor RequireActor(HttpContext context) =>
        CellBridgeActor.FromPrincipal(context.User)
        ?? throw new UnauthorizedAccessException("A mapped CellBridge subject is required.");

    private static IResult CreationError(string message, int statusCode) => Results.Content(
        DocumentLibraryPage.Error(message), "text/html; charset=utf-8", statusCode: statusCode);

    private static IResult Html(string body) => Results.Content(body, "text/html; charset=utf-8");

    private static string ContentType(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        _ => "application/octet-stream",
    };
}
