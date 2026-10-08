# Tested storage environments

CellBridge supports PostgreSQL state with PostgreSQL content, or PostgreSQL state
with accounted immutable filesystem content. In-memory storage is volatile.
Provider support alone does not establish recovery on every deployment.

## Record a qualification run

Run `python tools/testing/run.py` with the workspace AppHost stopped. The runner
uses disposable PostgreSQL and writes `recovery/environment.json`, TRX results
and `summary.json` under its ignored output directory. The report records the
exact PostgreSQL version, `fsync`, `synchronous_commit`, `full_page_writes`, host
OS/runtime and drive-root filesystem types for the temporary and output paths. It contains
no connection strings or account credentials.

The drive-root types describe direct local paths. Nested mounts, directory
junctions and symbolic links need separate actual-volume inspection before
using this report to qualify their content storage.

All three database durability settings must be `on`. An environment report is
metadata, not a passing recovery result. Retain it with successful test results
and the source commit when qualifying a deployment.

| Configuration | Relevant checks | Boundary established by passing checks |
| --- | --- | --- |
| PostgreSQL state and content | `ProcessTests.ProcessDeathBeforeAndAfterCommitPreservesPublicationBoundary` and provider conformance | Killing the writer before publication exposes previous state. Killing it after commit preserves content, receipt and pending delivery; retry recovers the commit. |
| PostgreSQL state and local filesystem content | `ProcessTests.FileSystemProcessDeathBeforeAndAfterCommitPreservesPublicationBoundary` | The same process boundary with immutable files under the reported temporary root. |
| PostgreSQL state and filesystem content under the run output root | `ProcessTests.ConfiguredFileSystemProcessDeathBeforeAndAfterCommitPreservesPublicationBoundary` | The same process boundary on the reported output filesystem. Missing qualification inputs cause a skip. |
| Portable recovery across PostgreSQL/filesystem content | `PortabilityProviderTests.PostgreSqlToFileSystemToPostgreSqlRetainsEverySnapshotRootAndRestores` and portability failure tests | Export/import preserves current and retained graph/content roots, history, receipts and pending delivery. A reopened provider can restore a retained revision. |
| Disposable local Linux PostgreSQL, with PostgreSQL or local filesystem content | `DatabaseRecoveryTests`, run through `tools/testing/recovery.py` | Database process termination before publication SQL, clean database stop/start after acknowledged saves, and quiescent custom-format `pg_dump`/`pg_restore`, each with state and content verification. |
| In-memory state and content | Volatile provider conformance | API behavior during a process lifetime, with no restart recovery guarantee. |

Current CI also exercises PostgreSQL 18 in a Linux container and protocol/package
checks on Windows. A runner label does not identify its backing filesystem or
qualify physical power loss. Record a successful run's environment before adding
a concrete configuration to a deployment's supported matrix.

The local recovery run on 2026-10-06 used .NET 10.0.12 on Windows build
10.0.26200, with PostgreSQL 18.3 in a Linux container. The database reported
`fsync`, `synchronous_commit` and `full_page_writes` as `on`. The PostgreSQL
content process checks passed. The filesystem process checks passed on the
temporary C: NTFS root and the output D: ReFS root. These results qualify the
application process interruption boundary above, not database or hardware
failure. Filesystem object reclamation remains Linux-only; Windows rejects that
operation and preserves the stored objects and accounting.

The repository selects stable .NET 10 SDKs through `global.json`; it permits
newer .NET 10 feature bands and excludes prerelease SDKs. See Microsoft's
[SDK selection rules](https://learn.microsoft.com/en-us/dotnet/core/tools/global-json).
The final runner records the selected SDK in `summary.json`.

## Limits and recovery

The `ProcessTests` checks terminate application writers. The dedicated
`DatabaseRecoveryTests` also terminate the disposable PostgreSQL process and
restart the same container. None of these checks cut disk power, force replica
failover or qualify SMB/NFS semantics. Those scenarios
remain unqualified until their own fault/restore procedures pass with independent
state and content verification. Flushing and atomic rename are prerequisites,
not proof of hardware durability.

Every host must read the same filesystem content. Separate local roots on
different machines cannot provide shared content. Enable `SharedContent` only
after verifying the actual shared storage semantics.

Back up PostgreSQL state and content together when content is in PostgreSQL.
For filesystem content, preserve every object referenced by the database recovery
point and prevent collection from removing those objects during backup. Restore
into an isolated environment and verify identities, permissions, graph closure,
content hashes, receipts, history and pending delivery before serving traffic.
Recover authentication and external destination receipt/policy stores separately
using coordinated recovery points.

The portable archive tests validate recovery into an empty destination with the
documented context restrictions. The database runner below tests one quiescent
logical backup procedure. Other operator procedures, physical snapshots and
replica backups still need their own qualification. See [provider recovery](provider-portability.md),
[storage configuration](storage-providers.md) and [durable host upgrades](storage-providers.md#upgrading-a-durable-host).

## Controlled database interruption and logical restore

On Linux with Docker, the pinned Aspire SDK and .NET 10 available, run:

```sh
python3 tools/testing/recovery.py
```

The runner builds the storage tests and starts its own minimal AppHost with
`--isolated`. It refuses an active AppHost in this worktree and removes inherited
connection strings and storage configuration. It creates a PostgreSQL 18.3
container with a unique ownership marker, dynamic ports and PGDATA in the
container's writable layer. The image's unused default data mount is tmpfs.
There are no database bind mounts or persistent volumes. Removing the container
discards its database. Filesystem objects and copied backup roots are under the
run's ignored `artifacts/recovery` directory. The runner retains the exact
container during faults and removes it after stopping its AppHost.

All six recovery cases must execute and pass. Missing opt-in inputs skip the
tests in ordinary test runs; skips do not qualify a database recovery run. The
runner also executes existing process, provider-portability and environment
checks, then stops only its own AppHost and removes its verified container.
Keep its summary, TRX results, environment reports and backup artifacts together.
Reports identify the source commit and dirty state, host and container OS,
SDK/runtime, Npgsql, Aspire CLI, Docker, image digest, PostgreSQL and backup-client
versions, durability settings and filesystem mounts. They omit connection strings
and passwords.

The local Debian run on 2026-10-08 used Debian 13.7, SDK 10.0.401,
.NET 10.0.12 and Npgsql 10.0.3. PostgreSQL and both backup clients were 18.3,
from the Debian 13 container image. Local content was on ext4; PGDATA was on
the container overlay filesystem. All three recorded database durability settings
were `on`. Passing raw-state/content comparisons alone do not qualify the run;
the retained-reply independent decoding checks must pass as well.

The interruption case holds a real provider transaction after calculating the
proposed save, before executing publication SQL. It kills the verified database
container, releases the test gate and requires failure while the database stays
down. After restart, heads, every retained state snapshot, security, history,
receipts and pending delivery must match the prior recovery point before retry.
Staged immutable objects can remain charged after rollback. The ledger must still
equal object lengths plus persisted state and recovery-receipt bytes. Two retries
must publish exactly one revision, save receipt and pending delivery entry. This
does not establish interruption during COMMIT or external delivery exactly once.

The clean stop/start case requires a zero database exit status, then compares all
raw CellBridge table rows and verifies the provider through its existing pool
and a fresh pool. Neither verification reinitializes the schema or repairs
accounting.

The backup case has no concurrent writer, publisher or collector. It captures
expected raw rows and referenced object bytes, runs a complete custom-format
[pg_dump](https://www.postgresql.org/docs/18/app-pgdump.html), and copies the
filesystem objects when applicable. It restores into a separately created empty
database using [pg_restore](https://www.postgresql.org/docs/18/app-pgrestore.html)
with `--single-transaction --exit-on-error`. It compares every raw table row before
any initialization, checks SHA-256 and length against the frozen content manifest,
and materializes current and historical file graphs. Retained binary save replies
must preserve their frozen source bytes and independent OfficeInspectors decoding
results and decode fully. The runner saves source reply bytes, profile and fixture
provenance before verification, so a source wire/fixture defect remains diagnosable
even when restored bytes match exactly. An unchanged decoding failure still rejects
qualification. Separate file-save replies from the supported SharePoint reference
request fixtures provide fully decoded positive controls after recovery. Missing
and same-length corrupted filesystem objects used only by historical state must
reject verification. A valid restore must preserve authorization and tombstones,
restore a retained revision, and accept new reference saves with idempotent retries.

This procedure covers CellBridge provider tables and content only. Roles,
authentication accounts, protection keys and external destination ledgers need
coordinated backups and separate restore checks. The local writable-layer database
and local content roots do not qualify deployed database volumes, shared content,
replication failover or hardware power loss. These server checks add no desktop
Office compatibility or coauthoring evidence.
