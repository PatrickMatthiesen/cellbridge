# Host-authorized Lists example

This executable stores list data in CellBridge PostgreSQL tables and permissions in a separate host-owned table. Its asynchronous provider evaluates a bounded request batch in one PostgreSQL query. It creates no CellBridge membership or grant rows. Two trusted example subjects have different permissions over three lists in two workspaces.

Pass an owner connection, a restricted runtime connection, and the runtime role name. The role must already exist. Use a disposable database; setup creates the CellBridge Lists schema and an `example_list_permissions` table. Do not use the owner connection as the runtime connection.

```sh
dotnet run -c Release --project examples/HostAuthorizedLists -- \
  "$LISTS_ADMIN_CONNECTION" "$LISTS_RUNTIME_CONNECTION" cellbridge_lists_runtime

dotnet run -c Release --project examples/HostAuthorizedLists -- \
  "$LISTS_ADMIN_CONNECTION" "$LISTS_RUNTIME_CONNECTION" cellbridge_lists_runtime --cache
```

The example creates a record, updates it and verifies the saved value through a read-only subject. It checks denied writes, a private list, another workspace and a mismatched tenant. It follows continuations over one-candidate pages, including an empty denied page, then revokes the reader's permission, commits that change and optionally invalidates the local cache before verifying denial. A `finally` block removes this run's permission rows and workspaces, including their records. Shared schema tables remain.

JSON output includes actual provider point/batch calls, authority SQL calls and elapsed time for the first and repeated mixed list page. The measured engine starts with a fresh cache. The undecorated authority keeps batch calls intact. The cache currently queries each cold point decision separately, so its first page can cost more SQL calls; the repeated page reuses both allows and denies. These small sequential samples demonstrate that tradeoff, not throughput, remote latency or a many-workspace workload.

Caching remains optional. `--cache` uses a capacity of 64 and a 10-second TTL. Permission changes can remain usable until that TTL expires unless the host invalidates the relevant process caches. The example exercises invalidation; controlled-clock integration tests separately check TTL expiry for grants and denies. Production hosts must derive actors from authenticated server context, use their own permission service and decide how to deliver invalidations across instances. See [host list authorization](../../docs/host-list-authorization.md).
