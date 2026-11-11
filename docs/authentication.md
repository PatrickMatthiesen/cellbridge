# Authentication and document permissions

The sample host uses operator-provisioned ASP.NET Core Identity accounts in
PostgreSQL. Browser and Office requests use a Secure, HttpOnly cookie.
Each document has an owner and explicit user grants. Access is denied unless
ownership or a grant permits it. Owners can read and write; Write implies Read.
Creating documents requires a separate account permission.

WebDemo explicitly enables Office forms authentication for testing. With Entra
disabled, desktop Office signs in through the [MS-OFBA challenge](https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-ofba/c2c4baef-c611-4e7b-9a4c-d009e678e3d2).
Office displays the host's sign-in form and uses the resulting cookie for
document requests.

## Microsoft Entra sign-in

`CellBridge.AspNetCore` supplies native Office Entra token validation, discovery
and token-to-cookie exchange. WebDemo uses that packaged implementation and
adds OpenID Connect for browser sign-in. Local browser login remains available.

Direct personal Microsoft accounts are currently unsupported in CellBridge's
native Office integration. The tested consumer challenge was rejected by Office's
MSA resource validation before authentication. This is a boundary of the current
CellBridge integration and tested flow, not a claim that Microsoft products
universally reject personal accounts. A tenant-issued guest identity is a separate
login context; inviting users into a tenant does not meet direct sign-in without
an invitation.

Enable this explicitly through the AppHost's operator configuration:

```text
Parameters__PublicOrigin=https://office.example.test
Parameters__Entra__Enabled=true
Parameters__Entra__TenantId=TENANT_GUID
Parameters__Entra__ClientId=APPLICATION_GUID
Parameters__EntraClientSecret=SECRET_STORE_VALUE
```

The AppHost treats `EntraClientSecret` as a secret parameter. Outside Aspire,
configure `Authentication:Entra:Enabled`, `TenantId`, `ClientId`, and
`ClientSecret`, plus `Authentication:PublicOrigin` and `Protocol:PublicOrigin`.
Keep the credential outside source control. The browser uses it to redeem its
authorization code. WebDemo also uses it to read the app registration's supported
account types through Microsoft Graph. Native Office obtains its own token;
native token validation does not use the client secret. `TenantId` is always the
app registration's home tenant, even when the registration allows other tenants
or personal accounts.

Register the reachable HTTPS origin as an Application ID URI in the Entra API
registration and configure v2 access tokens. The native Office client can request
that origin as its resource. Expose the delegated `access_as_user` scope and
authorize the Microsoft Office client
`d3590ed6-52b3-4102-aeff-aad2292ab01c` for that scope. Add a **Web** redirect URI
of `https://office.example.test/signin-oidc` for browser login. Do not add Office's
broker redirect URI to the server registration. The server validates the API
application GUID as the v2 token audience, issuer and signing-key issuer, expiry,
`tid`, `oid` and `ver=2.0`, delegated scope and native Office client ID. The
registration must have `requestedAccessTokenVersion=2`.

In Entra mode, HTTP OPTIONS on the marked protocol discovery routes returns
generic protocol capabilities without a cookie when no authentication probe is
requested. An unauthenticated OPTIONS probe with
`X-IDCRL_OPTIONS: force-auth-challenge` receives 401 with the OAuth challenge
and configured `cookie_uri`. On these forced probes,
`IgnoreCookieAuthentication: t` prevents cookie reuse for authentication
negotiation. A plain `X-MS-CookieUri-Requested: t` requests authentication metadata
on the 200 capability response without forcing another native sign-in round.
Authenticated probes return 200 capabilities unless a forced probe explicitly
requests cookie authentication to be ignored. The same rules apply to desktop
and mobile clients; user-agent text does not select the challenge. Document downloads,
PROPFIND, SOAP operations, RTC and historical versions still require
authentication and their existing document permissions.

Reverse proxies must preserve Office request headers whose names contain
underscores, including `X-IDCRL_OPTIONS`. For Nginx, set
`underscores_in_headers on;` in the CellBridge virtual server and verify a forced
OPTIONS probe through the public origin. Nginx disables these headers by
[default](https://nginx.org/en/docs/http/ngx_http_core_module.html#underscores_in_headers).
If the proxy drops this header, a forced sign-in probe can receive ordinary 200
capabilities instead of the required 401 challenge.

The challenge advertises a same-origin `cookie_uri` at
`/_api/SP.OAuth.NativeClient/Authenticate`. A POST with a validated Office token
and a linked, permitted local account issues the existing application session
cookie, named `SPOIDCRL` in Entra mode. Its explicit expiry cannot exceed the
token or configured cookie lifetime, and does not slide. The host explicitly
configures the selected cookie name as `SPOIDCRL`; package registration does not
rename cookies or replace authentication defaults. WebDemo selects that name
for its application cookie when Entra is enabled, including browser sessions. Token-derived native
cookies use `SameSite=None; Secure; HttpOnly`, matching the native Office reference
exchange. Browser sign-in cookies retain `SameSite=Lax`. An empty-Bearer POST
returns discovery information without issuing a cookie. Other missing or invalid
credentials cannot create a session.
An empty `Authorization: Bearer` probe on a document request permits cookie
authentication. A nonempty invalid token does not fall back to a cookie.

WebDemo keeps OAuth-to-MS-OFBA fallback disabled by default, independently of its
explicit forms registration. Set `Authentication:AllowOAuthToFormsFallback=true`
only when the test host should offer its forms login for unauthenticated protocol
requests or empty Bearer probes. Invalid nonempty credentials still receive the
OAuth failure, and the native cookie-exchange endpoint never uses forms fallback.

The fixed-tenant package mode accepts only the configured Entra tenant. With
account-type discovery enabled, the server accepts validated tokens according
to the Azure registration's current audience. It still requires the registered
API resource, Microsoft Office client ID and an existing permitted local account.
Browser OIDC sign-in and acceptance of a signed test token do not establish that
stock Office can acquire or renew that token. Direct personal-account sign-in in
native Office is unsupported. The native challenge uses Office's resource-based
`/oauth2/authorize` endpoint; the API registration independently selects v2 access
tokens. Browser OIDC uses the v2 authorization endpoint. Other providers need
their own implemented and tested Office flow. The FSSHTTP and FSSHTTPB
implementations do not choose an identity provider.

The operator must link each Entra tenant ID and user object ID to a local account.
Email addresses and display names do not establish a link. Existing links keep
the local subject, document ownership and grants:

```sh
dotnet run --project tools/CellBridge.Admin -- link-entra-user --subject local:USER_ID --tenant-id TENANT_GUID --object-id USER_OBJECT_GUID
dotnet run --project tools/CellBridge.Admin -- create-entra-user --id editor --login editor --display-name Editor --can-create true --tenant-id TENANT_GUID --object-id USER_OBJECT_GUID
```

`create-entra-user` creates a passwordless local account for the linked identity.
It does not create an Entra user. Unknown, disabled, locked-out and locally
two-factor-enabled accounts cannot use this integration. Local two-factor login
remains available; Entra sign-in does not substitute for that second-factor flow.
Bearer requests recheck the linked account on each request. Browser cookies keep
the existing security-stamp validation interval. Document grants still apply to
downloads, saves, history, restore and collaboration.

By default, an authenticated account without document access receives a masked
404 for downloads, including HEAD requests, with `Cache-Control: private, no-store`.
Set `CellBridgeOptions.ConcealUnauthorizedDownloads=false` to return an explicit
403 for these denials instead. This discloses the denial; genuine missing files
still return 404. WebDemo exposes this as `Protocol:ConcealUnauthorizedDownloads`,
or `Parameters:ConcealUnauthorizedDownloads` through Aspire. This option does not
guarantee a particular Office error dialog or an account picker.
FSSHTTP operations retain their protocol permission errors. These denials do not
invalidate the session or request another login. A repeated challenge can reuse
the same Office account rather than display an account picker.
Routine ACL failures are reported by the individual SOAP or binary operations,
without setting `Response.ErrorCode` on the enclosing file response. A successful
QueryAccess operation can report denied read and write access; its successful
evaluation does not grant either permission.

To test another identity, use Office's account UI and reopen the same document.
Enable Debug logging for `CellBridge.Authentication.Entra` to verify the identity
actually used. Native token mapping logs the validated tenant and object IDs and
the mapped local subject. Authentication and completed-response logs include the
request ID, authentication outcome and whether a session cookie was issued.
Raw tokens, cookie values and email claims are not logged by these diagnostics.
Browser logout does not guarantee
that Office changes accounts. The selected identity still needs an explicit local
link and permission to the document.

## Follow Azure's supported account types

WebDemo enables `DiscoverAccountTypes` by default when Entra is enabled. It reads
`signInAudience` from the existing Azure app registration; there is no second
local account-audience setting. The same policy controls browser sign-in and
native token acceptance and challenges.

The table describes server token policy and authority selection. It does not
establish native Office acquisition for every audience; direct personal-account
sign-in remains unsupported in that integration.

| Azure `signInAudience` | Allowed issuing tenants | Authority tenant segment |
| --- | --- | --- |
| `AzureADMyOrg` | The registration's home tenant | Home tenant GUID |
| `AzureADMultipleOrgs` | Organizational tenants | `organizations` |
| `AzureADandPersonalMicrosoftAccount` | Organizational and personal accounts | `common` |
| `PersonalMicrosoftAccount` | Personal Microsoft accounts | `consumers` |

Classification uses the validated token's issuing tenant. A personal account
represented as a guest in an organizational tenant has that organization's
`tid` and its guest `oid`; it is a different login link from the direct personal
identity. Local invitations do not create Azure guest users or external-login
links. Email addresses do not merge those identities. Guest identity mapping
does not establish tested native guest acquisition or support direct personal
sign-in without a tenant invitation.

Discovery obtains a Microsoft Graph token from the home tenant with the
application credential and reads
`GET /applications(appId='APPLICATION_GUID')?$select=appId,signInAudience,api`.
It checks the returned application ID, known audience and v2 access-token
configuration. The application needs permission to read its registration. A
permission failure is reported without automatically requesting or granting
broader permissions. Do not give an application write permission just to change
its supported account types through this feature; this lookup is read-only.

A successful policy is cached for 30 seconds by default. Refresh runs on demand,
including when `/auth/entra/status` is requested. The JSON status reports
`available`, `supportedAccountTypes` and `lastRefreshedUtc`. Concurrent requests
share a refresh. An expired policy is never used to authorize a new sign-in or
token request. If discovery is unavailable, the host stays running and affected
operations return HTTP 503 with a sanitized unavailable state. Requests retry
after bounded backoff; throttling can delay a retry by up to five minutes.
The Graph credential token has its own cache and is renewed when needed.

Browser sign-in records the selected authority in protected authentication state
and uses it for both authorization and code redemption. Callback validation
checks the current account policy before creating a session, so a narrowing
change can reject a sign-in already in progress. The ordinary authorization-code
flow retains PKCE, nonce and correlation checks; pushed authorization requests
are disabled for this flow. Changing the Azure dropdown does not revoke existing
application cookies. Account validation, cookie expiry and explicit session
revocation continue to govern those sessions.

To test a change without editing code or restarting WebDemo:

1. Confirm `/auth/entra/status` reports the current audience, then complete a
   fresh sign-in with an already-linked account.
2. Change **Supported account types** in Azure and save. Azure may require other
   registration properties to meet that audience's constraints before accepting
   the change.
3. Wait at least 30 seconds with the default refresh interval, then request the
   status again. If it returns 503, resolve the reported credential, permission
   or registration error and retry; the old policy is not a fallback.
4. Start a fresh sign-in and verify the expected account is allowed or rejected.
   For an allowed new identity, provision or link its exact tenant/object ID with
   the [operator commands](#microsoft-entra-sign-in). Keep the existing database
   and accounts. Reusing a session cookie does not test new sign-in policy.
5. Test real Office open, save, reopen and renewal separately for the supported
   organizational-account flow. Mocked token and browser-flow tests do not
   establish native Office acquisition. A personal-capable Azure registration
   does not enable direct personal-account sign-in in this integration.

Changing an audience may also affect Azure credential acquisition or application
reads, especially for a personal-accounts-only registration. Verify that discovery
still succeeds after the cached Graph token expires. The dropdown-only behavior
applies after Azure accepts the registration change and the application can read
it; it does not bypass Azure's registration or client restrictions.

Set `Authentication:Entra:DiscoverAccountTypes=false` only to select WebDemo's
fixed-home-tenant behavior explicitly. Package consumers opt into discovery as
described below.

## First setup

Start the sample through Aspire. Its `storage-init` job initializes storage
schema version 7 and authentication schema version 1. The web host checks the
schema at startup. Creating a document records the signed-in user as its owner.

Retrieve `ConnectionStrings__cellbridge` privately from the web resource's
Aspire environment and supply it to the operator tool. Keep it out of logs and
committed configuration.

```sh
# Password is read from stdin.
dotnet run --project tools/CellBridge.Admin -- create-user --id operator --login operator --display-name Operator --can-create true
dotnet run --project tools/CellBridge.Admin -- create-user --login reader --display-name Reader
```

Passwords need at least 12 characters with uppercase, lowercase, a digit and a
nonalphanumeric character. Five failed attempts lock the account for 15 minutes.
Accounts are provisioned by the operator; there is no public registration.

Open WebDemo's allocated HTTPS endpoint followed by `/library`, sign in and create a document. To import
existing files with a chosen owner:

```sh
dotnet run --project tools/CellBridge.Admin -- import-directory --directory /absolute/path/to/documents --owner local:operator
```

Supply an enabled local account as owner and the web host's `Storage` settings.
Imports create missing files only, preserve saved content and ownership, and
record `system:imports` as creator and initial modifier. The
[demo guide](demo-library.md#import-existing-files) describes import behavior.

### Development database versions

Initialization accepts a fresh database or the current schema. Older experimental
schemas are rejected. Recreate an outdated development database, then provision
accounts and create or import documents again.

## Protect authentication keys

The sample authentication service encrypts ASP.NET Core Data Protection keys before it stores them in
PostgreSQL. On a local single-machine installation, the web and demo processes
use one generated certificate under the current user's local application-data
directory. Keep that user profile and the PostgreSQL key rows in backups. Both
are required to read existing authentication cookies and protected login state.
This certificate protects authentication keys, not document contents. On Unix,
local certificate creation synchronizes its file and ancestor directories before
startup can store keys encrypted with it.

Processes under the same user profile share the local default certificate. For
deployments with distinct user profiles, machines, or containers, supply the same
PKCS#12 certificate and password to every instance:

```text
Authentication__KeyProtection__CertificatePath=/run/secrets/cellbridge-key-protection.pfx
Authentication__KeyProtection__Password=secret-store-value
Storage__MultipleInstances=true
```

The certificate must contain an RSA private key of at least 2048 bits, permit key
encipherment, and be within its validity period. On Unix, the certificate file
must have owner-only permissions and its directory must not be writable by group
or other users. The path cannot be a symbolic link. On Windows, place it under a
profile or directory whose ACL grants access only to the service identity and
trusted administrators.

Do not generate a different certificate in each container. Losing or replacing
the certificate prevents startup while stored keys require the old private key.
Retain the original certificate in backups. This sample configures one wrapping
certificate; rotation requires a host extension that retains old decryption
certificates, or an explicitly coordinated reset of authentication keys and
sessions. At startup, CellBridge encrypts legacy plaintext
Data Protection key rows in place without changing their key IDs, so existing
cookies remain valid. Stop older CellBridge versions while the first upgraded
instance performs this migration.

Backups made before the migration still contain plaintext cookie keys. Restrict
or replace those backups. Startup checks that every existing key can decrypt
with the selected certificate and fails if a different certificate is selected.

The sample login POST requires `UseRateLimiter()` and `UseRequestTimeouts()` in
the host. Its absolute request timeout is 30 seconds. It admits at most
20 attempts per minute and two concurrent requests per remote IP, with aggregate
limits of 100 attempts per minute and eight concurrent requests. It returns
HTTP 429 without queueing. These limits belong to the login policy and preserve
the host's global rate limiter and rejection settings. Attempts rejected by an
exhausted per-IP limit do not consume the aggregate login budget.
Account lockout remains separate. Trusted forwarded
headers run before rate limiting so a known proxy can supply the client address.

## Manage access

Use the resource GUID from an authorized FSSHTTP response or an operator database
query. The admin commands need the database connection. Protocol and library
users cannot grant access.

```sh
dotnet run --project tools/CellBridge.Admin -- grant --resource-id GUID --subject local:USER_ID --access read
dotnet run --project tools/CellBridge.Admin -- grant --resource-id GUID --subject local:USER_ID --access write
dotnet run --project tools/CellBridge.Admin -- grant --resource-id GUID --subject local:USER_ID --access none
dotnet run --project tools/CellBridge.Admin -- set-owner --resource-id GUID --subject local:USER_ID
dotnet run --project tools/CellBridge.Admin -- set-user --subject local:USER_ID --enabled false
dotnet run --project tools/CellBridge.Admin -- set-user --subject local:USER_ID --can-create false
```

Owners retain Read and Write until ownership transfers. Transfer does not create
a grant for the former owner. Account IDs stay stable when display names change.
Account changes invalidate cookies at the next one-minute security-stamp check.

Document grants are checked on each operation. Revoking Write downgrades editor
sessions to readers and removes editing leases. Revoking Read removes sessions
too. Saves recheck permission and lease ownership before publishing; retry
receipts require the original writer and current Write access.

## Customize the sign-in page

Set Aspire's `Parameters__ApplicationName` to change the displayed application
name. Outside Aspire, use `Authentication__ApplicationName`. The default is
`CellBridge`.

To replace the sample login page:

```csharp
app.MapCellBridgeAuthentication(renderLoginPage: page => MyLoginPage.Render(page));
```

The renderer receives `LoginPageContext` with the application name, antiforgery
tokens, validated local return URL and sign-in failure flag. Submit a form by
POST to `/auth/login` with `login`, `password`, `returnUrl`, and a hidden
field named `page.Antiforgery.FormFieldName` containing
`page.Antiforgery.RequestToken`. HTML-encode inserted values. The existing
handler validates the token and credentials.

## Protocol behavior

Authentication runs before SOAP/MTOM parsing. In WebDemo's forms mode, anonymous
Office requests receive HTTP 403 with MS-OFBA login and completion URLs. Entra
mode returns its bearer challenge unless fallback is explicitly enabled.
JSON APIs return 401; browser
library requests redirect to login. Authenticated denials do not start another
sign-in exchange.

| Operation | Required permission |
| --- | --- |
| Catalog, GET, HEAD, metadata, versions, QueryChanges, lock status | Read |
| QueryAccess | Reports Read and Write separately |
| PutChanges, acquire/refresh editing locks, JoinCoauthoring | Write |
| Join/refresh EditorsTable as reader | Read |
| Join/refresh EditorsTable as editor | Write |
| Release own lock or leave own session | Read and matching authenticated owner |
| Create document | Account creation permission |

SOAP denials use `FileUnauthorizedAccess`; binary denials use
`E_ACCESSDENIED`. A caller without Read receives no resource IDs, canonical
document URLs or file properties. Each dependent SOAP operation is authorized
independently. Client or lock IDs cannot impersonate another user.

`WhoAmI` reports the caller; author metadata records the creator and
`ModifiedBy` the last successful writer. Login, logout and document creation
use antiforgery validation. SOAP accepts XML/MTOM and rejects browser simple
content types and cross-origin Origin headers. Health checks remain anonymous.

## Reusable hosts

Use your existing ASP.NET Core cookie/Identity setup or validated bearer tokens.
`CellBridge.AspNetCore` is independent of the sample authentication library and
its account database. Register authentication before authorization and map
validated principals to these claims:

| Claim | Meaning |
| --- | --- |
| `cellbridge:subject` | Required stable, authority-qualified user identity. |
| `cellbridge:display-name` | Optional display name. |
| `cellbridge:create=true` | Permission to create documents. |

The [package consumer](../examples/NuGetConsumer/README.md) demonstrates JWT
validation and claim mapping. Core registration and browser-login helpers do not
enable MS-OFBA. For packaged browser login pages, use
`AddCellBridgeIdentityLogin<TUser>` with existing Identity accounts. It supplies
authenticator and recovery-code continuation without a custom authenticator.
For custom account systems, use `AddCellBridgeLogin` with your cookie scheme and
a host credential checker. Supplying custom HTML is optional.
To enable the Office forms challenge and completion endpoint, separately register
`AddCellBridgeOfficeFormsAuthentication` with the same cookie scheme. This can
use the packaged pages or an existing complete host login flow. Enabling forms
authentication leaves OAuth-to-forms fallback disabled. See the
[explicit options](package-integration.md#enable-office-forms-authentication).

### Native Office Entra authentication

Enable native Entra authentication in the hosting callback on `AddCellBridge`.
`MapCellBridge()` maps the native cookie-exchange endpoint and selects the Office
scheme for protocol routes. No separate mapping call or Razor Pages dependency
is required. The same backend can serve Blazor, React, MVC or another frontend.

The host registers its accounts and application cookie first. For existing
ASP.NET Core Identity accounts:

```csharp
using CellBridge.AspNetCore;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;

// Existing AddIdentity<ApplicationUser, IdentityRole>() and user store go here.
builder.Services.ConfigureApplicationCookie(cookie =>
{
    cookie.Cookie.Name = EntraAuthentication.SessionCookieName;
    cookie.Cookie.Path = "/";
    cookie.Cookie.HttpOnly = true;
    cookie.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    cookie.Cookie.Domain = null;
});

builder.Services.AddCellBridge(provider,
    configure: options => options.PublicOrigin = "https://office.example.test",
    configureHosting: hosting => hosting.Authentication(auth =>
    {
        auth.UseIdentity<ApplicationUser>();
        auth.UseEntra(builder.Configuration.GetSection("Entra"));
    }));

// After UseAuthentication() and UseAuthorization():
app.MapCellBridge();
```

The `Entra` section requires the home `TenantId` and API `ClientId`. Calling
`UseEntra` enables this flow without an `Enabled` flag. By default,
`DiscoverAccountTypes=false` selects fixed-home-tenant authentication and needs
no client secret. To follow Azure's dropdown, configure:

```json
{
  "Entra": {
    "TenantId": "HOME_TENANT_GUID",
    "ClientId": "API_APPLICATION_GUID",
    "DiscoverAccountTypes": true
  }
}
```

Supply `Entra:ClientSecret` through the host's secret store for the read-only
Graph lookup. Keep `TenantId` set to the registration's home tenant; do not
replace it with `common` or `consumers`. `AudienceRefreshInterval` defaults to
30 seconds and accepts one second through five minutes. `DiscoveryTimeout`
defaults to ten seconds and accepts one through thirty seconds. Browser OIDC
registration, its client secret and callback routes remain host-owned. Follow
[Azure-driven account policy](#follow-azures-supported-account-types) and the API
registration steps under [Microsoft Entra sign-in](#microsoft-entra-sign-in).

`UseIdentity<TUser>` uses the existing Identity application cookie. It finds
external-login links with provider `EntraAuthentication.LoginProvider` and key
`EntraAuthentication.ProviderKey(tenantId, objectId)`, checks sign-in eligibility
and lockout, and rejects locally two-factor-enabled accounts. It never creates
an account or links by email. The consumer owns provisioning and authenticated
account-linking flows. Its default CellBridge subject uses the same `application`
namespace as packaged Identity login. Keep subject mapping stable after document
creation. Configure `Subject`, `DisplayName`, `CanCreate` and `IsAllowed` callbacks
on `UseIdentity<TUser>` for host-specific account fields and policies.

For a custom account system, replace `UseIdentity` with
`auth.UseMapper<MyExternalIdentityMapper>("MyCookie")`. The host registers that
cookie with the native attributes above. The scoped mapper implements
`ICellBridgeExternalIdentityMapper.MapAsync(ValidatedExternalIdentity,
CancellationToken)` and returns an authenticated host principal with the required
CellBridge subject claim, or null to reject sign-in. Its input contains the
validated provider, provider key, tenant ID and object ID. The package checks the
native token's issuer, audience, signature, lifetime, scope and Office client ID
before invoking the mapper. It does not copy provider-supplied CellBridge claims
into the host principal.

Adding a Google or other browser login through standard ASP.NET Core schemes
can map another external login to the same local account. That does not establish
native Google token acquisition or renewal in Office. Native provider support
remains limited to the implemented Entra flow; direct personal Microsoft accounts
are unsupported. Forms authentication and
OAuth-to-forms fallback remain separate explicit opt-ins.

Automated checks exercise signed tokens, all four server audience policies,
policy transitions, cookie exchange and repeated discovery. Mocked browser tests
exercise authorization, code redemption, nonce, correlation and PKCE, including
a policy change during sign-in. They do not run native Office. Real-client open,
save, reopen and renewal checks remain separate.

### Integration constraints

Request-facing service methods take a `CellBridgeActor`. `CreateAsync` records
the actor as owner and creator; trusted imports supply an owner and importer.
NameIdentifier, email, ClientID and forwarded headers are not substitutes for
the validated CellBridge subject.

The default `StoredGrantAuthorizationPolicy` uses document ownership and grants.
Register `ICellBridgeAuthorizationPolicy` before `AddCellBridge` to replace it.
The [DocumentLibrary example](../examples/DocumentLibrary/README.md) maps
Documents and Lists from one authenticated principal. It uses stored document
grants and a host-owned Lists authority. The
[HostAuthorizedLists example](../examples/HostAuthorizedLists/README.md) shows
Lists-only host authorization.

### Host-owned policy

`ICellBridgeAuthorizationPolicy` selects a binding for every creation/import and
resolves an immutable `ICellBridgeAuthorizationSnapshot` for existing state.
It receives the final resource GUID, including host-selected GUIDs. Resolve each
snapshot by resource ID and the complete persisted binding:

```csharp
public sealed record DocumentAuthorizationBinding(
    string PolicyDomain, int ContractVersion, long Revision);
// Stored at DocumentState.Security.AuthorizationPolicy, default null.
```

The external contract supports version `1` and positive, increasing revisions.
`PolicyDomain` is an ordinal, authority-qualified trust namespace configured by the
host, not a caller-selected value. A null binding selects stored grants only under
the default policy. External-policy hosts reject unbound documents. Bound
documents ignore owner/grants and fail closed under a default-policy host.
`Owner`, `CreatedBy` and `ModifiedBy` remain attribution; creating a document
does not grant its creator external access. Creation still requires the trusted
actor's `CanCreate`. The host must provide an initial snapshot before publication.
Failed binding/resolution publishes no document, though already staged content
can remain charged until ordinary orphan collection.

Snapshots evaluate stable subject strings, including owners already recorded in
editor sessions, leases and accepted receipts. Display names, login names,
request claims, client IDs and lock IDs do not grant permission. Undefined
subjects return `None`; `Write` includes `Read`. Request `DocumentAccessLimit`
continues to restrict access for its one resource.

Policy members run synchronously inside coordinated operations. They must be
bounded, immutable for each key and perform no I/O. Fetch permission data and
populate local snapshots outside those operations. Never replace permissions
under an installed resource/binding key. A missing snapshot, wrong resource,
foreign domain, unsupported version, malformed binding, undefined flags or
exception denies access. HTTP/SOAP/binary denials use the existing responses and
omit protected document metadata. The catalog applies this same policy before
counting visible offsets or returning rows.

### Permission update boundary

Prepare and install the next immutable snapshot, then call the trusted service
operation:

```csharp
bool committed = await documents.UpdateAuthorizationAsync(
    resourceId, expectedBinding, nextBinding, cancellationToken);
```

The operation compares the complete expected binding, requires an increasing
revision in the same domain/version, and commits the new binding together with
session/lease reconciliation. It increments the coordination generation so saves,
metadata uploads, history restores and host write tokens prepared under the old
revision cannot publish. Revoke then regrant also fences an old preparation.
Existing receipts remain recorded; replay requires the original authenticated
writer's current Write permission and rechecks after loading response content.

The successful document transaction is the effective permission-change boundary.
Installing a snapshot alone has no effect. A remote permission mutation that
bypasses this operation does not revoke CellBridge access. Hosts must coordinate
their permission source through this boundary, retry a failed/stale CAS using
fresh state, and acknowledge revocation only after the committed binding is
observed. Permission updates that cannot resolve the next snapshot fail without
publishing. A throwing recorded-subject evaluation aborts the update transaction.
If the commit outcome is unknown, read the stored binding before retrying.

All instances use shared document state and the same policy domain. Distribute
the exact new snapshot before or after committing it; a peer missing that
revision denies access until it arrives. No instance may fall back to its cached
old revision. Removing Read deletes sessions. Removing Write downgrades editors
and removes schema, exclusive, coauthor and host leases in the same transaction.
Editor partition knowledge advances when recorded sessions change. Existing
read operations may finish from an already authorized snapshot; revocation does
not recall bytes already sent. Prepared writes recheck at publication.

Upgrade every host and reader before installing external bindings. The optional
field preserves the current state encoding, but older binaries, including
`0.1.0-beta.1`, ignore it and therefore cannot safely share or restore bound state.
Do not downgrade such a store without a trusted policy migration. Structural
format compatibility does not make older authorization behavior compatible.

Permission administration is separate from document Write. This API is for
trusted host integration, with no general permission-update HTTP endpoint.
An expected null binding permits an explicit trusted conversion of a legacy
document to the configured external domain. Binding removal or foreign-domain
migration requires a separate trusted migration; this API rejects both.
Stored-grant operator commands reject bound documents.

[Portable recovery version 1](provider-portability.md#authorization-and-external-destinations)
supports stored grants and rejects any non-null `DocumentSecurity.AuthorizationPolicy`
in current or retained snapshots. Recovering host-bound documents requires a future
supported workflow that coordinates the external permission source with the exact
trusted policy snapshots and destination domain. Matching subject or domain text
alone does not establish authority. Never clear a binding to null or grant implicit
owner access to bypass recovery validation.

### Legacy evaluator

`ICellBridgeAccessEvaluator` retains its beta behavior on unbound documents under
the default policy. It can replace stored-grant decisions there, but does not
provide versioned external revocation or automatic lease reconciliation. Use the
new policy contract for host-owned permissions. With an external policy, an
explicit legacy evaluator is an additional ceiling and cannot bypass a missing
binding/snapshot. Evaluators must also use stable subjects, be bounded and perform
no I/O; mutable evaluator decisions are not a coordinated permission update.

The sample account database is outside the reusable NuGet packages.
`CellBridge.AspNetCore` packages the default login HTML, optional renderer and
Office forms challenge/completion flow and native Entra authentication. The account
database and browser provider setup remain host-owned.
See [login integration](package-integration.md#use-the-built-in-login-page).
Keep passwords, tokens, cookies and connection strings out of shared captures.
The authentication database holds cookie protection keys; protect its access
and backups.

## Desktop validation

Office must reach and trust the public HTTPS origin. Set
`Parameters__PublicOrigin` for remote clients; see the
[Tailscale setup](automated-testing.md#connect-the-windows-laptop-through-tailscale).
You can open a document from the library or launch its URL directly:

```powershell
Start-Process 'ms-word:ofe|u|https://dev-machine.example.ts.net:8444/shared/ofba-laptop.docx'
```

If Word blocks the sign-in prompt, open File > Options > Trust Center > Trust
Center Settings > Form-based Sign-in. Select "Ask me what to do for each host",
retry and approve the test host when prompted. Organization policy may control
this setting.

Office receives the HTTP 403 challenge, displays the CellBridge form, reaches
`/_cellbridge/auth/complete` and retries with its cookie. Use separate Windows profiles or
machines to test different accounts, because Office can share cached credentials.

For repeatable save/reopen checks, follow
[desktop Word testing](automated-testing.md#run-real-desktop-word).
Tested scenarios are listed in [client coverage](interoperability.md#client-coverage).
