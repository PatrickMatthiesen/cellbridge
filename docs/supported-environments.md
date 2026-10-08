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

These checks terminate application processes. They do not cut disk power, crash
the database, force replica failover or qualify SMB/NFS semantics. Those scenarios
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
documented context restrictions. They do not validate an operator's particular
`pg_dump`, snapshot or replica-backup procedure. See [provider recovery](provider-portability.md),
[storage configuration](storage-providers.md) and [durable host upgrades](storage-providers.md#upgrading-a-durable-host).
