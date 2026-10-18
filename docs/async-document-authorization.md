# Async host document authorization

The optional `IDocumentAuthorizationProvider` lets a host evaluate its own permissions asynchronously. It can query EF Identity metadata, PostgreSQL, another store, or a remote policy service. CellBridge chooses the action and enforces the result. Documents and Lists keep separate contracts.

Ordinary external authority admits an operation using a small immutable decision. A later revocation does not cancel already admitted work. Each later operation authorizes again. No permission writer callback, permission replica, all-member snapshot, or revision publication is required.

## Minimal setup

Register a singleton provider. A provider using EF should create and dispose a context per evaluation through `IDbContextFactory`, rather than capture a scoped context in the singleton. Materialize the decision and dispose any reader before returning.

```csharp
sealed class HostPermissions(IHostPermissionStore store) : IDocumentAuthorizationProvider
{
    public string PolicyDomain => "my-app:documents";

    public async ValueTask<DocumentAuthorizationDecision> AuthorizeAsync(
        DocumentAuthorizationRequest request, CancellationToken cancellationToken = default)
    {
        var allowed = await store.CheckAsync(request.Subject, request.Scope,
            request.ResourceId, request.Action, cancellationToken);
        return new(allowed);
    }
}

services.AddCellBridgeWithExternalAuthorization<HostPermissions>(storageProvider);
```

For a host owning its routes, construct `CellBridgeDocumentService` with the named `documentAuthorizationProvider` argument. `CellBridgeRequestProcessor.ExecuteAsync` uses the same authority without needing the Lists module. Public `CellBridgeActor.AccessLimit` remains a restrictive ceiling; it cannot grant access. Stable subjects come from trusted server authentication, never from a permission result supplied in an HTTP body.

Use `CreateExternalAsync` with a host-selected resource GUID and `DocumentAuthorizationScope` for ordinary creation. It requires both the actor's explicit `CanCreate` host capability and the provider's `Create` decision. `ImportExternalAsync` is a trusted import operation requiring `CanCreate`, not a user-level bypass for upload routes. The host supplies scope through trusted resource registration. Document paths do not determine tenant membership. Moving a path keeps the existing scope; changing authority scope requires explicit binding migration.

The runnable [HostAuthorizedDocuments example](../examples/HostAuthorizedDocuments/README.md) uses a real async PostgreSQL permission query, direct protocol processing, shared host locks, and whole-file saves. It has no dependency on ShareBridge or WopiHost.

## Operation boundaries

Provider I/O runs before state transaction locks. The engine then retains an internal admission for the trusted subject, resource, lifecycle generation, exact binding and required action. Applying it still checks deletion, binding/lifecycle identity and the existing state-version and lock fences. An admission cannot be constructed through the public actor API.

| Operation | Authorization boundary |
| --- | --- |
| History read, resolve, download or catalog candidate | A read evaluation for that operation/resource |
| Whole-file save or history restore | One write evaluation before preparation, reused through commit and uncertain-commit receipt resolution |
| Independently called host lock operation or `TryCommitAsync` | A new write evaluation |
| Outer SOAP metadata, editor or lock subrequest | One evaluation for that subrequest, reused by its nested helpers |
| Binary Cell request | A separate evaluation for each Cell suboperation; `QueryAccess` evaluates read and write capabilities |
| SOAP file envelope | A read evaluation to decide whether canonical identity may be disclosed |

The SOAP envelope check is separate from its suboperations. Their decisions may differ if policy changes while processing the request. A retry in a new API call authorizes again, including a durable save-receipt replay. Synchronous `Access(actor, state)` remains available for native and versioned consumers; it returns `None` for an ordinary external actor without an engine-created admission. Use `GetAccessAsync` to obtain fresh read/write capabilities.

Native stored permissions and version-1 `ICellBridgeAuthorizationPolicy` retain their current behavior. Their updates reconcile recorded lease owners and fence prepared writes. Ordinary external permission changes intentionally have different completion semantics. They do not scan other subjects or mutate existing leases. A revoked owner must authorize its next save or refresh; its old lease can continue blocking another editor until lease expiry or trusted cleanup.

There are two separate delays. A cached permission can remain usable for its authorization TTL. A retained lease can block an editor until its own lease expiry, which can be much longer. The permission TTL does not bound lock blockage. Editors-table flags describe recorded session state in this mode and are not permission grants.

Accepted revisions in the external publication outbox remain accepted work. Background delivery uses destination concurrency/idempotency contracts and does not impersonate the original writer or reauthorize a human user.

## Optional caching

Caching is off unless the host constructs `CachedDocumentAuthorizationProvider`. Keep it separate from the minimal provider setup.

```csharp
var cache = new CachedDocumentAuthorizationProvider(hostPermissions,
    new DocumentAuthorizationCacheOptions {
        Capacity = 4096,
        DecisionTtl = TimeSpan.FromSeconds(10)
    });
var service = new CellBridgeDocumentService(storageProvider,
    documentAuthorizationProvider: cache);

// Optional, after a host permission transaction commits.
cache.InvalidateSubject(subject);
cache.InvalidateResource(resourceId);
cache.InvalidateWorkspace(workspaceId);
```

Invalidation discards matching cached decisions and supersedes matching evaluations still pending admission. The cache checks invalidation state and admits a result under the same short lock used by invalidation. An invalidated pending result is reevaluated, with a bounded retry count; continuous invalidation fails closed. Already admitted work may finish. Capacity eviction alone does not revoke an operation. Supporting pending-entry and invalidation metadata are bounded too.

Legitimate denies are cached too. A newly granted permission can therefore wait for expiry or invalidation before a later operation sees it.

The implementation uses a bounded LRU and marks only matching resident entries as invalidated. It does not retain an object or tombstone for every resource/user ever seen. When capacity evicts an entry whose provider evaluation is still running, a later invalidation conservatively supersedes that evaluation, even if unrelated. A single generation counter preserves this guarantee without retaining evicted identities. The decorator is process-local. A host may broadcast invalidation notifications to its instances or accept TTL expiry on other instances. There is no bundled distributed notification or strong revocation coordinator.

Expiry is the earliest of evaluation-call start plus TTL, the provider's original `EvaluatedAtUtc` plus TTL, and its `FreshUntilUtc`. Omitted evaluation time asserts evaluation during this call and uses call start. Providers reusing their own cached data must preserve its original evaluation time and source freshness deadline. Errors and timeouts do not become cached allows. Expired allows are never served while refreshing.

Metadata uses UTC. Future evaluation times, inconsistent deadlines and freshness already expired at admission fail closed, with zero positive clock-skew tolerance. Source timestamps are authority assertions, not proof of synchronized clocks or fresh data. Hosts must account for their authority's clock uncertainty in the metadata they supply. Local TTL elapsed time uses `TimeProvider` monotonic timestamps; backwards wall-clock movement cannot extend it. A forward wall-clock jump can cause an earlier deny or refresh.

## Binding migration and compatibility

Version-2 `DocumentAuthorizationBinding` identifies ordinary external mode. It persists the domain, scope and configuration revision, not permissions. Changing a host permission needs no binding update. Native documents remain unbound; version-1 documents retain exact snapshot/CAS semantics. Selecting ordinary external mode does not silently take over either kind.

To migrate, stop overlapping older writers, configure the new provider, and call trusted `BindExternalAuthorizationAsync` with the expected state version and binding. CAS failure makes no change. Successful migration clears sessions and leases and advances the coordination generation, so staged writes cannot cross the binding change. Content, history and receipts remain intact. Reconnect editing clients afterward. Old engines reject version-2 binding rather than falling back to owner grants, but mixed writer versions are unsupported.

The provider domain and scope are durable identifiers. Changing scope/domain is another explicit trusted migration, not a permission edit. Take backups before migrating production state. Portable recovery currently rejects external bindings; this revision does not add coordinated backups of a host authority or widen the existing recovery contract.

An external authority in the same PostgreSQL database still uses separate transactions from CellBridge. That supports ordinary admitted-work semantics; it does not create atomic cross-module revocation. No multi-process instant-revocation guarantee is implied by the choice of database.

## Consumer measurements and storage limits

The initial hot-resource trial used one changed save followed by 99 no-op
receipts. It demonstrated fewer authority queries with caching, but did not test
repeated editing. The [document example](../examples/HostAuthorizedDocuments/README.md)
now performs changing saves, verifies saved and historical bytes, and mixes 512
history reads across 64 resources with eight concurrent callers. It also checks
creation, reader/writer separation, scope isolation, grants, revocation, receipt
replay and retained locks.

The first correction stopped newly published indexes from copying revision
mappings from every retained index. Publication takes the selected index's
mappings and fresh wrappers while retaining historical elements, identity checks
and serial watermarks. That reduced 100-save state JSON from 37.3 MB to 16.4 MB.
The remaining duplication was metadata: each revision repeated cumulative graph
descriptors. Raw file and graph payloads already used content handles.

Storage encoding 1 now stores immutable descriptors once per state. Each current
or historical partition retains its exact ordered membership through direct
ranges into that table. Decoding shares descriptor objects across revisions;
it does not remove required history. Capture also reuses unchanged descriptors.
Quota checks count encoded UTF-8 bytes through a bounded buffer and weakly
memoize that count for immutable states. The codec reuses serializer type
metadata per worker thread and clears operation graph references in `finally`.
PostgreSQL schema 6 requires an [explicit migration with older writers stopped](storage-providers.md#upgrading-a-durable-host).
Logical document format 2 and native/version-1/version-2 authorization semantics
are unchanged. Async authorization still revalidates current state after awaiting
the host decision.

Current metadata, catalog rows and HTTP downloads now use the optional
`IDocumentReadStore` capability with the built-in native or ordinary async authority.
`ListRevisionSummariesAsync` returns live newest-first pages of at most 256 headers;
`GetRevisionSummaryAsync` returns one selected publication's content handle. Neither
returns partition graphs. The database returns the requested summaries with the
current authorization/lifecycle fence in one statement after the host decision.
A content handle is an identifier; later download requests authorize again.

PostgreSQL schema 6 adds transactional current-metadata and revision-summary tables.
Upgrading schema 5 builds these indexes from current heads without rewriting snapshot
JSON. It preserves current history membership even when older retained snapshots
contain additional revisions. In-memory stores project the same records under their
state lock. Existing providers and arbitrary full-state policy callbacks keep their
complete-state fallback. The original graph-returning history APIs remain available;
when an external authorization check confirms the same immutable state version,
they reuse the decoded state instead of reading its JSON a second time. A changed
version triggers a complete current-state read. Saves, restores and protocol graph
operations retain their complete state and identity requirements.

The following codec measurements compare the expanded schema-4 snapshots with
compact schema-5 snapshots, before selective reads.

On 2026-10-17, equivalent Release workloads ran sequentially against isolated
Aspire-owned PostgreSQL 18.3, without concurrent builds or other test work. Each
10/20/40/100-save workload ran with in-memory and PostgreSQL document stores,
each cached and uncached. All 16 before and 16 after trials passed the same 11
behavior/history checks. Host permissions used PostgreSQL in every mode; the
optional cache had capacity 32 and a ten-second TTL. Each trial used a fresh
database. The package size was approximately 834 bytes.

The uncached durable trials measured:

| Changed saves | Current state JSON before | Encoded state after | Total managed allocation before | Allocation after |
| ---: | ---: | ---: | ---: | ---: |
| 10 | 259,277 B | 68,353 B | 1.094 GB | 0.540 GB |
| 20 | 817,546 B | 127,522 B | 2.861 GB | 0.831 GB |
| 40 | 2,864,084 B | 245,861 B | 9.613 GB | 1.590 GB |
| 100 | 16,443,721 B | 600,890 B | 60.276 GB | 5.449 GB |

At 100 saves, all 101 revisions remained readable. The 64 retained state snapshots
occupied 540.92 MB of logical JSON before and 26.53 MB after. Physical bytes for
the fresh database's public tables and indexes fell from 197.58 MB to 8.99 MB;
this includes other fixture documents, TOAST, indexes and free space, and excludes
WAL and system catalogs. Total elapsed time fell from 88.37 to 26.33 seconds.
These are single trials, not throughput guarantees.

Observed peak process RSS fell from 2,167,748 KiB to 288,332 KiB in the uncached
durable trial, and from 2,157,436 KiB to 325,012 KiB with caching. RSS was sampled
every 50 ms and measures the whole process. An optional diagnostic full GC ran
only after workload operations. With the service, stores, current state, packages
and expanded denial-check JSON still owned, the uncached live managed heap fell
from 1,417.25 MB to 89.22 MB. In-memory 100-save allocation fell from 2.866 GB to
0.546 GB uncached and from 2.884 GB to 0.539 GB cached. Default public-record JSON
still expands graph descriptors; the example reports that size separately from
encoded storage bytes.

Larger synthetic DOCX trials added one MiB of deterministic varied XML text,
producing a 636,979-byte package. Both stores passed 20 changed saves and the
same checks. Uncached durable allocation fell from 5.001 GB to 2.989 GB, while
peak RSS fell from 540,716 KiB to 468,944 KiB. Content dominated physical database
size, which changed from 116.88 MB to 114.86 MB. In three paired in-memory trials,
allocation fell slightly, from about 2.624 GB to 2.596 GB, and post-GC live heap
fell from 132.91 to 135.01 MB to 130.65 to 132.20 MB. Peak RSS did not improve: its mean
rose from 428,599 KiB to 488,841 KiB, with overlapping ranges. These measurements
do not identify the cause. Large-content buffer and process-memory behavior
remains a limit requiring separate investigation.

Raw JSON, process metrics, source/binary hashes, executable snapshots and runnable
drivers remain in ignored `artifacts/durable-history-memory/`. The example's
`--saves`, `--payload-bytes` and `--measure-gc` switches reproduce the bounded
workloads. No collection occurs as part of the storage fix.

The codec fixes descriptor duplication in stored state for these workloads. Full graph operations
still read complete compact states and retain the configured full snapshots. The
selective metadata paths described below avoid that work for catalog pages,
revision summaries and HTTP downloads.
Detached revisions still have arrays of shared descriptor references, and graph
processing and payload buffers still allocate. Portable recovery manifests keep
their existing expanded v1 representation and size limits. These results do not
establish bounded runtime-pool growth, rule out every possible leak, or qualify
live Office/OOS editing, external publishing or hardware-failure recovery.

### Selective read measurements

On 2026-10-18, matched workloads compared compact schema-5 snapshots with the
schema-6 selective reads. The optional `--read-metrics` workload adds 200 authorized
catalog pages of 16 rows, 512 mixed revision-summary pages of eight headers, and
512 selected historical download-metadata reads. The two history groups run at
concurrency eight. A reflection measurement adapter projects the older full-history
APIs to the same fields; its allocations remain included. Each trial checks page
identities, ordering, continuation offsets, selected historical bytes and revocation.

All 18 matched pairs passed the same 17 checks: 10/20/40/100 changed saves with
both stores and both cache modes, plus 20-save larger-content trials for both stores.
Each trial used a fresh database on the same owned PostgreSQL 18.3 instance, with
no concurrent builds or tests. The uncached PostgreSQL scaling results were:

| Changed saves | Whole-workload allocation before | After | Selected-metadata allocation before | After |
| ---: | ---: | ---: | ---: | ---: |
| 10 | 2.855 GB | 0.898 GB | 400.90 MB | 20.70 MB |
| 20 | 4.038 GB | 1.072 GB | 826.00 MB | 20.70 MB |
| 40 | 7.069 GB | 1.554 GB | 1,898.46 MB | 20.71 MB |
| 100 | 21.084 GB | 4.200 GB | 6,688.91 MB | 20.73 MB |

Selected metadata remains approximately constant in this bounded workload while
all 101 revisions remain available. The 100-save uncached elapsed time fell from
46.00 to 33.86 seconds. Peak RSS changed from 319,388 to 313,984 KiB; post-GC live
heap changed from 122.18 to 122.77 MB. Lower allocation did not produce a matching
reduction in retained heap or RSS in this trial.

The 100-save cached PostgreSQL pair measured:

| Operation | Managed allocation before | After | Elapsed before | After |
| --- | ---: | ---: | ---: | ---: |
| 200 authorized catalog pages | 5,334.82 MB | 448.10 MB | 12,745.88 ms | 7,622.25 ms |
| 512 mixed revision-summary pages | 3,422.94 MB | 29.58 MB | 2,187.71 ms | 178.36 ms |
| 512 selected historical metadata reads | 6,690.40 MB | 19.02 MB | 4,092.19 ms | 148.54 ms |
| 100 changing saves and readbacks | 1,445.91 MB | 1,450.39 MB | 19,104.88 ms | 19,090.34 ms |

Authority-provider calls matched in these phases: 7,300 for catalog pages, 248 for
mixed summary pages, zero for the cached selected-metadata group, and two for saves.
Whole-workload allocation fell from 21.09 GB to 4.17 GB and elapsed time from 45.29
to 32.90 seconds. Peak sampled RSS fell from 320,360 to 274,560 KiB. Post-GC live
managed heap with the reported owners retained fell from 124.82 to 90.87 MB.

Current state JSON stayed approximately 600.9 KB and all 64 retained snapshots
approximately 26.53 MB. Across the full 65-resource fixture, the read tables held
65 current heads and 165 revision summaries: 145,674 logical JSON bytes and 311,296
physical relation/index bytes. Total public relation bytes rose from 9,003,008 to
9,314,304. Full-workload WAL rose from 9,642,608 to 10,273,768 bytes. The WAL delta
covers the whole owned server during schema setup, document and host fixtures,
permission changes and background activity; it is not a save-only or device-write
measurement.

Selective reads retain validation work over revision keys to check count, lifecycle
and latest number. That work still scales with history length. Saves and protocol
graph operations continue to allocate complete graph state. In-memory stores
already own decoded state, so projecting fresh metadata and the measurement adapter
can add allocation there. At ten saves, uncached in-memory total allocation rose
from 135.15 to 141.01 MB. In the larger 20-save DOCX pair, durable allocation fell
from 6.203 to 3.240 GB, but RSS rose from 387,444 to 412,392 KiB. Larger in-memory
allocation changed from 2.642 to 2.649 GB and RSS from 395,836 to 410,128 KiB.
These are single trials; the process-memory causes remain unresolved.

Explicit schema-5-to-6 initialization on a clone of the 100-save fixture took
370.41 ms and allocated 22.25 MB. The whole process took 0.452 seconds and peaked
at 83,384 KiB RSS. It preserved all 128 retained snapshot rows across 65 resources,
27,113,298 JSON bytes and the logical usage ledger exactly, checked with a SHA-256
for every row. It built 65 heads and 165 summaries from current heads. Whole-server
WAL during this migration was 16,888,056 bytes, with the same background-activity
scope limitation as the workload WAL measure.

Raw data, source/binary hashes, excluded stale-output setup diagnostics, the
comparison script and migration probe remain in ignored `artifacts/selective-reads/`.
`--read-metrics` reproduces the extra read phases. The automated regressions make
full state reads fail while exercising metadata, catalog, paging and downloads;
they also cover unsigned revision numbers, stale/corrupt projections, current-head
membership after migration/import, authorization races and custom-policy fallback.


Code review identified that small range sequences could expand into many reference
arrays even with shared descriptors. The codec now preflights compact and legacy
membership counts before descriptor or membership deserialization. The configurable
per-state `MaxDecodedGraphMembershipBytes` budget defaults to 128 MiB of reference
slots. It also applies on writes and migration; recovery bounds the combined
reference slots across snapshots. This counter excludes descriptor objects, array
headers and other process memory. It changes admission, not retention: over-budget
operations fail, and migration rolls back without dropping history. Writers also
reject duplicate identities within one partition so they cannot publish compact
JSON that their reader rejects. Identity reuse across revisions and allowed
null-serial watermark upgrades remain supported.

A bounded follow-up compared the reviewed selective-read build with these
admission guards on identical fresh-database workloads. The uncached durable
100-save run changed from 33.40 to 34.21 seconds and 4.163 to 4.202 GB allocated.
The cached pair changed from 33.26 to 33.60 seconds and 4.165 to 4.170 GB. The
larger 20-save pair changed from 17.59 to 17.84 seconds with allocation about
3.236 GB in both builds. All six trials passed 17 checks. RSS and live heap
remained variable; the guards bound admitted reference arrays, not total process
memory. Raw evidence and hashes remain in `artifacts/review-memory-budget/`.
