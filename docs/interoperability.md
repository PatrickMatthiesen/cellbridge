# Protocol support and compatibility

CellBridge implements the desktop Office editing protocols described by
[MS-OCPROTO, MS-FSSHTTP and MS-FSSHTTPB](protocol-version-decision.md#authorities).
This page lists the implemented operations, remaining gaps and tested clients.

## Protocol parity

### Implemented behavior

Office discovers and downloads documents over HTTP. Editing requests go to
`/_vti_bin/cellstorage.svc` as SOAP with inline binary data or MTOM attachments.
The server resolves the document, checks access and executes the requested
operations.

| Feature | Implementation |
| --- | --- |
| Read/write permissions | Binary `QueryAccess` reports the caller's access. |
| Read file changes | `QueryChanges` supports repeated file queries with independent knowledge/errors and one shared data package. Mapped cell scopes follow current dependencies; manifest/cell-change inclusion, filtered knowledge and whole-cell rounding are supported. |
| Save changes | `PutChanges` combines changed and retained file parts, reconstructs the document and checks for stale/conflicting updates. Save receipts identify retries so an accepted save is not published twice. |
| Editing presence and locks | `Coauth`, `EditorsTable`, `SchemaLock`, `ExclusiveLock`, `LockStatus` and `AmIAlone` manage sessions and locks. |
| Document information | `WhoAmI`, `ServerTime`, `GetDocMetaInfo` and `GetFileProps` return identity and current file information. `GetVersions` reports the current version. |

File content, application metadata and editor presence have separate synchronization
partitions. File saves update the content partition. Editor queries return current
participants; binary application-metadata queries return storage-index information.
SOAP dependencies determine which subsequent operations execute.

### Known limitations

- Uploads marked partial, uploads spanning multiple requests, alternate coherency
  modes and writes to non-file partitions are unsupported. Ordinary saves can
  still reuse unchanged file parts.
- Historical-version queries, unmapped cell scopes, waterline-only query controls
  and some filters are unsupported. Optional unsupported filters fall back to
  more data. CellBridge returns an error when `FailIfUnsupported` permits failure.
- Repeated metadata/editor queries and independent binary routing across SOAP
  partitions remain unsupported. Repeated file queries deduplicate immutable
  elements; a later failure preserves earlier payloads and save acknowledgements.
- `QueryKnowledge`, `QueryRawStorage`, `PutRawStorage`,
  and `QueryDiagnosticStoreInfo` are unsupported. These legacy enum values are
  outside the current normative four-operation inventory.
- SOAP `FileOperation`, `Properties` and `Versioning`, historical downloads
  and version restoration are unimplemented. Internal storage snapshots are
  separate from Office version history.
- Lock conversions and transition acknowledgement are implemented with persisted
  coauthor membership. Native desktop transition behavior still needs qualification.
- The binary application-metadata stream is incomplete.
- The generic graph resolver has durable capture/restore codecs. File saves can
  resolve inherited objects and declared BLOBs by identity, keeping object
  partitions and cells separate. General non-file publication, OneNote adapters
  and notebook/page synchronization remain unimplemented. Reference captures for
  the new graph shapes and scoped queries remain required.
- Automatic graph pruning is disabled. The host retains graph and save identities
  and rejects growth at its [storage limits](storage-providers.md#limits-and-qualification).

`AllocateExtendedGuidRange` is implemented for counts 1 through 100000 with a fresh
UUID namespace per allocation. Concurrent hosts need no shared integer counter.
Zero/oversized requests return an explicit unsupported error. Allocation requires
write access and does not change content or retained graph state.

Binary ClientAndPlatform identity is supported alongside GUID identity. Optional
binary target selectors must match the selected SOAP partition; independent
subrequest routing across partitions remains part of scoped-query work. See the
[requirements ledger](protocol-requirements.md) for normative applicability,
implementation evidence and remaining audit gates.

SharePoint lists and search are outside the project's scope.

Scoped multi-cell queries include the selected index/manifest, required ancestor
records and current referenced cells, with bounded traversal that permits cell
cycles. They exclude unrelated cells and detached history. Scoped knowledge
uses waterline zero because it does not establish complete partition possession.
The cell-dependency interpretation follows the [Cell ID filter model](https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttpb/65b2e89a-9c3f-4263-b8d2-99e59691b11e)
and has synthetic coverage; specialization of the separate filter wire objects
still uses the documented fallback. The obsolete in-memory root control is not
a new serialized query argument.

Self-contained legacy file graphs retain their existing reconstruction path.
Strict scoped resolution can reject incomplete unused ancestry in those graphs;
only that query receives an unsupported error. Storage corruption and I/O
failures remain distinct. Each query's byte constraint applies before package
union; the union also has a server byte budget. Unscoped/single-cell durable
queries verify the selected index and manifest before reading unknown payloads,
so fully known queries read these two scope records but no file/object/BLOB data.
Explicit multi-cell scopes currently load the retained graph for validation even
when client knowledge is complete. Reducing those reads is performance work #33.

## Client coverage

| Client or scenario | Coverage |
| --- | --- |
| Word open, save and reopen | Remote saves and reopen tested, including repeated manual tests while signed in. |
| Word permissions | Manual sign-in, writer saves, reader read-only open and Write revocation while editing tested. |
| Excel save and reopen | Authenticated desktop saves and reopen tested on the current PostgreSQL host. Captured saves also run in automated replay. |
| PowerPoint save and reopen | Manually tested, including complex Copilot-generated slides. |
| Blank Office file creation | Automated package checks. |
| Two service instances | Automated identity, shared lease and protocol routing checks against one database. |
| Two desktop clients coauthoring | Unverified, including simultaneous changes and reconnect/recovery. |
| Large files and production concurrency | Not qualified. Synthetic benchmarks measure provider behavior. |

The recorded Word trial on 2026-10-03 used Word `16.0.20430`, Tailscale HTTPS
and PostgreSQL. Ten saves matched client ETags, all 17 binary responses passed
the independent Office Inspectors parser, and the final download matched the
client's SHA-256. Reopen used a new Word process. That trial used the earlier
anonymous host.

A separate authenticated trial on the same date verified writer/reader access
and revocation. Word required explicit forms-sign-in host approval. Repeated
authenticated save/reopen and the complex PowerPoint test were confirmed by the
project maintainer.

On 2026-10-05, the maintainer created and edited `excel-test.xlsx` and
`word-test.docx` through the authenticated Tailscale HTTPS host backed by
PostgreSQL. Server captures recorded three successful Excel `PutChanges`
operations and two successful Word `PutChanges` operations. The downloaded
files had content versions 4 and 3 respectively; both passed ZIP integrity
and XML parsing checks. The maintainer confirmed that edits remained in both
documents after fully closing and reopening them from the library. This was
a single-desktop test, with no coauthoring transition qualification.

## Automated checks

The [test runner](../tools/testing/run.py) starts disposable PostgreSQL and two
hosts through Aspire. It covers protocol replay, provider consistency, quota
races, retry/failure behavior, metadata retention and quiescent cleanup.
Query tests reconstruct saved files from prior client knowledge and returned
parts. CI also uses the independent [Office Inspectors parser](office-inspectors.md).

Buffered and streaming reconstruction use the same reviewed save captures.
HTTP tests check multipart preambles/headers, content length and capture/output
byte equality. Durable-save tests compare streamed graph, response, versions and
metadata with the buffered path, including ingestion failure, cancellation and
expanded ZIP limits before publication.

See [automated testing](automated-testing.md) for commands and desktop test
procedures. Hardware power loss, asynchronous database failover and Office
recovery after graph eviction are outside the tested scenarios.

## Fixtures and generated evidence

Reviewed regression fixtures live in `testdata/`; their READMEs describe
provenance and sanitization. Raw captures, saved Office files and run reports
belong in ignored local output or CI artifacts. Sanitize document content and
embedded metadata as well as URLs and credentials before publishing a fixture.
See the [capture guide](capture-kit.md).
