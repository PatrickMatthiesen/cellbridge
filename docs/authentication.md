# Authentication and document permissions

The sample host uses operator-provisioned ASP.NET Core Identity accounts in
PostgreSQL. Browser and Office requests use a Secure, HttpOnly cookie.
Each document has an owner and explicit user grants. Access is denied unless
ownership or a grant permits it. Owners can read and write; Write implies Read.
Creating documents requires a separate account permission.

Desktop Office signs in through the [MS-OFBA challenge](https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-ofba/c2c4baef-c611-4e7b-9a4c-d009e678e3d2).
Office displays the host's sign-in form and uses the resulting cookie for
document requests.

## First setup

Start the sample through Aspire. Its `storage-init` job initializes storage
schema version 4 and authentication schema version 1. The web host checks the
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

Open `https://localhost:7292/library`, sign in and create a document. To import
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

Authentication runs before SOAP/MTOM parsing. Anonymous Office requests receive
HTTP 403 with MS-OFBA login and completion URLs. JSON APIs return 401; browser
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
validation and claim mapping. For the packaged default login page, register `AddCellBridgeLogin` with your
cookie scheme and a host credential checker. Supplying custom HTML is optional.
If you already have a login page, use `AddCellBridgeOfficeFormsAuthentication`
for just the Office challenge and completion endpoint.
See [login integration](package-integration.md#use-the-built-in-login-page).

### Integration constraints

Request-facing service methods take a `CellBridgeActor`. `CreateAsync` records
the actor as owner and creator; trusted imports supply an owner and importer.
NameIdentifier, email, ClientID and forwarded headers are not substitutes for
the validated CellBridge subject.

The default `StoredGrantAuthorizationPolicy` uses document ownership and grants.
Register `ICellBridgeAuthorizationPolicy` before `AddCellBridge` to replace it.
The [host authorization consumer](../examples/HostAuthorization/README.md) runs
JWT authentication, catalog/download checks and a coordinated revocation without
Identity accounts or CellBridge grants.

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
If the commit outcome is unknown, read the durable binding before retrying.

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

The sample account database and login pages are outside the reusable NuGet packages.
`CellBridge.AspNetCore` packages the default login HTML, optional renderer and
Office challenge/completion flow. The account database remains host-owned.
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
