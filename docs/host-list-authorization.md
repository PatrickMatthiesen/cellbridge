# Host-owned list authorization

The native list engine can use the host's permission store instead of
`cellbridge_lists.memberships` and `list_grants`. Documents keep their existing
authorization contracts. List permissions do not grant document permissions.

`SingleProcessListAuthorizationProvider` supports one API process, including its
background jobs. Register one provider instance per authority domain and share it
with every engine and permission writer. Do not run overlapping processes or
rolling upgrades against that authority. Matching domain names on two independent
provider instances do not make their in-memory guards shared.

## Register the storage anchor and provider

An operator first upgrades the list schema with `PostgreSqlListSetup.InitializeAsync`
and registers the workspace without creating a membership:

```csharp
await PostgreSqlListSetup.RegisterHostWorkspaceAsync(
    adminSource, workspaceId, "Customer workspace", "sharebridge:lists", ct);
```

The workspace row supplies storage foreign keys, quotas and workspace RLS. Its
one-time authorization domain is an opaque, case-sensitive identifier. It contains
no permissions or per-change authorization revision. Registration cannot convert
an existing built-in workspace or change an existing host domain.

Create one provider with an asynchronous host snapshot loader:

```csharp
var authority = new SingleProcessListAuthorizationProvider(
    "sharebridge:lists",
    async (actor, workspace, cancellation) =>
        await permissions.LoadSnapshotAsync(actor.Subject, workspace, cancellation));

IListEngine lists = new PostgreSqlListEngine(runtimeSource, limits: null, authority);
```

`permissions` is the host's authority-store adapter, not a CellBridge service.
The loader runs after acquiring the shared host guards and before opening a
PostgreSQL list transaction. It must honor cancellation and return a complete
immutable snapshot for exactly that subject, workspace and domain. Register the
provider as a singleton in an HTTP host. The loader must eagerly materialize its
snapshot and finish and dispose its authority-store transaction and connection
before returning. It cannot return a lazy database view or retain authority row
locks while the engine runs. This also applies when the authority uses PostgreSQL.
The authority and list transactions remain separate even in the same database.
`ActorResolver` still obtains the trusted,
stable subject from server-side authentication. Never accept permission bits,
snapshots or an actor subject from a request body as authority.

The [SQLite consumer example](../examples/HostAuthorizedLists/README.md) compiles
against this checkout and demonstrates snapshot loading and coordinated changes.
These APIs are newer than the published beta.4 packages.

## Permission decisions

`ListAuthorizationSnapshot` copies and freezes its per-list overrides. A snapshot
contains `PolicyDomain`, `Subject`, `WorkspaceId`, `WorkspaceAccess`,
`DefaultListAccess`, `ListOverrides` and an optional diagnostic `Revision`.
The revision is not published to PostgreSQL.

All list operations require `ListWorkspaceAccess.UseLists`. The other workspace
capabilities require `UseLists` and have separate purposes:

| Capability | Effect |
| --- | --- |
| `CreateLists` | Create a list in the workspace. |
| `ManagePermissions` | Describe host permission-management authority. The host must enforce it in its own management flow. |
| `ReadAudit` | Read the workspace list audit trail. This can disclose events for every list in that workspace. |

Per-list overrides replace `DefaultListAccess`, including an explicit `None` deny.
An absent override uses the default, including for a newly created list. `Read`,
`Write` and `Design` do not imply each other:

| Operation | Required list access |
| --- | --- |
| Enumerate host-authorized list definitions, read records, query, get views, export and OData | `Read` |
| Create or delete a record | `Write` |
| Update a record or preview its merge | `Read` and `Write` |
| Publish a schema or save a view | `Design` |
| Assign a reference, add a reference field or resolve a reference label | Appropriate source access plus `Read` on each target list checked by that operation |

References use the same operation snapshot for source and targets. Unchanged
references retain the existing patch/schema behavior; editing an unrelated field
does not revalidate an unchanged reference grant. A denied target neither loads
its definition nor locks its record. Enumeration permissions become parameterized
PostgreSQL predicates, so the engine does not fetch unauthorized definitions and
filter them in application code. Record filtering and paging also remain in SQL.

Creation does not create an implicit owner grant. Supply the intended access in
the host's default or prepare a per-list policy for the chosen list ID. Preparing
host metadata and creating PostgreSQL data are separate transactions.

A valid deny snapshot has `WorkspaceAccess.None`. A missing snapshot, failing
loader, identity/domain mismatch or unavailable authority fails closed. The engine
does not fall back to built-in grants. Host engines reject built-in workspace
anchors; built-in engines reject host-bound anchors. Unused local grants cannot
restore access. The existing built-in constructor and permission behavior remain
available for built-in workspaces.

In host mode `SetMembershipAsync` and `SetListGrantAsync` throw
`ListFailure.NotSupported`, even for a host permission administrator. The grant HTTP
endpoint returns 501. These methods never update ignored local grants. Use the
host's permission-management flow instead. The host also owns auditing its
permission changes; the CellBridge list audit records list data/schema operations.

## Coordinate every permission change

Permission writers must call the concrete engine's coordination helpers. Calling
`AcquireWriteAsync` directly omits the PostgreSQL transaction drain and is
insufficient. This includes administrative scripts and background jobs.

```csharp
await engine.CoordinateAuthorizationChangeAsync(workspaceId, async cancellation =>
{
    // This method owns and settles its complete authority-store transaction.
    await permissions.RevokeWorkspaceAccessAndCommitAsync(subject, workspaceId, cancellation);
    return true;
}, ct);

await engine.CoordinateGlobalAuthorizationChangeAsync(async cancellation =>
{
    await permissions.DisableAccountAndCommitAsync(subject, cancellation);
    return true;
}, ct);
```

Use the workspace helper only when a change affects that workspace alone. Account
disablement, global roles and changes affecting several workspaces require the
global helper. Global means this authority domain's list operations, not documents
or other modules.

Operations acquire domain-shared then workspace-shared host guards, and load the
snapshot under those guards. They open a PostgreSQL transaction and await shared
domain and workspace advisory locks in completed commands before sending data
mutations. They retain both the host guard and PostgreSQL locks through transaction
completion and disposal.

A workspace permission change acquires domain-shared/workspace-exclusive host
guards. A global change acquires the exclusive domain guard. The helper then opens
a short PostgreSQL drain transaction with the corresponding exclusive advisory
lock, commits it and disposes it before invoking the host callback. The drain
waits for earlier server transactions even when their clients lost a commit reply.
A request that has not received shared-lock confirmation cannot already have
submitted a data mutation.

The callback runs outside the list PostgreSQL transaction while the exclusive host
guard remains held. It must own its entire authority-store transaction and return
only after commit or rollback settles. It must not start detached writes, reenter
list operations or call another coordination helper. Its permission checks belong
to trusted host code; the coordination helper itself is not an end-user
authorization check and must not be mapped directly to an HTTP endpoint.

Acquisition and drain use `ListLimits.StatementTimeout` as a deadline. The callback
receives that operation's cancellation token. A canceled or expired deadline does
not release an unsettled callback's guard. The helper awaits the actual callback,
so a callback that ignores cancellation can delay operations until it settles.
Failed guard acquisition or drain never invokes the callback. A callback failure
does not prove rollback; the callback must implement and report its own transaction
outcome correctly.

Successful coordinated completion is the list revocation boundary. Previously
authorized reads may already have produced data; revocation cannot retract it.
HTTP adapters use the same engine checks, including each OData page and extract.
The helpers do not make host permission changes and PostgreSQL list data changes one
atomic transaction. They avoid replicated permissions and per-change revision
publication, but still coordinate operations with PostgreSQL.

## Storage and deployment limits

PostgreSQL domain locks use its two-integer advisory-key space, with namespace
391814 and `hashtext(domain)`. Workspace locks retain the existing bigint key from
`hashtextextended(workspaceId.ToString("D"), 391813)`. PostgreSQL separates those
key spaces. Hash collisions within a level can add exclusion but cannot grant
access. Keys do not use process-randomized .NET hashes.

Workspace RLS, storage foreign keys and data/audit limits remain active. Host mode
does not add actor RLS. Runtime database credentials remain a trusted application
boundary, and existing setup retains the built-in authority tables and runtime
privilege checks. Host-mode operations do not read or write their ACL rows.

This PR does not include an interprocess provider. A replacement provider would
need shared/exclusive ownership across every permission writer and list caller,
held until transactions settle. An expiring distributed lock is insufficient if
its former owner can still commit. Use the supplied provider only under the
single-process restriction above. Durable versioned bindings are a different
design and would require per-change PostgreSQL revision publication.
