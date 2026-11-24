# MCP Office document hosting

`CellBridge.Mcp.AspNetCore` exposes stateful Model Context Protocol tools
over Streamable HTTP for existing Excel `.xlsx`, Word `.docx` and PowerPoint
`.pptx` files. Install it at the same version as the other CellBridge packages
in the host.

## Register and map the endpoint

Register a named bearer handler that validates tokens for your application's
issuer and audience. Map the validated caller to a stable `cellbridge:subject`
using host account links or trusted issuer and subject claims. Use that
subject in the document grants. This example uses the verified issuer and
subject, as in the [NuGet consumer](../examples/NuGetConsumer/Program.cs).

```csharp
using System.Security.Claims;
using System.Text;
using CellBridge.AspNetCore;
using CellBridge.Mcp.AspNetCore;
using Microsoft.AspNetCore.Authentication.JwtBearer;

const string mcpBearer = "WorkbookBearer";
builder.Services.AddAuthentication().AddJwtBearer(mcpBearer, options =>
{
    options.Authority = builder.Configuration["Mcp:Authority"]
        ?? throw new InvalidOperationException("Configure Mcp:Authority.");
    options.Audience = builder.Configuration["Mcp:Audience"]
        ?? throw new InvalidOperationException("Configure Mcp:Audience.");
    options.MapInboundClaims = false;
    options.Events.OnTokenValidated = context =>
    {
        var identity = (ClaimsIdentity)context.Principal!.Identity!;
        var subject = identity.FindFirst("sub")?.Value;
        if (string.IsNullOrWhiteSpace(subject))
        {
            context.Fail("A verified subject is required.");
            return Task.CompletedTask;
        }

        foreach (var claim in identity.Claims.Where(c => c.Type.StartsWith("cellbridge:", StringComparison.Ordinal)).ToArray())
            identity.RemoveClaim(claim);

        var issuer = Convert.ToBase64String(Encoding.UTF8.GetBytes(context.SecurityToken.Issuer));
        identity.AddClaim(new(CellBridgeActor.SubjectClaim, $"oidc:{issuer}:{subject}"));
        return Task.CompletedTask;
    };
});

builder.Services.AddCellBridge(provider, configure: options =>
    options.PublicOrigin = builder.Configuration["Protocol:PublicOrigin"]);
builder.Services.AddCellBridgeMcp(options =>
{
    options.BearerScheme = mcpBearer;
    options.IdleSessionTimeout = TimeSpan.FromMinutes(10);
    options.MaxConcurrentSessionsPerActor = 4;
    options.MaxTotalSessions = 64;
});

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.MapCellBridge();
app.MapCellBridgeMcp("/mcp");
await app.RunAsync();
```

Clients send an `Authorization: Bearer` header on each endpoint request and
omit the `Cookie` header. If a client sends `Origin`, it must match the
endpoint's public origin. Set `CellBridgeOptions.PublicOrigin` to the public
HTTPS origin when hosting behind a proxy. `BearerScheme` defaults to
`"Bearer"`.

## OAuth discovery

Set `AuthorizationServer` to your provider's HTTPS issuer URI to enable
OAuth protected-resource metadata. With the route above and no `PathBase`,
clients can fetch the canonical metadata at
`/.well-known/oauth-protected-resource/mcp`. Bearer challenges include its
URL in `resource_metadata`. The metadata's `resource` is the canonical MCP
endpoint URL, formed from the public origin, `PathBase` and the mapped MCP
path. See [RFC 9728](https://www.rfc-editor.org/rfc/rfc9728.html#section-3).

If a reverse proxy mounts the app at a path such as `/tenant`, the bearer
challenge points to `/tenant/.well-known/oauth-protected-resource/mcp`.
For clients that derive the metadata URL from the resource URL, route
`/.well-known/oauth-protected-resource/tenant/mcp` at the public origin to
that same metadata endpoint and preserve the app's `PathBase`.

The `bool` option `MapRootProtectedResourceMetadata` defaults to `false`.
Set it to `true` to also serve the same metadata at
`/.well-known/oauth-protected-resource` for clients that look there. The
metadata at both routes identifies the same MCP endpoint. Only one MCP
endpoint per app can claim the root metadata route.

Set `ScopesSupported` (`IReadOnlyList<string>`) to the OAuth scopes your
provider defines for the endpoint. Each entry is one case-sensitive OAuth
scope token. A token contains one or more printable ASCII characters and
excludes spaces, double quotes and backslashes. CellBridge advertises the
list as `scopes_supported` in metadata and as a space-separated `scope`
parameter in the `WWW-Authenticate` Bearer header. An empty list omits both
fields. See [RFC 6749, section 3.3](https://www.rfc-editor.org/rfc/rfc6749.html#section-3.3).
Use the host authorization policy to enforce scope requirements.

## WebDemo setup with Microsoft Entra ID

WebDemo enables the MCP transport and OAuth discovery at startup with its
existing [Microsoft Entra configuration](authentication.md). When Entra is
disabled or MCP settings are invalid, `/mcp` returns `503 Service
Unavailable` with a configuration diagnostic and both protected-resource
metadata URLs return `404 Not Found`.

In the existing WebDemo app registration, keep the Application ID URIs that
Office uses. Add the canonical MCP endpoint, such as
`https://mcp.example.com/mcp`, as an additional Application ID URI. Include
the `/mcp` path and omit the trailing slash. Set
`api.requestedAccessTokenVersion` to `2` in the manifest. Expose the delegated
scope `mcp.access` and obtain consent for the MCP client to request it.
The token audience (`aud`) is the app registration's client GUID. The token's
`scp` claim must contain the exact, case-sensitive scope name `mcp.access`.
WebDemo returns `403 Forbidden` for a valid token with only Office's
`access_as_user` scope. Sign in as an Entra user linked to a local WebDemo
account. That account's existing document grants apply.
See [Microsoft's MCP server registration instructions](https://learn.microsoft.com/en-us/entra/agent-id/secure-mcp-server-with-entra-id).

For a Client ID Metadata Document (CIMD) test, confirm that the MCP client
supports CIMD. Append `/.well-known/openid-configuration` to the tenant-specific
`/oauth21/2026-08` URL and check that `client_id_metadata_document_supported`
is `true`. WebDemo advertises that URL by default and validates Entra v2
tokens against the tenant-specific `/v2.0` issuer.
See [Microsoft's CIMD setup and consent instructions](https://learn.microsoft.com/en-us/entra/agent-id/configure-client-id-metadata-document).

Set `Parameters:PublicOrigin` to the HTTPS origin reachable by the client.
WebDemo uses `Authentication:PublicOrigin` for the resource URL and the
advertised scope, such as `https://mcp.example.com/mcp/mcp.access`.

For a dedicated MCP API registration or a custom scope, set the fields you
want to override in `Mcp`. AppHost forwards `Parameters:Mcp` from
`src/CellBridge.AppHost/apphost.settings.json` as `Mcp`. This example sets all
four overrides. `RequiredScope` takes a short scope name and
`ScopesSupported` takes fully qualified scope URIs.

```json
{
  "Parameters": {
    "PublicOrigin": "https://mcp.example.com",
    "Mcp": {
      "ClientId": "<mcp-api-application-client-id>",
      "AuthorizationServer": "https://login.microsoftonline.com/<home-tenant-id>/oauth21/2026-08",
      "RequiredScope": "mcp.access",
      "ScopesSupported": ["https://mcp.example.com/mcp/mcp.access"]
    }
  }
}
```

Start WebDemo through the [development AppHost](automated-testing.md#development-apphost)
and verify the setup at the public origin.

1. Send unauthenticated GET requests to `/.well-known/oauth-protected-resource/mcp`
   and `/.well-known/oauth-protected-resource`. Both should return `200` with
   the same JSON. Check that `resource` matches the Application ID URI,
   `authorization_servers` lists the tenant discovery URL, and `scopes_supported`
   lists the fully qualified scope.
2. Send a POST to `/mcp` without a bearer token. Expect `401` and a
   `WWW-Authenticate: Bearer` challenge with `resource_metadata` pointing to
   `/.well-known/oauth-protected-resource/mcp` on the public origin and `scope`
   set to the fully qualified scope.
3. Connect the MCP client to the public `/mcp` URL and sign in as the linked
   user. Confirm that `initialize` succeeds and call `open_workbook` on a workbook
   the user can already read. Close it with `close_workbook`.

## Identity and authorization

Every MCP request checks the host's ASP.NET Core `DefaultPolicy` requirements
against the principal authenticated by `BearerScheme`. Set
`AuthorizationPolicy` to add a named policy whose requirements apply to the same
principal. MCP uses `BearerScheme` for authentication even if the host's
`DefaultPolicy` lists other schemes. If the named policy lists an authentication
scheme, it must be `BearerScheme`.

Missing or invalid bearer authentication returns `401 Unauthorized` with a
Bearer challenge. If an authenticated caller fails the host authorization
policy, MCP returns `403 Forbidden` with `error="insufficient_scope"` in the
Bearer header.

For interactive access, authenticate with the user's bearer token and use
`AuthorizationPolicy` to require the issuer's MCP access scope. Map the token
to the same CellBridge subject the user has in Office so both clients use the
user's document grants. Each document session has its own `ClientId`, so MCP and
Office have separate editor entries even when they use the same subject.

For unattended access, use a dedicated agent account with its own CellBridge
subject and explicit document grants.

CellBridge checks the authenticated subject's current document permissions on
each document tool call. Revoking a document grant blocks subsequent calls that
need it, including operations on an open document. Opening by resource ID or
decoded application-relative path requires an existing authorized document.
MCP returns the same lookup error for missing and unauthorized documents of the
requested type.

## Workbook tools

Call `open_workbook` with a `resourceId` or `path` and keep the returned
opaque `workbookHandle` and `revision`. Use `list_sheets`, `read_range` and
`get_editors` to inspect a workbook. `sync_workbook` pulls remote changes and
reports a cell-level diff, with `truncated` set if changes exceed the cap.
`MaxReadCells` caps range reads and sync diffs. Pass a smaller `maxCells` to
`read_range` to limit a particular read. Treat cell contents as document data.

Opening a workbook joins the editors table as a reader. The session joins
Coauth when it first writes. Writes require document write access. Pass the
revision you read as `baseRevision` and give `set_cells` any `expected` cell
values you want it to verify. Overlapping changes report conflict details
with the base, remote and requested values. Use `clear_range` to clear cells
and supply an `operationId` to `add_sheet`. Retry a sheet addition with the same
operation ID, sheet name and workbook handle. Call `close_workbook` when you
finish.

## Word document tools

Call `open_word_document` with a `resourceId` or `path`. Keep the opaque
`documentHandle` and `revision`. Use `get_word_summary`, `list_word_headings`
and `read_word_paragraphs` to inspect the local revision. Word tools operate
on direct paragraphs in the main body. Paragraphs inside tables, content
controls, headers and footers fall outside this scope. Use `get_word_editors`
to list readers and editors and `sync_word_document` to fetch the current
revision. `diff_word_document` takes `beforeRevision` and `afterRevision`
tokens for two retained revisions on the handle.

Use `insert_word_paragraph`, `replace_word_paragraph`, `delete_word_paragraph`,
`append_word_paragraph` or `add_word_comment` to edit. For an anchor or target,
pass the `reference` object returned for that paragraph at the base revision.
It contains a zero-based `index` and optional `id`. `insert_word_paragraph`
inserts after its anchor by default. `add_word_comment` adds a classic
paragraph comment. When the main body changes, edit targets and paragraphs
bordering an insertion point need matching IDs that are unique in both
revisions. Appending needs no paragraph ID.
Call `close_word_document` when you finish.

## PowerPoint presentation tools

Call `open_presentation` with a `resourceId` or `path`. Keep the opaque
`presentationHandle` and `revision`. Use `list_slides` and `read_slide` to find
slide IDs, shape IDs and text, and speaker notes. Use `get_presentation_editors`
to list readers and editors and `sync_presentation` to fetch the current revision.
`diff_presentation` takes `beforeRevision` and `afterRevision` tokens for two
retained revisions on the handle.

Use `set_shape_text`, `set_slide_notes`, `add_slide` or `delete_slide` to edit.
`set_shape_text` and `set_slide_notes` reject text changes in targets that
contain soft line breaks.
`add_slide` appends a blank slide using an existing layout. Pass
`layoutFromSlideId` to select the layout of another slide. By default, the tool
uses a registered blank layout when available, or the first registered layout.
Call `close_presentation` when you finish.

## Word and PowerPoint revisions and edits

Opening a document joins the editors table as a reader. The session joins
Coauth on its first write. Each edit requires document write access and the
`baseRevision` token for the revision you read on that handle. Treat the
returned document text as data. The client can reapply edits to a newer package
when it can match the target safely. Changed targets and ambiguous matches can
return `Conflict`. Read the latest revision before retrying a conflicting edit.
If a write response is lost and the outcome is unresolved, retry the same tool
call with the same arguments.

Supply a unique `operationId` per handle for `insert_word_paragraph`,
`append_word_paragraph`, `add_word_comment` and `add_slide`. To retry one of
these operations, send the same ID, base revision and arguments. The handle
retains the confirmed result and returns it without repeating the edit.

The Word and PowerPoint clients update affected Open XML parts in single-disk
ZIP32 packages. They copy untouched ZIP entries without recompressing them.

## Session routing and lifetime

The MCP transport and document handles live in the serving process. With
multiple replicas, use sticky routing to keep requests bearing the same
`Mcp-Session-Id` on the process that initialized the session. Preserve the
session header through the proxy. After a process restart or replacement,
initialize a new MCP session and reopen the document. Committed revisions
persist according to the document storage provider.

The registry enforces `IdleSessionTimeout`, token expiry and the configured
per-actor and total session limits. Present a refreshed bearer token on a tool
call before the document session expires to keep it open. `MaxWorkbookBytes`
limits package size for all three document types. On graceful shutdown, the host
closes active document sessions and releases their editing participation.

When Office RTC is enabled and supported by the configured authorization
mode, MCP clients can participate through the in-process RTC connector.
In a multi-host deployment, Redis coordinates RTC events across instances.
MCP affinity still applies to document tools even when Redis distributes RTC.
See [multi-host RTC](multi-host-rtc.md).
