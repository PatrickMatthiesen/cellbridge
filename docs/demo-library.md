# Demo document library

The Razor Pages library lets you create, browse, download and open Office files
stored by CellBridge. It runs as Aspire's `demo` resource and is available through
the web host at `/library`.

## Run

From the repository root:

```sh
aspire start --apphost aspire/apphost.cs --non-interactive
aspire wait web --non-interactive
aspire wait demo --non-interactive
aspire describe --non-interactive
```

[Provision an account](authentication.md#first-setup), open
`https://localhost:7292/library` and sign in. The library shows files you can
read, with their size, saved version, modification time and active sessions.
Search filters by filename. Reload to refresh the listing.

## Create and edit a document

Choose **New document**, select Word, Excel or PowerPoint and enter a filename.
CellBridge adds the file immediately. Duplicate names are rejected.

Use the Office open button to open the server URL in the matching desktop app.
Edit and save normally. The download button retrieves the latest stored file.

Office must be installed on the client computer and trust the server's HTTPS
endpoint. Word may require approval of its forms-sign-in host; see
[Office sign-in](authentication.md#desktop-validation).
[Client coverage](interoperability.md#client-coverage) lists tested scenarios.

## Import existing files

After provisioning an owner, supply the admin command with the same database
connection and `Storage` configuration as the web host:

```sh
dotnet run --project tools/CellBridge.Admin -- import-directory --directory /absolute/path/to/documents --owner local:operator
```

Top-level files become `/shared/<filename>`. Subdirectories and Office
`~$` lock files are skipped. Imports create missing files only, preserving
existing saved content and ownership.

Saves update CellBridge storage and leave the import directory unchanged.
Aspire uses persistent PostgreSQL storage. An explicitly configured in-memory
provider loses documents and edits on restart.

## Connect another computer

Set Aspire's `Parameters__PublicOrigin` to the HTTPS origin reachable by the
browser and Office. The default is `https://localhost:7292`; another computer
needs a hostname that resolves to the server and a trusted certificate.
See [Tailscale setup](automated-testing.md#connect-the-windows-laptop-through-tailscale).

For an independently hosted demo, configure `CollabServer:BaseUrl` as its
internal API address and `CollabServer:PublicBaseUrl` as the browser/Office
address. Aspire supplies both automatically.

## API and document identity

The standalone solution is `demo/CellBridge.Demo.slnx`.
The library calls `GET /api/documents` and creates documents with
`POST /api/documents` using JSON such as
`{ "name": "My test", "type": "docx" }`. The other types are `xlsx` and
`pptx`. Creation returns 201, invalid input returns 400 and duplicate names
return 409. Requests use the signed-in caller's cookies and antiforgery token.

A supplied protocol ResourceID takes precedence over the URL. Unknown IDs fail;
lookup never creates a document or redirects an obsolete ID to another file.
After restarting volatile storage, create a fresh document to get a new identity.
GET and HEAD return content and headers from one saved revision.

## Test the demo

With Aspire stopped:

```sh
dotnet test demo/CellBridge.Demo.slnx
```

The [isolated test runner](automated-testing.md) also exercises catalog and
session requests against live hosts.
