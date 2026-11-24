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

**Copy link / access** opens a read-only page with the document URL. Copying it
does not grant access. The owner can also see the existing recipients and their
roles when the host supplies a trusted recipient resolver.

Owners can choose **Delete**, review the confirmation page, and confirm deletion.
The server checks current write permission and rejects deletion while an editor
or lock is active. Deletion removes the document from the library and retains its
stored history and tombstone. The library does not currently provide an undo or
restore-deleted-document control.

Office must be installed on the client computer and trust the server's HTTPS
endpoint. Word may require approval of its forms-sign-in host; see
[Office sign-in](authentication.md#desktop-validation).
[Client coverage](interoperability.md#client-coverage) lists tested scenarios.

## Edit an Excel workbook with the emulator

Copy the workbook URL from **Copy link / access**. Set `CELLBRIDGE_LOGIN` and
`CELLBRIDGE_PASSWORD` for a WebDemo account or set `CELLBRIDGE_BEARER_TOKEN`
for a host that accepts bearer authentication. Give the account write access
before attempting a save. Start the interactive client and use `sheets` to find
the sheet ID.

```sh
dotnet run --project tools/CellBridge.Emulator -- interactive "$WORKBOOK_URL"
```

For a desktop Excel capture, use a disposable workbook and distinct accounts
for desktop Excel and the emulator. Start the capture, open the workbook in
desktop Excel, then save one cell with the emulator. Enable server capture
with `Protocol:CaptureDirectory` to record the emulator request alongside
Excel's traffic when using Windows process-local capture. In this example the
sheet ID is `1` and the new value in `B2` is the number `42`. Start with a
different value in `B2` so the emulator sends a `PutChanges` request.

```sh
dotnet run --project tools/CellBridge.Emulator -- save "$WORKBOOK_URL" 1 B2 42
```

Before reopening, record whether Excel shows the new value, prompts to reload,
or reports repair. Save a disjoint edit from desktop Excel and reopen the
workbook to check that both values survived. Close Excel and let the RTC
connection close before stopping and validating the capture.
Confirm the emulator sent `PutChanges` and record any channel 3 notifications
and their `FullSaveId` or `SavRev` fields. Record if no matching notification
appeared. [Capture workflow](capture-kit.md)

Emulator and MCP saves publish a replacement workbook and send an RTC
persisted-content hint when connected. They omit Excel's prior runtime revision
pointer because neither writer produces Excel's separate revision stream.
Open desktop Excel can show **Refresh Recommended** after a peer save. Reopen
to load the committed workbook before making more edits. If Excel already has
unsaved edits, keep its unmerged copy before closing the merge conflict pane.

Two captured native Excel `PutChanges` requests had two revision roots in
the file partition. Root `2:84defab9-aaa3-4a0d-a3a8-520c77ac7073` carried the
saved workbook. Root `1:6385c870-7f8d-428c-9445-de467fcdaebf` carried a
separate ZIP with an empty `xl/workbook.xml`. Its workbook relationship
pointed to `xl/revisionEx/revIndex.xml`, which referenced `revStream1.xml`.
For each captured cell edit, the stream contained a forward record and an
undo record with the same revision number and worksheet UID. The saved
workbook's `revIDLastSave` and `uidLastSave` agreed with the index's saved
revision number and the stream's revision UID in the first capture.

The package writer emits only the saved workbook root. MS-FSSHTTPD defines
the primary content root and leaves other streams to the client.
[MS-FSSHTTPD Cell Properties](https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttpd/62ba389e-e8f4-4a03-b00b-42d75bec605c)

The captures lack the rules for generating and advancing the second root
across writers and saves. The channel 3 `FullSaveId` events did not establish
a mapping to the revision UID. MS-XLSX requires other writers to omit Excel's
runtime pointer. CellBridge keeps the refresh behavior above until it can
verify a native live merge across consecutive peer saves.
[MS-XLSX revision pointer definition](https://learn.microsoft.com/en-us/openspecs/office_standards/ms-xlsx/5082e3cf-b020-4599-b656-3b4207f0470d)

## Edit Word and PowerPoint files with the emulator

Copy the document URL from **Copy link / access** and use the same login or
bearer environment variables as for Excel. The account needs write access to
save. Each command opens a session that joins Coauth on its first write by
default. Pass `--editor` to join when the session opens.

```sh
dotnet run --project tools/CellBridge.Emulator -- word "$DOCUMENT_URL"
dotnet run --project tools/CellBridge.Emulator -- powerpoint "$PRESENTATION_URL"
```

In Word, use `summary`, `headings` and `read [start-index [max-paragraphs]]`
to inspect direct main-body paragraphs. For example, `append "Review draft"`
adds a paragraph. `replace`, `insert-before`, `insert-after`, `delete` and
`comment` take a zero-based paragraph index or `index:paragraph-id` from the
read results. Text arguments are JSON strings. For example,
`comment 0 ["Check this claim","Reviewer"]` anchors a comment to the first
paragraph. When the main body changes, edit targets and paragraphs bordering
an insertion point need matching IDs that are unique in both revisions.
Appending needs no paragraph ID.

In PowerPoint, use `slides` and `read <slide-id>` to find slide and shape IDs.
Use `set-text <slide-id> <shape-id> <json-text>` to replace text in an editable
shape and `set-notes <slide-id> <json-text>` to replace speaker notes.
If the target contains a soft line break, these tools reject changes to
its text.
`add-slide [layout-source-slide-id]` appends a blank slide using an existing
layout. `delete-slide <slide-id>` removes a slide.

Both modes have `help`, `editors`, `revision`, `sync` and
`diff <before> <after> [max-changes]`. Edits use the current local revision by
default. Use `mark <name>` to save a revision and `base @name` to keep using
it as the base across edits and syncs. Use `base current` to restore the
default. Changed targets and ambiguous matches can return `Conflict`.
Use `--script <file>` for a command file or `--script -` for standard input.
Scripts expect `Success` for each edit unless `expect <status> <edit-or-retry>`
specifies another status.

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

For a reverse proxy that needs a stable local upstream, set
`Parameters:WebDemoHttpPort` in the AppHost's ignored `apphost.settings.json`.
This fixes Aspire's HTTP proxy port while the application's target port remains
managed by Aspire. Keep the relay or reverse proxy pointed at this proxy port,
not the application's target port. Start without `--isolated` for this setup,
because isolation randomizes ports. Set `Parameters:PublicOrigin` to the trusted
external HTTPS origin; the local HTTP hop does not change that public origin.

## API and document identity

The page uses the document service in process. HTTP consumers can call
`GET /api/documents` and create documents with
`POST /api/documents` using JSON such as
`{ "name": "My test", "type": "docx" }`. The other types are `xlsx` and
`pptx`. Creation returns 201, invalid input returns 400 and duplicate names
return 409. Requests use the signed-in caller's cookies and antiforgery token.

`MapCellBridgeDocumentDeletion()` maps authenticated
`DELETE /api/documents/{resourceId}` for reusable hosts. Its JSON body requires
`expectedLifecycleGeneration` and `expectedStateVersion` from the document's
current metadata. Cookie-authenticated callers must send the host's antiforgery
header and paired cookie. Only the current owner with write access can delete;
stale state, active editors, locks, or pending deliveries return 409. Deletion
returns 204, and unavailable or unauthorized documents return 404. This endpoint
does not implement a native Office Delete command.

A supplied protocol ResourceID takes precedence over the URL. Unknown IDs fail;
lookup never creates a document or redirects an obsolete ID to another file.
The in-memory provider loses documents when the process stops. After a restart,
create a new document; it receives a new resource ID.
GET and HEAD return content and headers from one saved revision.

## Test the demo

With Aspire stopped:

```sh
dotnet test tests/CellBridge.WebDemo.Tests
```

The [integration fixtures](automated-testing.md) also exercise catalog and
session requests against real PostgreSQL hosts.
