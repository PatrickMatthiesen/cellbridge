# Host-authorized lists with SQLite

This source-checkout console example keeps accounts and list permissions in SQLite while CellBridge stores list data in PostgreSQL. It creates a host-bound workspace with no CellBridge membership or grant rows, creates one list and record, revokes that list access, and checks that later reads and writes fail.

The example references the projects under `src/`. The host authorization APIs are not in the published `0.1.0-beta.4` packages, so this example does not change the existing package consumer examples or claim a new package release.

Set these environment variables before running it:

```sh
export CELLBRIDGE_LISTS_ADMIN_CONNECTION_STRING='Host=...'
export CELLBRIDGE_LISTS_RUNTIME_CONNECTION_STRING='Host=...'
export CELLBRIDGE_LISTS_AUTHORITY_PATH='/private/path/list-authority.db'
dotnet run --project examples/HostAuthorizedLists/HostAuthorizedLists.csproj
```

The administrator connection must already have permission to register a CellBridge list workspace. The runtime connection must use an already-provisioned list runtime role. The example does not initialize the PostgreSQL schema or create global PostgreSQL roles, and it never prints connection strings. `CELLBRIDGE_LISTS_AUTHORITY_PATH` is optional. If omitted, the example creates a new SQLite file in the system temporary directory.

The console actor is the fixed `example:trusted-console-operator` process identity. It is not an example of HTTP authentication, and client input never selects its permissions.

Use one `SingleProcessListAuthorizationProvider` instance for every engine and permission writer in the process. Its policy domain must match the domain passed to `RegisterHostWorkspaceAsync`. The provider calls `LoadSnapshotAsync` under its read guard before the engine opens a PostgreSQL transaction.

Every host permission change must go through `CoordinateAuthorizationChangeAsync` or `CoordinateGlobalAuthorizationChangeAsync`. Do not call `AcquireWriteAsync` directly. The callback must begin and finish its SQLite transaction before returning. Do not detach writes, call list operations from the callback, or reenter an authorization guard.

Account enablement and disablement affect every workspace, so they require `CoordinateGlobalAuthorizationChangeAsync`. If SQLite reports an error while committing, the commit outcome may be unknown. Reconcile the stored account and permission revisions before retrying or accepting more permission changes.

These guards coordinate one process only. They do not make overlapping processes or rolling upgrades safe. Production hosts need shared coordination before running more than one process against the same authority domain. A coordinated host permission change and a list write cannot commit across SQLite and PostgreSQL as one atomic transaction. The helpers provide ordered exclusion and drain in-flight PostgreSQL list transactions before the SQLite change begins.
