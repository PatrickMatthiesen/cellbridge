# Embed CellBridge in another host

The beta targets .NET 10. Use `CellBridge.AspNetCore` with a storage provider;
the protocol and provider packages can also be consumed separately. See
[provider composition](storage-providers.md#consume-the-packages) and the
[package consumer](../examples/NuGetConsumer/README.md).

## Use the built-in login page

`CellBridge.AspNetCore` includes a standard username/password page and its login
handler. It supplies CSRF protection, named-cookie sign-in, the Office challenge
and the completion endpoint. You connect your account system by implementing
`ICellBridgeLoginAuthenticator`; you do not need to write HTML or challenge headers.

```csharp
builder.Services.AddAuthentication("Cookies").AddCookie("Cookies", options =>
{
    options.LoginPath = "/auth/login";
});
builder.Services.AddCellBridgeLogin<MyAccountAuthenticator>("Cookies");
builder.Services.AddCellBridge(provider);

// After building the app:
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.MapCellBridge();
```

The authenticator is scoped per request. Its `AuthenticateAsync` method receives
the HTTP context, username, password and cancellation token. Return an authenticated
`ClaimsPrincipal` with the [CellBridge identity claims](authentication.md#reusable-hosts)
only after all account checks succeed, or null on failure. Your account system
remains responsible for password verification, disabled accounts, lockout and any
second factor. The package rejects unauthenticated or unmapped principals before
issuing a cookie. The repository Identity sample retains its existing Identity
credential handler and uses the same default HTML renderer.

Direct browser sign-in returns to the app root when no safe return URL is supplied.
Set `CellBridgeLoginOptions.DefaultReturnPath` to another local path, such as
`/library`, to choose the host's home page. The package includes the app's `PathBase`.
Office supplies its completion URL explicitly, so its sign-in still ends at the
completion endpoint. The standard form reports empty or invalid credentials on
the page rather than using the embedded browser's validation popup.

### Supply your own HTML

The standard page works without a renderer. To replace just the HTML:

```csharp
builder.Services.AddCellBridgeLogin<MyAccountAuthenticator>("Cookies", options =>
{
    options.ApplicationName = "My document library";
    options.RenderPage = page => MyLoginHtml(page);
});
```

`CellBridgeLoginPageContext` supplies the application name, resolved form action,
antiforgery token set, validated return URL, return-field name and failure flag.
Your HTML posts to `page.FormAction` with `login`, `password`, the antiforgery field
and `page.ReturnUrlParameter`. HTML-encode every supplied value. The package still
owns the POST handler, cookie and Office completion flow. You can also use
`CellBridgeLoginPage.Render(page)` within a custom wrapper.

### Use an existing host login page

If you already have a complete login flow, register
`AddCellBridgeOfficeFormsAuthentication("Cookies")` instead of `AddCellBridgeLogin`.
This registers the Office challenge and completion endpoint without mapping a login
page. Both integrations use the selected cookie's `LoginPath` and `ReturnUrlParameter`.
The login must redirect to the validated local completion path supplied by the
challenge. `/_cellbridge/auth/complete` accepts GET and HEAD and requires the selected
cookie's principal to map to a `CellBridgeActor`.

Existing cookie callbacks and principal validation still run. Ordinary browser
routes retain their redirects. Bearer authentication can remain the host's default;
CellBridge's mapped Office routes select the named cookie. Document permission
denials return 403 without restarting login.

Map `MapCellBridge()` at the application root. For a mounted app, call
`UsePathBase` before routing; prefixed route groups are rejected. Form actions and
completion URLs include PathBase. Office must reach the external HTTPS origin.
Configure trusted forwarded headers before authentication when behind a proxy,
or set `PublicOrigin` to a fixed HTTPS origin in the registration options.
`CompletionPath` changes the completion route. The origin has no path.

Cookie schemes using `CookieAuthenticationOptions.EventsType` are currently
unsupported; use an `Events` instance. Invalid schemes and missing login
authenticators fail at startup. `IsRequestAllowed` optionally gates both login
methods before token or credential processing. The example uses this for its
private development accounts; real account policy belongs in the authenticator.
These APIs are part of the unpublished beta.2 candidate.

## Execute parsed SOAP requests

`AddCellBridge` registers `CellBridgeRequestProcessor` and
`CellBridgeDocumentService`. A host can call the processor without mapping
CellBridge's HTTP endpoints:

```csharp
using CellBridge.AspNetCore;
using CellBridge.FssHttp;
using CellBridge.Storage.Abstractions;

// Obtain these from validated host identity and file permission checks.
CellBridgeActor actor = new(new SubjectIdentity(subject, login, displayName))
{
    AccessLimit = new DocumentAccessLimit(resourceId, DocumentAccess.Read),
};
CellStorageRequest request = CellStorageRequestParser.Parse(soapXml);
CellStorageExecution execution = await processor.ExecuteAsync(
    request, "https://documents.example", actor, cancellationToken);
string responseXml = execution.Response.ToSoapEnvelope();
```

The processor uses the same implementation as `/_vti_bin/cellstorage.svc`,
including supported metadata, editor/session, lock and Cell operations, ordered
subrequest dependencies, current-state authorization and durable provider
coordination. Unsupported operations retain explicit protocol errors.

The host owns authentication, request admission limits, SOAP/MTOM parsing,
resolving MIME references into binary subrequest data, cancellation, response
serialization and transport headers. Use `MtomMessageParser` and
the host's MIME reference resolution for multipart requests. Inline SOAP alone does not cover
Office's MTOM uploads. Pass an HTTP(S) origin with no credentials, query,
fragment or non-root path. The processor preserves the input request and uses
the resolved document for canonical response URLs and metadata.

## Resource identities

The additive `CreateAsync(Guid resourceId, ...)` and
`ImportAsync(Guid resourceId, ...)` overloads accept stable host-selected GUIDs.
Existing overloads still generate identities. Both the path and ID are unique:
a conflict returns null and never replaces the existing document. Imports are
privileged operations and record the supplied owner and importing actor.

Protocols still use GUID resource identities. Hosts with arbitrary string IDs,
such as opaque WOPI IDs, must retain a stable mapping or supply their own
collision-safe GUID derivation. Host-selected GUIDs do not remove that obligation.
Explicit unknown or invalid ResourceIDs are never replaced with URL lookup.

## Request permissions

`DocumentAccessLimit` is an immutable ceiling bound to one resource. The service
intersects it with `ICellBridgeAccessEvaluator.Evaluate`; `Write` includes `Read`.
A ceiling for another resource grants no access. It cannot elevate the default
stored ACL, bypass current-state permission checks, or grant creation permission.
Resolve asynchronous request ceilings before executing the request. For host-owned
authorization, use the [versioned policy contract](authentication.md#host-owned-policy)
and its coordinated permission-update boundary. The source consumer demonstrates
creation bindings, exact snapshots and revocation without mirrored grants.

Evaluators run synchronously during coordinated operations. They must be bounded,
perform no I/O and return consistent permissions for the supplied document state.
They also evaluate editor-owner actors, which have no current request ceiling.
Do not depend on ambient HTTP state or require an `AccessLimit` to be present.
Persist permission changes through versioned document/coordination updates that
advance the affected editor graph knowledge; changing a mutable external
permission cache alone can change graph bytes without changing their version.
The legacy evaluator remains available for unbound beta integrations. It does not
replace the coordinated policy-update contract. The published `0.1.0-beta.1`
packages predate this new source API; use project references until a release
includes it.

## Accepted saves and external content

Both binary `CellExecution` and SOAP `CellStorageExecution` expose `AcceptedSaves`.
Each `AcceptedSave` identifies the resource, content version, immutable content
handle, receipt operation key and whether this was a receipt replay. Accepted
no-ops are included; queries and rejected saves are not. `IsReplay` means the
operation was accepted earlier and did not publish again. Durability depends on
the provider: in-memory receipts disappear at restart.

Inspect all receipts even if a later subrequest reports an error. If a later
storage/operation exception prevents returning a result, `AcceptedSaveException`
preserves known earlier receipts and the original exception. The processor can
return those receipts alongside a mapped `CellRequestFail` for a failed Cell
subrequest. An exception's receipts are not a complete journal: the failing
operation may still have an unknown commit outcome.

Cancellation stays cancellation and may prevent returning receipts. Recover by
replaying the same stable save request and inspecting its accepted receipt.
Current-version retries require the original writer, current Write access and
identical operation content. Superseded retries return a coherency error.
Do not assume an interrupted call rolled back every earlier operation.

Read the exact content handle from a receipt through the provider, rather than
rereading the document's current revision after another save. The handle is
protected by retained state snapshots, not an indefinite external delivery lease.
This API does not provide ordered delivery, an outbox or external atomic
publication. Hosts need their own recovery, serialization and destination CAS
before claiming reliable write-back. Replaying a receipt may be needed to
recover an interrupted external write; simply ignoring every replay can lose it.
CellBridge's revision history describes its own accepted saves and does not
implement an external file store's revision or lock domain.

## WOPI integration boundary

These APIs address the reusable execution, request permission and GUID identity
needs reported by [WopiHost PR #735](https://github.com/petrsvihlik/WopiHost/pull/735#issuecomment-5993115599).
Accepted receipts provide a narrower alternative to comparing version counters;
they are not a reliable post-publication callback.

Beta.1 did not provide shared WOPI/FSSHTTP leases or conditional lifecycle APIs.
The beta.2 candidate adds opt-in shared base-lock authority, recoverable publication
and lifecycle operations. The host must supply destination CAS and durable receipts.
Do not expose conflicting write paths outside that authority. Testing the existing WopiHost adapter against
CellBridge packages establishes package compatibility, not Office Online Server
editing qualification or that WopiHost has adopted these new APIs upstream.

For external write-back, shared WOPI/FSSHTTP locks and conditional state deletion,
follow [external host reliability](external-host-reliability.md). Accepted-save
receipts alone do not provide recoverable external delivery.
