# Office operation costs and workload evidence

The first performance requirement is to keep document history out of small,
frequent operations. A large response can justify work proportional to its bytes;
a lock refresh cannot justify decoding every retained revision. Save optimization
must separate changed bytes, current graph work, retained history, and durability.
This cost model does not depend on a particular replacement storage design.

The protocol inventory and original volume evidence were audited at `f569399`
on 2026-10-28. The implementation cost table below describes the replacement
repository. Capacity measurements are maintained separately in
[Office performance](office-performance.md). Protocol authority comes
from Microsoft's [outer operation enumeration][outer], [binary operation
enumeration][binary], [Query Changes][query], and [Put Changes][put]. The
[requirements ledger](protocol-requirements.md) records implementation limits and
remaining interoperability gates. Implemented behavior is not desktop qualification.

## Units and uncertainty

| Symbol | Meaning |
| --- | --- |
| `D` | Bytes in the materialized document package, including ZIP structure. |
| `delta` | Changed payload bytes sent by the client, not characters typed. |
| `G` | Number of descriptors in the current stored partition graph/catalog. This can include retained elements beyond the latest file's reachable closure. |
| `K` | Encoded client/server knowledge bytes; count and range arrangement also affect processing. |
| `q` | Number of knowledge entries/ranges before normalization. |
| `H` | Retained immutable revision count. |
| `E` | Active editor/coauthor/lease entries; these are distinct collections, not interchangeable identities. |
| `P` | Property count; property value lengths must be measured separately. |
| `N` | Files/identities in a namespace, including relevant tombstones/reservations. |
| `R` | Actual serialized response bytes, including selected graph data and knowledge. |
| `g`, `d` | Descriptors reachable in one selected graph, and descriptors changed by one operation. |
| `S` | Serialized complete document state, including history, receipts and descriptor catalogs. |
| `B`, `C` | Bytes in a selected graph closure, and fixed protocol/control fields plus bounded identifiers. |
| `h`, `p` | Revision/property entries actually returned, with `h <= H` and `p <= P`. |

Counts are not bytes. Writing `O(G)` assumes bounded descriptor size; variable
strings, object references and knowledge ranges require their own byte accounting.
`S` cannot always be replaced by `G + H`: histories with unrelated memberships
can contain much more metadata than an append-only shared-prefix history.

The tables give successful-operation minimum and conditional maximum shapes.
They do not assign one universal maximum Office file size. Errors can be small
even after expensive processing. `U/U` means that empirical mean request/response
size is unknown. No Microsoft protocol average is supplied by the specifications.
`C` includes required fields, so a minimum of `C` never means a zero-byte HTTP
request. SOAP, MTOM, HTTP, authentication and TLS add framing; inline base64 also
expands binary data. Measure those separately from the inner payload.

For reproducible volume scoring, score serialized bytes at the chosen layer:
1 is at most 1 KiB; 2 is over 1 through 16 KiB; 3 is over 16 through 256 KiB;
4 is over 256 KiB through 4 MiB; 5 is over 4 MiB. A formula such as `v(K+B)`
means apply those bands after measuring those bytes. A range `1..5` means the
operation can span the bands; it is not an observed distribution. Small control
fields are expected in band 1 before framing, but variable names/metadata can
raise that score. The only measured save score below is the fixture's band 3.

## Request and response inventory

Every row reports input minimum..maximum, output minimum..maximum, then mean
input/output and volume scores. Maximum formulas condition on the admitted
document, graph, history and strings, rather than inventing exact wire limits.
`V` denotes caller-supplied variable text bytes. Query input also includes any
selected scope/filter identifiers in `V`.

| ID | Operation and support | Request shape, min..max | Response shape, min..max | Mean I/O | Volume score min/mean/max, input; output |
| --- | --- | --- | --- | --- | --- |
| A1 | Discovery/OPTIONS and authenticated document metadata, host endpoints | `C..C+V` | `C..C+V` | U/U | `v(C)/U/v(C+V); v(C)/U/v(C+V)` |
| A2 | Current download, host endpoint | `C..C+V` | `D..D` for complete body; conditional responses can omit it | U/U | `v(C)/U/v(C+V); v(D)/U/v(D)` |
| A3 | WhoAmI; ServerTime | `C..C+V` | `C..C+V` identity fields or time | U/U | `v(C)/U/v(C+V); v(C)/U/v(C+V)` |
| A4 | Cell QueryAccess | `C..C` | `C..C` read/write access flags | U/U | `1/U/1; 1/U/1` |
| A5 | Cell AllocateExtendedGuidRange | `C..C` count | `C..C` GUID and integer bounds, not one item per allocated ID | U/U | `1/U/1; 1/U/1` |
| B1 | Coauth JoinCoauthoring, RefreshCoauthoring, ExitCoauthoring | `C..C+V` | `C..C+V` status/transition fields where applicable | U/U | `v(C)/U/v(C+V); v(C)/U/v(C+V)` |
| B2 | Coauth ConvertToExclusive, MarkTransitionComplete | `C..C+V` | `C..C+V` | U/U | `v(C)/U/v(C+V); v(C)/U/v(C+V)` |
| B3 | Coauth GetCoauthoringStatus, CheckLockAvailability; AmIAlone; LockStatus | `C..C+V` | `C..C+V`, no full editor list | U/U | `v(C)/U/v(C+V); v(C)/U/v(C+V)` |
| B4 | SchemaLock GetLock, RefreshLock, ReleaseLock, CheckLockAvailability, ConvertToExclusive | `C..C+V` | `C..C+V` | U/U | `v(C)/U/v(C+V); v(C)/U/v(C+V)` |
| B5 | ExclusiveLock GetLock, RefreshLock, ReleaseLock, CheckLockAvailability, ConvertToSchema, ConvertToSchemaJoinCoauth | `C..C+V` | `C..C+V` | U/U | `v(C)/U/v(C+V); v(C)/U/v(C+V)` |
| C1 | EditorsTable JoinEditingSession, RefreshEditingSession, LeaveEditingSession | `C..C+V` | `C..C+V` acknowledgement | U/U | `v(C)/U/v(C+V); v(C)/U/v(C+V)` |
| C2 | EditorsTable UpdateEditorMetadata, RemoveEditorMetadata | `C..C+V` metadata or selected keys | `C..C+V` acknowledgement | U/U | `v(C)/U/v(C+V); v(C)/U/v(C+V)` |
| C3 | Cell QueryChanges, editors table | `C..C+K+V` | `C+K..C+K+serialized(E)` | U/U | `v(C)/U/v(C+K+V); v(C+K)/U/v(R)` |
| D1 | Cell QueryChanges, unchanged file/current knowledge | `C..C+K+V` | `C+K..C+K` when no payload is missing | U/U | `v(C)/U/v(C+K+V); v(C+K)/U/v(C+K)` |
| D2 | Cell QueryChanges, incremental file | `C..C+K+V` | `C+K..C+K+B` selected missing closure | U/U | `v(C)/U/v(C+K+V); v(C+K)/U/v(R)` |
| D3 | Cell QueryChanges, initial/unknown knowledge or whole-cell retransmission | `C..C+K+V` | `C+K..C+K+B` full selected graph | U/U | `v(C)/U/v(C+K+V); v(C+K)/U/v(R)` |
| D4 | Cell QueryChanges, metadata partition, selected cell or storage-index-only | `C..C+K+V` | `C+K..C+K+B` for that scope | U/U | `v(C)/U/v(C+K+V); v(C+K)/U/v(R)` |
| E1 | Cell PutChanges, complete file publication with reused graph objects | `C..C+delta+serialized(d)+K` | `C..C+K+added-IDs+applied-index` | U/U; archived fixture input mean 17,733 bytes, final replacement overload mean 18,124 bytes | `v(C)/U/v(request); v(C)/U/v(R)`; both fixture inputs `3/3/3` |
| E2 | Cell PutChanges, full package replacement | `C+D..C+B+K` for supplied full graph | `C..C+K+added-IDs+applied-index` | U/U | `v(C+D)/U/v(request); v(C)/U/v(R)` |
| E3 | Cell PutChanges, complete supported opaque metadata graph | `C..C+B+K` | `C..C+K+added-IDs+applied-index` | U/U | `v(C)/U/v(request); v(C)/U/v(R)` |
| E4 | Exact accepted-save receipt replay; coherency conflict | Same submitted save as E1/E2/E3 | `C..C+stored-acknowledgement` or error | U/U | Same input triple as original save; `v(C)/U/v(R)` output |
| F1 | GetDocMetaInfo; Properties PropertyEnumerate | `C..C+V` | `C..C+serialized(P)` names/values as applicable | U/U | `v(C)/U/v(C+V); v(C)/U/v(R)` |
| F2 | Properties PropertyGet | `C..C+serialized(p IDs)` | `C..C+serialized(p values)` | U/U | `v(C)/U/v(request); v(C)/U/v(R)` |
| F3 | GetVersions; Versioning GetVersionList | `C..C+V` | `C..C+serialized(h)` plus author/URL fields | U/U | `v(C)/U/v(C+V); v(C)/U/v(R)` |
| F4 | Authorized historical download, host endpoint | `C..C+V` revision selector | `Dhistorical..Dhistorical` complete body | U/U | `v(C)/U/v(C+V); v(Dhistorical)/U/v(Dhistorical)` |
| F5 | Versioning RestoreVersion | `C..C+V` version selector | `C..C+V` acknowledgement | U/U | `v(C)/U/v(C+V); v(C)/U/v(C+V)` |
| G1 | FileOperation Rename; host MoveDocument | `C..C+V` path/name | `C..C+V` acknowledgement/metadata | U/U | `v(C)/U/v(C+V); v(C)/U/v(C+V)` |
| G2 | Host namespace list | `C..C+V` selector/page | `C..C+serialized(N)` | U/U | `v(C)/U/v(C+V); v(C)/U/v(R)` |
| G3 | Host lifecycle create/import/recreate | `C+D..C+D+V` supplied file | `C..C+V` identity/metadata | U/U | `v(C+D)/U/v(request); v(C)/U/v(R)` |
| G4 | Host lifecycle delete; storage purge/collection | `C..C+V` identity/selection | `C..C+V` result | U/U | `v(C)/U/v(C+V); v(C)/U/v(C+V)` |
| X1 | Enum-only QueryKnowledge, QueryRawStorage, PutRawStorage, QueryDiagnosticStoreInfo | Unqualified; bounded parser input | Explicit unsupported error | U/U | `U/U/U; v(error)/U/v(error)` |
| X2 | Partial, PartialLast, LastWriterWinsOnNextChange; Additional Flags D/E/F; unsupported partition writes | Can carry a large supplied graph | Explicit unsupported error | U/U | `v(C)/U/v(admitted input); v(error)/U/v(error)` |

The outer Cell wrapper is a carrier for binary operations, not a fifth binary
operation. QueryKnowledge is not a supported substitute for QueryChanges. The
current normative binary enumeration contains exactly four operations. The
normative [file operation][rename], [versioning][versions] and [properties][props]
subtypes are Rename, GetVersionList/RestoreVersion, and
PropertyEnumerate/PropertyGet respectively. Host move, import and namespace
listing are separate APIs, not new SharePoint compatibility claims.

MultiRequestPutHint is accepted on otherwise supported complete saves; it is not
the rejected Partial/PartialLast staging mode. The rejected [Additional Flags][put-flags]
D/E/F (`0x38`) are CoherencyCheckOnlyAppliedIndexEntries, FullFileReplacePut and
RequireStorageMappingsRooted. Full package replacement without the optional
FullFileReplacePut flag is supported as E2. Put acknowledgements can also carry
added element IDs, so their size grows with the acknowledgement contents, not
just knowledge.

QueryChanges can return knowledge without new file payload, yet its knowledge
may describe many graph elements. A small delta response is not necessarily
`O(delta)` overall. Requested scopes, conservative unknown-knowledge fallback,
whole-cell rounding and negotiated hashes change `R`. Hash records can reduce
transferred data while still requiring content reads/hashing. Fragment delivery,
continuation and higher historical version-token profiles remain unsupported;
the selected profile ignores QueryChangesVersioning as documented in the ledger.

SOAP batches, dependency evaluation and binary packages compose these rows.
One HTTP request can contain several outer and binary subrequests; repeated
queries can share deduplicated package data. Count logical operations, HTTP
requests and bytes independently. Request parsing must still examine admitted
input even when the operation will fail, and successful earlier independent
subrequests are not a transaction rolled back by a later failure.

## Limits are not averages

| Evidence class | Actual constraint or observation | Consequence |
| --- | --- | --- |
| Normative field | [Allocation response][allocation] has a 16-byte GUID and an exclusive upper integer bound in 1,000..100,000. | Allocating many IDs does not return that many GUIDs. |
| Normative field | QueryChanges Maximum Data Elements is a compact unsigned 64-bit byte constraint. | It limits serialized data elements, not their count or the whole SOAP response. Its integer representation is not a supported document maximum or a measured response size. |
| Normative operation | [GetVersionList][version-list] lets the server decide how many recent versions to return. | A bounded recent list is possible; its output still costs `Omega(h)`. |
| Host default | [StorageLimits](../src/CellBridge/Storage/StorageLimits.cs) admits 128 MiB documents, 512 MiB graph payloads, 100,000 graph elements and 1,000 history revisions. | These are admission limits, not qualification at those sizes. |
| Host default | Document stored bytes 1 GiB; decoded memberships 128 MiB. Provider-wide byte and document-count caps were removed by the approved rewrite. | Individual document/request limits remain independent of namespace size. |
| Host default | [Transport/storage configuration](storage-providers.md#configuration) documents 128 MiB SOAP/MTOM requests; PropertyGet accepts at most 256 IDs in current code. | Framing consumes request budget; property count is not property-byte size. |
| Observed archived fixture | Overload input min/mean/max 17,732/17,733/17,734 binary bytes, 16,700 changed ZIP bytes. | This distribution covers the synthetic fixture only; response mean was not established. |
| Observed replacement fixture | Final overload input min/mean/max 18,025/18,124/18,636 binary bytes, 16,700 changed ZIP bytes. | These are submitted save sizes including warm-up, not an empirical Office average. |
| Planning assumption | 256 KiB or 1.5 MiB package, 16 KiB editable XML. | These are scenario parameters, not ordinary Office averages. |

Microsoft's [SharePoint storage planning example][size-planning] uses 250 KB and
its [migration guide][size-migration] uses Office files around 1.5 MB. Neither
provides an empirical modern Office average suitable for this workload. Sample
the intended collection by application and package size; report percentiles,
mean, compression ratio and edit/changed-part distributions before weighting costs.

## Costs of the replacement implementation

### Database operation accounting

Database-wide `pg_stat_database.xact_commit` includes explicit transactions and
implicit statement transactions. A mixed-load total divided by successful saves
also includes reads, authentication, pooled connection reset SQL, health checks,
diagnostics, warm-up and drain. It is not the number of write transactions or
durability flushes per save. SQL log records are not network round trips either;
publication batches contain many statements inside one write transaction.

The isolated HTTP attribution in [Office performance](office-performance.md)
showed avoidable connection and transaction overhead in save preparation.
Submitted immutable descriptors, selected mapping serial claims, current binding
group descriptors and object definitions now share the existing preparation
snapshot through `SavePreparationRequest.Graph`. This projection loads metadata,
not every retained payload. Element and owner queries use batches of at most 256
keys. File reconstruction batches only stored file groups selected by the
effective file bindings, using the existing verified content reader. Unknown or
historical references retain their ordinary lookup path.
`SavePreparationRead.PreparedMappingKeys` preserves which keys the snapshot
checked, including absent mappings, so graph resolution does not query known
absence again. Keys outside that set still require a provider lookup.

`PartitionReadRequest.IncludeMappings` returns mappings with the selected head,
partition and catalog snapshot. QueryChanges uses those mappings instead of
opening another coherent read. The provider reuses the already selected current
sequence; historical selections still resolve the matching mapping intervals.
These changes preserve fresh authorization reads, document-scoped staging,
payload hash checks and the final locked publication fence. They do not make
complete saves proportional only to changed bytes or remove history-dependent
query work.

### Work and working memory

These are source-based bounds, not measured latency. SQL indexes add logarithmic
lookup/index-maintenance costs. Let `L` count current resolved object bindings,
`M` count selected storage-index mappings, `A` count serialized permission bytes,
and `U` count expanded ZIP bytes examined during package validation. Variable
record bytes still count even where a table uses descriptor counts below.

| Operation | Current work and working memory | Coordination and remaining cost |
| --- | --- | --- |
| Resource metadata/access | The repository resource lookup uses one indexed head statement and decodes `A` plus current metadata; independent of `H`. Service authorization can reread the head. A path lookup also reads its indexed claim. | Reads do not lock the namespace. Due editor expiry can require a document control update. |
| Lease/editor control | Read current coordination, editor entries and partition headers, approximately `O(E + A)`. | One affected document row; no application revision or file graph load. |
| Current package download | Read `L` bindings and their selected immutable graph/payload closure, reconstruct and verify `D` bytes. | No application-history scan. Reconstruction uses a temporary seekable file. |
| File save | Validate admitted input, read current bindings, resolve changed/selected graph objects, reconstruct/hash `D` and examine up to `U` expanded ZIP bytes. Store new canonical payloads, changed mappings/bindings, revision and receipt. | Matching SQL state/content publish bounded provisional payloads and document ownership in one transaction, taking the document row lock once after inserting the payloads. External or larger saves reserve ownership before staging and take the document lock again for publication. Ownership lookups and inserts depend on the batch's keys, not total history. It stores no additional complete package; reconstruction and validation still cost document-sized work. |
| Mapping/binding publication | Insert changed keys and their immutable version records; update current projections. | Checkpoints copy current bindings when the 128-change or 1 MiB replay bound is reached. This costs `O(L)` on that save, outside the document lock. |
| QueryChanges | Read the selected live catalog and mappings, then process knowledge and select/serialize the response. Working memory includes `G + M + K` and selected response buffers. | The current service still enumerates the catalog and mappings even for warm knowledge. `G` and `M` can grow with protocol history. Indexed storage APIs alone do not make this operation output-sensitive. |
| Point application revision / recent version page | Indexed revision selection; offset paging also scans the skipped entries. Returned records cost at least `O(h)`. | A full SOAP version list can still return all `H` records. |
| Historical package / restore | Read selected checkpoint bindings and bounded delta tail, plus selected graph closure and package bytes. Restore may traverse retained base references/catalog data. | Explicit cold operation. Publication uses the affected document row; restore is not constant time. |
| Recovery/export/collection | Visit retained records and referenced payloads. Import uses bounded EF flush batches and a private payload spool inside one atomic transaction. | Offline operations require quiescence. The supplied complete archive/snapshot itself is not bounded by the EF batch size. |

Current bindings avoid replaying all application revisions to open the latest
file. They do not remove the lower bound of reading returned bytes, eliminate
whole-package validation, or make arbitrary protocol base chains constant time.
No provider-wide counter, bucket pool or namespace publication lock participates
in normal saves. Request admission limits still bound concurrent resource use.

Save preparation and the publication fence read only their affected current
mapping keys, in batches of at most 256. They check the selected logical root
once, rather than looking up the root and a historical interval for every key.
Preparation reuses partition headers and replay positions inside its coherent
transaction. Current projections are maintained atomically with history;
offline recovery checks their completeness against the retained intervals.

## Time and space priorities

The following is an explicit planning rubric, not a measured cost ranking.
At nine-second saves, the scenario offers 166.67 save actions/second and 62.5
queries/second including opens. For controls absent from the synthetic workload,
assume one control/lock action per user per 30 seconds (50/second), and one
presence action per 60 seconds (25/second). These assumptions justify testing
frequent small operations; they do not claim desktop Office follows those rates.
History browsing, restore and namespace mutations are assumed below one/second.

Time priority is T1 for a family offered at least 25 operations/second with
avoidable state/graph work. T2 covers lower-rate interactive operations or costly
bursts; T3 covers rare background/administrative work. Within a class, rank by
`operation rate * measured avoidable CPU or wait per operation`; those measurements
are missing, so no numeric benefit ranking is claimed. Query and control subtypes
share a family priority until their mix is known. E4 rises to T1 during retry storms.
The A3/A4 T2 ratings assume identity/time/access checks mainly accompany opens.
Their actual rates are unknown; if polling invokes them at the control-family
rate, they inherit T1. This is a workload sensitivity, not a reason to permit
complete-state reads on those paths.

Working-memory priority M1 covers potentially full document/graph bodies, M2
variable catalogs/knowledge/editor lists, and M3 bounded scalar/metadata results.
Storage priority W1 covers novel document/graph content and revision growth,
W2 metadata/receipt/coordination growth or rewrites, and W3 no intended stored
write. These classify the legitimate operation; accidental full-state loading or
rewriting is a defect to remove across all classes. M1 downloads with W3 differ
from M1 saves with W1. Concurrency and `D/G/H/E` can change the relative urgency.
Current scalar outer dispatch can also publish incidental editor/lease expiry.
Where this occurs, A3/B3 and GetDocMetaInfo in F1 incur W2 rather than W3. The
desired scalar-read cost does not authorize dropping protocol-visible expiry
updates; record them in coordination/partition state when required.

Desired bounds below describe required work plus indexed access, not promises
already achieved. `log N`, `log G` and `log H` represent lookup/index maintenance;
they omit fixed network round trips. Every serialized response has an unavoidable
`Omega(R)` time cost. Streaming can bound buffers without making output constant.
These logs describe logical lookup scopes. A shared compound database index has
height based on its total indexed rows; include collection size when comparing
growth across documents. Arbitrary knowledge normalization adds `O(q log q)`
sorting work as well as reading `K` bytes.

Complete-state decoding alone costs `Omega(S)` time and materializes its metadata.
Thus current scalar SOAP controls inherit `Omega(S)` before their small result,
queries add catalog/knowledge/selected-payload work, and saves add materialization
and publication. Complete-state writes also serialize `S`, even for a small lease
change. Shared memberships reduce normal append-history work toward `O(G+H)`;
arbitrary membership arrangements retain more expensive validation paths. These
are source-based growth statements, not fitted latency estimates or proof that
every provider/policy takes the same path.

| Operations | Time / working memory / persistent storage | Current cost driver | Desired per-operation work and space |
| --- | --- | --- | --- |
| A1 metadata/discovery | T2 / M3 / W3 | Selective metadata reads exist; full-state policy fallback remains. | Metadata lookup `O(log N)` plus output; bounded metadata buffers. Discovery should not inspect a graph. |
| A2 current download | T2 / M1 / W3 | Content reads and transport buffering; authorization path can affect state reads. | Cached/direct-package path `O(log N + D)` with bounded streaming buffers. Lazy cold generation also resolves the selected graph and reads its payload closure before emitting `D` bytes; no universal bound in `D` alone. |
| A3 WhoAmI/ServerTime | T2 / M3 / W3 | SOAP file lookup/authorization loads complete state before scalar dispatch. | Authorized identity/time with metadata lookup only; no `G` or `H` dependence. |
| A4 QueryAccess | T2 / M3 / W3 | Same SOAP/binary state access despite scalar result. | Current permission lookup, no graph/history decode; bounded result. |
| A5 GUID allocation | T3 / M3 / W3 | Full-state authorization around a constant-sized generated range. | Authorization plus UUID/range generation; no graph/history or global allocation counter. |
| B1 membership join/refresh/exit | T1 / M3 / W2 | Complete-state transition plus lease/member scans. | Indexed member mutation and expiry work proportional to entries affected; at worst `O(E)` for a bounded active set. |
| B2 conversions/transition completion | T1 / M3 / W2 | Same state transition; ownership and competing leases matter. | Atomic current coordination checks, `O(E)` or indexed equivalent; no content/history. |
| B3 status/alone/availability | T1 / M3 / W3 | Complete-state load for current coordination response. | Current coordination lookup/count; bounded result independent of `D/G/H`. |
| B4 SchemaLock; B5 ExclusiveLock | T1 / M3 / W2 | Full-state transitions and coordination restoration. | Current lease read/write with conflict fencing; no historical graph work. |
| C1 presence lifecycle; C2 metadata mutation | T1 / M2 / W2 | Complete-state transition and editors-table serialization. | Actor/editor lookup plus changed metadata; amortized bounded expiry, no file history; retain `O(E+V)`. |
| C3 editors query | T1 / M2 / W2 when expiry is due, otherwise W3 | Whole-state access plus generated editors graph/knowledge; `ExpireEditorsAsync` persists the removal of expired entries and advances affected partition knowledge/serials. | `O(K+E+R)` full query, or changed entries where knowledge permits; output `Omega(R)`. Publish due expiry in coordination/partition state without file-history rewrites. |
| D1 unchanged file query | T1 / M2 / W3 | State decode, catalog enumeration, serial ambiguity and mapping checks, knowledge rebuild. | Indexed knowledge comparison plus necessary knowledge output; `O(K+G)` may remain when all graph knowledge must be emitted. No unrelated revision decode. |
| D2 incremental query | T1 / M2 / W3 | Catalog work plus verified selected payload reads. | `O(K+q log q+q log G+d+R)` only where an index can prove the selection; full-knowledge output can force `O(G)`. |
| D3 initial/full query | T2 / M1 / W3 | Catalog work plus full selected payload/knowledge assembly. | `O(g+B+K)` plus lookup, bounded streaming where framing permits; no unrelated history. |
| D4 scoped/metadata query | T2 / M2 / W3 | Some multi-cell paths restore full graph to determine closure. | Work proportional to selected closure plus knowledge; no unrelated cells/history. Large opaque scopes inherit M1. |
| E1 delta save | T1 / M1 / W1 | Fresh full state, retained catalog checks, selective payload reads, materialized `D`, new content and state publication. | Normal current-base append metadata can approach `O(d log G+log H)` plus validation/output. An old-base branch switch can change many more current bindings than the upload's `d`; checkpoints add construction/write work. Current full-file validation remains `Omega(D)` until separately changed and justified. |
| E2 full replacement | T2 / M1 / W1 | Full graph/package validation and stored content insertion plus state/history work. | `O(D+g+log H)` or input graph bytes, bounded buffering; unavoidable full input and novel stored bytes. Promote T1 if replacements are common. |
| E3 metadata publication | T2 / M2 / W1 | Full graph/closure validation and complete-state commit. | Changed closure plus index/history publication; opaque full graphs still cost `Omega(B)`. Promote M1 for large graphs. |
| E4 replay/conflict | T2 / M3 / W3 | Submitted body parsing, current authorization/coherency and receipt lookup in state. | Indexed receipt/version checks after input validation; no new stored content on exact replay. Input cost is still unavoidable. |
| F1 metadata/enumeration; F2 property get | T2 / M3 / W3 | Outer operations use full-state transition; property builder constructs the property set. | `O(P+R)` enumeration; selected property reads `O(p+R)` with indexed lookup; no graph/history. |
| F3 version list | T2 / M2 / W3 | SOAP enumerates retained revisions; author matching can add `O(H squared)` work. Separate selective paged API exists. | `O(log H+h+R)` recent headers, indexed author mapping; no revision graph materialization. |
| F4 historical download | T3 / M1 / W3 | History selection and verified content read; selective summary API can avoid full state. | Cached/direct-package path `O(log H+Dhistorical)`. Lazy cold reads add checkpoint decoding, effective-delta replay, graph resolution and payload closure reads. Bound replay records/bytes explicitly; full output still costs `Omega(Dhistorical)`. |
| F5 restore | T2 / M1 / W1 | Historical graph validation/remapping, current-state fencing and publication. | Selected revision lookup/resolution plus graph and payload work, fresh affected protocol identities/serials and publication. A checkpoint representation adds decode/replay; replacing current bindings can change `Theta(L)` rows for `L` current bindings. No unrelated history scan is justified. |
| G1 rename/move | T2 / M3 / W2 | Atomic namespace/authority checks and full-state representation; path admission can scan namespace. | Unique indexed path reservation `O(log N)` plus bounded lifecycle/coordination update, no content rewrite. |
| G2 namespace list | T3 / M2 / W3 | SQL projects namespace fields, but full enumeration is `O(N)` and server JSON processing is not free. | Page `O(log N+page size)` where contract allows; streaming a full inventory still costs `Omega(N)`. |
| G3 create/import/recreate | T3 / M1 / W1 | Namespace admission, full package/graph construction, content insertion and state publication. | Indexed namespace admission plus `Omega(D)` validation/persistence; promote T1 during bulk import/recovery. |
| G4 delete/purge/collection | T3 / M2 / W2 | Lifecycle tombstones/reservations and retained content references; collection may enumerate inventories. | Indexed lifecycle update, then collection proportional to affected references or explicit inventory scope. Reclaimed stored bytes can dominate the space benefit without a large wire response. |
| X1/X2 unsupported operations | T3 / M3 / W3 | Parser/authorization can still consume admitted input/state. | Bounded early rejection after required validation; do not optimize hypothetical successful execution. |

These current-path findings come from [SOAP execution](../src/CellBridge/Execution/CellBridgeRequestProcessor.cs),
[file query selection](../src/CellBridge/Execution/FileQueryResponseBuilder.cs),
[binary execution](../src/CellBridge/Execution/CellBridgeDocumentService.cs),
[outer operations](../src/CellBridge/Execution/OuterDocumentOperations.cs),
[selective reads](../src/CellBridge/Execution/SelectiveDocumentReadOperations.cs),
and [namespace projection](../src/CellBridge.Storage.PostgreSql/PostgreSqlNamespaceReads.cs).
For GetVersionList, per-revision `Array.FindIndex` over distinct authors creates
the quadratic worst case. It is secondary to avoiding whole-state reads for
frequent controls, but inexpensive to remove when that operation is revised.

## Workload arithmetic and existing results

For 1,500 active users with ten documents each, intervals apply per user, not per
document. A 10,000-save/minute offer requires a nine-second mean save interval:
`1500 * 60 / 9 = 10000`. Queries every 30 seconds add 50 user actions/second;
opens every 120 seconds add 12.5 actions/second. Each open downloads and queries
its graph, so it adds another query beyond the periodic query clock. Do not
equate 229.17 actions/second with HTTP requests or binary subrequests/second.
At nine seconds, saves plus the two clocks offer 229.17 actions/second before
skips, retries, batching and the extra operations within an open.

| Existing final run | Offered saves/minute | Strict commits/minute | Late acknowledgements | Failed HTTP steps including warm-up | Interpretation |
| --- | ---: | ---: | ---: | ---: | --- |
| Eight-second cadence | 11,250 | 8,151 | 121 | 6,334 | Overloaded, did not establish 10,000 successful saves/minute. |
| Thirteen-second cadence | About 6,923 | 6,913 | 10 | 58 | No measurement-period failures, but warm-up failed the gate. |
| Fifteen-second cadence | 6,000 | 5,995 | 2 | 0 | 5,997 measured starts; passing bounded synthetic run. |

All used 30 seconds of warm-up and 60 seconds of measurement on four logical
CPUs and about 13 GiB shared by generator, WebDemo and PostgreSQL. Strict commits
must start and finish inside the measured minute. The raw final comparison is
`artifacts/office-performance/quota-final-comparison.json` (local generated data);
the [performance guide](office-performance.md) records configuration and evidence limits.

The final overload inventory grew from 176,507,473 to 3,925,647,428 logical stored
bytes across 12,048 commits including warm-up and drain. Net growth was about
303.89 KiB/commit, roughly 17.5 times the 17,733-byte mean binary save request.
This is logical retained content/state growth, not physical I/O: it excludes SQL
projections, indexes, WAL and page effects. It is also not the gross number of
bytes rewritten per transaction. Measure stored bytes, WAL, state serialization,
content inserts and network independently before attributing amplification.

Retained space depends on representation as well as wire deltas. Keeping a
distinct materialized file object for each revision can cost `sum(Di)` across
history even when each upload is small. Sharing immutable graph descriptors alone
does not deduplicate those full-file bytes. A representation that shares content
can approach initial content plus unique changed bytes and revision metadata, but
its reconstruction, integrity checks and collection costs need separate evidence.
Per-save storage work must therefore distinguish newly retained bytes from
rewritten state, SQL index/projection changes, WAL and eventual collection.

## Skeptical review and next measurements

The [generator fixture](../tests/CellBridge.TestFixtures/IncrementalOfficeWorkload.cs)
uses a 256 KiB DOCX with a 16 KiB uncompressed XML part at the ZIP
tail and shared initial random padding. Unique edited payload hashes establish
distinct saves, but shared unchanged bytes and identifiers favor warm reuse.
Small tail edits do not represent full replacement, compressed entries that grow,
middle-of-archive changes, large media, Excel or PowerPoint. Moving a growing XML
entry ahead of padding reduced fixed-chunk reuse from 75% to zero in a separate
offline check. That check is a layout sensitivity result, not measured HTTP benefit.

The final sampled host was about 95% CPU busy while WebDemo averaged about
1.35 cores. Generator and database CPU therefore matter. Low I/O wait does not
remove commit/WAL latency, and shared-host saturation cannot identify the server's
independent limit. A 90-second fresh-document run also has young histories. The
aged in-memory diagnostic improved full-save median time at 250 revisions from
100.78 to 9.32 ms, while shared-membership-only HTTP throughput did not improve
in its single young-history run. These are different bottlenecks and workloads.

If an optimized component is fraction `f` of end-to-end time, a tenfold speedup
there gives `1 / ((1-f) + f/10)` overall under a simple serial model. At an assumed
`f=0.05`, that is only about 1.047 times. This is an illustration, not a measured
fraction. Concurrency, queues, admission and storage waits further complicate the
comparison. Removing history growth remains necessary even when young-history
HTTP throughput changes little.

Before choosing the next optimization, measure the following independently:

- Capture a pilot's operation/subtype rates, batching, retries, no-change polls,
  editor counts and request/response percentiles. The synthetic workload does not
  supply realistic lock, presence, restore or namespace frequencies.
- Use a separate generator and report generator, server and database CPU, GC,
  allocations, active connections, admission rejections, WAL and commit latency.
  Repeat the nine-second target and nearby cadences; report run variation.
- Age histories independently of `D` and `delta`, including unrelated membership
  layouts, 1/250/1,000 revisions, distinct cold documents and hot shared documents.
  Include small controls against each aged document to expose accidental `S` cost.
- Vary package size, ZIP compression/layout, changed-part size and full-replacement
  share. Use measured pilot percentiles when available; until then label each
  mixture as a planning scenario, not an average Office workload.
- Sweep polling intervals, active editors and cold-open bursts. Higher polling
  rates raise D1/B/C priority; frequent large replacements raise E2 priority;
  long histories raise avoidable state-read cost even when wire payloads stay small.
- Separate transient peak memory, retained unique bytes and physical database
  bytes. Record state/knowledge/response bytes per logical operation. Preserve
  correctness, authorization, restore and exact-retry checks outside timing.

[outer]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttp/a3bb03aa-bbc6-4fab-96f4-4909fb2b813b
[binary]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttpb/4d227fdc-d30c-4f50-8394-76b77f09ff62
[query]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttpb/5b8d1d29-0adf-4b29-b3d1-1a1fe8590642
[put]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttpb/e4c224ca-0717-4b02-b713-e75d353904bb
[put-flags]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttpb/57ddba13-18da-452d-8cca-c43ddce28267
[rename]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttp/ade57c1d-c752-4a92-bc9e-b4993a00a509
[versions]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttp/01aa24ee-02c0-49b9-9f60-837d78d277e6
[props]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttp/b66e6237-5d97-4792-8f03-cb224f3fa9f5
[allocation]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttpb/426a9c19-db24-46fd-a97d-83044af1c3c3
[version-list]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttp/025134f4-f3a9-48df-a90c-8e0e07b30ae6
[size-planning]: https://learn.microsoft.com/en-us/SharePoint/administration/storage-and-sql-server-capacity-planning-and-configuration
[size-migration]: https://learn.microsoft.com/en-us/sharepointmigration/sharepoint-online-and-onedrive-migration-speed
