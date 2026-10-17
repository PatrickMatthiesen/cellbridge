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

Invalidation discards cached decisions and supersedes evaluations still pending admission. The cache checks its epoch and admits a result under the same short lock used by invalidation. An invalidated pending result is reevaluated, with a bounded retry count; continuous invalidation fails closed. Already admitted work may finish. Capacity eviction does not revoke an operation. Supporting pending-entry and invalidation metadata are bounded too.

Legitimate denies are cached too. A newly granted permission can therefore wait for expiry or invalidation before a later operation sees it.

The implementation uses a bounded LRU and one invalidation epoch because capacity eviction alone does not establish this admission boundary. It does not retain an object or tombstone for every resource/user ever seen. Invalidation scans only resident entries. It conservatively supersedes unrelated pending evaluations too. The decorator is process-local. A host may broadcast invalidation notifications to its instances or accept TTL expiry on other instances. There is no bundled distributed notification or strong revocation coordinator.

Expiry is the earliest of evaluation-call start plus TTL, the provider's original `EvaluatedAtUtc` plus TTL, and its `FreshUntilUtc`. Omitted evaluation time asserts evaluation during this call and uses call start. Providers reusing their own cached data must preserve its original evaluation time and source freshness deadline. Errors and timeouts do not become cached allows. Expired allows are never served while refreshing.

Metadata uses UTC. Future evaluation times, inconsistent deadlines and freshness already expired at admission fail closed, with zero positive clock-skew tolerance. Source timestamps are authority assertions, not proof of synchronized clocks or fresh data. Hosts must account for their authority's clock uncertainty in the metadata they supply. Local TTL elapsed time uses `TimeProvider` monotonic timestamps; backwards wall-clock movement cannot extend it. A forward wall-clock jump can cause an earlier deny or refresh.

## Binding migration and compatibility

Version-2 `DocumentAuthorizationBinding` identifies ordinary external mode. It persists the domain, scope and configuration revision, not permissions. Changing a host permission needs no binding update. Native documents remain unbound; version-1 documents retain exact snapshot/CAS semantics. Selecting ordinary external mode does not silently take over either kind.

To migrate, stop overlapping older writers, configure the new provider, and call trusted `BindExternalAuthorizationAsync` with the expected state version and binding. CAS failure makes no change. Successful migration clears sessions and leases and advances the coordination generation, so staged writes cannot cross the binding change. Content, history and receipts remain intact. Reconnect editing clients afterward. Old engines reject version-2 binding rather than falling back to owner grants, but mixed writer versions are unsupported.

The provider domain and scope are durable identifiers. Changing scope/domain is another explicit trusted migration, not a permission edit. Take backups before migrating production state. Portable recovery currently rejects external bindings; this revision does not add coordinated backups of a host authority or widen the existing recovery contract.

An external authority in the same PostgreSQL database still uses separate transactions from CellBridge. That supports ordinary admitted-work semantics; it does not create atomic cross-module revocation. No multi-process instant-revocation guarantee is implied by the choice of database.

## Local measurement

On 2026-10-17 the Release example ran three uncached/cached pairs on Linux with four available CPUs and PostgreSQL 18.3 in the isolated Aspire test container. Each process used one subject, one 831-byte DOCX, concurrency one, an indexed host permission row and an in-memory document store. Connections were open before measurement. Each group includes its first execution; these are elapsed-time samples, not a steady-state throughput qualification.

| Workload per run | Uncached provider calls | Cached provider calls | Uncached median elapsed | Cached median elapsed |
| --- | ---: | ---: | ---: | ---: |
| 1,000 history reads | 1,000 | 1 | 250.3 ms | 5.4 ms |
| 200 direct SOAP metadata requests | 400 | 0 | 158.1 ms | 40.2 ms |
| 100 whole-file saves with distinct receipt keys | 100 | 0 | 116.9 ms | 76.0 ms |

The cache capacity was 512 and TTL was 10 seconds. Lock acquisition had already cached Write; the first history read cached Read. Total provider calls, including setup and the post-invalidation deny check, were 1,502 uncached and 3 cached in every run. Whole-file saves changed content once, then recorded 99 no-op receipts. Across the three runs, whole-file elapsed times ranged from 106.3 to 131.2 ms uncached and 73.6 to 79.1 ms cached.

This demonstrates call reduction for one hot resource. It does not establish durable write throughput, many-workspace scalability, cold-cache batch efficiency or Office interoperability. Caching costs and benefit depend on the host authority, resource distribution and accepted TTL. The example prints raw counts/timings so consumers can repeat it against their own authority.
