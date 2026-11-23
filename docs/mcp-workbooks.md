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
`"Bearer"`. Set `AuthorizationPolicy` to require a named host policy in
addition to a valid bearer token. Set `AuthorizationServer` to the HTTPS issuer
URI to advertise RFC 9728 protected-resource metadata. With the route above,
clients can fetch it at `/.well-known/oauth-protected-resource/mcp`.

## Identity and authorization

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
per-actor and total session limits. `MaxWorkbookBytes` limits workbook package
size. On graceful shutdown, the host closes active workbook sessions and
releases their editing participation.

When Office RTC is enabled and supported by the configured authorization
mode, MCP clients can participate through the in-process RTC connector.
In a multi-host deployment, Redis coordinates RTC events across instances.
MCP affinity still applies to workbook tools even when Redis distributes RTC.
See [multi-host RTC](multi-host-rtc.md).
