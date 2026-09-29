# Demo document library

The standalone solution is `demo/OfficeCollabServer.Demo.slnx`. It contains an
ASP.NET Core Razor Pages site and its tests. It reads the collaboration server's
`GET /api/documents` endpoint over HTTP and has no reference to the protocol or
storage projects. The catalog is a demo API, not an implementation of a SharePoint
list API.

## Run

From the repository root:

```powershell
aspire start --apphost aspire/apphost.cs --non-interactive
aspire wait web --apphost aspire/apphost.cs --non-interactive
aspire wait demo --apphost aspire/apphost.cs --non-interactive
aspire describe --apphost aspire/apphost.cs --non-interactive
```

Open the `demo` HTTPS endpoint. Aspire supplies the collaboration server's HTTPS
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

Aspire's `collab-public-url` parameter supplies the browser/Office origin,
defaulting to `https://localhost:7292`. Windows DNS on this machine cannot resolve
`web-aspire.dev.localhost`, even though browsers and curl can reach it.
Override that parameter when testing from other computers.

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

The server was already keyed by document URL and seeded three documents:

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
name as an example replace the example. Restart after adding or changing an
input file. There is no upload form or automatic folder watcher.

The store remains in memory. Office saves update the served copy, not the input
file on disk. Restarting loses those edits and reloads the input files. Download
any edits you want to keep before restarting.

## Concurrent access and current limits

Each document has independent content, versions, binary graph identities, locks
and editing sessions. Clients joining the same schema lock can share a document.
The catalog counts client sessions, which are not necessarily different people.
The demo server has no authentication and its `WhoAmI` response still uses the
shared `officelab` identity.

Storage reads and session snapshots are synchronized with updates. HTTP GET and
HEAD capture content and headers from one revision, so a simultaneous save cannot
produce a content-length header from another revision. Binary saves retain the
existing graph-based conflict checks.

Desktop Word open and save were confirmed before this change. Two real Word
clients exchanging edits without closing remains unverified. The current save
handler accepts Word, Excel and PowerPoint package main parts. Excel's recorded
complete save now passes, but launch links do not establish full desktop editing
or coauthoring support. Partial/multi-request saves
and non-file partition uploads remain unsupported.

## Validation

Stop Aspire before building the main solution. Stop and validate any active
capture first, following `capture-kit.md`.

```powershell
dotnet test tests/OfficeCollabServer.FssHttp.Tests/OfficeCollabServer.FssHttp.Tests.csproj
dotnet test tests/OfficeCollabServer.FssHttpB.Tests/OfficeCollabServer.FssHttpB.Tests.csproj
dotnet test demo/OfficeCollabServer.Demo.slnx
```

The live catalog test downloads all listed files, checks GET/HEAD headers and
attachment responses, joins two clients concurrently on `test.docx`, verifies
that `save-test.docx` has unchanged session counts, then removes both sessions.
Run against a fresh server with the examples enabled:

```powershell
$env:OFFICECOLLABSERVER_INTEROP_ENDPOINT = 'https://localhost:7292/_vti_bin/cellstorage.svc'
dotnet test tests/OfficeCollabServer.Interop.Tests/OfficeCollabServer.Interop.Tests.csproj --no-build --filter FullyQualifiedName~DocumentLibraryTests
```

This is a server protocol test, not a substitute for a two-desktop Word test.

Validation on 2026-09-28 passed 191 offline tests in the main solution and 11
tests in the demo solution. The live catalog/concurrent-session test and the
existing two-save/download/reopen replay both passed. Browser checks covered
search filtering, the original Office URLs and layouts at 1440px and 390px.

The subsequent New document change passes 91 SOAP/storage tests and 15 demo
tests, including Open XML package validation, filename handling and concurrent
duplicate-name rejection. Browser creation of all three types and duplicate
error handling were checked on the running services.

## Excel URL and lock correction, 2026-09-28

Desktop Excel exposed a linked encoding and identity bug. Responses returned
`Excel%20test.xlsx` with `UrlIsEncoded="false"`. Excel escaped that URL again.
Its close request did send `ReleaseLock`, with the original `ResourceID`, but
the server ignored that identity and created a zero-byte document for the
changed URL. The release consequently missed the original lock. The new
document identities also explain the observed refresh prompts; the original
workbook had not advanced beyond version 1.

Responses now mark escaped URLs correctly. Requests with `UseResourceID=true`
and a supplied ID resolve the existing document by ID, rejecting invalid or
unknown IDs without URL fallback. Initial requests without an ID resolve by
URL. SOAP misses no longer create documents. Canonical response URLs escape
stored path segments once. HTTP downloads also escape the already-decoded
ASP.NET request path before handing it to the URL-normalizing store, keeping
spaces distinct from literal percent sequences.

These rules follow the [MS-FSSHTTP Request definition](https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttp/91fb9fd5-ef2d-4484-b57c-f0aedf7d0f00)
and its [encoded response URL example](https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttp/0019690e-418d-4773-aab2-31d7392c636d).
Lock and client GUID comparisons now accept equivalent textual representations.
Locks still require ownership and expire normally; an empty coauthor session
list is not grounds for clearing an Excel exclusive lock.

Validation: 103 SOAP/storage tests, 9 offline interop tests, and the live
`IdentityRoutingTests` regression pass. The live test acquires a lock, releases
it using a double-encoded URL plus the original ID, acquires/releases another
lock, and checks unchanged bytes/ETag and rejection of unknown URLs/IDs.
All six existing nonempty files were backed up and their SHA-256 hashes checked
after rebuilding. Three empty aliases created by the bug were backed up and
excluded from the startup import. Storage still resets identities and version
history on restart; the byte backup is not persistent metadata storage.
Close cached Office windows and reopen from the library after this restart.
Desktop Excel editing/saving and two-desktop coauthoring remain unverified.

## Recorded Excel save fix, 2026-09-28

The subsequent Excel upload was a complete 13,389-byte PutChanges request with
Flags `0x09`, AdditionalFlags `0x8004`, and empty ContentVersionCoherencyCheck.
The broad unsupported-flags guard rejected reserved bit 15. MS-FSSHTTPB
[Additional Flags](https://learn.microsoft.com/el-gr/openspecs/sharepoint_protocols/ms-fsshttpb/57ddba13-18da-452d-8cca-c43ddce28267)
requires ignoring reserved bits. The guard now rejects unsupported defined modes
only. It also ignores the abort-on-failure flag and legacy content-version item
as required by [Put Changes](https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttpb/e4c224ca-0717-4b02-b713-e75d353904bb).
Package validation now checks the main part for the target extension, allowing
Excel and PowerPoint packages while rejecting a Word package uploaded to XLSX.
The retained graph, expected-index and lock checks remain in place.

`testdata/api/excel-save-20260928.bin` reconstructs the five logged base64 chunks
from evidence ID `27a18f5a31f5470289af88d0e6c9df37`. The regression rebases only
the expected storage index to its fresh test document. It checks the recorded
flags, successful save, Open XML workbook validation, saved cell text, retained
graph materialization, and idempotent retry. 106 SOAP/storage tests and 9 offline
interop tests pass. Two live tests pass: the Excel upload/download/graph-reopen
replay and the two successive Word saves/download/reopen replay. Both create
isolated documents instead of overwriting the user's `test.docx`.

The concurrent Word failure on save-check/save-test occurred before PutChanges:
Word supplied cached ResourceIDs from before the 09:01:41 server restart and
received FileNotExistsOrCannotBeCreated. Persistence of content, resource IDs,
versions and retained graph state across restarts remains needed. Do not add
an unrestricted URL fallback for unknown IDs. Fresh `Word save retry.docx` and
`Excel save retry.xlsx` were created after deploying this fix for desktop testing.

## PowerPoint package repair fix, 2026-09-28

The generated slide master related to a slide layout but omitted the
`p:sldLayoutIdLst` entry registering that layout. Open XML schema validation
accepted the package, but installed desktop PowerPoint rejected a downloaded
copy with HRESULT 0x80070570 when opened through COM with alerts disabled.
Adding only the missing layout registration made that same copy open with one
slide and one layout. The generated master now includes layout ID 2147483649
and its existing rId1 relationship, matching Microsoft's
[minimal presentation example](https://learn.microsoft.com/en-us/office/open-xml/presentation/how-to-create-a-presentation-document-by-providing-a-file-name).

A regression checks the slide/layout/master relationship and registration;
107 SOAP/storage tests pass. After rebuilding, `PowerPoint open retry.pptx`
was created through the API, downloaded, opened in desktop PowerPoint, saved
to a separate local copy and reopened successfully. This validates the file
package, not remote PowerPoint coauthoring or saving. Existing presentations
retain their original bytes; create a new one to use the corrected template.
