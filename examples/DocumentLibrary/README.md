# Package-only document library

For a remote HTTPS origin, set `Protocol__PublicOrigin` to that fixed origin.
Office challenges, protocol responses and library open links use it rather than
an incoming Host header. Without a fixed origin, remote protocol requests need
a specific `AllowedHosts` list; loopback is available for local testing.

This example hosts CellBridge from NuGet packages. It keeps CellBridge state and content in PostgreSQL. A separate destination directory stores immutable Office files. Each document has one atomically replaced manifest that selects the current file revision and retains delivery receipts.

This revision uses unreleased async authorization APIs from PR93. Published
`0.1.0-beta.4` packages do not provide these APIs. Build the local packages and
select the local feed with an isolated package cache as described below before
running or testing this version of the example.

This is separate from the repository's Razor Pages demo at `/library`. Both
support creating blank Word, Excel and PowerPoint files. This example also
uploads existing documents and delivers saves to a separate destination.

The workspace has a file list with modification dates, file sizes and file
status. Search, file-type filters and sorting work in the browser on the
documents returned for your account. Use **New document** or **Upload** to add
files, and a file's actions menu to download it, see saved versions or change
access. Document links open desktop Office. These links and forms also work
without JavaScript; search, filters and sorting require it.

The example uses the package's `AddCellBridgeCookieLogin` with its standard
username/password page. Supplying custom HTML is optional. The package handles
the form, CSRF checks, cookie sign-in and Office challenge/completion. No login
page or challenge headers are implemented in this example.

## Application setup

`Program.cs` uses public methods from `CellBridge.AspNetCore`:

```csharp
builder.Services.AddCellBridgeWithExternalAuthorization<DocumentLibraryPermissionProvider>(provider);
builder.Services.AddCellBridgeExternalPublishing<DocumentLibraryDestination>();
builder.Services.AddCellBridgeCookieLogin<LocalLoginAuthenticator>("Cookies");
```

`AddCellBridgeWithExternalAuthorization<TProvider>` registers a singleton async
provider. This host reads current permissions from its durable destination
manifest for each read or write operation.
`AddCellBridgeExternalPublishing<TDestination>` registers the host's destination,
publisher and background queue worker. `AddCellBridgeCookieLogin<TAuthenticator>`
creates a cookie scheme and enables the standard login pages. These methods are
available to any package consumer.

`DocumentLibraryStorage.cs` selects this application's PostgreSQL connection and
destination directory. The example supplies its file catalogue, manifest-based
permission provider and private test accounts. It explicitly selects `Cookies` as
its default scheme and customizes the cookie name for this test application.

Ordinary hosts receive stored owner/grant authorization through `AddCellBridge(provider)`.
They need no custom policy. Applications with existing Identity accounts can use
`AddCellBridgeIdentityLogin<TUser>` with their account store; see
[package integration](../../docs/package-integration.md).

Startup checks storage health and reconciles publication bindings before serving
requests or starting the publication worker. New resources receive version-2
authorization bindings with the server-selected `document-library` tenant and
`local` workspace. These fixed identifiers describe this single-host example;
paths and form input never select authority scope.

## Document permissions and optional caching

The host grants Create to its local owner. Interactive creation and upload pass
the real authenticated actor to `CreateExternalAsync`, which also requires the
actor's `CanCreate` capability. The permission-admin form commits host permissions
without publishing a new CellBridge policy revision. Legacy permission snapshots,
document history and delivery receipts remain intact; permission edits keep one
current permission map instead of appending snapshots.

Caching is off by default. To enable the package's bounded, process-local
`CachedDocumentAuthorizationProvider`, set:

```text
DocumentLibrary__AuthorizationCache__Enabled=true
DocumentLibrary__AuthorizationCache__Capacity=1024
DocumentLibrary__AuthorizationCache__DecisionTtl=00:00:10
```

The same singleton decorator serves every document operation. After a permission
manifest commits, the host calls `InvalidateResource` on that instance, so both
cached allows and cached denies refresh. External manifest changes need explicit
invalidation or TTL expiry. This example permits only one process per destination
root and does not broadcast invalidations across processes.

Work already admitted before revocation may finish. Later operations check again.
Revocation does not remove an editor's old lease; that lease may block another
editor until it expires or a trusted operator clears it. Permission TTL and lease
expiry are separate limits. Provider failures deny access and never reuse expired
allows.

Use a fresh example database and destination root. This example does not migrate
legacy authorization state. Startup rejects legacy bindings, native documents,
and unrelated authority domains or scopes without changing their authorization.
Keep any existing database and destination together in a separate location if
you need them for recovery. The example never resets or takes over that storage.

A staged upload whose Create commit is uncertain remains marked pending. Startup
can finish publication binding when the authorized CellBridge state exists. It
refuses to import a pending upload with missing state, because trusted recovery
must not bypass a failed user Create check. A definitive Create denial or returned
path/resource conflict discards only its newly staged, unaccepted upload.

## Run the example

For private development testing, sign in as `ofba-operator` or `owner` to create
and manage documents, `editor` to edit, or `reader` to read. All use the test
password `Test1234!`. These fixed credentials belong only to this example. Both
login methods return 404 outside Development/Testing or for a known non-loopback
connection. A missing remote address is allowed by the test host. A loopback proxy
also passes the address check, so keep this example on a private test network.

Desktop Office has a separate sign-in session from your browser. Select an Office
link, sign in if prompted, and the dialog completes at
`/_cellbridge/auth/complete` before Office retries with its cookie.
The package defaults to `PersistOfficeSession = true`, so Office can retain this login after
closing. It expires after eight hours without sliding renewal. Word, Excel and
PowerPoint may reuse the same login cookie. Browser sign-ins request session cookies.

Run the package-only app and its PostgreSQL database through the standalone AppHost.
First run `python3 tools/verify_packages.py` at this source revision, then select
its local feed and isolated cache using the configuration below. The generated
`artifacts/packages/manifest.json` records the source commit and package hashes.
A launch without this configuration uses the machine's configured NuGet sources
and can restore older packages with the same beta version.

Run these commands from the repository root after the verifier has populated
`artifacts/packages`:

```powershell
$env:DocumentLibrary__RestoreConfigFile = (Resolve-Path artifacts/packages/NuGet.Config).Path
$env:DocumentLibrary__RestorePackagesPath = (Resolve-Path artifacts/packages/consumer-cache).Path

aspire start --apphost examples/DocumentLibrary/aspire/apphost.cs --non-interactive
aspire wait document-library --apphost examples/DocumentLibrary/aspire/apphost.cs --non-interactive
aspire describe --apphost examples/DocumentLibrary/aspire/apphost.cs --non-interactive
# Open the document-library HTTPS endpoint reported by aspire describe.
aspire stop --apphost examples/DocumentLibrary/aspire/apphost.cs --non-interactive
```

The AppHost does not auto-detect local package directories. This revision requires
both settings. Use the double-underscore environment names shown above because
.NET configuration maps them to `DocumentLibrary:RestoreConfigFile` and
`DocumentLibrary:RestorePackagesPath`.

The AppHost creates PostgreSQL, runs the package-only setup project, and starts the web app only after schema initialization succeeds. Its normal mode keeps a named PostgreSQL volume and stores destination files under the ignored `artifacts/document-library` directory. Set `Testing__Enabled=true` in the environment for disposable PostgreSQL and a unique temporary destination. This disposable mode also exposes a local HTTP endpoint for browser checks that cannot trust the developer certificate. Only one app process may use a destination root. The app holds an exclusive lock file for its full lifetime.

## Customer lists

The standalone AppHost also enables `/lists`, using the optional list packages.
This UI deliberately retains the native workspace membership and list-grant
demonstration. It does not exercise PR93's external list authorization provider
or list authorization cache. The separate
[HostAuthorizedLists example](../HostAuthorizedLists/README.md) covers that mode.
The setup process owns database provisioning; the web process receives a dedicated
non-owner runtime login with document DML and workspace-isolated list access.
It receives no setup credentials. Outside the AppHost, lists are opt-in through
`Lists:Enabled=true` and `ConnectionStrings:lists`.

The local owner sees Acme and Contoso. Editor and reader see only Acme. Each
customer starts with Work packages, Tasks and Deliverables from pinned company
templates. Task records link to a work package, receive a `TASK-` number and have
an External ID hidden from ordinary forms. Owner can add optional text fields and
create list J. Editor can edit records; reader can browse and download extracts.
These local account hints affect the UI only; the engine checks current grants.
An uncertain field addition retains its field ID, name and target list. Retry
checks the current schema for that ID before sending another update, so a lost
response cannot turn the same addition into a second field.

Open the same task in two tabs to try merging. Save a change in one tab, then
save another in the other tab. The second tab retains its draft, shows original,
current and proposed values, and offers choices for conflicting fields. A further
concurrent edit repeats the comparison without an unconditional overwrite.

CSV and XLSX links download one bounded page, with explicit extraction-limit
headers. The reporting feed provides typed metadata and a restricted OData subset.
Live Excel/Power BI imports and hosted refresh remain unverified. The small form
does not edit numeric fields; applications can supply their own precision-aware
controls through the same API. See the [list guide](../../docs/lists.md) for host
responsibilities, permissions and limits.

Setup resumes an interrupted initial seed and records completion in an
operator-owned table. Completed workspaces preserve edited records, deleted seed
records and revoked access on subsequent runs. Previously completed seeds without
a marker are recognized through their lists and durable record identities.
The seed is an initial local demonstration. Keep provisioning separate from
customer management; normal record changes remain transactional in the list engine.

The Status column shows whether the downloadable file has caught up with the
saved content in CellBridge. A committed save may remain pending if the
destination is unavailable or its revision changed outside CellBridge. The
recovery worker scans durable CellBridge state after every restart and retries
pending deliveries with their original operation IDs.

Back up PostgreSQL and the destination root as one coordinated recovery point. If a manifest says CellBridge binding completed but PostgreSQL no longer has that document, startup fails. The destination baseline cannot prove that PostgreSQL had no accepted, undelivered save before an independent database loss.

Desktop Office links use the authenticated `/shared/{fileName}` CellBridge URL. Preserve the external HTTPS scheme and hostname when using a reverse proxy so the links and sign-in headers point to the address your Office client can reach. Endpoint checks verify the sign-in handshake and authenticated document requests. Native Office open/edit/save behavior, distinct desktop identities, and Internet deployment remain separate qualification work.
