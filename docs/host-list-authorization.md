# Host list authorization

CellBridge Lists can ask a host permission service for each operation instead of using
`cellbridge_lists.memberships` and `cellbridge_lists.list_grants`. The built-in mode and its SQL grant joins remain
available. A workspace chooses one mode when it is registered. The database does not allow that choice to change.

## Register a host workspace

Upgrade the list schema, then register the workspace with its policy domain and tenant scope:

```csharp
await PostgreSqlListSetup.InitializeAsync(adminSource, runtimeRole, cancellationToken);
await PostgreSqlListSetup.RegisterHostWorkspaceAsync(
    adminSource,
    workspaceId,
    "Customer workspace",
    "example:list-policy",
    tenantId,
    cancellationToken);
```

The schema stores `authorization_mode`, `authorization_domain`, and `authorization_scope` on the workspace row.
An update trigger makes all three values immutable. Re-registering the same anchor may update the workspace name.
Registration fails if the workspace already uses another mode, domain, or scope.

Construct the engine with an asynchronous provider:

```csharp
IListAuthorizationProvider authority = new HostListAuthorizationProvider(permissionClient);
IListEngine lists = new PostgreSqlListEngine(runtimeSource, limits: null, authority);
```

Register the provider as a singleton if it owns connection pools or other shared resources. The actor subject and
tenant must come from server-side authentication. The engine checks the actor tenant against the stored workspace
scope and sends the stored value to the provider. A request cannot select another tenant or policy domain.

For the HTTP adapter, configure `CellBridgeListsOptions.ActorResolver` to return a
`new ListActor(stableSubject, trustedTenantId)` from the authenticated server context. The default resolver supplies
only a subject for native workspaces and cannot satisfy a host workspace's tenant anchor. Do not take the tenant
from a request body or unvalidated header.

The provider receives `UseLists` for every operation. It then receives the operation-specific request:

| Action | Required operation |
| --- | --- |
| `CreateList` | Create a list. |
| `ReadAudit` | Read the workspace audit trail. |
| `Read` | Read a definition or record, query, export, OData, get views, or resolve a reference. |
| `Write` | Create or delete a record. Updates and merge previews also require `Read`. |
| `Design` | Publish a schema or save a view. |

`EvaluatedAtUtc` and `FreshUntilUtc` are optional freshness assertions. Omit both when the provider evaluated the
decision during that call. If the provider reuses its own cached result, preserve its original evaluation time and
freshness deadline. CellBridge rejects future evaluation times, expired or inconsistent intervals, provider failures,
and incomplete batch results. It does not apply a clock-skew allowance. The host and authority therefore need
reasonably synchronized clocks when they exchange timestamps.

Provider I/O finishes before CellBridge opens the data transaction. The operation keeps a small immutable admission
and rechecks the stored workspace mode, policy domain, and tenant scope inside the transaction before it reads a
definition or locks a record. A permission change after admission does not cancel that operation. The next operation
authorizes again.

Host mode never reads or writes local memberships and grants. Creating a list does not add a creator grant.
`SetMembershipAsync` and `SetListGrantAsync` throw `ListFailure.NotSupported`; the HTTP adapter returns 501. Denied
list resources use the same not-found response as missing resources. PostgreSQL row-level security still isolates
workspaces. It does not enforce actor permissions; the operation admission does that before the data transaction.

Reference writes authorize the source first. The engine may then read the authorized source definition in a short
preflight transaction to identify changed reference fields. It authorizes their target list IDs before the write
transaction. A denied target definition or target record is not loaded or locked. Unchanged references do not require
a new target decision during an unrelated update.

## Enumeration and paging

`IListQueryAuthorizationProvider` can return one of three explicit scopes for list enumeration:

| Scope | Behavior |
| --- | --- |
| `All` | The provider explicitly grants `Read` for every list in this workspace for this decision lifetime. |
| `None` | The page is empty. |
| `CheckCandidates` | The engine scans at most 256 candidate IDs and checks each one before loading definitions. |

Without the query interface, the engine uses `CheckCandidates`. `IListBatchAuthorizationProvider` can evaluate that
bounded request set in one call. The SQL definition query contains only authorized IDs. It never fetches all
definitions and filters them in application code. Record filters and record keyset paging still execute in SQL.

Candidate paging bounds work per request, but a sparse policy can require many pages to find a small number of allowed
lists. It does not turn host policy into an indexed database predicate. Hosts that can express a workspace-wide `All`
or `None` decision avoid those per-candidate checks.

`IPagedListEngine.GetListsPageAsync` returns an authenticated, encrypted continuation. It binds the candidate cursor
to the subject, tenant, workspace, authorization mode, policy domain, expiry, and engine key. The token does not grant
a later page. Every page checks `UseLists` and obtains a new query decision. `CheckCandidates` pages then authorize the
bounded candidate set again. An empty or short authorized page can still have a continuation because denied candidates
consume the bounded scan.

The engine creates an ephemeral 256-bit continuation key by default. Tokens then stop working after process restart.
Pass the same 32-byte `continuationKey` to the native or host-authorized engine constructor when continuations need to
survive restarts or move between instances. Keep that key in the host's secret store. For example, native engines can
use `new PostgreSqlListEngine(runtimeSource, limits: null, continuationKey: sharedKey)`.
Tokens have a 512-character limit and store a fingerprint of the exact identity and authorization context.
The private token encoding is not a release compatibility contract. This unreleased prototype changes its encoding
from v2 to v3, so v2 tokens are invalid even when the host retains the same key.

The existing `GetListsAsync` method retains the native SQL behavior. In host mode it follows bounded pages up to the
configured `ListLimits.MaxLists`, then sorts the accumulated definitions by name. Hosts serving large workspaces should use the
paged method or the HTTP `GET /api/workspaces/{workspaceId}/lists/page` endpoint to bound work per request.

## Optional decision cache

External decisions are uncached by default. A host may opt in by constructing
`CachedListAuthorizationProvider` with `ListAuthorizationCacheOptions`. The cache has a fixed capacity, coalesces
concurrent requests for the same key, caps the host's freshness deadline with its local TTL, and fails closed on bad
metadata. Its invalidation methods cover a subject, workspace, or list. Invalidation discards a pending evaluation and
retries it before admission, subject to `MaximumInvalidationRetries`.

The decorator caches legitimate denies too, so a new grant can wait for expiry or invalidation. Its point-decision
and query-decision caches each have the configured capacity. Cold batch misses currently use individual provider
checks; the undecorated batch-provider path preserves the host's bulk call. Measure that tradeoff when selecting
caching for a remote authority.

Process-local invalidation does not coordinate separate hosts. Use a short TTL or call invalidation in every process
after a permission change. Invalidation is optional and does not replace the TTL. A decision already admitted to an
operation remains frozen for that operation. The cache uses monotonic elapsed time for its local TTL. Authority wall
clock timestamps only cap that TTL and remain assertions from the authority.
