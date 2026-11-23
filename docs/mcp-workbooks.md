# MCP workbook hosting

`CellBridge.Mcp.AspNetCore` exposes stateful Model Context Protocol tools
over Streamable HTTP for existing Excel `.xlsx` documents. Install it at
the same version as the other CellBridge packages in the host.

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

## Opt-in WebDemo testing with Microsoft Entra ID

WebDemo disables MCP by default. To enable it, start with a working Entra
configuration where `Authentication:Entra:Enabled` is `true`. Use an Entra
user linked to an existing local WebDemo account with access to an `.xlsx`
workbook. MCP uses the configured `Authentication:Entra:TenantId` and maps
the token to that same account, so the user's existing workbook grants apply.

In that tenant, create a dedicated MCP API app registration with a client ID
distinct from the browser and Office app registrations. Set
`api.requestedAccessTokenVersion` to `2` in its manifest. Set the Application
ID URI to the canonical MCP endpoint, such as `https://mcp.example.com/mcp`,
with the `/mcp` path and no trailing slash. Expose a delegated scope such
as `mcp.access` and obtain consent for the MCP client to request it. The token
audience (`aud`) is the MCP API application's client GUID. The token's `scp`
claim must contain the exact, case-sensitive short scope name `mcp.access`.
See [Microsoft's MCP server registration instructions](https://learn.microsoft.com/en-us/entra/agent-id/secure-mcp-server-with-entra-id).

For a Client ID Metadata Document (CIMD) test, confirm that the MCP client
supports CIMD. Append `/.well-known/openid-configuration` to the tenant-specific
`/oauth21/2026-08` URL and check that `client_id_metadata_document_supported`
is `true`. Set `AuthorizationServer` to the `/oauth21/2026-08` URL for
discovery. WebDemo validates Entra v2 tokens against the tenant-specific
`/v2.0` issuer.
See [Microsoft's CIMD setup and consent instructions](https://learn.microsoft.com/en-us/entra/agent-id/configure-client-id-metadata-document).

Merge these settings into the ignored `src/CellBridge.AppHost/apphost.settings.json`.
Replace the example host and IDs. `Parameters:PublicOrigin` must be an HTTPS
origin reachable by the client. AppHost forwards `Parameters:Mcp` to WebDemo
as `Mcp`. Set `RequiredScope` to the short scope name and list the fully
qualified scope URI in `ScopesSupported`.

```json
{
  "Parameters": {
    "PublicOrigin": "https://mcp.example.com",
    "Mcp": {
      "Enabled": true,
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
user's document grants. Each workbook session has its own `ClientId`, so MCP and
Office have separate editor entries even when they use the same subject.

For unattended access, use a dedicated agent account with its own CellBridge
subject and explicit workbook grants.

CellBridge checks the authenticated subject's current document permissions on
each workbook tool call. Revoking a document grant blocks subsequent calls that
need it, including operations on an open workbook. Opening by
resource ID or decoded application-relative path requires an existing authorized
document. MCP returns the same lookup error for missing and unauthorized workbooks.

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

## Session routing and lifetime

The MCP transport and workbook handles live in the serving process. With
multiple replicas, use sticky routing to keep requests bearing the same
`Mcp-Session-Id` on the process that initialized the session. Preserve the
session header through the proxy. After a process restart or replacement,
initialize a new MCP session and reopen the workbook. Committed revisions
persist according to the document storage provider.

The registry enforces `IdleSessionTimeout`, token expiry and the configured
per-actor and total session limits. Present a refreshed bearer token on a tool
call before the workbook session expires to keep it open. `MaxWorkbookBytes`
limits workbook package size. On graceful shutdown, the host closes active
workbook sessions and releases their editing participation.

When Office RTC is enabled and supported by the configured authorization
mode, MCP clients can participate through the in-process RTC connector.
In a multi-host deployment, Redis coordinates RTC events across instances.
MCP affinity still applies to workbook tools even when Redis distributes RTC.
See [multi-host RTC](multi-host-rtc.md).
