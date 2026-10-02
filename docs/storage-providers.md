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

Replace the content argument with `new FileSystemContentStore(root)` for local
external content and use `multipleInstances: false`. A host explicitly using
volatile providers must call `AddCellBridge(..., requireDurability: false)`.

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

Run `python tools/verify_packages.py` with Aspire stopped to pack the nine
libraries and build `examples/NuGetConsumer` using only package references.
That consumer exercises a custom state-provider wrapper, concurrent transitions,
lease persistence, discovery, download, HEAD and Cell QueryAccess. Packages and
the isolated consumer cache are under ignored `artifacts/packages`.

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

Published objects, graph history, JSON state snapshots and receipts are retained
permanently in this release. There is no automatic garbage collection or public
version restore API. Failed staging can leave orphan objects; an abrupt
filesystem-writer kill can leave `.tmp` files. Remove temporary files only while
all writers are stopped. Do not delete `.blob` files based on age or the current
materialized document. Historic graph references may still be required by a
delta save.

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
are never recycled. Open a stream for each read and report corruption or storage
failure explicitly. Do not implement an independent file overwrite followed by
a metadata update.

Run `CellBridge.Storage.Conformance.ProviderConformance.VerifyAsync(provider)`
against a disposable backend. It retains its test records/objects. Add the
repository's save, lease, crash and replay cases before claiming a custom durable
provider is ready; the small public conformance runner cannot prove hardware
flushes, distributed locking or every protocol behavior.

## Limits and qualification

The content providers accept streams, but SOAP/binary parsing, graph merging,
package materialization and ZIP validation still buffer data. Large saves can
allocate several copies of a package, and retained graphs and revisions grow
with the edit history. The 512 MiB object quota is not a tested document-size
guarantee. Profile allocations and database/WAL growth for the intended workload
before choosing size and concurrency limits.

There is no automatic published-object cleanup, graph compaction, provider
export/import or public version restoration API. Changing content providers
requires an explicit data migration; changing configuration alone cannot move
existing objects.

Automated process-termination tests exercise the publication boundary, receipt
recovery and a subsequent graph update. They do not qualify power loss, Windows
or shared-filesystem flush behavior, or asynchronous PostgreSQL replication
failover. Test those guarantees in the deployment environment.

The [testing guide](automated-testing.md) explains provider measurements and
crash checks. [Interoperability coverage](interoperability.md) records the scope
of current desktop and replay validation. Authentication and two-desktop
coauthoring are separate from durable storage.
