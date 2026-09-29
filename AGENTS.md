# AGENTS.md — Orchestration Notes

This file is the coordination hub for AI agents working on CellBridge.
Update it as the project evolves so context survives compactions.

## Project Goal

Build an experimental ASP.NET Core server implementing **MS-FSSHTTP** and
**MS-FSSHTTPB** so that **Microsoft Word desktop** can open, edit, save, and
coauthor a `test.docx` against our server instead of SharePoint.

The existing SharePoint farm is the **known-good reference** for wire traffic.

## Protocol Authority (use ONLY these)

- [MS-OCPROTO] — Office Collaboration Protocols Overview
- [MS-FSSHTTP] — File Synchronization via SOAP over HTTP Protocol
- [MS-FSSHTTPB] — File Synchronization via SOAP over HTTP Protocol: Binary
  Structures

Do **not** implement unrelated SharePoint APIs (lists, search, etc.).

## ⚠️ CRITICAL PROTOCOL VERSION FINDING (verified 2026-08-28)

The two protocols **work together** — they are NOT competing alternatives.

1. **MS-FSSHTTP (SOAP)** — the OUTER protocol Word desktop uses against
   SharePoint `/_vti_bin/cellstorage.svc`. SOAP envelope with
   `CellStorageRequest`/`CellStorageResponse`, `SubRequestType` (Cell, Coauth,
   ExclusiveLock, SharedLock, SchemaLock, WhoAmI, ServerTime, EditorsTable,
   GetDocMetaInfo, GetVersions, FileOperation, Versioning, AmIAlone,
   LockStatus, Properties), and `SubRequestData`/`SubResponseData` elements.
   SOAP action:
   `http://schemas.microsoft.com/sharepoint/soap/ICellStorages/ExecuteCellStorageRequest`

2. **MS-FSSHTTPB (2024 rev, v20240820)** — the BINARY payload INSIDE the SOAP
   `Cell` subrequest's `SubRequestData`/`SubResponseData` (base64-encoded).
   Uses `Query Access` / `Query Changes` / `Put Changes` with `Stream Object
   Header` framing. This IS the correct binary protocol for the classic SOAP
   Cell subrequest.

**IMPORTANT: A subagent's claim that the 2024 FSSHTTPB spec is a "different
protocol not used by Word" was WRONG.** The confusion: it expected the binary
spec to contain the SOAP-level GetChanges/PutChanges/CoauthSession structures,
which actually live in MS-FSSHTTP. The binary spec correctly contains
QueryAccess/QueryChanges/PutChanges which are the binary payloads of the SOAP
Cell subrequest. Confirmed by the Interop-TestSuites test suite which has both
S01-S10 (SOAP subrequests) and S11-S13 (binary QueryAccess/QueryChanges/
PutChanges).

**DECISION: Build BOTH layers.** `OfficeCollabServer.FssHttp` = SOAP layer
(CellStorageRequest, cellstorage.svc, OPTIONS). `OfficeCollabServer.FssHttpB`
= binary serialization (QueryAccess/QueryChanges/PutChanges with Stream Object
Headers).

Spec text (extracted from downloaded PDFs):
- MS-FSSHTTP (SOAP): `%TEMP%\fsshttp-official-research\MS-FSSHTTP.txt`
- MS-FSSHTTPB (2024): `%TEMP%\fsshttp-official-research\MS-FSSHTTPB.txt`

Reference impl: Interop-TestSuites cloned at
`%TEMP%\ITS\FileSyncandWOPI\Source\`:
- `Common\Proxy\MS-FSSHTTP\MS-FSSHTTP.cs` — WSDL-generated SOAP proxy.
- `SharedTestSuite\SharedAdapter\Stack\FSSHTTPB\` — binary serialization stack.
- `MS-FSSHTTP-FSSHTTPB\TestSuite\` — test suite (S01-S20).

## Success Criteria (numbered)

1. Serve `test.docx` at a stable HTTPS URL.
2. Respond to Office OPTIONS capability discovery.
3. Cause Word desktop to select MS-FSSHTTP.
4. Implement `/_vti_bin/cellstorage.svc`.
5. Parse and log every FSSHTTP request Word sends.
6. Return valid protocol responses.
7. Allow Word to open `test.docx`.
8. Allow Word to save a modification.
9. Two Word instances join a coauthoring session.
10. A change by instance A becomes visible to instance B without closing.

## Repository Layout

```
OfficeCollabServer/
├── src/
│   ├── OfficeCollabServer.Web/        # ASP.NET Core host, cellstorage.svc, OPTIONS
│   ├── OfficeCollabServer.FssHttp/    # MS-FSSHTTP SOAP layer (CellStorageRequest/Response)
│   ├── OfficeCollabServer.FssHttpB/   # MS-FSSHTTPB binary serialization (standalone lib)
│   └── OfficeCollabServer.Storage/    # Document/subdocument/cell storage + coauthoring
├── tests/
│   └── OfficeCollabServer.FssHttpB.Tests/  # xUnit tests for binary lib
├── docs/                              # Protocol notes, architecture, traffic logs
└── aspire/                            # Aspire AppHost
```

## Reference Repositories

- **Interop-TestSuites** (OfficeDev): https://github.com/OfficeDev/Interop-TestSuites
  — official protocol test suites; useful for validating our wire behavior.
- **WopiHost** (petrsvihlik): https://github.com/petrsvihlik/WopiHost
  — open-source WOPI host; WOPI is a *different* protocol but shares concepts
    (coauthoring, file storage, discovery). Candidate to borrow patterns from.

## Running & Observing the Server (Aspire)

The app runs under Aspire. Use the CLI, not raw `dotnet run`:

- **Start**: `aspire start` (from repo root or `aspire/`)
- **Status**: `aspire ps` — prints the dashboard URL with login token
- **Resource health**: `aspire wait web`
- **Resource graph**: `aspire describe`
- **Logs/traces/metrics**: open the dashboard URL from `aspire ps`; the `web`
  resource exports OTel (traces, metrics, structured logs) via ServiceDefaults.
- **Stop**: `aspire stop`

Gotchas agents must know:
- `dotnet build` FAILS while the server is running (file lock on
  `OfficeCollabServer.Web.exe`). Run `aspire stop` (or kill the
  `OfficeCollabServer.Web` process) before building.
- The file-based AppHost (`aspire/apphost.cs`) requires the
  `#:project ../src/.../X.csproj` directive for `AddProject<Projects.X>` to
  compile; paths are relative to the `aspire/` folder.
- Word interop testing: `ms-word:ofe|u|https://web-aspire.dev.localhost:7292/shared/test.docx`
  (Aspire renames the host to `web-aspire.dev.localhost`). Word's probe
  sequence is logged per-request by the middleware in `Program.cs` — check
  those logs first when Word misbehaves.
- `test.docx` is seeded at startup by `MinimalDocx.Create()`; the store is
  in-memory, so content resets on restart.

Latest Word-open evidence, captured 2026-09-01:
- Word sends a 10-subrequest MTOM `ExecuteCellStorageRequest`, including the
  three expected Cell partitions. The server routes token 8 to EditorsTable,
  token 7 to Metadata, and token 6 to FileContents.
- The server returns HTTP 200 without an exception, with numeric UTC-tick
  `ServerTime` and conforming `WhoAmI` attributes. Word immediately follows it
  with `GET /shared/test.docx`, then `HEAD /shared/test.docx`, and opens
  read-only without a `PutChanges` request.
- The metadata response still contains placeholder XML and the editors stream
  has not passed through MS-FSSHTTPD chunking. Treat both as unvalidated
  protocol data until compared with a corresponding SharePoint response.

## Agent Orchestration

2026-09-28 PowerPoint template fix: MinimalPptx's slide master must register
its related layout in p:sldLayoutIdLst. Missing registration passed the SDK
validator but caused desktop PowerPoint repair dialogs. Adding the layout ID
2147483649/rId1 alone changed the original package from a COM open failure to
success. 107 SOAP/storage tests pass; a freshly created/downloaded presentation
opens, saves locally and reopens in installed PowerPoint. Remote PowerPoint
save/coauthoring is not established by this check.

2026-09-28 save follow-up: Excel's recorded complete upload uses Flags=0x09,
AdditionalFlags=0x8004. Reserved additional bits MUST be ignored. The save
handler now ignores those and the legacy content-version item/abort flag,
and validates DOCX/XLSX/PPTX main parts by file extension. The actual Excel
upload is `testdata/api/excel-save-20260928.bin`; 106 SOAP/storage tests plus
live Excel save/download/reopen and two Word saves/download/reopen pass.
Word's later failure was cached ResourceIDs from a previous server process,
before any PutChanges. Use fresh demo documents for desktop retries. Durable
resource/content/graph persistence is still absent; do not mask this by URL
fallback for unknown resource IDs. See docs/demo-library.md.

2026-09-28 Excel identity fix: encoded SOAP response URLs must carry
`UrlIsEncoded=true`. Honor `UseResourceID` when `ResourceID` is supplied;
initial requests without an ID use the URL. Never create documents for SOAP
lookup misses. Excel sent ReleaseLock to a double-encoded URL with the correct
resource ID, and ignoring the ID had created empty aliases and left the real
lock intact. `DocumentRequestResolver` now returns canonical URLs and resolves
the same stored instance. HTTP request paths are already decoded and must be
escaped before the store normalizes them. See `docs/demo-library.md` for the
evidence and live regression. 103 SOAP/storage tests and the live identity/lock
test pass. Files remain in memory; restarts lose IDs/history even if bytes are
backed up and reimported.

Use the available agent tools to delegate research, implementation, and review.

Division of labor:
- **Research** (read-only, no code): protocol details, WopiHost, test suites.
- **Implementation**: build libraries/tests in isolation.
- **Review**: check wire correctness against spec.

## Working Rules

- Demo library: `demo/OfficeCollabServer.Demo.slnx` is a separate Razor Pages
  solution, run as Aspire resource `demo`. It reads `/api/documents` over HTTP
  and links to the original files using Office URI schemes. `Documents:Directory`
  loads top-level files at startup; `Documents:SeedExamples` defaults to true.
  Files, versions and sessions are per document, not hardcoded to one file.
  Storage stays in memory and saves do not write back to the import directory.
  See `docs/demo-library.md` for configuration and validation. Excel/PowerPoint
  links do not imply tested editing support. Two-desktop live coauthoring and
  distinct authenticated user identities remain unverified/unimplemented.
  Validation on 2026-09-28: 191 main offline tests, 11 demo tests, live catalog
  GET/HEAD/download and two-client join/exit isolation, plus the existing
  two-save/download/reopen replay pass. Desktop and mobile browser layouts and
  filename search were checked. `collab-public-url` controls the browser/Office
  origin separately from the demo's internal HTTP connection.
  Desktop reachability correction on 2026-09-28: Windows DNS cannot resolve
  `web-aspire.dev.localhost` on this machine although browser/curl probes work.
  The demo's default `collab-public-url` is now `https://localhost:7292`;
  Windows HTTP GET/HEAD, OPTIONS discovery and the live two-client test pass.
  Remote clients require a real resolvable server hostname, not localhost.
  The demo now has New document for DOCX/XLSX/PPTX via `POST /api/documents`.
  `DocumentCreation` generates blank packages; test-only Open XML SDK validation
  covers all three. Atomic `DocumentStore.TryAdd` rejects duplicate names without
  overwriting existing documents. Creation validation: 91 SOAP/storage tests and
  15 demo tests pass; browser creation of all three types and duplicate rejection
  were verified. Creation does not extend Excel/PowerPoint save support.
- Capture kit: `docs/capture-kit.md` documents `tools/capture`. Normal Aspire startup
  includes a `sharepoint` reverse HTTPS proxy on port 8443 using the existing dev
  certificate, with `capture-upstream`, `capture-port`, `capture-upstream-ca`, and
  `capture-output` parameter resources. The AppHost uses only `Parameters`
  configuration and always creates the reverse proxy. Standalone forward mode remains available. No Windows proxy/trust changes are needed for
  reverse mode. Raw captures and certificate keys stay local. Stop a
  capture with `tools/capture/stop.py` before stopping Aspire, then validate it.
- Working SharePoint reverse-capture mapping, user-confirmed 2026-09-25:
  client `https://sharepoint.dev.localhost:8443` → upstream `http://192.0.2.24:42292`.
  Example portal / Extranet needs internal AAM `http://sharepoint.dev.localhost:42292`
  mapped to public `https://sharepoint.dev.localhost:8443`, in addition to the HTTPS
  internal row. Without the HTTP row, SharePoint redirected to HTTP port 42292.
  Word open was user-confirmed after correcting the actual HTTP AAM port typo
  from 4229 to 42292. The typo caused FSSHTTP `URL is not for this web` errors
  despite working browser navigation. The user then confirmed save, close, reopen,
  and further editing. Capture flows 46 and 59 contain successful SOAP save responses;
  flow 71 contains a successful reopen response. Coauthoring remains unverified.
  The initial Word run reports a recorder PermissionError and must not be treated
  as a complete reference fixture. The recorder now retries brief Windows file
  replacement locks and records detailed failures; 47 tests and a clean live
  smoke capture passed. A full Word session on the patched recorder remains to test.
  See `docs/capture-kit.md` for the full mapping. Do not change ports to fix this.
- File-partition binary saves now apply retained object graphs through
  `FilePartitionSaveHandler` and `PartitionGraphSnapshot`. Keep the graph-aware
  path; never extract the largest BLOB. It checks expected indices and locks,
  validates DOCX content, assigns/acknowledges serial numbers, commits bytes,
  and serves the retained graph on reopen. Partial/multi-request saves and
  non-file partition uploads still return unsupported. Storage is in-memory.
- Paired open/save/reopen fixtures now live in `testdata/sharepoint/fixtures/save_reopen`.
  `SharePointSaveReopenTests` exercises exact captured requests through our MTOM,
  SOAP and binary decoders. They exposed and cover DataElement framing and
  omitted-UserAgent compatibility fixes. The source run is incomplete as a whole;
  the four selected exchanges have individually verified body integrity.
- API comparison: `docs/api-comparison-2026-09-25.md` records replay results,
  the default Cell partition fix, and the next implementation gaps. The API
  rejected Word's selector-free QueryAccess despite accepting other Cell requests.
  Selector tests and live replay now pass. The actual Word attempt reached
  JoinCoauthoring but reverted to read-only after incomplete file/metadata
  QueryChanges responses. Its SOAP request is `testdata/api/word-enable-editing.xml`.
  File graph mappings, metadata/version SOAP responses, and shared lock ownership
  were corrected. Word diagnostics in `%LOCALAPPDATA%/Temp/Diagnostics/WINWORD`
  exposed an MTOM root-part framing failure missed by plain SOAP replay. Matching
  SharePoint framing fixed it: the next desktop attempt found XML, recognized
  JoinCoauthoring Alone, then failed with an empty default revision and read-only.
  Fixed manifest schema/root/cell identities and a node-based file graph are now
  deployed; 158 offline tests and two updated multipart live replays pass.
  User confirmed this graph build stays editable in desktop Word on 2026-09-25.
  The initial attempted save showed Upload Failed because PutChanges was absent.
  Save implementation now passes two sequential captured uploads plus HTTP GET
  and graph reopen comparison. 175 offline tests and three relevant live replays
  pass. Desktop saving was confirmed on 2026-09-25 after commit `f08a0bc`
  corrected expected-index comparisons to use mapping IDs instead of sender
  serial numbers. `save-check.docx` committed version 2, 12,214 bytes, zero
  errors; an independent HTTP download contained the user's added `asd` text.
  Word close/reopen and two-client coauthoring remain to be confirmed. Storage
  still resets on restart. Current validation is 180 offline tests plus the live
  two-save replay. Raw save attachment evidence is logged in numbered base64
  chunks for desktop attempts.

- Write docs as you go (see `docs/`) so compactions don't lose context and later agents has the information avliable.
- Keep MS-FSSHTTPB serialization in its own library with extensive unit tests.
- Log decoded requests/responses in human-readable form for comparison with
  SharePoint farm traffic.
- Don't reason about every type up front; implement incrementally.

## CellBridge publication snapshot, 2026-09-29

This repository starts with one clean root commit from sanitized source.
The original development history remains in a separate private repository.
Do not import or merge that history into CellBridge. C# project names and
namespaces retain the OfficeCollabServer prefix; public branding is CellBridge.
Use the personal GitHub author identity configured for this repository.
Keep lab identities, real upstream addresses, raw captures, and keys out of Git.
Public fixtures are sanitized regression inputs, not exact original wire bytes.
The capture upstream is an example domain and needs local configuration.
Read `docs/publication-audit.md` before publication.
