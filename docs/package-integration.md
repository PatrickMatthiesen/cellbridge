# Embed CellBridge in another host

Document responses preserve the native media type only for `.docx`, `.xlsx` and
`.pptx`. Other formats download as `application/octet-stream` attachments, even
after rename or move. Responses include `nosniff` and a sandbox CSP. Direct
downloads and history return HTTP 404 for unreadable documents; SOAP retains its
protocol-specific permission errors.

Set `CellBridgeOptions.PublicOrigin` to the external HTTPS origin, or configure
Office forms authentication's fixed origin. Without a fixed origin, remote
requests require a specific `AllowedHosts` list. A universal `*` does not establish
trust; only loopback hosts work without a specific list. Keep forwarded headers
restricted to known proxies.

Cellstorage POSTs, PROPFIND and current/historical document GETs share admission
limits of eight executing requests per process and two per authenticated subject.
Bounded FIFO queues absorb short native-client bursts, with up to 32 requests
waiting for one subject and 128 waiting for global capacity. A subject wait does
not reserve global capacity. Set either queue limit to zero for fail-fast
admission at that boundary. A full subject queue returns 429; a full global queue
returns 503. Both responses include `Retry-After: 1`.
A download holds its permit through package reconstruction and response delivery.
The absolute deadline defaults to two minutes and covers queuing and execution.
Metadata-only HEAD
requests and OPTIONS discovery do not reconstruct packages.
Large request bodies spool to private temporary files before parsing. SOAP XML
is limited to 16 Mi characters, so use MTOM attachments for large binary payloads.
Binary PutChanges accepts at most 1,024 author logins with 65,536 cumulative
UTF-16 characters. Configure `CellBridgeOptions` for HTTP admission limits and
keep host/proxy upload and response limits aligned with them.

The beta targets .NET 11. Use `CellBridge.AspNetCore` with a storage provider.
Direct integrations can use `CellBridge` without the HTTP adapter, and
`CellBridge.FssHttpB` remains standalone. See
[provider composition](storage-providers.md#consume-the-packages) and the
[package consumer](../examples/NuGetConsumer/README.md).

The snippets below show the CellBridge integration in an existing application.
The [DocumentLibrary example](../examples/DocumentLibrary/README.md) is a small
package-only host that maps Documents and Lists from one authenticated principal.
It uses volatile document storage and a preprovisioned PostgreSQL Lists database;
it is not a document-management application.

## Use the built-in login pages

`CellBridge.AspNetCore` supplies script-free browser and Office login pages through
minimal API endpoints. The browser gets a centered card; the Office dialog gets a
white page with padding. Both use the same protected submission handler. No Razor
Pages or Blazor registration is required, and custom HTML is optional.

### Use existing ASP.NET Core Identity accounts

Keep your application's Identity registration, user database, claims factory and
account policies. Add the packaged login integration after registering Identity:

```csharp
builder.Services.AddCellBridge(provider);
builder.Services.AddCellBridgeIdentityLogin<ApplicationUser>(options =>
{
    options.ApplicationName = "My document library";
    options.DefaultReturnPath = "/library";
    options.IdentityAuthority = "my-company:accounts";
});

var app = builder.Build();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.MapCellBridge();
```

The adapter requires `UserManager<ApplicationUser>`, `SignInManager<ApplicationUser>`,
and the standard `Identity.Application` and `Identity.TwoFactorUserId` cookie schemes.
For example, existing `AddIdentity` or `AddIdentityApiEndpoints` registrations can
supply these services. An `AddIdentityCore` registration must also register a sign-in
manager and Identity cookies. Missing services, incompatible cookie schemes and
conflicting routes fail at startup. A sign-in manager using a different application
scheme is unsupported.

The package uses Identity's password sign-in, account confirmation and lockout
checks. Accounts requiring two-factor authentication continue to an authenticator
code or recovery-code page. No application cookie is issued until sign-in finishes.
Recovery attempts also recheck account eligibility and lockout, and invalid recovery
codes increment Identity's failed-attempt count when its store supports lockout.
The pages never create a remembered-device cookie; Identity's existing remembered
devices still follow the host's policy. Email/SMS/custom second-factor delivery,
passkeys, registration, password reset and external-provider buttons are not supplied
by this adapter. Use an existing complete login flow for those features.

The primary form protects its chosen presentation and destination in `_cellbridgeState`,
bound to its antiforgery token and mounted path. Editing its return or presentation
fields cannot change the packaged Office flow. Primary state expires after fifteen
minutes. Oversized browser destinations fall back to the configured homepage before
protected state is issued. Protected tokens are limited to 3,000 characters so they
fit ordinary cookies and bounded form fields.

The pending two-factor flow uses an encrypted, HttpOnly, Secure cookie and a matching protected
form field. It binds the destination and layout to the pending Identity user, security
stamp, login attempt and mounted path. Its default lifetime is five minutes, configurable
through `PendingLifetime` up to fifteen minutes. A new primary POST replaces the pending
attempt; simply visiting the primary page does not cancel it. Start over is a
CSRF-protected POST. Shared deployments need shared ASP.NET Core Data Protection keys.

Identity issues and validates its own application cookie. Existing cookie callbacks,
security-stamp checks and claims factories remain active. After successful validation,
the selected cookie's principal receives a CellBridge subject generated from
`IdentityOptions.ClaimsIdentity.UserIdClaimType` and `IdentityAuthority`, with each
component encoded separately. Keep the authority stable after creating documents;
changing it changes generated subjects and document ownership. Existing explicit
CellBridge subject and permission claims are preserved. Generated mappings grant no
creation permission. Ambiguous identities are rejected. Other cookies, bearer tokens,
external-provider cookies and pending MFA cookies are not mapped by this adapter.

Signing in does not grant access to every document. The configured document policy
still checks ownership, grants or host-owned permission revisions on each operation.
An API's default bearer authentication can remain unchanged; CellBridge's Office
endpoints select `Identity.Application` explicitly.

### Connect a custom account system

For an account system other than Identity, implement `ICellBridgeLoginAuthenticator`:

```csharp
builder.Services.AddCellBridge(provider);
builder.Services.AddCellBridgeCookieLogin<MyAccountAuthenticator>();
```

The scoped authenticator receives the HTTP context, username, password and cancellation
token. Return an authenticated principal with the [CellBridge identity claims](authentication.md#reusable-hosts)
only after all required checks succeed, or null on failure. Your backend owns password
verification, disabled accounts, lockout and second factors. This interface has no
multi-step MFA continuation; use the Identity adapter or an existing complete flow
when one is required.

`AddCellBridgeCookieLogin` creates a named cookie scheme, defaulting to `CellBridge`,
with an HttpOnly, Secure, SameSite=Lax cookie, an eight-hour ticket and no sliding
expiration. It supplies `/auth/login` and returns 403 on permission denial.
HTTPS is required. Its cookie and login options remain configurable:

```csharp
builder.Services.AddCellBridgeCookieLogin<MyAccountAuthenticator>("documents",
    configureCookie: cookie => cookie.LoginPath = "/sign-in",
    configure: login => login.ApplicationName = "My document library");
```

Packaged Office logins request persistent cookies by default, so Office can retain
its login after closing. Set `login.PersistOfficeSession = false` to opt out.
This applies to `AddCellBridgeCookieLogin`, `AddCellBridgeLogin` and
`AddCellBridgeIdentityLogin`. Only a protected Office login flow requests persistence; browser sign-ins and
legacy custom forms without protected state request session cookies.
The selected cookie handler controls ticket duration, sliding expiration and
revocation, and its sign-in callback can override persistence. With the dedicated
cookie's defaults, the retained login expires after eight hours without renewal.
Office applications may share their cookie store, so signing into one can sign
the others in too. Identity's two-factor checks still run before issuing an
application cookie; this option does not remember a two-factor device.

The helper does not select a default authentication scheme. ASP.NET Core can use
a sole scheme as its implicit default, but adding another scheme removes that
implicit choice. Mixed-scheme applications must explicitly select defaults or
name schemes in their authorization policies. An existing explicit bearer or
cookie default stays unchanged; CellBridge's Office endpoints select the login
cookie explicitly. Keep `AddCellBridgeLogin<TAuthenticator>("Cookies")` when the
host already registers that cookie. Do not register the same scheme twice.
Identity applications should use `AddCellBridgeIdentityLogin<TUser>` instead.

### Register document permissions and file delivery

`AddCellBridge(provider)` supplies stored owner/grant authorization. For ordinary
async host permissions, use `AddCellBridgeWithExternalAuthorization<MyProvider>(provider)`.
The provider implements `IDocumentAuthorizationProvider`; caching is off by default.
See [async authorization and migration](async-document-authorization.md).

For the existing versioned host-owned snapshot policy, the typed overload registers one shared singleton for both the concrete
policy and `ICellBridgeAuthorizationPolicy`:

```csharp
builder.Services.AddCellBridge<MyDocumentPermissions>(provider);
```

Select the policy in the first CellBridge registration. An existing singleton
factory for the concrete policy is reused. Scoped/transient policies, a competing
interface registration or a later attempt to replace an existing CellBridge policy
are rejected. The policy's own dependencies must also support singleton use.

To deliver accepted saves to your application's file store:

```csharp
builder.Services.AddCellBridgeExternalPublishing<MyFileDestination>(options =>
    options.PollingInterval = TimeSpan.FromSeconds(2));
```

`MyFileDestination` implements `IExternalRevisionDestination`. An existing concrete
singleton factory is reused. The helper registers a shared `ExternalRevisionPublisher`
and `ExternalRevisionPublicationWorker`. Do not register competing publishers or
destinations or additional hosted workers, including opaque factories. No directory, permissions or document bindings are created for you.
Bind each document explicitly with `ExternalRevisionPublisher.BindAsync`; the
[external publication contract](external-host-reliability.md) describes stored receipts,
conflicts and recovery.

The worker polls retained pending revisions after startup and retries transient
failures. `Wake()` requests a pass after a save; concurrent requests coalesce.
`PublishPendingOnceAsync()` runs one serialized scan, attempting at most one queued
revision per document. Serialization is local to that worker instance. Multiple
hosts still require stored destination receipts and the provider's atomic state
transitions. Initialize the destination and permissions before starting the host.
Polling intervals must be at least one millisecond and fit a timer. Transient
failures delay background retries by that interval, including when wakes arrive. Stopping the worker cancels
active and waiting passes and waits for the active pass within the shutdown timeout; interrupted revisions remain queued for recovery.

### Routes and custom HTML

The selected cookie's `LoginPath` and `ReturnUrlParameter` determine the browser GET
route, shared POST route and return field. `OfficeLoginPath` defaults to
`/_cellbridge/auth/login`. Office challenges advertise that separate page, whose
successful sign-in always returns to `CompletionPath`, defaulting to
`/_cellbridge/auth/complete`. Browser sign-in uses a validated local destination or
`DefaultReturnPath`, which defaults to the app root. Mounted applications keep browser
returns inside their `PathBase` boundary. Failed attempts preserve the presentation.
Identity additionally maps `TwoFactorPath`, defaulting to `/_cellbridge/auth/two-factor`.

Replace either presentation independently:

```csharp
builder.Services.AddCellBridgeIdentityLogin<ApplicationUser>(options =>
{
    options.RenderBrowserPage = page => MyBrowserLoginHtml(page);
    // RenderOfficePage remains unset, so Office uses the packaged default.
});
```

The renderer context supplies the application name, form action, antiforgery token,
validated return URL, return-field name, failure flag, `IsOffice`, `RequiresTwoFactor`,
`UseRecoveryCode`, `ProtectedState` and `RestartAction`. New renderers must handle both
credential and two-factor stages. The packaged `CellBridgeLoginPage.Render(page)`
is also available.

Primary forms submit `login`, `password`, the supplied antiforgery field,
`_cellbridgeState`, the supplied return field and `_cellbridgePresentation` set to
`office` or `browser`. The protected state determines the destination and presentation. Two-factor
forms submit the antiforgery field, `_cellbridgeState`, `code`, and `method` set to
`authenticator` or `recovery`. To start over, submit the antiforgery field,
`_cellbridgeState` and `cancel=1` to `RestartAction`. HTML-encode supplied values and
preserve all protected fields. HTML replacement does not replace the server checks.
The layout selector never grants trust or permissions.

The older `RenderPage` override remains a fallback for primary forms in both
presentations. It does not replace the packaged MFA page. For compatibility, legacy primary
renderers may omit protected state and retain validated form return handling.
Use a new stage-aware renderer or include the supplied `_cellbridgeState` field
to retain the protected primary destination. `RenderBrowserPage` and
`RenderOfficePage` take precedence. Forms report invalid or empty credentials inline.

### Use an existing complete host login

Register `AddCellBridgeOfficeFormsAuthentication("Cookies")` instead of a packaged
login integration. This supplies the Office challenge and completion endpoint while
leaving your existing login routes and UI in charge. The challenge uses the selected
cookie's `LoginPath` and `ReturnUrlParameter`. Your flow must return to the validated
local completion path after all authentication steps succeed.

Completion accepts GET and HEAD and requires the selected cookie's principal to map
to a `CellBridgeActor`. Ordinary browser routes retain their original cookie redirects.
Document permission denials return 403 without restarting login. These integrations
do not automatically turn a frontend's bearer token into desktop Office authentication.

Map `MapCellBridge()` at the application root. For a mounted app, call `UsePathBase`
before routing; prefixed route groups are rejected. Office must reach the external
HTTPS origin. Configure trusted forwarded headers before authentication behind a proxy,
or set `PublicOrigin` to a fixed HTTPS origin without a path.

Office RTC is enabled by default. To disable its discovery and endpoints, configure
`AddCellBridge(..., configure: options => options.EnableOfficeRealTime = false)`.
WebDemo uses `Protocol:EnableOfficeRealTime=false`; AppHost uses
`Parameters:EnableOfficeRealTime=false`. Disable RTC for multi-instance hosts or
ordinary asynchronous authorization providers. The relay requires a single host
and stored grants or an immutable versioned authorization policy.

Cookie schemes using `CookieAuthenticationOptions.EventsType` are unsupported here;
use an `Events` instance. `IsRequestAllowed` gates every packaged GET and POST before
credential or token processing. Connect host rate limiting and other admission policy
to all primary and second-factor submissions. The example uses a private-development
request gate; real account policy belongs to the account system.

CellBridge and
its storage/authentication dependencies have not been qualified under Native AOT.

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
subrequest dependencies, current-state authorization and provider
coordination. Unsupported operations retain explicit protocol errors.

For decoded binary Cell requests, call `CellBridgeDocumentService.ExecuteAsync`
with the document resource ID, SOAP partition default, request attributes and
validated actor. The processor uses this same method. InMemory and PostgreSQL
share target selection, ordered execution, save receipts and query assembly.
File and metadata operation handlers are internal implementation details.

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
When `UseResourceID` is true, an explicitly supplied empty, malformed or
unknown `ResourceID` is never replaced with URL lookup. If the optional
attribute is omitted, the required URL identifies the initial request and the
response can establish the stable ID.

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
The legacy evaluator remains available for documents without a host-policy binding.
It does not replace the coordinated policy-update contract.

## Accepted saves and external content

Both binary `CellExecution` and SOAP `CellStorageExecution` expose `AcceptedSaves`
and `IsCancelled`. Each `AcceptedSave` identifies the resource, content version,
immutable content handle, receipt operation key and whether this was a receipt
replay. Accepted file-content no-ops are included; queries, rejected saves and
metadata-only saves do not produce these file receipts. `IsReplay` means the
operation was accepted earlier and did not publish again. Durability depends on
the provider: in-memory receipts disappear at restart.

Inspect all receipts even if a later subrequest reports an error. If a later
storage/operation exception prevents returning a result, `AcceptedSaveException`
preserves known earlier receipts and the original exception. The processor can
return those receipts alongside a mapped `CellRequestFail` for a failed Cell
subrequest. An exception's receipts are not a complete journal: the failing
operation may still have an unknown commit outcome.

When cancellation is observed after an established save, execution stops and
returns `IsCancelled = true` with the known `AcceptedSaves`. This is an incomplete
request, not a successful execution of every operation. Earlier saves remain
committed. The binary response reports Win32 `ERROR_OPERATION_ABORTED` (`995`);
the SOAP processor preserves the partial result and marks the current file
`SubRequestFail`. A cancelled Cell subrequest reports `CellRequestFail`. The
processor does not continue later subrequests or files. Metadata-only
saves retain their state and stored replies without producing file receipts.
Inspect `IsCancelled` even when the last completed operation succeeded.

Before any save is established, cancellation can still throw
`OperationCanceledException`. If cancellation interrupts a commit await, the
service uses uncancelled reads only to verify the matching stored receipt and
its current authorization before reporting acceptance. It does not start another
save. A missing receipt or an exception does not establish that the interrupted
operation rolled back. Recover an uncertain outcome by replaying the same stable
save request. Current-version retries require the original writer, current Write
access and identical operation content. Superseded retries return a coherency
error.

The reusable APIs return their completed-save information even when cancellation
stops later work. A disconnected HTTP client may still receive no response, and
an endpoint timeout can prevent response delivery. Neither case undoes completed
saves. Consumers own optional external file writes and decide when to await them
or respond.

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

Hosts can opt into shared base-lock authority, recoverable publication
and lifecycle operations. The host must supply destination CAS and stored receipts.
Do not expose conflicting write paths outside that authority. Testing the existing WopiHost adapter against
CellBridge packages establishes package compatibility, not Office Online Server
editing qualification or that WopiHost has adopted these new APIs upstream.

For external write-back, shared WOPI/FSSHTTP locks and conditional state deletion,
follow [external host reliability](external-host-reliability.md). Accepted-save
receipts alone do not provide recoverable external delivery.

## Nested paths and migration

Standard endpoints support nested `/shared` document paths. Use
`CellBridgeDocumentService.MoveDocumentAsync` to move an existing resource
without changing its ID. See [document paths and migration](document-paths.md)
for the decoded-path contract, retired-link behavior and host metadata
reconciliation after API moves or Office protocol renames.
