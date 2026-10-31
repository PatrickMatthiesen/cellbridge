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
| Read changes | `QueryChanges` supports repeated and mixed file, metadata and editor queries with independent knowledge/errors and one shared data package. Explicit binary targets override the SOAP default. Mapped file-cell scopes follow current dependencies; manifest/cell-change inclusion, filtered knowledge and whole-cell rounding are supported. |
| Save changes | `PutChanges` combines changed and retained file parts, reconstructs the document and checks for stale/conflicting updates. Complete application metadata graphs publish atomically with key-level coherency. Requested applied-index entries are returned and preserved in stored receipts. |
| Editing presence and locks | `Coauth`, `EditorsTable`, `SchemaLock`, `ExclusiveLock`, `LockStatus` and `AmIAlone` manage sessions and locks. |
| Coauthor transition discovery | MS-SHDACCWS `IsOnlyClient` reports whether a transition is pending, using the persisted tracker and current read authorization. |
| Document information | `WhoAmI`, `ServerTime`, `GetDocMetaInfo` and `GetFileProps` return identity and current file information. `GetVersions` and `Versioning/GetVersionList` report servable history. |

File content, application metadata and editor presence have separate synchronization
partitions. File saves update the content partition. Editor queries return current
participants with immutable identities for each distinct editor stream;
binary application-metadata queries return persisted opaque graphs and scoped knowledge.
See [complete uploads and metadata](application-metadata.md) for publication and retry rules.
SOAP dependencies determine which subsequent operations execute.

### Known limitations

- Uploads marked partial, staged uploads spanning multiple requests, alternate coherency
  modes and buffered or coalescing metadata writes are unsupported. Ordinary saves can
  still reuse unchanged file parts. Complete saves accept `MultiRequestPutHint`
  as a coalescing hint. Unsupported partial operations do not cancel independent
  operations in the same binary request.
- Historical queries accept numeric versions and the compact version-token form
  observed from Microsoft 365 16.0.20527. Other token formats, unmapped cell scopes,
  waterline-only query controls and some filters are unsupported. Optional unsupported filters fall back to
  more data. CellBridge returns an error when `FailIfUnsupported` permits failure.
- Mixed and repeated queries deduplicate immutable elements. A later failure
  preserves earlier payloads and save acknowledgements. Metadata starts as a
  storage-index-only response until a complete graph is uploaded. The document
  service reads and publishes opaque graphs with both InMemory and PostgreSQL.
- `QueryKnowledge`, `QueryRawStorage`, `PutRawStorage`,
  and `QueryDiagnosticStoreInfo` are unsupported. These legacy enum values are
  outside the current normative four-operation inventory.
- SOAP Rename, Properties enumeration/retrieval, history listing/download and
  graph-aware restore are implemented. [Revision history](revision-history.md)
  documents retention, retry guarantees and remaining reference/desktop gates.
- Lock conversions and transition acknowledgement are implemented with persisted
  coauthor membership. Native desktop transition behavior still needs qualification.
- The generic graph resolver has capture/restore codecs. File saves can
  resolve inherited objects and declared BLOBs by identity, keeping object
  partitions and cells separate. Complete application metadata graphs are persisted
  atomically. OneNote adapters and notebook/page synchronization remain unimplemented. Reference captures for
  the new graph shapes and scoped queries remain required.
- Automatic graph pruning is disabled. The host retains graph and save identities
  and rejects growth at its [storage limits](storage-providers.md#limits-and-qualification).

`AllocateExtendedGuidRange` is implemented for counts 1 through 100000 with a fresh
UUID namespace per allocation. Concurrent hosts need no shared integer counter.
Zero/oversized requests return an explicit unsupported error. Allocation requires
write access and does not change content or retained graph state.

Binary ClientAndPlatform identity is supported alongside GUID identity. Optional
binary target selectors independently route each operation to a known partition,
overriding the SOAP default. Unknown selectors return explicit errors. See the
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
union; the union also has a server byte budget. Unscoped/single-cell
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
| Native Version History | Word, Excel and PowerPoint 16.0.20527 listed persisted revisions, opened older content and restored it as a new revision. Fresh processes repeated listing and historical open after server restart. |
| Blank Office file creation | Automated package checks. |
| Two service instances | Automated identity, shared lease and protocol routing checks against one database. |
| Two Word desktop clients | Distinct accounts exchanged paragraph changes in both directions, including receipt while idle. Concurrent offline paragraph additions survived reconnect; one client needed manual Save to recover. |
| Two PowerPoint desktop clients | Distinct accounts preserved slide 1 and added slide 2; the first client received the new slide while idle. |
| Two Excel desktop clients | With modern discovery enabled, distinct accounts edited concurrently with AutoSave and exchanged cell values and remote selections. Participant names remained unresolved. Earlier discovery profiles converted to exclusive access and blocked the second editor. |
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

On 2026-10-30, native Microsoft 365 Word, Excel and PowerPoint `16.0.20527`
were tested against an authenticated localhost HTTPS host backed by PostgreSQL.
Each document had an original revision and a saved revision with a distinct
marker. Each app listed both revisions, opened the original content and used its
native Restore command. Server captures recorded successful `RestoreVersion`
requests. Authenticated downloads confirmed that the new third revision matched
the original file's SHA-256 and that the second revision retained its marker.
After stopping and restarting the host, all nine revision downloads retained
their hashes. Fresh Office processes listed all three revisions and opened the
second revision with its saved marker. This was a single-user trial on one
Windows desktop. It does not qualify other Office builds or coauthoring.

A separate 2026-10-30 trial used two Windows machines, one x64 and one ARM64,
with native Microsoft 365 Office `16.0.20527`, distinct CellBridge accounts and
a shared PostgreSQL host reached through Tailscale HTTPS. Word initially missed
idle updates while its `IsOnlyClient` requests returned HTTP 404. After adding
the authenticated MS-SHDACCWS operation, both open Word clients received the
other client's saved paragraph without local typing, Save or reopen. Both
clients then appended paragraphs during a brief outage of the test endpoint.
One recovered automatically; the other showed Upload Failed and recovered with
Ctrl+S. Both native windows and an independent server download contained all
eight test paragraphs afterward.

PowerPoint permitted both editors. The second added a slide, and the first
received it while idle. The server download retained both slide markers.
Excel requested `ConvertToExclusive` on the first client and blocked the second
editor with File In Use. These checks do not qualify character-by-character
delivery, latency, same-text conflict resolution, participant avatars or other
Office builds. Rapid polling remains visible in the captures; this trial does
not establish its cause or a production concurrency limit.

Native Word against OneDrive uses a separate RTC service. A scoped reference
capture observed `FindSession`, SignalR negotiation, a successful WebSocket
upgrade and Office messages including `UpdatePresence`, `presenceUpdated` and
`SendUpdateNotification`. The open Word window received text and displayed a
remote cursor marker. This is reference-service evidence, not CellBridge support.
The [SignalR Hub Protocol](https://github.com/dotnet/aspnetcore/blob/main/src/SignalR/docs/specs/HubProtocol.md)
defines the outer JSON framing; it does not define Office's payloads or document
authorization. The reference also returned Office Collaboration Service discovery
properties and completed an OCS session join before the WebSocket connection.

The same reference test also used two native Excel and PowerPoint clients.
Each new file was saved to an explicit OneDrive HTTPS path, with a captured
successful upload before the second client opened it. Both Excel clients
received the other's committed cell value and displayed a remote selection.
PowerPoint received the second client's title edit and displayed a remote
selection with an author indicator. Excel requested RTC properties and opened
a WebSocket without an OCS join. Its coauthoring status changed from `Alone`
to `Coauthoring` when the second client joined. These clients used existing
Microsoft sign-in; this does not qualify distinct Microsoft identities.

A controlled Word insertion in one continuous reference capture appeared in an
outbound RTC typing message. A nearby OCS Execute response accepted a revision.
The temporary insertion was undone and remained absent after native reopen.
This correlates the two services with a known edit; it does not decode OCS's
document blobs or independently verify publication to the file host.

The relay implements the captured SignalR methods and document-scoped tickets.
`CellBridgeOptions.EnableOfficeRealTime` defaults to true; set it to false to
disable RTC discovery and endpoints. WebDemo accepts `Protocol:EnableOfficeRealTime=false`,
and AppHost accepts `Parameters:EnableOfficeRealTime=false`.
It requires a single host and rejects `multipleInstances: true`. It also requires
stored grants or an immutable versioned authorization policy so each delivery can
check all participants against one permission snapshot. Enabling RTC with an
ordinary asynchronous authorization provider or a legacy access evaluator fails
at startup. Initial native opens did not request RTC properties. Replaying the
captured OneDrive OPTIONS response caused Word to request Properties and Excel
to request RTC properties, negotiate SignalR and open a WebSocket to CellBridge.
The relay implements the observed storage and authoring discovery headers
and authenticated file metadata through `PROPFIND` with `Depth: 0`. Resource IDs,
replication tags, file sizes, ETags and timestamps come from persisted metadata.
OPTIONS responses use the same discovery profile on document and SOAP routes.
They advertise only the HTTP methods those routes implement. This does not
establish which individual discovery field controls native RTC startup.
Captured Excel presence contains application state without an `A` sender field;
the relay accepts that shape and uses the authenticated client's presence key.
The early probes did not establish delivery to a second native client or
visible remote cursors. Excel's diagnostics then showed that it classified the
server as lacking cloud collaboration and converted to an exclusive lock.
Subsequent tests with the complete reference discovery response enabled RTC
startup and simultaneous Excel editing. The host then replaced the borrowed
server identity with `CellBridge` and removed unsupported method advertisements.
On 2026-10-31, distinct accounts on both Windows machines exchanged Word
paragraphs, Excel cell values and a second PowerPoint slide using that host
profile. AutoSave completed without manual Save or reopen. Independent server
downloads retained both editors' markers in each file. The open Excel clients
displayed the other client's selected cell, including a later selection change.
Names remained `Loading name...` or `Getting name...`; named presence and typing
latency remain unqualified. Fresh Excel and PowerPoint opens had also produced
a client-side busy error before successful subsequent opens. The successful
retries alone do not establish reliable startup across builds. A later cold
Excel failure matched native PROPFIND and initial SOAP HTTP 429 responses to
the host's per-subject admission limit. With bounded admission queues, one
fresh Excel process opened the same workbook, remained editable and saved a
new cell automatically. The second machine received it without Save or reopen
and displayed the first machine's selection. Names still showed
`Loading name...`; cold Word and PowerPoint startup on that build remain
unqualified.
The relay does not implement OCS or WOPI.
`IsOnlyClient` does not provide the RTC service.

The shared Word test also included an iPhone on the same running service and
document as both Windows clients. A phone-entered phrase reached both open
desktop documents and a fresh authenticated server download. Desktop activation
preceded observation. This establishes saved-change propagation, not streaming
latency or an RTC connection.

## Automated checks

The [integration fixtures](automated-testing.md) reuse `CellBridge.AppHost` with
disposable PostgreSQL and two WebDemo instances. They cover protocol replay, provider consistency, quota
races, retry/failure behavior, metadata retention and quiescent cleanup.
Query tests reconstruct saved files from prior client knowledge and returned
parts. CI also uses the independent [Office Inspectors parser](office-inspectors.md).

Direct-service and HTTP replay tests use reviewed save captures with InMemory
and PostgreSQL.
HTTP tests check multipart preambles/headers, content length and capture/output
byte equality. Save tests compare decoded responses, reconstructed files, versions
and metadata.
They cover ingestion failure, cancellation and expanded ZIP limits before saving
changes.

See [automated testing](automated-testing.md) for commands and desktop test
procedures. Hardware power loss, asynchronous database failover and Office
recovery after graph eviction are outside the tested scenarios.

## Fixtures and generated evidence

Reviewed regression fixtures live in `testdata/`; their READMEs describe
provenance and sanitization. Raw captures, saved Office files and run reports
belong in ignored local output or CI artifacts. Sanitize document content and
embedded metadata as well as URLs and credentials before publishing a fixture.
See the [capture guide](capture-kit.md).
