using System.Text;
using System.Text.Encodings.Web;
using CellBridge.AspNetCore;
using CellBridge.Lists;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.Extensions.Options;

namespace CellBridge.DocumentLibrary;

/// <summary>Small list UI for the locally provisioned customer workspaces.</summary>
public static class DocumentLibraryLists
{
    public static readonly Guid WorkspaceId = DocumentLibraryListIds.WorkspaceId;
    public static readonly Guid OtherWorkspaceId = DocumentLibraryListIds.OtherWorkspaceId;
    public static readonly Guid TasksListId = DocumentLibraryListIds.TasksListId;
    public static readonly Guid WorkPackagesListId = DocumentLibraryListIds.WorkPackagesListId;
    public static readonly Guid DeliverablesListId = DocumentLibraryListIds.DeliverablesListId;
    public static readonly Guid TaskTitleFieldId = DocumentLibraryListIds.TaskTitleFieldId;
    public static readonly Guid TaskStatusFieldId = DocumentLibraryListIds.TaskStatusFieldId;
    public static readonly Guid TaskWorkPackageFieldId = DocumentLibraryListIds.TaskWorkPackageFieldId;
    public static readonly Guid TaskExternalIdFieldId = DocumentLibraryListIds.TaskExternalIdFieldId;
    public static readonly Guid WorkPackageTitleFieldId = DocumentLibraryListIds.WorkPackageTitleFieldId;
    public static readonly Guid DeliverableTitleFieldId = DocumentLibraryListIds.DeliverableTitleFieldId;

    public static void MapDocumentLibraryLists(this WebApplication app)
    {
        app.MapGet("/lists", async (HttpContext context, IListEngine engine,
            IAntiforgery antiforgery, IOptions<AntiforgeryOptions> antiforgeryOptions) =>
        {
            context.Response.Headers.CacheControl = "private, no-store";
            var mapped = CellBridgeActor.FromPrincipal(context.User);
            if (mapped is null) return Results.Unauthorized();
            var actor = new ListActor(mapped.Identity.Subject);
            var workspaces = new List<(Guid Id, string Name)>();
            var unavailable = false;
            foreach (var candidate in new[] { (WorkspaceId, "Acme"), (OtherWorkspaceId, "Contoso") })
            {
                try
                {
                    if ((await engine.GetListsAsync(actor, candidate.Item1, context.RequestAborted)).Count > 0)
                        workspaces.Add(candidate);
                }
                catch (ListException error) when (error.Failure is ListFailure.NotFound or ListFailure.Forbidden) { }
                catch (ListException) { unavailable = true; }
            }

            var token = antiforgery.GetAndStoreTokens(context).RequestToken!;
            // Local account names are presentation hints only. Every API operation
            // independently checks current membership and grants in IListEngine.
            var canEdit = actor.Subject is DocumentLibraryService.OwnerSubject or DocumentLibraryService.EditorSubject;
            var canDesign = actor.Subject == DocumentLibraryService.OwnerSubject;
            return Results.Content(Page(context.User.Identity?.Name ?? "", workspaces, token,
                antiforgeryOptions.Value.HeaderName ?? "RequestVerificationToken", canEdit, canDesign, unavailable),
                "text/html; charset=utf-8");
        }).RequireAuthorization();
    }

    private static string Page(string account, IReadOnlyList<(Guid Id, string Name)> workspaces,
        string token, string tokenHeader, bool canEdit, bool canDesign, bool unavailable)
    {
        var options = new StringBuilder();
        foreach (var workspace in workspaces)
            options.Append($"<option value=\"{workspace.Id:D}\">{E(workspace.Name)}</option>");
        var writes = canEdit ? "<button id=\"new-record\" type=\"button\">New record</button>" : "";
        var design = canDesign ? """
            <details class="design"><summary>Customize lists</summary>
              <form id="add-field"><h3>Add an optional text field</h3><label>Field name<input name="name" maxlength="200" required></label><button>Add field</button></form>
              <form id="create-list"><h3>Create a custom list</h3><label>List name<input name="name" value="J" maxlength="200" required></label><p>The new list starts with a required Title field.</p><button>Create list</button></form>
            </details>
            """ : "";
        var empty = workspaces.Count == 0 ? "You have no visible customer lists." : "Loading lists...";
        if (unavailable) empty = "Some customer lists are unavailable. Try again later.";
        return $$"""
            <!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
            <title>Customer lists | CellBridge</title><meta name="list-csrf-token" content="{{E(token)}}"><meta name="list-csrf-header" content="{{E(tokenHeader)}}">
            <link rel="stylesheet" href="/lists.css"><script src="/lists.js" defer></script></head>
            <body><header><a class="brand" href="/">CellBridge</a><nav aria-label="Workspace"><a href="/">Documents</a><a href="/lists" aria-current="page">Customer lists</a></nav><span>{{E(account)}}</span>
            <form method="post" action="/local-logout"><input type="hidden" name="__RequestVerificationToken" value="{{E(token)}}"><button class="quiet">Sign out</button></form></header>
            <main id="lists-app" data-can-edit="{{(canEdit ? "true" : "false")}}"><h1>Customer lists</h1>
              <p>Tasks, work packages and deliverables for each customer.</p>
              <div class="toolbar"><label>Customer<select id="workspace" {{(workspaces.Count == 0 ? "disabled" : "")}}>{{options}}</select></label><label>List<select id="list" disabled></select></label>{{writes}}<button id="refresh" type="button">Refresh records</button></div>
              <p id="status" role="status" aria-live="polite">{{E(empty)}}</p>
              <section id="list-content" hidden><h2 id="list-name"></h2><p id="schema-info"></p>
                <div class="exports"><a id="csv">Download CSV</a><a id="xlsx">Download XLSX</a><a id="feed">Read-only reporting feed</a></div>
                <p class="note">Exports contain one bounded page. Hidden fields are omitted by default. Hidden and read-only display settings do not restrict API access. Reporting clients need their own authenticated session.</p>
                <div class="table-scroll"><table id="records"><caption class="sr-only">List records</caption><thead></thead><tbody></tbody></table></div><button id="more" type="button" hidden>Load more</button>
                {{design}}
              </section>
              <section id="editor" hidden><h2 id="editor-title"></h2><form id="record-form"><fieldset id="record-fields"></fieldset><div class="form-actions"><button id="save-record">Save record</button><button id="cancel-record" type="button" class="quiet">Cancel</button></div></form><p id="draft-note" class="note"></p></section>
              <section id="merge" hidden><h2>Review concurrent changes</h2><p>Your draft is still open. Choose a value for each conflict before saving.</p><div class="table-scroll"><table><thead><tr><th>Field</th><th>Original</th><th>Current</th><th>Mine</th><th>Resolution</th></tr></thead><tbody id="merge-fields"></tbody></table></div><button id="save-merge" type="button">Save resolved changes</button><button id="back-to-draft" type="button" class="quiet">Return to draft</button></section>
            </main></body></html>
            """;
    }

    private static string E(string value) => HtmlEncoder.Default.Encode(value);
}
