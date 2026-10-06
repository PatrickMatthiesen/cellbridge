# Package-only document library

This example hosts CellBridge from NuGet packages. It keeps CellBridge state and content in PostgreSQL. A separate destination directory stores immutable Office files. Each document has one atomically replaced manifest that selects the current file revision and retains delivery receipts.

The built-in owner, editor, and reader accounts exist only for local testing. The sign-in endpoint rejects non-loopback clients and runs only in the Development or Testing environment. This is not an Internet-ready authentication design.

Desktop Office uses a separate sign-in session from your browser. An unauthenticated document or protocol request receives the Office forms-authentication challenge, which opens the same local account selector. Select an identity and sign in; the dialog returns to `/auth/complete` before Office retries the document with its cookie. Browser visits still use `/local-login`. A loopback reverse proxy also passes the connection-address check, so anyone allowed through that proxy can choose a test identity. Keep this example on a private test network.

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

The library reports CellBridge commit status separately from destination delivery. A committed save may remain pending if the destination is unavailable or its revision changed outside CellBridge. The recovery worker scans durable CellBridge state after every restart and retries pending deliveries with their original operation IDs.

Back up PostgreSQL and the destination root as one coordinated recovery point. If a manifest says CellBridge binding completed but PostgreSQL no longer has that document, startup fails. The destination baseline cannot prove that PostgreSQL had no accepted, undelivered save before an independent database loss.

Desktop Office links use the authenticated `/shared/{fileName}` CellBridge URL. Preserve the external HTTPS scheme and hostname when using a reverse proxy so the links and sign-in headers point to the address your Office client can reach. Endpoint checks verify the sign-in handshake and authenticated document requests. Native Office open/edit/save behavior, distinct desktop identities, and Internet deployment remain separate qualification work.
