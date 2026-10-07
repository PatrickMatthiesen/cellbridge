using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using CellBridge.Storage.Abstractions;
using Microsoft.AspNetCore.Http.Extensions;

namespace CellBridge.DocumentLibrary;

internal static class DocumentLibraryPage
{
    public static string Library(HttpContext context, IReadOnlyList<LibraryDocument> documents,
        string token, bool canCreate, bool permissionAdmin)
    {
        var rows = new StringBuilder();
        foreach (var document in documents)
        {
            var type = Path.GetExtension(document.FileName).TrimStart('.').ToLowerInvariant();
            var url = UriHelper.BuildAbsolute(context.Request.Scheme, context.Request.Host,
                context.Request.PathBase, document.CellBridgePath);
            var office = type switch { "docx" => "ms-word", "xlsx" => "ms-excel", "pptx" => "ms-powerpoint", _ => null };
            var openUrl = office is null ? $"/library/files/{document.ResourceId:D}/download" : $"{office}:ofe|u|{url}";
            var status = document.DeliveryBlock is not null ? "Needs attention"
                : document.PendingDeliveries > 0 ? "Updating file" : "Up to date";
            var statusClass = document.DeliveryBlock is not null ? "blocked" : document.PendingDeliveries > 0 ? "pending" : "delivered";
            var access = permissionAdmin ? $$"""
                <div class="access-form">
                  <p class="menu-heading">Change access</p>
                  <form method="post" action="/library/files/{{document.ResourceId:D}}/permissions">
                    {{HiddenToken(token)}}
                    <label>Account<select name="subject"><option value="{{DocumentLibraryService.EditorSubject}}">Editor</option><option value="{{DocumentLibraryService.ReaderSubject}}">Reader</option></select></label>
                    <label>New access<select name="access" required><option value="" selected disabled>Choose access</option><option value="none">Remove access</option><option value="read">Can view</option><option value="write">Can edit</option></select></label>
                    <button class="button secondary" type="submit">Apply access</button>
                  </form>
                </div>
                """ : "";
            rows.Append($$"""
                <tr data-document data-name="{{E(document.FileName)}}" data-type="{{E(type)}}" data-modified="{{new DateTimeOffset(document.ModifiedUtc.ToUniversalTime()).ToUnixTimeMilliseconds()}}" data-size="{{document.Length}}">
                  <td class="name-cell"><div class="file-name">{{FileIcon(type)}}<div><a class="document-name" href="{{E(openUrl)}}">{{E(document.FileName)}}</a><span class="file-kind">{{TypeLabel(type)}}</span></div></div></td>
                  <td class="modified-cell"><time datetime="{{document.ModifiedUtc.ToUniversalTime():O}}">{{document.ModifiedUtc.ToUniversalTime().ToString("dd MMM yyyy", CultureInfo.InvariantCulture)}}<span class="time-detail">{{document.ModifiedUtc.ToUniversalTime():HH:mm}} UTC</span></time></td>
                  <td class="size-cell">{{Size(document.Length)}}</td><td class="version-cell">{{document.ContentVersion}}</td>
                  <td class="status-cell"><span class="status {{statusClass}}"><span class="status-dot"></span>{{status}}</span></td>
                  <td class="actions-cell"><details class="action-menu"><summary aria-label="Actions for {{E(document.FileName)}}">{{Icon("more")}}</summary><div class="menu file-menu">
                    {{(office is null ? "" : $"<a href=\"{E(openUrl)}\">{Icon("open")}Open</a>")}}
                    <a href="/library/files/{{document.ResourceId:D}}/download">{{Icon("download")}}Download</a>
                    <a href="/library/files/{{document.ResourceId:D}}/history">{{Icon("history")}}Version history</a>
                    {{access}}
                  </div></details></td>
                </tr>
                """);
        }

        var creation = canCreate ? $$"""
            <details class="tool-menu"><summary class="button primary">{{Icon("plus")}}New document{{Icon("chevron")}}</summary>
              <div class="menu creation-menu"><h2>New document</h2>
                <form method="post" action="/library/create">{{HiddenToken(token)}}
                  <label for="new-type">Document type</label><select id="new-type" name="type"><option value="docx">Word document</option><option value="xlsx">Excel workbook</option><option value="pptx">PowerPoint presentation</option></select>
                  <label for="new-name">File name</label><input id="new-name" name="name" placeholder="Untitled" maxlength="180" required>
                  <button class="button primary" type="submit">Create document</button>
                </form>
              </div>
            </details>
            <details class="tool-menu"><summary class="button quiet">{{Icon("upload")}}Upload{{Icon("chevron")}}</summary>
              <div class="menu creation-menu"><h2>Upload a document</h2>
                <form method="post" action="/library/upload" enctype="multipart/form-data">{{HiddenToken(token)}}
                  <label for="upload-file">Choose file</label><input id="upload-file" type="file" name="file" accept=".docx,.xlsx,.pptx" required>
                  <button class="button primary" type="submit">Upload document</button>
                </form>
              </div>
            </details>
            """ : "";

        var body = $$"""
            <section class="workspace-heading"><div class="workspace-icon">{{Icon("folder")}}</div><h1>Documents</h1></section>
            <section class="library" aria-label="Document library">
              <div class="toolbar"><div class="toolbar-actions">{{creation}}<a class="button quiet refresh" href="/">{{Icon("refresh")}}Refresh</a></div>
                <label class="sort-control" hidden data-enhanced>Sort by<select id="sort"><option value="name">Name</option><option value="modified">Last modified</option><option value="size">File size</option></select></label>
              </div>
              <div class="list-heading"><div><h2 id="view-title">All documents</h2><span id="file-count" role="status" aria-live="polite">{{documents.Count}} {{(documents.Count == 1 ? "file" : "files")}}</span></div></div>
              <table class="file-table"><caption class="sr-only">Documents</caption><thead><tr><th scope="col">Name</th><th scope="col" class="modified-cell">Modified</th><th scope="col" class="size-cell">File size</th><th scope="col" class="version-cell">Version</th><th scope="col" class="status-cell">Status</th><th scope="col" class="actions-cell"><span class="sr-only">Actions</span></th></tr></thead><tbody id="documents">{{rows}}</tbody></table>
              <div class="empty-state" id="empty-state" {{(documents.Count > 0 ? "hidden" : "")}}>{{Icon("folder")}}<h3>{{(documents.Count > 0 ? "No matching documents" : "No documents")}}</h3></div>
            </section>
            """;
        return Page("Documents", body, context, token, true);
    }

    public static string History(HttpContext context, Guid resourceId, IReadOnlyList<DocumentRevision> revisions, string token)
    {
        var rows = string.Join("", revisions.Select(r => $$"""
            <tr><td><span class="revision-number">{{r.RevisionNumber}}</span></td><td>{{r.ContentVersion}}</td><td>{{E(r.Author.DisplayName)}}</td><td><time datetime="{{r.CreatedUtc:O}}">{{r.CreatedUtc.ToString("dd MMM yyyy, HH:mm", CultureInfo.InvariantCulture)}} UTC</time></td><td><a class="button quiet" href="/_cellbridge/history/{{resourceId:D}}/{{r.LifecycleGeneration}}/{{r.RevisionNumber}}">{{Icon("download")}}Download</a></td></tr>
            """));
        return Page("Version history", $$"""
            <a class="back-link" href="/">{{Icon("back")}}Back to documents</a>
            <section class="workspace-heading"><div class="workspace-icon">{{Icon("history")}}</div><h1>Version history</h1></section>
            <section class="library history-library"><div class="list-heading"><h2>Saved versions</h2><span>{{revisions.Count}} {{(revisions.Count == 1 ? "revision" : "revisions")}}</span></div><div class="history-scroll"><table class="file-table"><caption class="sr-only">Saved document revisions</caption><thead><tr><th scope="col">Revision</th><th scope="col">Version</th><th scope="col">Saved by</th><th scope="col">Saved on</th><th scope="col">File</th></tr></thead><tbody>{{rows}}</tbody></table></div></section>
            """, context, token, false);
    }

    public static string Error(string message) => Page("Could not create document", $$"""
        <section class="error-panel"><div class="workspace-icon">{{Icon("folder")}}</div><h1>Could not create document</h1><p>{{E(message)}}</p><a class="button primary" href="/">{{Icon("back")}}Back to documents</a></section>
        """, null, null, false);

    private static string Page(string title, string body, HttpContext? context, string? token, bool isLibrary)
    {
        var account = context?.User.Identity?.Name ?? "";
        var initial = account.Length > 0 ? account[..1].ToUpperInvariant() : "C";
        var signOut = token is null ? "" : $"<form method=\"post\" action=\"/local-logout\">{HiddenToken(token)}<button type=\"submit\" class=\"sign-out\">{Icon("logout")}Sign out</button></form>";
        var filters = isLibrary ? $$"""
            <div class="nav-group" hidden data-enhanced><p class="nav-label">FILE TYPES</p>
              <button class="nav-item" type="button" data-filter="docx" aria-pressed="false">{{FileIcon("docx")}}Word documents</button>
              <button class="nav-item" type="button" data-filter="xlsx" aria-pressed="false">{{FileIcon("xlsx")}}Excel workbooks</button>
              <button class="nav-item" type="button" data-filter="pptx" aria-pressed="false">{{FileIcon("pptx")}}PowerPoint slides</button>
            </div>
            """ : "";
        return $$"""
            <!doctype html><html lang="en"><head><meta charset="utf-8"><meta http-equiv="X-UA-Compatible" content="IE=edge"><meta name="viewport" content="width=device-width,initial-scale=1"><title>{{E(title)}} | CellBridge</title><link rel="stylesheet" href="{{AssetPath(context, "workspace.css")}}"><script src="{{AssetPath(context, "workspace.js")}}" defer></script></head><body class="workspace">
            <a class="skip-link" href="#main">Skip to documents</a>
            <header class="topbar"><a class="brand" href="/" aria-label="CellBridge documents"><span class="brand-mark">{{Icon("bridge")}}</span>CellBridge</a>
              {{(isLibrary ? $"<label class=\"search\" hidden data-enhanced>{Icon("search")}<span class=\"sr-only\">Search documents</span><input type=\"search\" id=\"search\" placeholder=\"Search this library\" autocomplete=\"off\"></label>" : "")}}
              <div class="topbar-account"><span class="account-name">{{E(account)}}</span><span class="avatar" aria-hidden="true">{{E(initial)}}</span></div>
            </header>
            <div class="workspace-layout"><aside class="sidebar"><div class="site-identity"><span class="site-monogram">CB</span><div><strong>Team workspace</strong><span>Document library</span></div></div><nav aria-label="Library navigation"><p class="nav-label">WORKSPACE</p><a class="nav-item active" href="/" {{(isLibrary ? "data-filter=\"all\" aria-current=\"page\"" : "")}}>{{Icon("folder")}}Documents<span class="nav-indicator"></span></a>{{filters}}</nav><div class="sidebar-bottom">{{signOut}}</div></aside>
            <main id="main" tabindex="-1">{{body}}</main></div>
            </body></html>
            """;
    }

    private static string E(string value) => HtmlEncoder.Default.Encode(value);
    private static string AssetPath(HttpContext? context, string name)
    {
        var file = context?.RequestServices.GetRequiredService<IWebHostEnvironment>()
            .WebRootFileProvider.GetFileInfo(name);
        if (file is not { Exists: true }) return "/" + name;
        using var stream = file.CreateReadStream();
        return $"/{name}?v={Convert.ToHexString(SHA256.HashData(stream))}";
    }
    private static string HiddenToken(string token) => $"<input type=\"hidden\" name=\"__RequestVerificationToken\" value=\"{E(token)}\">";
    private static string Size(long length) => length < 1024 ? $"{length} B" : length < 1024 * 1024
        ? (length / 1024d).ToString("0.#", CultureInfo.InvariantCulture) + " KB"
        : (length / (1024d * 1024)).ToString("0.#", CultureInfo.InvariantCulture) + " MB";
    private static string TypeLabel(string type) => type switch { "docx" => "Word document", "xlsx" => "Excel workbook", "pptx" => "PowerPoint presentation", _ => "File" };
    private static string FileIcon(string type) => $"<span class=\"file-icon {type switch { "docx" => "word", "xlsx" => "excel", "pptx" => "powerpoint", _ => "generic" }}\" aria-hidden=\"true\"><span>{type switch { "docx" => "W", "xlsx" => "X", "pptx" => "P", _ => "F" }}</span></span>";
    private static string Icon(string name)
    {
        var path = name switch
        {
            "folder" => "<path d=\"M3 6h6l2 2h10v11H3z\"/><path d=\"M3 10h18\"/>",
            "bridge" => "<path d=\"M3 18V8m18 10V8M3 12c4-8 14-8 18 0M3 16h18M8 10v6m8-6v6\"/>",
            "search" => "<circle cx=\"10.5\" cy=\"10.5\" r=\"6.5\"/><path d=\"m16 16 5 5\"/>",
            "plus" => "<path d=\"M12 5v14M5 12h14\"/>",
            "chevron" => "<path d=\"m8 10 4 4 4-4\"/>",
            "more" => "<circle cx=\"5\" cy=\"12\" r=\"1\"/><circle cx=\"12\" cy=\"12\" r=\"1\"/><circle cx=\"19\" cy=\"12\" r=\"1\"/>",
            "upload" => "<path d=\"M12 16V4m-5 5 5-5 5 5M4 16v4h16v-4\"/>",
            "download" => "<path d=\"M12 4v12m-5-5 5 5 5-5M4 17v3h16v-3\"/>",
            "history" => "<path d=\"M3 11a9 9 0 1 1 2 7M3 4v7h7m2-4v6l4 2\"/>",
            "open" => "<path d=\"M13 4h7v7m0-7L10 14M9 5H4v15h15v-5\"/>",
            "refresh" => "<path d=\"M20 10a8 8 0 0 0-14-4L3 9m0-6v6h6m-5 5a8 8 0 0 0 14 4l3-3m0 6v-6h-6\"/>",
            "logout" => "<path d=\"M10 4H4v16h6m-1-8h12m-4-4 4 4-4 4\"/>",
            "back" => "<path d=\"M20 12H4m6-6-6 6 6 6\"/>",
            _ => throw new ArgumentOutOfRangeException(nameof(name)),
        };
        return $"<svg class=\"icon\" width=\"20\" height=\"20\" viewBox=\"0 0 24 24\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"1.6\" stroke-linecap=\"round\" stroke-linejoin=\"round\" aria-hidden=\"true\">{path}</svg>";
    }
}
