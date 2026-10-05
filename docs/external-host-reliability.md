# External host reliability

CellBridge can retain accepted revisions for ordered delivery into another host's
file store. This is an opt-in integration contract. A host must implement durable
compare-and-swap at the destination and use the same CellBridge provider for both
WOPI and FSSHTTP write authority. `OpenWriteAsync` without a revision precondition
cannot satisfy the contract.

The contracts implement the server failure boundaries in issues
[#44](https://github.com/PatrickMatthiesen/cellbridge/issues/44),
[#45](https://github.com/PatrickMatthiesen/cellbridge/issues/45) and
[#46](https://github.com/PatrickMatthiesen/cellbridge/issues/46).
Synthetic tests establish those boundaries. Reference traffic, distinct desktop
coauthoring and Office Online Server qualification remain separate gates. No
WOPI HTTP routes or upstream WopiHost changes are included.

## Ordered delivery

After importing and verifying the initial external file, call
`ExternalRevisionPublisher.BindAsync` with its resource ID, lifecycle generation,
state version, a new durable binding GUID, the destination ID and its current
opaque revision token. The binding is immutable. Binding to a baseline asserts
that the imported package corresponds to that external revision. The host must
establish that correspondence before binding.

A changed file save appends an `ExternalRevision` inside the same provider
transition as its content and receipt. It records the exact immutable content
handle, content version, resource generation, delivery sequence and operation
GUID. Replay and accepted no-ops do not append. Metadata-only graph commits do
not change the external file bytes and do not append.

`ExternalRevisionPublisher.PublishNextAsync` delivers one queue head. A recovery
worker can periodically page through `IDocumentStateStore.ListAsync`, load each
state and call the publisher for bound documents. Start another pass from offset
zero after reaching the end. Offset pagination provides eventual discovery in
ordinary workloads; it does not guarantee fairness under sustained path churn.
Polling must resume after restart. An `AcceptedSave` notification may wake the
worker, but request completion is not a delivery acknowledgement.

The publisher verifies the whole immutable file into temporary disk storage
before calling `IExternalRevisionDestination.CompareExchangeAsync`. External I/O
runs outside the state transition. Calls receive a 30-second cancellation budget.
The destination must honor cancellation and its own I/O deadlines. Cancellation,
network failure and an uncertain remote outcome retain the original queue entry.
The next attempt uses the same operation identity and expected revision.

The destination implementation must atomically persist:

- File bytes and a new external revision token.
- A receipt keyed by durable binding and operation identity, with the complete
  request fingerprint including resource, generation, sequence, content hash,
  length and expected external revision.
- The resulting revision token needed to recover a lost acknowledgement.

Checking an ETag before an unconditional write is insufficient. The comparison,
write and receipt must be one atomic destination operation. Other writers must
advance the same revision token. Tokens must never be reused after remote edits,
deletion or recreation. Content hash ETags alone allow an ABA race.

A duplicate operation returns its original receipt without writing bytes again,
even after another writer advances the destination. Reusing the operation ID
with a different fingerprint must fail. A destination that compacts old receipts
must retain deduplication protection and return `Stale` for compacted operations.
An unresolved operation must retain its resulting revision receipt. Deduplication
protection survives file deletion, local recreation and binding changes.

Two publishers can submit the same head. Destination CAS and durable receipts
make that safe. CellBridge acknowledges only the exact matching current head.
Delayed acknowledgements and failure reports cannot remove or block another
head. The next entry uses the acknowledged resulting external revision as its
precondition. A non-CellBridge remote edit produces an explicit conflict and
preserves the newer external bytes.

Conflicts and missing or corrupt content block the queue with a bounded reason.
`RetryAsync` requires the binding and current head operation ID. It clears the
block and retries the same bytes and precondition. It does not adopt a newer
remote revision or discard an undelivered save. Remote conflicts require host
reconciliation outside this API. Network failures remain retryable without
blocking. Workers should apply a bounded delay/backoff between attempts.

Pending entries are roots in `StorageReferences.Handles`, even when their
original state snapshots have aged out. `MaxPendingExternalRevisions`,
`MaxPendingExternalBytes` and the existing per-document/provider budgets bound
retention. A full queue rejects the next changed save atomically; it never drops
an accepted pending revision. Quiescent orphan collection still requires all
readers, publishers and preparation work to be stopped.

## Shared write authority

`SharedDocumentLocks` implements the base WOPI exclusive lock shape in the same
provider transitions that store FSSHTTP schema and exclusive leases. WOPI tokens
are opaque and case-sensitive. Subject identity owns the token. Acquiring,
refreshing, releasing or replacing a token runs under provider coordination;
expiry uses provider time obtained after acquiring that coordination. The base
lock expires after 30 minutes unless refreshed.

An active WOPI lock blocks FSSHTTP acquisition, conversion and saves. Active
FSSHTTP schema or exclusive leases block WOPI acquisition. FSSHTTP LockStatus
reports an external lock as exclusive without exposing its opaque token.
Sharing a token or subject does not allow one protocol to impersonate the other.
WOPI extended/shared locks and cross-protocol lock conversion are unsupported.
Existing FSSHTTP schema/exclusive conversions continue within their own domain.

A host stages immutable package and graph handles before committing a write.
`TryCommitAsync` requires the `HostWriteToken` returned by acquisition or refresh,
the prepared state's version, an actor, a bounded deterministic state callback,
and a stable operation GUID. Inside the provider transaction it rechecks current
permission, subject, token, lifecycle generation, coordination generation and
expiry, then commits the prepared state and journal entry together. The callback
must preserve independent current state fields and publish a coherent graph.
It must perform no external I/O. Delivery follows through the same journal.
Direct WOPI destination writes bypassing these rules are outside the guarantee.

`CoordinationFencing.Capture` advances the authority epoch only when coordination
state changes. Content and graph publication alone do not advance it. Refresh,
release, conversion, revocation and lease expiry fence prepared writes. An FSSHTTP
save preserves its initial fence across preparation retries and lost commit
acknowledgements. A changed fence rejects new publication. Receipt replay
revalidates the current receipt and access but performs no new publication.
An explicit expired FSSHTTP lock ID cannot turn into an unlocked save.

The normative foundations are [MS-OCPROTO web file access](https://learn.microsoft.com/en-us/openspecs/office_protocols/ms-ocproto/2d268a23-420b-4339-8c56-9e4f8a924171),
[MS-FSSHTTP common lock and expiry rules](https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttp/918c3440-7d05-4c3a-8d51-0bad627bdb23),
[exclusive acquisition](https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttp/72cbdece-1eb4-45cc-a937-d1a9283ce20e),
[MS-FSSHTTPB committed change processing](https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttpb/94a442bc-ce2f-4f25-a245-82976b0b063c)
and [MS-WOPI base lock duration](https://learn.microsoft.com/en-us/openspecs/office_protocols/ms-wopi/e46e1f59-5781-4a35-8d8d-9df253b7c58a).
The outbox, lifecycle generation and destination CAS are host reliability
contracts, not additional protocol wire fields.

## Deletion, recreation and local eviction

`IDocumentLifecycleStore` is an optional additive provider capability implemented
by the memory and PostgreSQL state stores. Custom beta providers can keep their
existing interface; hosts must require this capability when they need deletion.

`TryDeleteAsync` requires the expected lifecycle generation and state version.
It atomically publishes a tombstone. A duplicate delete using the original
precondition succeeds. Stale preconditions conflict. Pending external delivery
blocks deletion so it cannot race an unacknowledged remote write. Deletion does
not delete the external file. An external host must coordinate its own conditional
file deletion separately and retain its destination deduplication records.

Normal lookup, listing and transitions hide or reject tombstones. The retired
resource GUID, path, graph, content, sessions, leases and receipts remain in the
tombstone for recovery and reference closure. They no longer grant active access.
Tombstones consume quota. Detached readers may finish against their immutable
content. No online graph/content reclamation is introduced.

Ordinary creation never reuses a retired GUID or path. `TryRecreateAsync` is an
explicit conditional provider operation. Prepare a clean `DocumentState` with a
fresh resource GUID, the same canonical path, generation exactly one greater,
empty coordination/sessions/receipts/retired-path reservations and no external publication binding. Supply
the retired tombstone's expected generation and state version. The provider
atomically records its `ReplacedBy` GUID and creates the replacement. Retry an
uncertain outcome with the same proposed replacement GUID. Another proposed
GUID conflicts. The old GUID remains permanently retired.

Provider-owned `RetiredPathKeys` survive deletion and recreation on the old
tombstone. Creation cannot claim these aliases, and recreation rejects foreign
retired aliases. Repeated recreation at the exact canonical path remains valid
when earlier generations have tombstones at that path. PostgreSQL namespace
writers acquire the shared transaction advisory guard before path or document
row locks. Reservation checks read current snapshots after the budget guard.
The separately owned rename implementation must use that same namespace guard.

The host may map the same opaque external file ID to the new protocol resource.
The new GUID prevents reuse of old ETags and receipts. SOAP Cell subrequests and
outer mutations for a recreated path require `UseResourceID` with that current
GUID. URL-only mutations are rejected; ordinary download and read-only metadata
discovery can still resolve the path. This closes the delayed URL-only request
race that a new GUID alone would leave open. A new destination binding requires
a newly verified baseline; the destination retains old deduplication protection.

`DocumentStore.TryEvict` affects only the compatibility context's local cache.
It requires the exact cached object and expected content version. A replaced
attachment or newer content prevents stale eviction. It does not change durable
state, delete external bytes or invalidate detached readers. The reusable durable
service reads provider snapshots and has no process-wide document cache to evict.

## Storage compatibility and validation

Document JSON remains format 2 with additive defaulted properties. Public beta
constructors and `IDocumentStateStore` remain compatible. PostgreSQL schema 4 adds
the tombstone flag and live-path uniqueness. Explicit `InitializeAsync` migrates
schema 3 to 4 in a transaction while retaining documents/content/ownership. Stop
all hosts against that store before migration. Schema-3 writers fail closed after
the upgrade; mixed schema-3/schema-4 hosting is unsupported. Older schemas remain
unsupported. This migration is not run against a shared host by this work.

`ExternalPublicationTests`, `SharedHostLockTests` and `HostLifecycleTests` cover
lost acknowledgements, competing publishers/protocols, delayed failure reports,
remote changes, missing content, quotas, retained content pins, fenced commits,
expiry/release/conversion, deletion races and recreation. PostgreSQL cases use two
independent provider connections and a transactional destination receipt store.
The full isolated runner also exercises existing process crash tests and the two
HTTP hosts. These are server tests, not Office client compatibility evidence.
