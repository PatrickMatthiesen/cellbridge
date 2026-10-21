# Demo document library

The Razor Pages library lets you create, browse, download and open Office files
stored by CellBridge. `src/CellBridge.WebDemo` serves Razor Pages and protocol
endpoints in one process, available as Aspire's `webdemo` resource at `/library`.

## Run

From the repository root:

```sh
aspire start --isolated --apphost src/CellBridge.AppHost/CellBridge.AppHost.csproj --non-interactive
aspire wait webdemo --apphost src/CellBridge.AppHost/CellBridge.AppHost.csproj --non-interactive
aspire describe --apphost src/CellBridge.AppHost/CellBridge.AppHost.csproj --non-interactive
```

[Provision an account](authentication.md#first-setup), open
the HTTPS endpoint shown for `webdemo`, followed by `/library`, and sign in.
The library shows files you can read, with their size, saved version, modification time and active sessions.
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
browser and Office. Local runs use the allocated HTTPS endpoint; another
computer needs a hostname that resolves to the server and a trusted certificate.
See [Tailscale setup](automated-testing.md#connect-the-windows-laptop-through-tailscale).


## API and document identity

The page uses the document service in process. HTTP consumers can call
`GET /api/documents` and create documents with
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
dotnet test tests/CellBridge.WebDemo.Tests
```

The [integration fixtures](automated-testing.md) also exercise catalog and
session requests against real PostgreSQL hosts.
