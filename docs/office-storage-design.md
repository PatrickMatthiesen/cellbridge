# Office operation and storage design review

The user approved this replacement design on 2026-10-28. Implementation proceeds
from archived commit `1e35edd`; PR #104 was closed without merging. This document
describes the replacement contract and acceptance criteria. The implementation
costs and remaining algorithmic limits are recorded in
[operation costs](office-operation-costs.md#costs-of-the-replacement-implementation);
measurements are recorded separately in [Office performance](office-performance.md).
The user explicitly approved removing provider-wide byte and document-count
quotas, including shared accounting and buckets. Per-request/per-document bounds,
permissions, coherency and durability remain requirements.

- [Microsoft operation catalog](office-operation-catalog.md) lists requests,
  responses, advertised profiles, implementation coverage and size drivers.
- [Operation costs and priorities](office-operation-costs.md) separates protocol
  bounds, planning assumptions, observed fixtures and unknown production averages.
- [Existing measurements](office-performance.md) describe the previous experiments.

## Storage-index semantics checked during implementation

The transmitted storage index is an update message, not a replacement snapshot.
[MS-FSSHTTPB 2.2.2.1.4](https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttpb/e4c224ca-0717-4b02-b713-e75d353904bb)
requires atomic updates of supplied keys without overwriting other updates.
Expected values compare mapping targets. A mapping's transmission identifier and
serial number do not change that comparison. The earlier implementation selected
the uploaded index as a complete replacement; preserving that behavior would
preserve a protocol bug.

`ExpectedStorageIndex` supplies preconditions for the keys being updated. The
server parses it and compares the relevant targets, but does not publish its
mappings as new serial claims. Unreferenced transmission indexes also do not
create claims. `ExpectedIndexMappingClaimTests` covers these cases, including a
large expected envelope and serial aliases that must not change coherency.

[MS-FSSHTTPB 2.2.1.12.2](https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttpb/f5724986-bd0f-488d-9b85-7d5f954d8e9a)
defines the storage-index data element as temporary transmission data. New saves
keep current key/value mappings and version intervals, rather than storing each
incoming index package as canonical document content. The server constructs
transmitted indexes from the selected mapping version. In-memory mapping versions
share unchanged dictionary branches; SQL versions store changed keys and their
visibility intervals. Neither provider copies the full mapping table per save.

For non-coalescing Put Changes, the implementation returns knowledge of the
accepted elements and mappings without claiming a complete-cell waterline. This
is an inference from the complete-knowledge restriction in section 2.2.2.1.4 and
the knowledge representation in
[section 2.2.1.13](https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttpb/d6414c58-95ba-40ab-be03-17faf38b6bde).
It needs non-coalescing request/replay validation. It is not evidence of live
desktop Office interoperability. Query Changes knowledge remains a separate
operation with its own selected snapshot and response rules.

## Requirements before mechanisms

Microsoft's [binary abstract data model][model] describes immutable data elements
and explicit revision/base references. It permits different internal layouts
when observable behavior is preserved. It does not require JSON snapshots,
provider-wide byte counters, quota buckets, a particular database primary key,
or writing a complete downloadable Office package on every change.

The design must preserve authenticated document identity, current authorization,
partition identities, mapping and element serials, operation-specific coherency,
ordered dependency execution, response knowledge and explicit unsupported modes.
One SOAP message is not automatically one atomic database transaction. Accepted
earlier operations remain accepted if a later independent operation fails.

Four histories have different purposes and retention rules:

| History | Purpose | What cannot be inferred |
| --- | --- | --- |
| Application versions | User-visible versions, authorized downloads and restore | Removing an old version does not prove its protocol data is unused. |
| Protocol revision/base graph | Resolve inherited object data and client knowledge | A linked list in the wire model does not require linear database lookup. |
| Provider recovery snapshots | Capture coherent persisted state for recovery/export | Each foreground edit need not serialize the complete recovery inventory. |
| Database WAL/transaction log | Atomic commit and crash recovery | It is not a document version API or a portable export format. |

The main workload is many distinct users. For planning, 1,500 users saving every
nine seconds offer 10,000 saves/minute. Query and open actions add work. Ten
documents per user is a collection-size assumption, not a tenfold save multiplier.
Sizes and frequencies outside the synthetic fixture remain assumptions.

## Mechanisms replaced from the archived implementation

| Archived mechanism | Consequence | Replacement |
| --- | --- | --- |
| `DocumentState` contains current metadata, graphs, revisions, receipts, permissions and coordination | A whole-state transition can load and validate history for a small operation. | Explicit read projections and typed mutation contracts. No generic whole-history callback on ordinary requests. |
| `StageSnapshotAsync` serializes another complete aggregate | Repeated state bytes and work grow with retained metadata. | Append the new revision/change records and update the small current record. |
| `WriteSelectiveReadsAsync` reads and reconciles every revision summary | An existing projection does not make the save incremental. | Insert the new summary in the publication transaction; maintain counts transactionally. |
| `CurrentReadRows` performs two revision counts and a maximum lookup | Metadata requests still inspect historical indexes. | Read maintained current counters; perform full reconciliation during explicit integrity checks. |
| Every successful changed file save stores its materialized package | A small incoming edit causes another complete package object. | Treat the protocol graph as canonical and evaluate derived-package strategies separately. |
| Global publication/namespace gates and shared quota accounting | Unrelated documents share coordination. | Specific document/path constraints; separate any genuinely shared product policy. |

These observations describe archived `CellBridgeRequestProcessor`, `StorageContracts`,
`DocumentOperations`, `DocumentSelectiveReads` and `CellBridgeDocumentService`.
They do not imply that existing correctness checks can simply be deleted while
leaving their underlying invariants unimplemented.

## Data structures and algorithm alternatives

Let `a` be newly changed records, `delta` their bytes, `G` retained protocol
records, `H` application revisions, `K` encoded knowledge bytes, `q` its entry/range
count, `D` current package bytes and `R` returned bytes. The cost document's `G`
is the current stored catalog, a subset `Gcurrent` of this document's retained
records. The catalog's `G` denotes serialized selected graph bytes, not a count.
These quantities cannot be substituted without accounting for their units.
Complexity below excludes unavoidable
validation, selected graph traversal and serialization unless stated otherwise.

| Pairing | Strength | Hidden cost / strongest objection | Decision |
| --- | --- | --- | --- |
| Relational immutable elements/revisions, small current record, B-tree indexes and a current resolved-object projection | Uses database constraints and transactions; controls avoid history; ordinary inserts approach `O(a log G + log H)`. | Ordinary tables do not solve inherited visibility. Parent traversal can still be linear; full ancestor closure or copied membership can become quadratic. | Recommended foundation, subject to an explicit graph-lookup design below. |
| Immutable persistent ordered maps, copy changed tree paths and atomically publish roots | Old versions are directly addressable; changed metadata approaches `O(a log G)` without copying every membership list. | Tree-page writes, multiple index roots, cache misses, invariants and collection add a storage subsystem. A persistent hash map alone cannot answer ordered serial-range queries. | Serious comparator for graph indexes, not an automatic rewrite of every table. |
| Immutable change log, current SQL projections and bounded metadata checkpoints | Sequential change records; current reads can use projections instead of replay. | Index maintenance is still required. Checkpoint cost is roughly `O(S/C)` amortized for checkpoint size `S` and interval `C`; cold reads replay tails. A lagging background worker destroys a claimed bound. | Comparator for rarely read old revisions, with explicit foreground and cold-read costs. |
| Current JSON aggregate plus more caches, buckets or additional lock subdivisions | Small implementation delta | Retains the history-sensitive contract and hides costs behind cache warmth or contention management. | Reject as the replacement architecture. |

The recommended first pairing is ordinary SQL current bindings plus immutable
history and metadata checkpoints. It fits frequent latest-version operations and
rare historical reads with less recovery novelty than a custom persistent tree.
Persistent graph roots are the comparator if historical/base-chain measurements
justify them. A general append-log storage engine is the third choice. This is
a design recommendation to test, not a measured winner.

### Minimum logical records

| Record/index | Key and algorithm | Update scope |
| --- | --- | --- |
| Document control | Stable resource ID; lifecycle, current roots/versions, authority generation, deletion state and small counters | One document. Do not embed revision arrays or payloads. |
| Path claim | Unique normalized path mapped to resource/lifecycle, including required retired claims | Source/destination claims, enforced by unique indexes. |
| Grants, leases and editor presence | Document and principal/session; indexed expiry | Affected document and participants. Do not copy file history. |
| Protocol elements | Document/lifecycle/partition plus Extended GUID; immutable type, serial and payload handle | New identities only; verify reuse against stored immutable identity. |
| Mapping/index entries | Mapping key, target and its own mapping serial; current and historical visibility | Changed mappings, preserving untouched keys. |
| Revision manifests and object references | Explicit revision/base/object-group edges; current bindings keyed by document/lifecycle/target partition/cell/object Extended GUID/object-partition ID, selecting object-group version and payload/reference data | New revision and affected bindings, not all ancestors. The object-partition ID is distinct from the outer target partition. |
| Application revisions | Document/lifecycle/revision number, author, time and graph/package references | Append one publication record. |
| Save receipts | Document/lifecycle/actor/operation identity with digest, accepted version and response | One accepted effect; indexed replay lookup. |
| Payloads | Internal row/object ID and integrity digest; reuse where justified | New immutable bytes. Protocol identity remains separate from storage identity. |
| Export roots / external delivery | Consistent export inventory or required delivery intent | Explicit export or publication transaction, not every read. |

Using a numeric row ID and an indexed digest is valid. Using a digest as content
address is also valid. Neither choice alone prevents duplicate bytes inside
different package versions. Avoid cross-document deduplication as a mandatory
coordination dependency until its benefit is measured; independent document data
must remain independently publishable.

### Inherited object lookup is a required design choice

A bare `(revision, baseRevision)` table is insufficient. An object must resolve
in its declaring revision and explicit base chain, not in any old revision that
happens to contain the same ID. Copying every ancestor membership is also not a
solution. The prototype comparison must cover these two concrete pairings:

1. A current resolved-object SQL projection, immutable changes and metadata-only
   checkpoints for historical resolution. The current projection gives direct
   latest lookup. Application-version deltas record the effective difference from
   the previous published head, including removals/reversions on a branch switch.
   They cannot simply replay the raw incoming protocol delta. Bound both replay
   depth and replay bytes; reading a checkpoint still costs its map size. Protocol
   revisions never published as application heads need their separate explicit
   ancestry resolver and do not inherit this checkpoint bound. Restoring or
   switching to an old base can update `Theta(L)` current bindings, for `L`
   current bindings, even when the wire upload is small.
2. A resolved-object persistent ordered map root per revision, with shared
   unchanged branches. Updates copy changed paths; historical lookup follows its
   selected root rather than replay. Measure tree-page amplification and index
   maintenance, including adversarial branches and large object groups.

For example, head B changes X from x0 to x1. A new revision based on older A
changes only Y. Applying that upload to B incorrectly keeps x1 if the selected
base requires x0. Resolve against A first, then compute the effective publication
changes. This counterexample rules out an unqualified delta-only save claim.

An asynchronous checkpoint with no enforced backlog bound cannot be described as
bounded replay. A synchronous checkpoint must appear in save latency and write
cost, including worst-case costs. Do not advertise total delta-only saves from
the insertion cost alone.

For the first prototype, publish checkpoints synchronously when either the
configured change-count or accumulated effective-delta-byte threshold is reached.
Prepare and persist that checkpoint before taking the document guard, then
recheck its expected head at publication. Include checkpoint construction and
persistence in that save's latency. Numerical thresholds are experiment inputs
to select from measured cold-read and tail-latency costs, not protocol constants.
The current binding table accelerates only the selected current revision; it
cannot replace the immutable catalog or the resolver for unpublished bases.

### Query knowledge requires a separate access pattern

Index element and mapping serials by partition and serial namespace. Normalize
client ranges, seek candidate records, and apply the requested cell, revision,
type, hierarchy and knowledge rules. Indexed simple queries can approach
`O(K + q log q + q log G + selected records + R)`, including decoding and sorting
un-normalized ranges. Fragmented knowledge, inherited visibility
and whole-cell rounding may still require `O(G)` metadata work and large output.
A single high-water counter is not a substitute for complete knowledge semantics.
The current implementation skips or rejects the seven optional filter predicates;
adding indexes does not itself implement or advertise those filters. Preserve
their documented fallback/error behavior unless their scope is expanded explicitly.
Serial aliasing between multiple element IDs or between element and mapping
serials cannot prove payload possession. Keep conservative retransmission unless
a stronger, protocol-valid invariant establishes uniqueness; do not silently
discard this behavior when adding a serial index.

Full synchronization and complete history listing cannot be constant-time when
their required responses grow. Bounded streaming memory and elimination of
unrelated work are the goals for those operations.
In particular, a current file can still require old protocol base revisions.
History-independent metadata/current lookup means avoiding unrelated application
versions, not refusing to traverse data required by the selected protocol graph.

## Package bytes and validation are separate decisions

The existing save materializes a complete package, validates it, then stores it.
The observed roughly 304 KiB/save is net logical object/state growth in one
fixture. It is not physical I/O, nor a maximum or average Office edit size.

| Representation | Database writes per edit | Cost and objection |
| --- | --- | --- |
| Canonical protocol graph, derived package produced on demand and cached by immutable version | New graph payloads, references and receipt; no mandatory second full package in the database | Cold downloads resolve selected graph metadata and read its payload closure before producing `D` bytes. Historical reconstruction also reads its checkpoint and replays effective deltas. Those costs can exceed `D`. A cache must not become an unaccounted second canonical store. |
| Canonical graph plus deduplicated package manifests | New unique package chunks and changed manifest metadata, in addition to graph records | Preserves direct downloads but duplicates some representation and needs chunk boundaries, exact-byte reconstruction, integrity and collection. |
| Canonical graph plus a new whole package each save | Approximately `D` extra bytes before metadata and WAL | Keep as the control, not the target for small edits. |

Prefer the first as the simpler prototype. Compare the second if cold-download
latency makes it necessary. Existing protocol payload boundaries may already
provide reuse; do not automatically add a second content-defined chunking engine.
The [Venti snapshot work][venti] supports shared immutable blocks; [FastCDC][cdc]
studies the boundary-shift problem and CPU costs of content-defined chunking.
Neither establishes which package representation is fastest here.

Derived packages must reproduce the exact selected version's bytes, recorded
length, integrity digest and ETag behavior across process and schema upgrades.
Rebuilding a semantically equivalent ZIP with different order, timestamps or
compression does not satisfy this contract. Preserve a version-bound descriptor
that can reconstruct and verify those exact bytes when no full package is stored.

Preserve current full-package admission validation in the first prototype. It is
a CellBridge policy, not a general binary protocol requirement, but removing it
is a separate correctness decision. That prototype can reduce database writes
while still using `O(D)` materialization CPU and temporary-file I/O. Report scratch
bytes separately; do not claim that every full-file write has disappeared.
Incremental validation would need reusable validation evidence for unchanged
pieces and complete structural checks for changed ZIP/package relationships.
Measure it against full validation, with malformed packages, before adopting it.

## Publication and lock scope

Here, no application lock means no explicit advisory, process-wide or provider-wide
lock. Database writes still acquire database locks and update indexes. A conditional
update is not physically lock-free. [PostgreSQL][pg-isolation] and
[SQL Server][sql-isolation] have different isolation and retry behavior.

Use one row-level publication lock on the affected document as the initial
design. It makes authority, lease validity, mappings, receipt and the published
version one ordered decision for that document. An ordinary current-base append
keeps this critical section small. A branch switch or restore that changes `L`
current bindings can hold it for `O(L log L)` index maintenance; it is not
guaranteed short. Persistent root publication is the comparator if that matters.

Prepare, validate and persist new immutable payloads and any checkpoint
before taking the guard. Then begin a transaction, lock the document row, obtain
authoritative time, recheck authority/lifecycle/expected affected mappings, update
current bindings and atomically publish revision, receipt and required outbox
references. No acknowledged head may reference incompletely stored content.
Failed publication can leave unreachable staged objects for quiescent collection.
A head change is an internal concurrency event, not automatically a protocol
coherency error. Rebase/reprepare when necessary. Do not overwrite a concurrent
change with a package built from an obsolete root.

| Operation | Explicit coordination allowed in the proposed design | Coordination to remove from ordinary operation |
| --- | --- | --- |
| Identity/time/access/current metadata/status | Coherent current-record read, with authorization and existence precedence preserved | Whole-state mutation, file history scans and global accounting |
| File/metadata QueryChanges, current download and history read | Capture a coherent selected root and current authorization; read immutable data | Provider-wide publication lock and revision-array deserialization |
| Editors-partition QueryChanges | Pure read if no expiry is due; otherwise publish expired membership and new partition knowledge together under the affected document guard | File graph and user-history rewrite |
| Save, restore, permission or authority change | Affected document control row plus new/affected records and uniqueness constraints | Provider-wide publication gate, unrelated document rows and namespace inventory scan |
| Session/lease/editor mutation | Affected document coordination and participant rows | File payload and historical snapshot rewrites |
| Create/rename/delete/recreate | Affected document rows and source/destination path claims, acquired deterministically | Provider-wide namespace gate; missing-row races use insertion/unique constraints |
| Orphan collection | Initially genuine quiescent maintenance | No active-reader pin framework merely to support a hypothetical online collector |
| Export/import/recovery | Explicit coherent inventory and controlled activation | No ordinary save-wide export/maintenance barrier |

Restore may reuse historical payload bytes, but cannot simply reinstall historical
serial state or an old protocol root. Preserve current serial/identity rules and
create fresh affected wrappers/index mappings so clients knowing the pre-restore
state observe the new publication. Do not restore old permissions or leases.

Editor expiry deserves separate scrutiny. Computing whether a lease is active
from provider time need not rewrite file history. However, a visible EditorsTable
change needs new partition knowledge/serials. Merely filtering an expired row
can make an already-informed client miss its removal. Index the next editor
expiry for a pure-read fast path; when due, acquire the document guard, recheck
time and permissions, and publish membership/content identity/knowledge once.
Touch only editor records and affected partition metadata. Concurrent queries
must not allocate separate identities for the same expiry. Status/availability
operations also need classification by their actual transition semantics, not
their names. [Editor timeout][editor-timeout], [editor retrieval][editor-query].

CAS on document state can be evaluated later if the brief row-lock transaction
is a measured problem. It must compare the authority/lifecycle facts that matter,
not just content version. It must not repeatedly run arbitrary caller callbacks
or redo full validation for an unrelated editor heartbeat.

SQL Server currently uses serializable coherent reads. The proposed read design
uses an explicit snapshot transaction for multi-statement reads, requiring a
supported `ALLOW_SNAPSHOT_ISOLATION` setup/migration decision. Without it, do not
claim nonblocking coherent SQL Server reads. PostgreSQL uses repeatable-read for
multi-statement snapshots; a statement snapshot alone is insufficient. Writes
use targeted transactions and constraints, not blanket serializable scans.
InMemory and filesystem providers
must state their actual atomicity and multi-instance guarantees separately.

Quiescent collection means all service readers, writers, delivery workers and
exports are stopped/drained before deletion. Foreground publication gates must
not secretly remain as its implementation. If operational quiescence cannot be
established, collection stays disabled until an online lifetime design exists.
Process-level admission limits may still bound parallel resource consumption;
they are not a global mutual-exclusion rule for document correctness.

## The shared quota is a product policy, not a graph algorithm

The archived implementation used `MaxStoredBytes` and `MaxDocuments` for provider-wide limits. Neither
the operation catalog nor a requirement for safe same-document editing implies
those particular policies. Retaining an exact hard shared limit requires shared
coordination or reserved spending rights somewhere. Row-level locks cannot make
updates to one shared counter independent.

| Policy | Coordination implication | Decision needed |
| --- | --- | --- |
| Exact provider-wide hard cap | Coordinated admission or reservations persisted in shared storage; a shared dependency remains | Preserve only if this exact policy is required; disclose its cost. |
| Exact cap per genuinely independent workspace | Coordination within that workspace | A workspace is a product boundary, not automatically a user or server. |
| Global monitoring/soft admission threshold, bounded individual operations | No exact global counter in every save; overshoot becomes an explicit policy | Recommended for the minimal-lock design if a hard global cap is not required. This changes existing behavior. |

The selected policy removes provider-wide `MaxStoredBytes` and `MaxDocuments`
admission and their accounting. Do not add another quota framework, soft-cap
implementation or automatically growing buckets. Per-request/per-document limits
remain; resource exhaustion still causes an explicit storage failure. Historical
migrations remain intact, with new migrations retiring the accounting tables.
The alternatives above record why the removed policy had shared coordination;
they are not runtime modes to implement in this rewrite.

Document limits also cover content prepared by saves that fail to publish.
Before writing each batch, a save reserves its unique content handles and their
ownership metadata under the affected document row. Pending records remain
charged after interruption; successful publication marks referenced content
complete. Offline quiescent collection releases unused reservations. This adds
one document transaction per content batch, without coordinating unrelated
documents or scanning history. It does not restore provider-wide admission.

## Counterexamples that the recommendation must survive

| Challenge | Required answer before implementation is considered complete |
| --- | --- |
| Two saves touch different mappings while the document head changes | Preserve keywise protocol coherency. Head CAS failure alone cannot become a false wire conflict. |
| A permission or lease changes during preparation | Publication checks current authority and provider time under document coordination. |
| An editor expires after a client learned the EditorsTable | Publish correct changed knowledge/serials without rewriting file history. |
| An object ID exists in a sibling revision but not the selected base chain | Do not resolve it from a global history lookup. |
| Knowledge consists of many disjoint ranges or asks for whole-cell rounding | Charge range/selection/output work; do not claim constant queries. |
| A checkpoint worker falls behind or a revision branches from an old base | Enforce the promised replay bound or report foreground checkpoint/backpressure costs. |
| A small edit changes ZIP offsets or compression layout | Measure actual reuse and full validation work; the tail-edit fixture is insufficient. |
| A save commits but its HTTP response is lost | Stored receipt bound to actor, lifecycle and digest makes retry resolve the accepted outcome. |
| A collector runs while an old version is downloading | Use existing quiescence initially; online collection requires a separately designed publication/read lifetime rule. |
| External authorization or external file publication is enabled | Define fencing and accepted-save/outbox semantics explicitly; local row transactions alone do not bind an external service. |

## Evidence and implementation decision

The final earlier quota comparison reached 8,151 versus 7,974 strict saves/minute
in single overloaded runs. It did not meet 10,000/minute or establish a passing
capacity increase. The host also ran the generator and was near CPU saturation.
These results motivate investigation; they do not select a new storage layout.

Before a production replacement, compare complete implementations of the two
graph-lookup pairings, under the same control records and package strategy. Use
current and aged histories, branched bases, fragmented knowledge, different ZIP
layouts and document sizes, many identities, multiple hosts and cancellation.
Measure CPU stacks, allocations, canonical bytes, scratch writes, WAL, SQL rows
visited and written, retries, lock duration, warm/cold downloads and p95/p99.
Separate the generator or measure its resource consumption explicitly.

Acceptance is operational: small control requests do not load `H`/payloads;
ordinary saves do not reconcile every old summary; selected revision resolution
uses its bounded/indexed representation; canonical storage avoids a mandatory
whole-package copy; and ordinary document operations never explicitly lock an
unrelated document, namespace inventory or quota bucket pool. Account for
unavoidable graph validation and growing response output.
The selected quota policy above removes the shared admission dependency.

The bounded [publication state machine](../models/README.md) checks document
guards, current authority, keywise coherency, staging that survives a crash and atomic receipts.
Four safe configurations passed exhaustive TLC exploration; deliberately omitted
authority/root checks produced counterexamples. Its guide maps the abstract
transitions to transaction tests and states the assumptions. It does not prove
the C# implementation or online reclamation. Collection still requires explicit
quiescence; no throughput result follows from the model.

Beta permits replacing the whole-state callback and storage layout directly.
Update all repository callers and real migrations while preserving developer
data. Do not keep the expensive aggregate as a compatibility path on ordinary
operations. Recovery can reconstruct an aggregate explicitly off the hot path.

[model]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttpb/6c7e4447-6ccd-4764-8dbc-17a382fb631d
[venti]: https://www.usenix.org/legacy/publications/library/proceedings/fast02/quinlan/quinlan_html/index.html
[cdc]: https://www.usenix.org/conference/atc16/technical-sessions/presentation/xia
[pg-isolation]: https://www.postgresql.org/docs/18/transaction-iso.html
[sql-isolation]: https://learn.microsoft.com/en-us/sql/relational-databases/sql-server-transaction-locking-and-row-versioning-guide?view=sql-server-ver17
[tla]: https://lamport.azurewebsites.net/tla/high-level-view.html
[editor-timeout]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttp/8258f08c-787f-4e87-8831-5e8430e04fb0
[editor-query]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttp/830ffab6-06d8-4d5a-9128-23bca08a895e
