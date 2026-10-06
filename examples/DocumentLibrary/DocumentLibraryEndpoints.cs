using System.Net;
using System.Security.Claims;
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
        app.MapGet("/local-login", (HttpContext context, IAntiforgery antiforgery) =>
        {
            if (!LocalIdentityAllowed(context, environment)) return Results.NotFound();
            var token = antiforgery.GetAndStoreTokens(context).RequestToken!;
            return Html(LoginPage(token));
        }).AllowAnonymous();

        app.MapPost("/local-login", async (HttpContext context, IAntiforgery antiforgery) =>
        {
            if (!LocalIdentityAllowed(context, environment)) return Results.NotFound();
            await antiforgery.ValidateRequestAsync(context);
            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            var user = form["user"].ToString();
            var identity = LocalIdentity(user);
            if (identity is null) return Results.BadRequest("Unknown local demo identity.");
            await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
                new ClaimsPrincipal(identity), new AuthenticationProperties { IsPersistent = false });
            return Results.Redirect("/");
        }).AllowAnonymous();

        app.MapPost("/local-logout", async (HttpContext context, IAntiforgery antiforgery) =>
        {
            await antiforgery.ValidateRequestAsync(context);
            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Redirect("/local-login");
        }).RequireAuthorization();

        app.MapGet("/", async (HttpContext context, DocumentLibraryService library, IAntiforgery antiforgery) =>
        {
            var actor = RequireActor(context);
            var documents = await library.ListAsync(actor, context.RequestAborted);
            var token = antiforgery.GetAndStoreTokens(context).RequestToken!;
            return Html(LibraryPage(context, documents, token,
                context.User.HasClaim("document-library:permission-admin", "true")));
        }).RequireAuthorization();

        app.MapPost("/library/upload", async (HttpContext context, DocumentLibraryService library,
            IAntiforgery antiforgery) =>
        {
            await antiforgery.ValidateRequestAsync(context);
            var actor = RequireActor(context);
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

    private static ClaimsIdentity? LocalIdentity(string user)
    {
        var subject = user switch
        {
            "owner" => DocumentLibraryService.OwnerSubject,
            "editor" => DocumentLibraryService.EditorSubject,
            "reader" => DocumentLibraryService.ReaderSubject,
            _ => null,
        };
        if (subject is null) return null;
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, user),
            new(CellBridgeActor.SubjectClaim, subject),
            new(CellBridgeActor.DisplayNameClaim, "Local " + user),
        };
        if (user == "owner")
        {
            claims.Add(new(CellBridgeActor.CreateClaim, "true"));
            claims.Add(new("document-library:permission-admin", "true"));
        }
        return new(claims, CookieAuthenticationDefaults.AuthenticationScheme, ClaimTypes.Name, ClaimTypes.Role);
    }

    private static bool LocalIdentityAllowed(HttpContext context, IHostEnvironment environment) =>
        environment.IsDevelopment() || environment.IsEnvironment("Testing")
            ? context.Connection.RemoteIpAddress is null || IPAddress.IsLoopback(context.Connection.RemoteIpAddress)
            : false;

    private static CellBridgeActor RequireActor(HttpContext context) =>
        CellBridgeActor.FromPrincipal(context.User)
        ?? throw new UnauthorizedAccessException("A mapped CellBridge subject is required.");

    private static IResult Html(string body) => Results.Content(body, "text/html; charset=utf-8");
    private static string E(string value) => HtmlEncoder.Default.Encode(value);
    private static string HiddenToken(string token) =>
        $"<input type=\"hidden\" name=\"__RequestVerificationToken\" value=\"{E(token)}\">";

    private static string LoginPage(string token) => Page("Local sign-in", $$"""
        <h1>CellBridge document library</h1>
        <p class="warning">Local demo identities only. Do not expose this authentication setup to the Internet.</p>
        <form method="post" action="/local-login">
          {{HiddenToken(token)}}
          <label>Identity <select name="user"><option value="owner">Owner</option><option value="editor">Editor</option><option value="reader">Reader</option></select></label>
          <button type="submit">Sign in</button>
        </form>
        """);

    private static string LibraryPage(HttpContext context, IReadOnlyList<LibraryDocument> documents,
        string token, bool permissionAdmin)
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

        return Page("Documents", $$"""
            <h1>Documents</h1>
            <p class="warning">This sample uses local demo identities. It is not an Internet-ready authentication setup.</p>
            <form method="post" action="/local-logout">{{HiddenToken(token)}}<button type="submit">Sign out</button></form>
            <h2>Upload</h2>
            <form method="post" action="/library/upload" enctype="multipart/form-data">{{HiddenToken(token)}}<input type="file" name="file" accept=".docx,.xlsx,.pptx" required><button type="submit">Upload</button></form>
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
        <!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>{{E(title)}}</title>
        <style>body{font:16px system-ui,sans-serif;max-width:1100px;margin:2rem auto;padding:0 1rem;color:#18202a}table{border-collapse:collapse;width:100%}th,td{border:1px solid #cad0d8;padding:.6rem;text-align:left;vertical-align:top}form{margin:.8rem 0}button,input,select{font:inherit;padding:.35rem}.warning{border-left:4px solid #b45309;background:#fff7ed;padding:.75rem}</style></head><body>{{body}}</body></html>
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
