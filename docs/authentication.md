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
schema version 3 and authentication schema version 1. The web host checks the
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
validation and claim mapping. Desktop Office needs a compatible challenge/sign-in
exchange in addition to API authentication.

### Integration constraints

Request-facing service methods take a `CellBridgeActor`. `CreateAsync` records
the actor as owner and creator; trusted imports supply an owner and importer.
NameIdentifier, email, ClientID and forwarded headers are not substitutes for
the validated CellBridge subject.

Stored grants drive catalog filtering, sessions and leases. A custom
`ICellBridgeAccessEvaluator` can restrict those grants; it must be deterministic
and perform no I/O within document transactions. Replacing stored grants with an
external permission store requires integrating that store across these operations.

The sample authentication library is outside the reusable NuGet packages.
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
`/auth/complete` and retries with its cookie. Use separate Windows profiles or
machines to test different accounts, because Office can share cached credentials.

For repeatable save/reopen checks, follow
[desktop Word testing](automated-testing.md#run-real-desktop-word).
Tested scenarios are listed in [client coverage](interoperability.md#client-coverage).
