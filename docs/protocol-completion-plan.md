# Protocol completion plan

This plan separates missing behavior from optional optimizations and unverified
client scenarios. It covers CellBridge's MS-FSSHTTP/MS-FSSHTTPB scope and the
requested OneNote desktop open, edit and synchronization workflow. It is a plan,
not a claim that these features are implemented or that every protocol extension
has already been audited. The original inventory and phase order were reviewed against the code and
Microsoft specifications on 2026-10-05, with Astra reviewing the scope and order.
The implementation status below was reconciled with current source on 2026-10-08.
Phase descriptions retain the design gates; they are not an inventory of missing code.

The existing SOAP/MTOM transport, binary framing, durable providers, authorization,
knowledge handling and graph-aware file saves remain the foundation. The earlier
streamed durable saves, streamed MTOM responses, bounded content verification and
binary materialization improvements are already implemented. See the
[current capabilities](../README.md#features),
[protocol profiles](protocol-version-decision.md) and
[interoperability evidence](interoperability.md).

## Classification

| Class | Meaning | Completion evidence |
| --- | --- | --- |
| Missing behavior | A known operation or graph shape is rejected, absent or represented by a placeholder. | Normative rules, implemented behavior, reference fixtures and relevant integration tests. |
| Conditional requirement | Requiredness depends on the protocol version, advertised capability, negotiation or requested operation. | A documented profile decision followed by implementation and tests where applicable. |
| Valid fallback | The protocol permits the current full response or explicit unsupported result for this condition. | A cited fallback rule and a regression test; specialization is optional unless a target client needs it. |
| Operational feature | A new administration or storage lifecycle capability. | Provider contracts, failure/recovery tests and an operator-facing workflow. |
| Performance | Equivalent supported behavior with lower allocation, I/O, CPU or storage cost. | Measurements and unchanged wire/state behavior. |
| Unverified | The code may support the scenario, but adequate client or deployment evidence is absent. | A qualification run; any resulting defect becomes a separate missing-behavior item. |

An enum member does not establish an implemented operation. A parser test does
not establish desktop compatibility. Explicit rejection remains appropriate for
unknown requests and operations outside the selected profile.

## Implementation tracking

The [requirements ledger](protocol-requirements.md) records current field-level
status and profile decisions. GUID allocation, canonical version URLs, user-agent
identity, matching binary partition selectors and lock transitions now have
implementations and focused tests. The generic graph resolver now has durable
codecs and file save/scoped query integration. Complete opaque application metadata
graphs, recoverable revision history and selected outer document operations are
also implemented. General application adapters, staged modes, reference acceptance
and actual OneNote qualification remain.

The identifiers in this table are stable references for implementation work.
Acceptance criteria are expanded in the phases below.

| ID | Area and current evidence | Class | Improvement and relative effort |
| --- | --- | --- | --- |
| G1 | Generic cells, roots and object partitions have durable codecs, file adapter integration and complete opaque metadata publication. | Implemented foundation; reference/client qualification remains | OneNote-specific adapters and other application partitions remain #31/#27. |
| G2 | File and metadata graphs resolve inherited objects with revision/cell scope and nearest-definition precedence. | Implemented graph behavior; reference qualification remains | Required ancestors remain retained. Strict generic resolution preserves explicit errors for incomplete graphs. |
| G3 | File and metadata publication resolve declared BLOB IDs and preserve retained handles. | Implemented graph behavior; reference qualification remains | BLOB integrity and closure have restart/provider tests; no largest-payload recovery. |
| Q1 | Mixed/repeated file, metadata and editor queries share one package, preserving each scope/knowledge/error. Known binary targets override the SOAP default per operation. | Implemented query assembly; qualification remains | Filter specialization and reference qualification remain #23; metadata semantics #28 and historical profiles #7. |
| Q2 | Durable history supports authorized listing/download and graph-aware restore. The selected SharePoint 2010/2013 profile ignores bounded `QueryChangesVersioning` and returns current state. | Implemented history and valid profile fallback; qualification remains | Independent history/restore reference traffic and desktop qualification remain #7. Higher binary version-token profiles are unadvertised. |
| Q3 | Byte budgets needing continuation return errors; `AllowFragments` is parsed without implementing fragmented delivery. | Missing continuation capability | Bounded synchronization for large graphs. Substantial transaction/state work. Fragment permission alone does not require every response to fragment. |
| Q4 | `AllocateExtendedGuidRange` has typed codecs and authorized execution using fresh UUID namespaces. | Implemented operation | Exact count, exclusive upper bound and concurrent host decoding tests; no durable shared counter required. |
| Q5 | Negotiated schema-1 data-element hashing and excluded-data query responses are implemented. The selected product profile ignores the separate file-hash request. | Implemented negotiated facility and valid profile fallback | #26 is closed. Remaining advertised-field/reference audit belongs to #18; storage SHA-256 is separate. |
| W1 | Complete file and opaque metadata graphs publish atomically, including requested applied indexes and partition-scoped receipts. Partial/staged uploads, alternate coherency modes and buffered/coalescing metadata writes remain unsupported. | Implemented complete saves; missing synchronization modes | Durable staging needs an evidenced association/commit/retry contract under #27. `MultiRequestPutHint` alone does not define one. |
| M1 | Durable metadata queries return uploaded opaque graphs and scoped knowledge; complete noncoalescing metadata writes publish atomically. Uninitialized metadata and the legacy buffered executor retain the index-only response. | Implemented durable partition behavior; qualification remains | SharePoint application metadata reference traffic and client interpretation remain #28. See [metadata limits](application-metadata.md). |
| L1 | Exclusive/schema conversions and coauthor transition acknowledgement use durable explicit membership. | Implemented server transitions; qualification remains | Provider/HTTP tests establish state behavior. Reference traffic and two-desktop transition qualification remain in #29/#5. |
| S1 | Durable dispatch implements Rename, Versioning list/restore and Properties enumerate/get. Other suboperations return explicit unsupported errors. | Implemented bounded outer operations; applicability/reference gates remain | #30 owns remaining suboperation applicability and reference acceptance. See [revision history](revision-history.md#outer-document-operations). |
| N1 | OneNote schemas, graph publication strategy and a qualified native desktop workflow are absent. | New requested compatibility target | Desktop OneNote open/edit/sync, using G1–G3 and synchronization work. Large milestone; the format adapter is only part of the work. |

Evidence locations: [graph selection](../src/CellBridge.FssHttpB/PartitionGraphSnapshot.cs),
[object graph](../src/CellBridge.FssHttpB/ObjectGroupGraph.cs),
[binary request types](../src/CellBridge.FssHttpB/Enums.cs),
[buffered executor](../src/CellBridge.AspNetCore/CellBinaryRequestExecutor.cs),
[durable execution](../src/CellBridge.AspNetCore/CellBridgeDocumentService.cs),
[query selection](../src/CellBridge.AspNetCore/FileQueryResponseBuilder.cs),
[file save restrictions](../src/CellBridge.AspNetCore/FilePartitionSaveHandler.cs),
[response budgets](../src/CellBridge.AspNetCore/QueryChangesResponseShaper.cs),
[lock transitions](../src/CellBridge.AspNetCore/FssHttpLockCoordinator.cs),
[HTTP dispatch](../src/CellBridge.AspNetCore/CellBridgeEndpoints.cs) and
[version response](../src/CellBridge.AspNetCore/MetadataVersioningResponseBuilder.cs).

Microsoft specifies [outer subrequest types](https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttp/a3bb03aa-bbc6-4fab-96f4-4909fb2b813b),
[GUID allocation](https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttpb/2e79b02f-f9ab-4933-99d4-bc360c27f75d),
[Query Changes controls](https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttpb/5b8d1d29-0adf-4b29-b3d1-1a1fe8590642)
and [negotiated hashing rules](https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttpb/82458bac-fb0f-47d3-a919-9871defdc796).
Record MUST, SHOULD, MAY and product exceptions separately; a SHOULD-level
file-hash facility is not an unconditional failure in every supported profile.

## Other work and valid fallbacks

| Work | Classification | Expected gain |
| --- | --- | --- |
| Specialize currently unsupported query filters and knowledge forms where a full-response fallback is permitted. | Performance or optional compatibility; evaluate each control separately. | Less network traffic and client work. A full response can already be correct. |
| Load graph elements lazily; reduce eager restoration and payload reads. | Performance, independently of G1–G3. | Lower query latency and memory for large retained graphs. |
| Stream incoming HTTP/MTOM parsing and reduce nested serialization copies. | Performance. | Lower peak memory for large requests. Response/save streaming is already implemented. |
| Reduce remaining nested serialization and metadata-counting allocations. | Performance. | Query budget checks now measure element lengths without copying raw payloads. Current-profile admission and exact byte limits remain unchanged; #33 covers the remaining costs. |
| Enable automatic graph pruning/compaction. | Operational feature with storage-efficiency benefits. | Longer operation within quotas. It requires complete reference traversal and stale-client recovery; existing orphan collection is separate. |
| Qualify provider export/import and restoration on additional operator recovery procedures. | Operational qualification. | Public portability tooling is implemented and #32 is closed. See [provider portability](provider-portability.md). |
| Fuzzing and differential decoding. | Reliability and validation. | Find malformed-input and interpretation defects; neither is a performance feature. |
| Broader Word/Excel/PowerPoint restart/reconnect coverage and two distinct authenticated desktops editing together. | Unverified scenarios. | Authenticated single-desktop Word/Excel saves and reopen already have evidence; see [client coverage](interoperability.md#client-coverage). |
| Additional Office builds and deployment failure/power-loss qualification. | Unverified client/operational scenarios. | Establish explicit supported environments and recovery limits. |

## OneNote complexity and scope

OneNote reuses the transport and much of the synchronization/storage machinery.
Its content is an object graph rather than a ZIP file reconstructed from one
content root. [MS-ONESTORE defines separate storage schemas and header/data roots](https://learn.microsoft.com/en-us/openspecs/office_file_formats/ms-onestore/5d2c46d7-df4b-4ed3-ba27-49b7b34a31e2),
and [objects with metadata, data and file-data partitions](https://learn.microsoft.com/en-us/openspecs/office_file_formats/ms-onestore/b4270940-827e-468b-bf42-2c7afee23740).
The generic graph layer must preserve these identities and references. A format
adapter should decide how to validate and publish OneNote state, leaving the
Office ZIP adapter's validation intact.

The requested target is opaque online synchronization: OneNote interprets page
content, ink and equations; the server preserves and exchanges their graph data.
Physical `.one`/`.onetoc2` import/export is a separate revision-store file codec.
Semantic page inspection or browser preview is another feature layer and is not
a prerequisite for native desktop synchronization. Neither extra is included in
the current completion target.

The remaining work is a large milestone across graph representation, persistence,
query/upload execution and desktop qualification. It is not a small endpoint
addition. Once the shared graph and transaction work is complete, OneNote-specific
schema handling is a smaller addition, but actual client discovery remains a
schedule risk. A weeks-to-months planning allowance for one engineer is more
credible than a days estimate; this is a low-confidence size assessment, not a
delivery forecast. Phase 0 should replace it with estimates based on real traffic
and the resulting requirements ledger. Effort already needed for general graph
compatibility must not be counted again as solely OneNote cost.

[MS-OCPROTO's OneNote description](https://learn.microsoft.com/en-us/openspecs/office_protocols/ms-ocproto/58e3d77d-ec75-4f1c-b0d2-78f436fd2d40)
describes older clients and additional protocols as well as FSSHTTP. It does not
prove the discovery behavior of the desktop build we intend to support. Capture
that build before committing to a server workflow. If it requires unrelated
SharePoint APIs, record the incompatibility and an explicit scope decision.
[onenote.rs](https://github.com/msiemens/onenote.rs) is useful read-only parser
inspiration, not evidence of server write or synchronization behavior.

## Implementation phases

### Phase 0 — Requirements ledger and client discovery

Enumerate every outer operation/suboperation and binary operation, request flag,
extension object and partition selector against the advertised protocol profiles.
For each row record its normative source, version/product applicability, required
behavior or permitted fallback, code location, fixture, acceptance test and status.
Audit enum-only/raw/diagnostic operations rather than assuming they are required
because their names appear in code. Include hashing, version tokens, allocation,
dependencies, batching and coherency rules. Unknown applicability stays explicitly
unresolved until the specification or reference traffic resolves it.

Capture the selected OneNote desktop build's discovery/open/edit/reopen traffic
against a reference server, including section/notebook identity and authentication.
Define the supported build and hosting workflow. Keep raw traces in ignored
artifacts; retain only reviewed sanitized fixtures. The gate is a populated
requirements ledger and a bounded, evidenced OneNote workflow. This first phase
turns the known-gap inventory into an exhaustive audit for the selected profiles.

### Phase 1 — Generic graph representation

G1–G3 are implemented in the standalone graph library and persistent state. Qualify object
identity with its cell/revision scope and object partition. Resolve ancestor
revisions through the selected index, with explicit precedence and scope rules.
Support roots, cell references and BLOB references without interpreting arbitrary
object bytes as file-stream nodes. Keep byte reconstruction/ZIP validation in the
existing file adapter. Update persistence codecs and reference traversal together.

The gate is reference fixtures with multiple cells, repeated IDs across partitions,
inherited objects and BLOBs, plus deterministic results and malformed/missing
reference failures. Reject cycles where acyclic traversal is required, such as
revision-base chains and file reconstruction. Preserve permitted cycles among
opaque object/cell references and ensure traversal terminates within its budgets;
record permitted reference shapes in the ledger. Existing Word/Excel captures
must retain their behavior. Retained and caller-owned bytes must preserve current
ownership guarantees.

### Phase 2 — Query execution and allocation

Q1, Q4 and the applicable Q5 behavior are implemented. Qualify shared response packages
while preserving each query's scope, result, knowledge and errors. Add actual
cell/root selection and the required filter semantics; document permitted full
fallbacks. Allocate unique GUID ranges across hosts and restart without a process-local counter.
A fresh UUID namespace per request provides uniqueness without persistent allocator
state; the integer interval follows the specified exclusive upper bound. Implement wire hashing
only under the negotiated rules, including object ordering and excluded-data forms.

The gate is independent decoding of mixed/repeated queries, no over-advertised
knowledge, no collisions after concurrent allocation/restart, and correct hash
fixtures. Version queries wait for Phase 3; continuation waits for Phase 4.

### Phase 3 — Versions and outer document operations

The durable immutable history repository, graph-aware restore and selected S1
suboperations are implemented. Qualify them against independent reference traffic
and target clients. Historical binary version-token profiles need a separate
profile decision and actual wire structures; the current profile ignores the bounded
versioning extension. Do not reuse `Waterline` as a version token.
Audit any further S1 suboperations through Phase 0. Preserve stable resource
identity during supported file operations and check permissions on properties,
history and restoration. Restoration must create a coherent new publication.

The gate is version listing/query/restoration across restarts and hosts, captured
wire comparisons, correct resource resolution and no access to unauthorized
history. Existing snapshots must not be advertised as complete public versions
without satisfying the new contract. Provider codecs/migrations need explicit
backward-compatibility and upgrade tests.

### Phase 4 — Continuations and upload transactions

Complete file saves already accept `MultiRequestPutHint` as a coalescing hint.
It does not establish a staging key or a partial-upload transaction. True
`Partial` and `PartialLast` uploads still fail independently of other operations.

Implement Q3 and W1 on the generic graph. Define durable staging, fragment assembly,
expiry/abort, quotas and retry identities before accepting partial data. Follow the
specified coherency and batch transaction rules; do not turn every request batch
into a single transaction by assumption. Publish content, state, knowledge and
receipts only at the correct commit point. Recheck permissions and lock ownership
when publication occurs.

The gate includes interruption/restart, duplicate and out-of-order fragments,
malformed input, competing saves, stale coherency tokens, revoked access and
multiple hosts. Incomplete data must never become a published revision or claimed
knowledge. Explicit unsupported errors remain until each mode passes this gate.

### Phase 5 — Metadata and coordination

M1 complete durable metadata publication and L1 server lock transitions are
implemented. Qualify their application/transition semantics against reference
traffic and target clients. Metadata queries and writes must agree on graph state
and knowledge. Lock transitions must
be durable and respect owner, identity, expiry, permissions and concurrent hosts.
This work can proceed alongside earlier phases where its dependencies permit;
complete metadata writes use Phase 1 graphs; staged metadata modes also need Phase 4.

The gate is reference request/response traces, two-host transition races and
restart/expiry/revocation tests. Include the already implemented schema-to-exclusive
transition as a regression rather than describing it as missing.

### Phase 6 — OneNote desktop synchronization

Implement N1 using the Phase 0 client workflow and the shared graph/transaction
work. Add the OneNote schemas, root selection, content validation/publication and
necessary in-scope notebook/section identity handling. Use
[MS-ONESTORE's FSSHTTP encoding](https://learn.microsoft.com/en-us/openspecs/office_file_formats/ms-onestore/0ca8cbf7-85dd-4e34-b144-89d1ca16b5e2)
as the format authority. Preserve unsupported opaque content without adding a
semantic page renderer. Physical file import/export requires its own later scope.

The gate is desktop open, edit, server-verified save, restart and fresh-process
reopen, including text, pages/object spaces, images and attachments, offline
reconnection and the supported move/delete cases. Verify server graph/content
independently of client caches. Qualify concurrent desktops if coauthoring is
claimed. If Phase 0 exposes an out-of-scope discovery dependency, resolve that
scope decision before declaring this target achievable or complete.

### Phase 7 — Operational lifecycle

Provider export/import, migration and public history restoration are implemented.
Qualify additional recovery environments while preserving IDs, serials, metadata
and all referenced content. Enable automatic pruning only after retention traversal protects
active readers, continuations, ancestor/BLOB references, history, receipts and
recovery points. Establish stale-client full-resynchronization behavior first.

The gate is round trips across supported providers, interrupted migration/recovery,
stale clients, retained-version restoration and concurrent readers during pruning.
Finite quotas remain necessary after compaction. These features improve operation
and storage efficiency; they do not establish new client wire compatibility.

### Qualification throughout implementation

Each protocol change needs focused unit tests and independent decoding/reference
comparison. Add bounded fuzzing and differential tests around graph and transaction
boundaries. Use the repository's provider and two-host checks for durable changes.
Desktop gates require real Windows Office clients, correlated server requests,
published-state verification and a fresh process where specified.

Release qualification must cover remote Word/Excel/PowerPoint, the selected OneNote
workflow, distinct authenticated coauthors, reader restrictions and revoked writes.
Record client builds and deployment/recovery environments. Existing local package
checks and offline replay remain useful but cannot substitute for these scenarios.
Failures found here become ledger defects, not retroactive assumptions that the
whole feature was unimplemented.

## Separate performance queue

Measure peak allocations, I/O, CPU and latency on representative small/large files
and retained graphs. Then prioritize lazy loading, inbound streaming, nested copy
reduction and length counting from the measured cost. Preserve exact wire output,
ownership, quotas, validation and publication semantics. Performance work does not
block protocol completion unless measurements reveal exhaustion or another actual
correctness failure.

## Completion rule

Completion means every requirement applicable to the selected profiles and client
workflows has an implementation and passing evidence, or a cited allowed fallback.
Every ledger item must have a classification and a disposition; unresolved entries
prevent a blanket completion claim. Optional performance work, separate operational
features and excluded physical/semantic OneNote features retain their own status.
Update the README's capability claims only after the corresponding desktop gates
pass. This plan orders future work; no new protocol support is delivered by the
planning document itself.
