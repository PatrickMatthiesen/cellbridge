# Package-only document library

This example hosts CellBridge from NuGet packages. It keeps CellBridge state and content in PostgreSQL. A separate destination directory stores immutable Office files. Each document has one atomically replaced manifest that selects the current file revision and retains delivery receipts.

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

Run the package-only app and its PostgreSQL database through the standalone AppHost. A normal launch restores the published packages from the NuGet sources already configured on the machine:

```powershell
aspire start --apphost examples/DocumentLibrary/aspire/apphost.cs --non-interactive
aspire wait document-library --apphost examples/DocumentLibrary/aspire/apphost.cs --non-interactive
aspire ps --apphost examples/DocumentLibrary/aspire/apphost.cs --non-interactive
aspire stop --apphost examples/DocumentLibrary/aspire/apphost.cs --non-interactive
```

To smoke-test the unpublished `beta.2` packages produced by the final package verifier, select its feed configuration and isolated package cache explicitly. Run these commands from the repository root after the verifier has populated `artifacts/packages`:

```powershell
$env:DocumentLibrary__RestoreConfigFile = (Resolve-Path artifacts/packages/NuGet.Config).Path
$env:DocumentLibrary__RestorePackagesPath = (Resolve-Path artifacts/packages/consumer-cache).Path

aspire start --apphost examples/DocumentLibrary/aspire/apphost.cs --non-interactive
aspire wait document-library --apphost examples/DocumentLibrary/aspire/apphost.cs --non-interactive
aspire ps --apphost examples/DocumentLibrary/aspire/apphost.cs --non-interactive
# Open the document-library HTTPS endpoint reported by aspire ps.
aspire stop --apphost examples/DocumentLibrary/aspire/apphost.cs --non-interactive
```

The AppHost does not auto-detect local package directories. Leave both settings unset for a published-package restore. Use the double-underscore environment names shown above because .NET configuration maps them to `DocumentLibrary:RestoreConfigFile` and `DocumentLibrary:RestorePackagesPath`.

The AppHost creates PostgreSQL, runs the package-only setup project, and starts the web app only after schema initialization succeeds. Its normal mode keeps a named PostgreSQL volume and stores destination files under the ignored `artifacts/document-library` directory. Set `Testing__Enabled=true` in the environment for disposable PostgreSQL and a unique temporary destination. Only one app process may use a destination root. The app holds an exclusive lock file for its full lifetime.

The Status column shows whether the downloadable file has caught up with the
saved content in CellBridge. A committed save may remain pending if the
destination is unavailable or its revision changed outside CellBridge. The
recovery worker scans durable CellBridge state after every restart and retries
pending deliveries with their original operation IDs.

Back up PostgreSQL and the destination root as one coordinated recovery point. If a manifest says CellBridge binding completed but PostgreSQL no longer has that document, startup fails. The destination baseline cannot prove that PostgreSQL had no accepted, undelivered save before an independent database loss.

Desktop Office links use the authenticated `/shared/{fileName}` CellBridge URL. Preserve the external HTTPS scheme and hostname when using a reverse proxy so the links and sign-in headers point to the address your Office client can reach. Endpoint checks verify the sign-in handshake and authenticated document requests. Native Office open/edit/save behavior, distinct desktop identities, and Internet deployment remain separate qualification work.
