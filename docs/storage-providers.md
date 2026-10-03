# Storage providers and reusable hosting

CellBridge separates document state from binary content. PostgreSQL owns durable
document identity, selected revision, retained graph references, versions, save
receipts, sessions and leases. Binary content can use PostgreSQL chunks or the
filesystem provider. Both protocol layers remain in reusable libraries.

The packages target .NET 10. Build them locally with the package verification
tool below. The APIs are experimental; no published NuGet release is assumed.

## Run the durable sample

Build while Aspire is stopped, then start it normally:

```sh
dotnet build CellBridge.slnx
aspire start --apphost aspire/apphost.cs --non-interactive
aspire wait web --non-interactive
aspire wait demo --non-interactive
```

The AppHost starts PostgreSQL with the named `cellbridge-storage-data` volume.
`storage-migration` runs the versioned schema initialization before the web host.
The host checks the selected state and content stores before importing documents.
Health checks also probe both stores. Imports create missing paths and preserve
existing saved revisions. Removing the database volume removes persisted state.

For deployment without the sample AppHost, run the migration executable against
`ConnectionStrings__cellbridge` before starting any application instance. The
application checks the schema; it does not alter tables during normal startup.
An unsupported schema fails explicitly. Use deployment-controlled credentials
and PostgreSQL backup/restore procedures appropriate to the installation.

Schema version 2 adds a shared usage ledger. Stop all hosts before upgrading from
version 1. Run migration with the same `Storage` settings as the hosts; it sets
the authoritative database byte and document-count limits. With filesystem
content, also supply `Storage:ContentProvider=FileSystem` and `Storage:ContentRoot`
so migration inventories existing `.blob` files. Existing data is preserved even
when it already exceeds a limit. Version 1 binaries cannot write a version 2 database.

| Setting | Meaning |
| --- | --- |
| `Storage:Provider` | `PostgreSql` or explicitly volatile `InMemory`. Aspire selects PostgreSQL; the sample executable's standalone default is InMemory. |
| `ConnectionStrings:cellbridge` | Required PostgreSQL connection string, supplied by Aspire in local development. |
| `Storage:ContentProvider` | `PostgreSql`, the default for PostgreSQL state, or `FileSystem`. |
| `Storage:ContentRoot` | Absolute filesystem content directory, required for FileSystem. |
| `Storage:MaxObjectBytes` | Maximum bytes per binary object, default 512 MiB. This is a quota, not a tested document-size promise. |
| `Storage:MultipleInstances` | Require shared state and content; reject incompatible provider combinations. |
| `Storage:SharedContent` | Operator declaration that the filesystem root is shared with verified flush/rename semantics. Default false. |
| `Protocol:MaxRequestBytes` | Maximum SOAP/MTOM request size, default 128 MiB. Binary protocol parsing and materialization still buffer content. |
| `Protocol:MaxConcurrentRequests` | Maximum cellstorage requests per host, including response delivery, default 8; excess requests return HTTP 503. |
| `Protocol:MaxMtomParts` | Maximum MIME parts per request, default 128. |
| `Protocol:MaxMtomHeaderBytes` | Maximum header bytes per MIME part, default 16 KiB. |
| `Protocol:CaptureDirectory` | Opt-in directory for correlated raw requests/responses. Default disabled. Restrict access and manage retention. |

Environment variables use double underscores, for example
`Storage__ContentProvider=FileSystem`. To exercise two independent hosts, start
Aspire with `CELLBRIDGE_RUN_TWO_INSTANCES=1`. The peer uses ports 5182 and 7293;
the usual host uses 5181 and 7292. Both use the same migrated database.

Local directories on different machines cannot provide shared content. Do not
set `SharedContent` to bypass that restriction. PostgreSQL state with filesystem
content requires every service instance and recovery environment to see the
same immutable objects. Select one content configuration for a database;
changing it does not migrate existing objects.

## Consume the packages

`CellBridge.Storage.Abstractions` has no ASP.NET Core, protocol or database
dependency. `CellBridge.Storage` implements document codecs and detached protocol
state. `CellBridge.AspNetCore` provides endpoint registration and save orchestration.
Provider packages are `.InMemory`, `.PostgreSql` and `.FileSystem`. The filesystem
package implements content storage; it does not replace authoritative state.

For a host with a migrated PostgreSQL database:

```csharp
using CellBridge.AspNetCore;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.PostgreSql;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
var source = NpgsqlDataSource.Create(
    builder.Configuration.GetConnectionString("cellbridge")!);
builder.Services.AddSingleton(source);
var provider = new StorageProvider(
    new PostgreSqlStateStore(source), new PostgreSqlContentStore(source));
builder.Services.AddCellBridge(provider, multipleInstances: true);
var app = builder.Build();
await provider.CheckHealthAsync();
app.MapCellBridge();
app.MapHealthChecks("/health");
await app.RunAsync();
```

Replace the content argument with `new PostgreSqlFileSystemContentStore(source, root)` for local
external content and use `multipleInstances: false`. A host explicitly using
volatile providers must call `AddCellBridge(..., requireDurability: false)`.

Pass the same `StorageLimits` to the PostgreSQL state store, content store's
object-size argument, `StorageProvider`, and migration. Composing empty in-memory
stores through `StorageProvider` joins their counters. Standalone
`FileSystemContentStore` enforces object size only; the PostgreSQL wrapper adds
shared accounting. Custom providers must implement their own atomic accounting.
`StorageCapabilities.AtomicBudgets` reports coordinated accounting. Explicit
`StorageLimits` cause registration to reject an uncoordinated provider pair;
providers with default settings can still run without this guarantee. PostgreSQL
state and content stores in a pair must use the same `NpgsqlDataSource` object.

`CellBridgeDocumentService.CreateAsync` imports a missing document and returns
null on a conflicting path/identity. `ResolveAsync` preserves ResourceID
precedence when an ID is supplied. `ExecuteAsync` processes supported binary
operations. Sample catalog/creation APIs, Office package generation, imports and
Aspire dependencies remain in the executable project, outside the hosting package.
Some protocol utility types retain the existing `CellBridge.Web` namespace for
source compatibility, but reside in the hosting assembly.

The consuming host owns authentication and authorization middleware. This
release still returns the shared `officelab` identity and grants protocol access
without distinct-user authorization. Provider selection does not add those
features.

The [NuGet consumer example](../examples/NuGetConsumer/README.md) is a minimal
HTTP application using only package references. It registers in-memory storage,
maps the endpoints and creates one downloadable text file. Its storage is
deliberately volatile; the PostgreSQL composition above provides persistence.

Run `python tools/verify_packages.py` with Aspire stopped to pack the nine
libraries, build the example and run `tests/CellBridge.Packages.Tests`. The
package tests exercise a custom state-provider wrapper, concurrent transitions,
lease persistence, discovery, download, HEAD and Cell QueryAccess. Test-server
and conformance dependencies stay in the test project. Packages and the isolated
package cache are under ignored `artifacts/packages`.

## Commit and retry behavior

A save loads one immutable state snapshot, validates/merges the selected graph,
materializes the Office package and durably stages new binary objects. It then
acquires the document's PostgreSQL row, reads its current pointer and actual
database time, rechecks coherency and leases, and atomically publishes the new
snapshot with a receipt. Package/blob I/O happens outside the document lock.
Different documents use different rows. The migration's database-wide advisory
lock runs only during explicit schema initialization.

PostgreSQL publications require `fsync=on`, force `synchronous_commit=on`, and
use a five-second lock timeout and 30-second statement timeout. They read from
the primary. These settings provide local durable commit under the database and
storage system's guarantees; asynchronous replication failover needs its own
durability configuration. No failure switches to volatile storage.

Receipts bind a proposed storage index to the document, an operation digest and
the accepted response. Identical retry while that content version is current
returns the accepted response without advancing its version. Reusing the index
for different bytes fails. A delayed retry after a later save returns coherency
failure and preserves the current revision. A lost commit reply triggers a
receipt lookup before the service can reapply the operation.

Each supported PutChanges has its own commit boundary. A later failure cannot
undo an earlier accepted operation. Unsupported partial/multi-request modes are
rejected before any constituent operation publishes. SOAP dependency conditions
are checked before a dependent subrequest runs. Repeated binary QueryChanges
remains explicitly unsupported because one response carries one data package.

Session/lease updates use the same per-document transaction. Time is acquired
after waiting for its lock, so an expired lease cannot remain valid because a
transaction started earlier. Restart does not extend a lease. Editor expiry and
its partition knowledge advance together.

## Filesystem durability and retention

The content provider accepts non-seekable streams, checks the quota while
reading, hashes bytes and writes a temporary file. It flushes file data, then
publishes a SHA-256 key using a durable rename operation. Linux synchronizes the
directory and newly created parent entries. Windows uses write-through file
flushes and `MoveFileEx` with `MOVEFILE_WRITE_THROUGH`. Filesystem/storage hardware
must honor those operations. Windows and shared network filesystem power-loss
qualification has not been run in this environment.

Reads verify length and SHA-256. PostgreSQL verifies chunks while streaming;
filesystem reads verify the file before returning a stream. This filesystem
check adds a read pass. Downloads take content handles and headers from one
snapshot, so a concurrent save cannot mix revisions.

PostgreSQL retains the latest 64 JSON state snapshots per document by default,
including lease-only transitions. The current state still contains the retained
protocol graph and all receipt identities. Superseded receipt response handles
are omitted from new snapshots; operation digests remain so delayed or conflicting
retries cannot become new saves. Current-version retries keep their accepted response.

Content remains readable by detached readers until explicitly quiescent garbage
collection. Failed staging can leave charged orphan objects; an abrupt
filesystem-writer kill can leave `.tmp` files. Remove temporary files only while
all writers are stopped. Do not delete `.blob` files based on age or the current
materialized document. Historic graph references may still be required by a
delta save.

The migration tool can report objects unreferenced by **every retained snapshot**:

```sh
dotnet tools/CellBridge.Storage.Migrate/bin/Release/net10.0/CellBridge.Storage.Migrate.dll --collect-orphans
```

Supply the database connection and, for filesystem content, its root through
configuration. Stop every host, reader and writer before adding `--apply --quiescent`.
SQL locks alone cannot protect detached readers or filesystem writers that have
already reserved an object. Applying collection deletes orphan chunks or files
and releases their charge; it does not prune the live protocol graph. Filesystem
deletion currently requires Linux directory synchronization. Preserve separate
backup content before collection: backup recovery points are not collector roots.

A PostgreSQL-only backup contains state and binary content. With filesystem
content, preserve every object referenced by the database recovery point. Since
objects are append-only, a content copy completed after the database backup can
cover its references while writes continue, provided the backup tooling makes
complete objects available and retention is unchanged. Test restoration before
depending on that procedure. A missing referenced object is corruption; startup
must never reseed its document.

## Implement a provider

Implement `IDocumentStateStore` and `IContentStore`, then compose a
`StorageProvider`. Advertise durability/shared flags only for guarantees the
complete deployed backend provides. `TransitionAsync` must acquire exclusive
coordination for that resource, load one coherent current snapshot, then supply
authoritative time. Run its callback once and atomically publish `Next`, advancing
`StateVersion`, or publish nothing for null/exception. Never silently retry a
callback after an unknown commit outcome. Callbacks perform no I/O and must stay
bounded; content preparation belongs outside them.

Return immutable detached records, enforce unique path keys and resource IDs,
and prohibit identity/path changes in transitions. Preserve unsigned protocol
values, graph serials, GUIDs, timestamps and format versions exactly. Path keys
use the normalized path's invariant uppercase, with ordinal database comparison.
Percent escapes are decoded once. Unicode normalization is not applied.

`WriteAsync` must complete durable content publication before returning a handle.
Handles refer to immutable content, remain readable after a process restart and
are never recycled. Content reclamation requires quiescent maintenance over every
retained snapshot. Open a stream for each read and report corruption or storage
failure explicitly. Do not implement an independent file overwrite followed by
a metadata update.

To advertise atomic budgets, implement `IStorageBudgetParticipant` on both stores
and expose one scope object. Charge state JSON, unique content/reservations and
document identities under the same atomic admission mechanism. Merely implementing
the marker does not provide the accounting. Custom wrappers must preserve it.

Run `CellBridge.Storage.Conformance.ProviderConformance.VerifyAsync(provider)`
against a disposable backend. It retains its test records/objects. Add the
repository's save, lease, crash and replay cases before claiming a custom durable
provider is ready; the small public conformance runner cannot prove hardware
flushes, distributed locking or every protocol behavior.

## Limits and qualification

The sample binds these positive `StorageLimits` from `Storage` configuration:

| Setting | Default | Admission scope |
| --- | --- | --- |
| `MaxDocumentBytes` | 128 MiB | Materialized document and expanded ZIP data. |
| `MaxGraphBytes` | 512 MiB | Retained element payloads across document partitions. |
| `MaxGraphElements` | 100,000 | Retained graph elements across document partitions. |
| `MaxDocumentStoredBytes` | 1 GiB | Current state JSON plus distinct referenced content handles. |
| `MaxStoredBytes` | 10 GiB | Unique objects/reservations plus retained UTF-8 state JSON. |
| `MaxDocuments` | 10,000 | Published document identities. |
| `MaxObjectBytes` | 512 MiB | One content object while streaming. |
| `MaxSaveReceipts` | 10,000 | Receipt identities per document; identities are not evicted. |
| `MaxRetainedStateSnapshots` | 64 | PostgreSQL metadata history per document. |

Byte accounting includes charged orphans and reservations. It excludes PostgreSQL
row/index overhead, WAL, temporary spool files and allocator overhead. It is a
logical byte quota, not a disk or RSS limit. Each durable content-store instance
admits four spool writers. Quota failures before publication preserve the current
revision; successful staging before a later rejection can remain charged until
quiescent collection. Duplicate content is charged once. The sample creation API
returns HTTP 507; file PutChanges returns Win32 `ERROR_DISK_FULL` (112). Oversized
HTTP bodies return 413. Reverse proxies and HTTP server limits may reject earlier.

MTOM request parts and the binary reader now borrow the admitted request buffer.
File queries compare client GUID/serial ranges against persisted metadata before
reading payloads, and return elements whose possession is not established. Mapping
serials identify mappings separately from their target elements. New elements get
server serials above the persisted high-water mark. Retry processing preserves
existing serials. Reusing a nonnull mapping serial for a different key or target
fails before publication. Legacy snapshots recover mapping metadata from index payloads;
ambiguous element serials cause conservative retransmission. These rules follow
[data-element serial assignment](https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttpb/9db15fa4-0dc2-4b17-b091-d33886d8a0f6)
and [storage-index mapping knowledge](https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttpb/f5724986-bd0f-488d-9b85-7d5f954d8e9a).
The PutChanges writer retains the captured legacy serial-reassignment stream
shape. Desktop Word accepted ten saves and a fresh-process reopen after these
allocation changes; each save's capture matched the client's ETag.
Waterline-only
knowledge does not imply possession. Whole-cell rounding returns all visible
elements when any are unknown. Filtered knowledge excludes withheld elements
unless the request explicitly includes them. Unknown knowledge specializations
fall back to a complete response. Valid knowledge beyond 10,000 decoded entries
also falls back to a complete response after validating the entire exchange;
partial ranges never suppress payloads. Malformed framing fails. Unsupported optional
filters are ignored unless `FailIfUnsupported` requests an error. Version queries
and foreign cell scopes remain explicitly unsupported. See the protocol's
[Query Changes rules](https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttpb/5b8d1d29-0adf-4b29-b3d1-1a1fe8590642).

Graph merging, package materialization and ZIP validation still buffer data.
Configured defaults are admission limits, not qualified Office file sizes.
Automatic graph compaction remains disabled. Supported revision-base chains can
grow their required closure with a constant-size file. Object-group metadata is
now decoded as [change frequencies](https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttpb/507c6b42-2772-4319-b530-8fbbf4d34afd),
including custom values, and carries no references. `AnalyzeRetention` resolves
object references only in the declaring revision and its explicit base chain,
following the [protocol data model](https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttpb/6c7e4447-6ccd-4764-8dbc-17a382fb631d).
An incomplete analysis reports only a lower bound on required storage. The pure binary
`Compact` API rejects incomplete analysis and is not invoked by the host. Saves
eventually reject at a quota instead of silently deleting required history.

Provider export/import and public version restoration remain unimplemented.
Changing content providers requires an explicit data migration; changing
configuration alone cannot move existing objects.

Automated process-termination tests exercise the publication boundary, receipt
recovery and a subsequent graph update. They do not qualify power loss, Windows
or shared-filesystem flush behavior, or asynchronous PostgreSQL replication
failover. Test those guarantees in the deployment environment.

The [testing guide](automated-testing.md) explains provider measurements and
crash checks. [Interoperability coverage](interoperability.md) records the scope
of current desktop and replay validation. Authentication and two-desktop
coauthoring are separate from durable storage.
