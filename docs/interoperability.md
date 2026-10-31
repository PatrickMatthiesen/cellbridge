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

- Bounded QueryChanges responses use `MaxDataElements` as a byte threshold for
  starting partial delivery. A page includes the complete element that crosses
  the threshold, even when the first element is larger. The independent server
  response limit still applies. Partial responses return `PartialResult` with
  knowledge for delivered or already known elements, plus filtered-out knowledge
  only when requested. Clients continue by sending that knowledge in another query.
  The final page returns the storage index. Each query selects
  the current graph; the sequence does not pin one revision. Follow-ups use stored
  immutable identities and recheck authorization, without a process-local cursor.
  Whole-cell rounding and knowledge that cannot identify partial progress
  return explicit errors when a complete response cannot fit the page. Fragmented
  element downloads remain unsupported. These checks establish protocol/service
  behavior, not native Office download continuation.
  The [request specification](https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttpb/5b8d1d29-0adf-4b29-b3d1-1a1fe8590642)
  permits fragments without requiring them. Complete-element continuation does
  not depend on fragment range acknowledgements.
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
| Microsoft Entra authentication | Browser sign-in and native Excel open, edit and save. Native Word, PowerPoint and multi-client Entra editing lack complete desktop coverage. |
| Native Version History | Word, Excel and PowerPoint 16.0.20527 listed persisted revisions, opened older content and restored it as a new revision. Fresh processes repeated listing and historical open after server restart. |
| Blank Office file creation | Automated package checks. |
| Two service instances | Automated identity, shared lease and protocol routing checks against one database. |
| Two Word desktop clients | Distinct accounts exchanged paragraph changes in both directions, including receipt while idle. Concurrent offline paragraph additions survived reconnect; one client needed manual Save to recover. |
| Two PowerPoint desktop clients | Distinct accounts preserved slide 1 and added slide 2; the first client received the new slide while idle. |
| Two Excel desktop clients | Distinct accounts edited concurrently with AutoSave and exchanged cell values and remote selections. Participant names remained unresolved. |
| Large files and production concurrency | Not qualified. Synthetic benchmarks measure provider behavior. |

### Tested scope

Native tests cover Microsoft 365 Word, Excel and PowerPoint 16.0.20527 on
Windows x64 and ARM64 with distinct CellBridge accounts and a PostgreSQL-backed
HTTPS host. Both editors exchanged saved changes with AutoSave; independent
server downloads retained their changes. Word cursor indicators and Excel
remote selections appeared. Participant names remained unresolved.

Version History tests used one Windows desktop and one account. Each app listed
persisted revisions, opened older content and restored it as a new revision.
Downloads verified the restored bytes and retained revisions after a server
restart. Fresh Office processes repeated listing and historical open.

Word outage tests preserved separate paragraph additions from both editors;
one client needed manual Save to recover. An iPhone edit reached both open
Windows clients and a server download. These checks establish saved-change
propagation, not character-by-character delivery or measured latency.

Native Word requested replacement RTC tickets before expiry in a short-lifetime
test. Native Excel continued saving after more than one normal ticket lifetime.
The Excel result establishes continued file saving, not RTC renewal. Recovery
after missed renewal remains untested.

These tests do not establish compatibility with other Office builds, reliable
cold startup across all three apps, same-content conflict resolution or
production concurrency. See the remaining work below.

## Real-time collaboration

The single-host RTC relay implements the observed SignalR methods for typing,
presence and client notifications. `CellBridgeOptions.EnableOfficeRealTime`
defaults to true. Set it to false to disable discovery and endpoints. WebDemo
accepts `Protocol:EnableOfficeRealTime=false`; AppHost accepts
`Parameters:EnableOfficeRealTime=false`.

RTC requires stored grants or an immutable versioned authorization policy so
that each delivery can check participants against one permission snapshot.
Enabling it with an ordinary asynchronous authorization provider, a legacy
access evaluator or `multipleInstances: true` fails at startup.

Discovery exposes storage and authoring headers and authenticated file metadata
through `PROPFIND` with `Depth: 0`. Resource IDs, replication tags, file sizes,
ETags and timestamps come from persisted metadata. Document and SOAP routes
use the same discovery profile and advertise only their implemented methods.
Presence is keyed to the authenticated client; payloads need not contain an
`A` sender field.

RTC does not publish file revisions or implement OCS or WOPI. The separate
MS-SHDACCWS `IsOnlyClient` operation reports pending coauthor transitions;
it is not the RTC transport. See [protocol profiles](office-protocol-profiles.md)
for the service boundaries and reference protocols.

## Remaining collaboration work

This inventory separates missing implementation from observed defects and missing
test coverage. It is not an exhaustive Office conformance audit.

| Area | Current gap |
| --- | --- |
| Participant names | Observed defect: remote selections/cursor indicators appear, but names show `Loading name...` or `Getting name...`. The missing identity lookup contract is not established. |
| OCS | Missing service: the frame codec does not implement session state, WPM merging, revision clocks or saving merged content to the file host. RTC can operate without OCS. |
| Publication notifications | Missing integration: the RTC hub relays client notifications, but persisted file publication does not generate a server notification. |
| Multiple RTC hosts | Missing coordination: rooms and presence are process-local. Multi-instance hosts must disable RTC. Shared file storage and locking are separate and support multiple hosts. |
| Ticket renewal and reconnect | RTC tickets last 20 minutes. Native Word proactively renewed tickets in a short-lifetime test. Recovery after missed renewal, disconnect and presence loss needs coverage. |
| Startup | Bounded admission queues absorb opening bursts. Reliable cold startup across all three apps needs broader coverage. |
| Typing and latency | Test gap: saved edits and selection changes propagated, but character-by-character delivery and measured end-to-end latency are not established for CellBridge. |
| Conflicting/offline edits | Test gap: separate Word paragraph additions survived one outage/reconnect trial. Same-text, same-cell and same-object conflicts, undo and longer outages need coverage. |
| Native sharing | Owner-only service APIs now read, grant and revoke stored access with lifecycle and authority checks. Native Share endpoints and host recipient resolution are not implemented. OneDrive traffic exposed CSOM sharing reads and `/_vti_bin/DocumentSharing.svc`; see [protocol profiles](office-protocol-profiles.md#native-sharing). Email invitations and anonymous links are not supported. |
| WOPI integration | A host adapter must use indexed APIs, supply a production conditional revision destination and qualify OOS framing. CSPP Plus coauthor locks, chunk operations and sequence operations are not implemented by the RTC hub. |

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
