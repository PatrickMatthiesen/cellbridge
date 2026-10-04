# Protocol completion plan

This plan separates missing behavior from optional optimizations and unverified
client scenarios. It covers CellBridge's MS-FSSHTTP/MS-FSSHTTPB scope and the
requested OneNote desktop open, edit and synchronization workflow. It is a plan,
not a claim that these features are implemented or that every protocol extension
has already been audited. The inventory below was reviewed against the code and
Microsoft specifications on 2026-10-05, with Astra reviewing the scope and order.

The existing SOAP/MTOM transport, binary framing, durable providers, authorization,
knowledge handling and graph-aware file saves remain the foundation. The earlier
streamed durable saves, streamed MTOM responses, bounded content verification and
binary materialization improvements are already implemented. See the
[current capabilities](../README.md#status),
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

## Known implementation gaps

The identifiers in this table are stable references for implementation work.
Acceptance criteria are expanded in the phases below.

| ID | Area and current evidence | Class | Improvement and relative effort |
| --- | --- | --- | --- |
| G1 | `PartitionGraphSnapshot` uses one application schema/content root and selected cell; `ObjectGroupGraph` keys objects only by `ExGuid`, despite partition IDs in declarations. | Missing generic graph behavior | Multiple roots, cells and object partitions; essential for OneNote and broader graph compatibility. Large foundational change. |
| G2 | Base revisions are retained, but materialization uses explicitly selected groups without general ancestor object resolution. | Missing graph behavior | Resolve inherited objects with revision/cell scope and precedence. Substantial correctness work. |
| G3 | Object BLOB declarations/references are explicitly rejected; object bytes are interpreted using the file-stream materializer. | Missing graph behavior | Preserve opaque objects, cell references and separately stored BLOBs; keep Office ZIP reconstruction in its own adapter. Substantial graph/storage work. |
| Q1 | Both execution paths reject repeated `QueryChanges`; file queries reject unsupported cell/root/version controls. | Missing query behavior | Independently scoped results sharing a correctly assembled response package. Medium to large change after G1–G3. |
| Q2 | `QueryChangesVersioning` is rejected; `GetVersions` exposes the current entry with versioning disabled. Retained snapshots are not a public version repository. | Missing version/history capability | Real version tokens, version queries and coherent listing/restoration. Large storage and protocol change. |
| Q3 | Byte budgets needing continuation return errors; `AllowFragments` is parsed without implementing fragmented delivery. | Missing continuation capability | Bounded synchronization for large graphs. Substantial transaction/state work. Fragment permission alone does not require every response to fragment. |
| Q4 | `AllocateExtendedGuidRange` exists in the enum without its typed codec and executor. | Missing operation, profile applicability to record | Durable, unique GUID-range allocation across hosts. Medium change. |
| Q5 | File-hash requests are parsed without producing the requested hash; request/data-element hashing needs a profile audit. | Conditional requirement | Implement negotiated wire hashes with their specified ordering/schema. Storage SHA-256 is not a substitute. Medium change. |
| W1 | Partial and multi-request uploads, non-file uploads and alternate coherency modes are explicitly unsupported. | Missing synchronization modes | Durable staging and correct commit/retry semantics for incremental graph changes. Large change. |
| M1 | Metadata queries use a storage-index-only placeholder; application metadata writes are unsupported. | Missing partition behavior | Application metadata graphs and consistent read/write knowledge. Medium to large change. |
| L1 | Exclusive-lock `ConvertToSchema`/`ConvertToSchemaJoinCoauth`, and Coauth `ConvertToExclusive`/`MarkTransitionComplete`, return unsupported. Schema-lock `ConvertToExclusive` already exists. | Missing coordination transitions | Correct transitions between supported editing modes. Medium state-machine change. |
| S1 | Outer `FileOperation`, `Versioning` and `Properties` types exist but dispatch falls through to unsupported. | Missing outer operations, suboperations to inventory | Document management and property/version behavior within MS-FSSHTTP. Medium to large; exact scope follows the normative ledger. |
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
| Measure serialized lengths without serializing each element solely to count it. | Performance. | Lower allocation/CPU during response shaping. Preserve exact budget behavior. |
| Enable automatic graph pruning/compaction. | Operational feature with storage-efficiency benefits. | Longer operation within quotas. It requires complete reference traversal and stale-client recovery; existing orphan collection is separate. |
| Provider export/import, migration and public restoration tools. | Operational features. | Recovery and portability; not new binary framing support. |
| Fuzzing and differential decoding. | Reliability and validation. | Find malformed-input and interpretation defects; neither is a performance feature. |
| Remote PowerPoint, durable Excel requalification, authenticated fresh-process reopen and two distinct authenticated desktops. | Unverified scenarios. | Evidence for actual Office compatibility; do not assume every missing test is missing code. |
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

Implement G1–G3 in the standalone graph library and persistent state. Model object
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

Implement Q1, Q4 and the applicable Q5 behavior. Assemble shared response packages
while preserving each query's scope, result, knowledge and errors. Add actual
cell/root selection and the required filter semantics; document permitted full
fallbacks. Allocate unique GUID ranges durably across hosts. Implement wire hashing
only under the negotiated rules, including object ordering and excluded-data forms.

The gate is independent decoding of mixed/repeated queries, no over-advertised
knowledge, no collisions after concurrent allocation/restart, and correct hash
fixtures. Version queries wait for Phase 3; continuation waits for Phase 4.

### Phase 3 — Versions and outer document operations

Define a durable immutable version repository and its content/graph references,
retention limits and access checks. Implement Q2 using the actual version-query
wire structures; do not reuse the legacy `Waterline` field as a version token.
Implement the S1 suboperations established by Phase 0. Preserve stable resource
identity during supported file operations and check permissions on properties,
history and restoration. Restoration must create a coherent new publication.

The gate is version listing/query/restoration across restarts and hosts, captured
wire comparisons, correct resource resolution and no access to unauthorized
history. Existing snapshots must not be advertised as complete public versions
without satisfying the new contract. Provider codecs/migrations need explicit
backward-compatibility and upgrade tests.

### Phase 4 — Continuations and upload transactions

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

Implement M1 and L1 against reference partition/transition semantics. Metadata
queries and writes must agree on graph state and knowledge. Lock transitions must
be durable and respect owner, identity, expiry, permissions and concurrent hosts.
This work can proceed alongside earlier phases where its dependencies permit;
metadata writes depend on Phases 1 and 4.

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

Add provider export/import and migration workflows that preserve IDs, serials,
metadata and all referenced content. Add public restoration tooling on Phase 3's
version contract. Enable automatic pruning only after retention traversal protects
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
