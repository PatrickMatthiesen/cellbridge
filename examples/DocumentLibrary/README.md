# Package-only document library

For a remote HTTPS origin, set `Protocol__PublicOrigin` to that fixed origin.
Office challenges, protocol responses and library open links use it rather than
an incoming Host header. Without a fixed origin, remote protocol requests need
a specific `AllowedHosts` list; loopback is available for local testing.

This example hosts CellBridge from NuGet packages. It keeps CellBridge state and
content in PostgreSQL. A separate destination directory stores immutable Office
files. Each document has a manifest that selects the current file revision and
stores delivery receipts. Updates replace the whole manifest atomically.

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
builder.Services.AddCellBridge<DocumentLibraryPermissionPolicy>(provider);
builder.Services.AddCellBridgeExternalPublishing<DocumentLibraryDestination>();
builder.Services.AddCellBridgeCookieLogin<LocalLoginAuthenticator>("Cookies");
```

`AddCellBridge<TPolicy>` registers a shared singleton permission policy.
`AddCellBridgeExternalPublishing<TDestination>` registers the host's destination,
publisher and background queue worker. `AddCellBridgeCookieLogin<TAuthenticator>`
creates a cookie scheme and enables the standard login pages. These methods are
available to any package consumer.

`DocumentLibraryStorage.cs` selects this application's PostgreSQL connection and
destination directory. The example supplies its file catalogue, manifest-based
permission policy and private test accounts. It explicitly selects `Cookies` as
its default scheme and customizes the cookie name for this test application.

Ordinary hosts receive stored owner/grant authorization through `AddCellBridge(provider)`.
They need no custom policy. Applications with existing Identity accounts can use
`AddCellBridgeIdentityLogin<TUser>` with their account store; see
[package integration](../../docs/package-integration.md).

Startup checks storage health and loads document permissions before serving
requests or starting the publication worker.

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
Until the optional list packages are published, first run `python3 tools/verify_packages.py`
and select its local feed using the restore commands below. A launch without that
configuration uses the NuGet sources already configured on the machine:

```powershell
aspire start --apphost examples/DocumentLibrary/aspire/CellBridge.DocumentLibrary.AppHost.csproj --non-interactive
aspire wait document-library --apphost examples/DocumentLibrary/aspire/CellBridge.DocumentLibrary.AppHost.csproj --non-interactive
aspire describe --apphost examples/DocumentLibrary/aspire/CellBridge.DocumentLibrary.AppHost.csproj --non-interactive
aspire stop --apphost examples/DocumentLibrary/aspire/CellBridge.DocumentLibrary.AppHost.csproj --non-interactive
```

To smoke-test packages built locally by the package verifier, select its feed configuration and isolated package cache explicitly. Run these commands from the repository root after the verifier has populated `artifacts/packages`:

```powershell
dotnet restore examples/DocumentLibrary/aspire/CellBridge.DocumentLibrary.AppHost.csproj `
  --configfile artifacts/packages/NuGet.Config `
  --packages artifacts/packages/consumer-cache
$env:NUGET_PACKAGES = (Resolve-Path artifacts/packages/consumer-cache).Path
$env:RestoreConfigFile = (Resolve-Path artifacts/packages/NuGet.Config).Path

aspire start --apphost examples/DocumentLibrary/aspire/CellBridge.DocumentLibrary.AppHost.csproj --non-interactive
aspire wait document-library --apphost examples/DocumentLibrary/aspire/CellBridge.DocumentLibrary.AppHost.csproj --non-interactive
aspire describe --apphost examples/DocumentLibrary/aspire/CellBridge.DocumentLibrary.AppHost.csproj --non-interactive
# Open the document-library HTTPS endpoint reported by aspire describe.
aspire stop --apphost examples/DocumentLibrary/aspire/CellBridge.DocumentLibrary.AppHost.csproj --non-interactive
```

The AppHost does not auto-detect local package directories. Leave `NUGET_PACKAGES`
and `RestoreConfigFile` unset to use your ordinary NuGet configuration. These are
standard restore inputs inherited by the project build, rather than AppHost
resource settings.

The AppHost creates PostgreSQL, runs the package-only setup project, and starts
the web app only after schema initialization succeeds. It always keeps the named
PostgreSQL volume and stores destination files under the ignored
`artifacts/document-library` directory. Aspire allocates the HTTPS port. Tests own
separate fixtures and temporary destinations. Only one app process may use a
destination root. The app holds an exclusive lock file for its full lifetime.

## Customer lists

The standalone AppHost also enables `/lists`, using the optional list packages.
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
