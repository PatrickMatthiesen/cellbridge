# Demo document library

The standalone solution is `demo/CellBridge.Demo.slnx`. It contains an
ASP.NET Core Razor Pages site and its tests. It reads the collaboration server's
`GET /api/documents` endpoint over HTTP. It shares the sample authentication
library and forwards the signed-in caller's cookies for each request. The
catalog is a demo API, not an implementation of a SharePoint
list API.

## Run

From the repository root:

```powershell
aspire start --apphost aspire/apphost.cs --non-interactive
aspire wait web --apphost aspire/apphost.cs --non-interactive
aspire wait demo --apphost aspire/apphost.cs --non-interactive
aspire describe --apphost aspire/apphost.cs --non-interactive
```

Provision accounts using the [authentication guide](authentication.md), then
open `/library` on the `web` HTTPS endpoint and sign in. Aspire supplies the collaboration server's HTTPS
endpoint through `CollabServer__BaseUrl`. The site shows file names, sizes,
versions, modification times and active client sessions. Search filters by file
name. Reload the page to refresh the catalog and session counts.

Use **New document** to create a Word document, Excel workbook or PowerPoint
presentation. Enter a name without an extension, or use the matching extension.
Creation adds the file to the server immediately and returns to the library,
where its Office launch and download buttons are available. Existing names are
rejected. This uses `POST /api/documents` with JSON `{ "name": "My test", "type": "docx" }`;
the other supported types are `xlsx` and `pptx`. It returns 201 on creation,
400 for an invalid name/type and 409 for a duplicate name.

Aspire's `PublicOrigin` parameter supplies the browser/Office origin,
defaulting to `https://localhost:7292`. Desktop Office must resolve the public hostname through the client's operating
system. Set `Parameters__PublicOrigin` before starting Aspire when testing from
other computers. See [remote desktop testing](automated-testing.md).

Each Office link opens the original collaboration-server URL in Word, Excel or
PowerPoint according to the extension. Download links request an attachment from
the server. Unsupported extensions still have download links. Office must be
installed on the computer where the link is clicked. The URI syntax follows
[Microsoft's Office URI schemes](https://learn.microsoft.com/en-us/office/client-developer/office-uri-schemes).

For an independently hosted demo, configure `CollabServer:BaseUrl`. If the browser
and desktop Office need a different address from the server-side HTTP client,
set `CollabServer:PublicBaseUrl` too. For another physical computer, that public
address must resolve to the server and its HTTPS certificate must be trusted;
`*.localhost` points to the client computer itself.

## Available files

With example seeding enabled, the sample imports three documents:

- `/shared/test.docx`
- `/shared/save-test.docx`
- `/shared/save-check.docx`

It is not restricted to those names. To load your own files, configure the web
project's `Documents:Directory`, for example in its `appsettings.Development.json`:

```json
{
  "Documents": {
    "Directory": "C:/OfficeCollabDemoFiles",
    "SeedExamples": false
  }
}
```

The directory must exist. Relative paths resolve against the web project's
content root. Top-level files load at startup as `/shared/<filename>`; nested
directories and Office `~$` lock files are ignored. Imported files with the same
name as an example replace the example during the first import. Startup creates
only missing documents, so saved revisions take precedence on later starts.
There is no upload form or automatic folder watcher.

Aspire uses durable PostgreSQL storage. Office saves update the stored revision,
not the input file on disk. Restarts preserve the resource ID and graph needed by
the next save. An explicitly configured in-memory provider still discards edits
on restart. See [storage configuration](storage-providers.md).

## Concurrent access and identity

Each document has independent content, versions, graph identities, locks and
editing sessions. The catalog counts client sessions, which are not necessarily
different people. Earlier desktop evidence used the shared `officelab` identity.
The current
host requires sign-in and enforces persisted document grants. Authenticated
desktop Office editing still needs live validation; see
[authentication setup](authentication.md). Two desktop clients coauthoring one file remain unverified.
See [interoperability coverage](interoperability.md).

A supplied ResourceID takes precedence over the document URL for every SOAP
subrequest, including lock release. Unknown IDs fail explicitly. URL fallback
must not redirect a request carrying an obsolete ID to another document. When
using volatile storage, create a fresh document after restart so Office cannot
reuse a cached identity from a deleted instance.

GET and HEAD acquire metadata and content from one revision. Catalog listing
reads metadata only. File saves retain the graph used by later delta requests;
partial, multi-request and non-file partition uploads remain unsupported.

## Test the demo

Build while Aspire is stopped. Run the demo suite with:

```sh
dotnet test demo/CellBridge.Demo.slnx
```

For the catalog and session HTTP checks, use the
[isolated test runner](automated-testing.md). These checks create fresh documents
and use the sample API. They do not launch Office or establish desktop
coauthoring support.
