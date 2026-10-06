using System.Text;
using System.Text.Encodings.Web;
using CellBridge.AspNetCore;
using CellBridge.Storage.Abstractions;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http.Extensions;

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
            return Html(LibraryPage(context, documents, token,
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
                return Html(HistoryPage(resourceId, revisions, token));
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
        Page("Could not create document", $"<h1>Could not create document</h1><p>{E(message)}</p><a href=\"/\">Back to documents</a>"),
        "text/html; charset=utf-8", statusCode: statusCode);

    private static IResult Html(string body) => Results.Content(body, "text/html; charset=utf-8");
    private static string E(string value) => HtmlEncoder.Default.Encode(value);
    private static string HiddenToken(string token) =>
        $"<input type=\"hidden\" name=\"__RequestVerificationToken\" value=\"{E(token)}\">";

    private static string LibraryPage(HttpContext context, IReadOnlyList<LibraryDocument> documents,
        string token, bool canCreate, bool permissionAdmin)
    {
        var rows = new StringBuilder();
        foreach (var document in documents)
        {
            var url = UriHelper.BuildAbsolute(context.Request.Scheme, context.Request.Host,
                context.Request.PathBase, document.CellBridgePath);
            var office = OfficeScheme(document.FileName);
            var open = office is null ? "" : $"<a href=\"{office}:ofe|u|{E(url)}\">Open in desktop Office</a> · ";
            rows.Append($"<tr><td>{E(document.FileName)}</td><td>{document.ContentVersion}</td><td>{E(document.DeliveryStatus)}</td><td>{open}<a href=\"/library/files/{document.ResourceId:D}/download\">Download delivered file</a> · <a href=\"/library/files/{document.ResourceId:D}/history\">History</a></td></tr>");
            if (permissionAdmin)
            {
                rows.Append($"<tr><td colspan=\"4\"><form method=\"post\" action=\"/library/files/{document.ResourceId:D}/permissions\">{HiddenToken(token)}<label>Identity <select name=\"subject\"><option value=\"{DocumentLibraryService.EditorSubject}\">Editor</option><option value=\"{DocumentLibraryService.ReaderSubject}\">Reader</option></select></label> <label>Access <select name=\"access\"><option value=\"none\">None</option><option value=\"read\">Read</option><option value=\"write\">Write</option></select></label> <button type=\"submit\">Change permission</button></form></td></tr>");
            }
        }

        var upload = canCreate ? $$"""
            <div class="document-tools">
            <div class="tool"><h2>New document</h2>
            <form method="post" action="/library/create">
              {{HiddenToken(token)}}
              <label for="new-name">File name</label>
              <input id="new-name" name="name" placeholder="Untitled" maxlength="180" required>
              <label for="new-type">Document type</label>
              <select id="new-type" name="type"><option value="docx">Word document</option><option value="xlsx">Excel workbook</option><option value="pptx">PowerPoint presentation</option></select>
              <button type="submit">Create document</button>
            </form></div>
            <div class="tool"><h2>Upload a document</h2>
            <form method="post" action="/library/upload" enctype="multipart/form-data">{{HiddenToken(token)}}<input type="file" name="file" accept=".docx,.xlsx,.pptx" required><button type="submit">Upload</button></form></div>
            </div>
            """ : "";
        return Page("Documents", $$"""
            <h1>Documents</h1>
            <form method="post" action="/local-logout">{{HiddenToken(token)}}<button type="submit">Sign out</button></form>
            {{upload}}
            <h2>Library</h2>
            <table><thead><tr><th>File</th><th>CellBridge version</th><th>Save status</th><th>Actions</th></tr></thead><tbody>{{rows}}</tbody></table>
            """);
    }

    private static string HistoryPage(Guid resourceId, IReadOnlyList<DocumentRevision> revisions, string token)
    {
        var rows = string.Join("", revisions.Select(r =>
            $"<tr><td>{r.RevisionNumber}</td><td>{r.ContentVersion}</td><td>{E(r.Author.DisplayName)}</td><td>{r.CreatedUtc:O}</td><td><a href=\"/_cellbridge/history/{resourceId:D}/{r.LifecycleGeneration}/{r.RevisionNumber}\">Download</a></td></tr>"));
        return Page("History", $"<h1>History</h1><p><a href=\"/\">Back to library</a></p><table><thead><tr><th>Revision</th><th>Content version</th><th>Author</th><th>Created</th><th>File</th></tr></thead><tbody>{rows}</tbody></table>");
    }

    private static string Page(string title, string body) => $$"""
        <!doctype html><html lang="en"><head><meta charset="utf-8"><meta http-equiv="X-UA-Compatible" content="IE=edge"><meta name="viewport" content="width=device-width,initial-scale=1"><title>{{E(title)}}</title>
        <style>body{font:16px/1.5 "Segoe UI",Arial,sans-serif;max-width:1100px;margin:2rem auto;padding:0 24px;color:#202b3c;background:#f3f5f8}table{border-collapse:collapse;width:100%}th,td{border:1px solid #cad0d8;padding:.6rem;text-align:left;vertical-align:top}form{margin:.8rem 0}button,input,select{font:inherit;padding:10px 12px;border:1px solid #a7b2c3;border-radius:5px}button{background:#2457ce;color:#fff;border-color:#2457ce;cursor:pointer}.document-tools{display:flex;gap:24px;flex-wrap:wrap}.tool{flex:1;min-width:240px;padding:20px;background:#fff;border:1px solid #dbe1ea;border-radius:8px}.tool label{display:block;margin:12px 0 5px}.tool input,.tool select{width:100%;box-sizing:border-box}.tool button{margin-top:16px}h2{font-size:20px}table{background:#fff}.warning{border-left:4px solid #b45309;background:#fff7ed;padding:.75rem}</style></head><body>{{body}}</body></html>
        """;

    private static string? OfficeScheme(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".docx" => "ms-word",
        ".xlsx" => "ms-excel",
        ".pptx" => "ms-powerpoint",
        _ => null,
    };

    private static string ContentType(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        _ => "application/octet-stream",
    };
}
