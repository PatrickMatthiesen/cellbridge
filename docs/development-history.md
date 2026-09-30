# Development history through 2026-09-28

These notes were moved from AGENTS.md on 2026-09-30. They record observations
at the time, including intermediate failures, test counts and superseded
implementation details. They are not current agent instructions.

Use the root README for current capabilities, docs/capture-kit.md for capture
setup, and aspire/apphost.cs for the current resource model. The AppHost now
uses AddCSharpApp and AddPythonApp. Historical references to Parameters-only
configuration and required #:project directives no longer describe it.
Commit IDs mentioned below belong to the private development history and may
not resolve in this repository. Public fixtures are sanitized regression inputs,
not exact original wire bytes.

On 2026-09-30 the projects, namespaces, solution files and project directories
were renamed from OfficeCollabServer to CellBridge. Paths in the historical
notes below retain their original spelling.

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
