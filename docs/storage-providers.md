# Storage providers and reusable hosting

See [tested environments and recovery limits](supported-environments.md) and the
[upgrade procedure](#updating-a-host) before updating a host with stored documents.

CellBridge stores document state separately from file content. PostgreSQL or
SQL Server holds document identities, revisions, synchronization graphs, save
receipts, sessions and leases. Content uses database chunks or immutable filesystem
objects. In-memory stores are available for disposable development.

The `CellBridge` core package contains document and storage contracts. Provider
packages depend on core; `CellBridge.AspNetCore` adds HTTP and hosting adapters.
Document storage and Lists use separate contexts, migrations and package
dependencies. PostgreSQL and SQL Server each own their EF model. Legacy schema
upgrades retain their data transforms.

PostgreSQL and SQL Server use the same document operations. Each provider owns
its database mappings and migration history. Required database locks stay enabled.

`CellBridge.Storage.SqlServer` and `CellBridge.Lists.SqlServer` are independent
.NET 11 packages. Use the same version for every CellBridge package. Each
package has its own schema and explicit migrator. Neither depends on PostgreSQL
or on the other SQL Server package. The demo does not use these providers. Live
Office editing against SQL Server has not been validated. See [Lists setup](lists.md#sql-server-setup-and-runtime)
and [SQL Server engine checks](automated-testing.md#sql-server-engine-checks).

SQL Server consumers run initialization separately with setup credentials and
all hosts stopped:

```csharp
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.SqlServer;
using Microsoft.EntityFrameworkCore;

var limits = new StorageLimits();
var setupOptions = new DbContextOptionsBuilder<SqlServerDocumentDbContext>()
    .UseSqlServer(setupConnectionString).Options;
await new SqlServerStorageMigrator(setupOptions, limits).MigrateAsync();
```

The host then uses restricted runtime credentials:

```csharp
var runtimeOptions = new DbContextOptionsBuilder<SqlServerDocumentDbContext>()
    .UseSqlServer(runtimeConnectionString).Options;
var provider = new StorageProvider(
    new SqlServerStateStore(runtimeOptions, limits),
    new SqlServerContentStore(runtimeOptions, limits.MaxObjectBytes), limits);
builder.Services.AddCellBridge(provider, multipleInstances: true);
await provider.CheckHealthAsync();
```

Import `CellBridge.AspNetCore` for `AddCellBridge`. Pass the same runtime options
instance to both stores so they share one byte budget. Registration, health
checks and requests never migrate schemas. Do not enable EF automatic execution
strategy retries. Retry protocol operations with the same operation identity to
replay an accepted save.

SQL Server requires `DELAYED_DURABILITY = DISABLED`. Runtime credentials need
data access to the document tables, read access to `schema_version` and `usage`,
and update access to `usage.StoredBytes` and `usage.DocumentCount`. They do not
need schema, migration-history or quota-limit write permissions. Host
authorization still controls document operations. The provider uses database
locks to coordinate hosts; these locks cannot be disabled.

SQL Server provides explicit quiescent collection through
`CellBridge.Storage.SqlServer.StorageMaintenance`. Stop every host, reader, writer
and publisher before applying it. Use maintenance credentials with delete access
to the owned content objects and chunks, read access to the document inventory and
usage ledger, and update access to `usage.StoredBytes`. The normal runtime login
used by the engine tests cannot delete content.

```csharp
var maintenance = new CellBridge.Storage.SqlServer.StorageMaintenance(options, limits);
var preview = await maintenance.CollectOrphansAsync();
var applied = await maintenance.CollectOrphansAsync(apply: true, quiescent: true);
```

The dry run removes nothing. Apply deletes only unreferenced objects and chunks
and updates the byte ledger in one transaction. Stop all readers and writers
before applying collection. The provider checks retained snapshots, history,
receipts and recovery data before deleting anything. An invalid inventory aborts
the operation. Database locks protect the collection transaction, but they do not
replace the requirement to stop all hosts and workers first.

## Run the PostgreSQL sample

With Aspire stopped, build and start from the repository root:

```sh
dotnet build CellBridge.slnx
aspire start --isolated --apphost src/CellBridge.AppHost/CellBridge.AppHost.csproj --non-interactive
aspire wait webdemo --apphost src/CellBridge.AppHost/CellBridge.AppHost.csproj --non-interactive
```

Aspire uses a PostgreSQL volume that retains data between runs. The `storage-init`
resource initializes fresh/current storage, and the host checks both stores
before serving requests. [Provision an account](authentication.md#first-setup)
to use the library. Removing the volume removes its persisted data.

Outside Aspire, build and run `tools/CellBridge.Storage.Setup` with
`ConnectionStrings__cellbridge` before starting the host. Setup accepts fresh
or schema-6 databases and upgrades schemas 3, 4 and 5 while preserving documents
and retained snapshots. Stop all hosts and workers using the database first.
Unsupported schemas, corruption or exceeded limits abort the upgrade without
changing persisted data. Follow the [upgrade procedure](#updating-a-host)
for backups and rollback.

Consumers can invoke the same migration implementation from their own initializer
or deployment application. The implementation is in the reusable
`CellBridge.Storage.PostgreSql` package:

```csharp
await using var source = NpgsqlDataSource.Create(connectionString);
await new PostgreSqlStorageMigrator(source, limits).MigrateAsync();
```

Run this step with setup credentials and every writer stopped. Service
registration, health checks and ordinary document operations do not create or
migrate tables. Existing SQL-created schemas keep their documents, content,
graphs, replies and retained snapshots when setup records the EF migration
history. The application keeps ownership of its own business tables.

Current metadata and paged revision summaries avoid loading complete document
history with the built-in stores. See [reading document metadata and history](async-document-authorization.md#read-document-metadata-and-history).
Graph operations, saves and restores still load complete states. Derived read
indexes take physical disk space in addition to the authoritative state and
content counted by storage quotas.

### Configuration

| Setting | Meaning |
| --- | --- |
| `Storage:Provider` | `PostgreSql` or explicitly volatile `InMemory`. Aspire selects PostgreSQL; the sample executable's standalone default is InMemory. |
| `ConnectionStrings:cellbridge` | Required PostgreSQL connection string, supplied by Aspire in local development. |
| `Storage:ContentProvider` | `PostgreSql`, the default for PostgreSQL state, or `FileSystem`. |
| `Storage:ContentRoot` | Absolute filesystem content directory, required for FileSystem. |
| `Storage:MaxDecodedGraphMembershipBytes` | Maximum graph-reference bytes per document state, default 128 MiB. Applies to reads, writes and upgrades. This is separate from disk quotas and does not limit total process memory. |
| `Storage:MaxObjectBytes` | Maximum bytes per binary object, default 512 MiB. This is a quota, not a tested document-size promise. |
| `Storage:MultipleInstances` | Require shared state and content; reject incompatible provider combinations. |
| `Storage:SharedContent` | Operator declaration that the filesystem root is shared with verified flush/rename semantics. Default false. |
| `Protocol:MaxRequestBytes` | Maximum SOAP/MTOM request size, default 128 MiB. Large bodies spool to private temporary files before one parsing allocation. Requests and retained graph payloads still occupy memory. |
| SOAP XML limit | Fixed 16 Mi characters before parsing, independent of the HTTP body limit. Inline base64 reaches this ceiling before a 128 MiB transport body; use MTOM for larger binary payloads. |
| `Protocol:MaxConcurrentRequests` | Maximum cellstorage requests per host, including response delivery, default 8; excess requests return HTTP 503. |
| `Protocol:MaxConcurrentRequestsPerSubject` | Maximum active protocol requests per authenticated subject, default 2; excess requests return HTTP 429. |
| `Protocol:RequestTimeout` | Absolute deadline through body reading and response delivery, default `00:02:00`. |
| `Protocol:PublicOrigin` | Optional fixed external HTTPS origin. Without a fixed Office/Protocol origin, remote hosts must configure a specific `AllowedHosts` list; loopback remains available for local tests. |
| `Protocol:MaxCaptureBytes` | Per-process capture byte budget including existing evidence, default 1 GiB. Use a separate directory per process. |
| `Protocol:MaxMtomParts` | Maximum MIME parts per request, default 128. |
| `Protocol:MaxMtomHeaderBytes` | Maximum header bytes per MIME part, default 16 KiB. |
| `Protocol:CaptureDirectory` | Opt-in directory for correlated raw requests/responses. Default disabled. Restrict access and manage retention. |


The graph-reference limit counts logical memberships in current and historical graphs,
even when immutable memberships share backing storage. Exceeding
it rejects the operation without removing history. Raise it only with enough
memory for concurrent operations; graph objects and file buffers consume memory
in addition to this limit. Recovery also has a fixed 128 MiB aggregate limit on
graph-reference bytes across retained snapshots.

Environment variables use double underscores, such as
`Storage__ContentProvider=FileSystem`. Multi-host integration tests customize the
main AppHost before starting it, with a fresh database and a second WebDemo process.

### Deployment constraints

Filesystem content must be visible to every host and recovery environment.
Separate local directories on separate machines cannot provide shared content.
`SharedContent` declares an actual shared root with suitable flush/rename
semantics; it does not make local content shared.

New content directories, content files, spool files and portable recovery files
use owner-only Unix permissions at creation, `0700` for directories and `0600`
for files. Existing directory permissions are preserved. Review existing storage
roots and old objects before deployment; this change does not repair their ACLs.
Shared filesystem deployments should run under the same operating-system
identity. If distinct identities need access, the operator must configure
restricted ACLs for those trusted identities, including access to newly published
files. `SharedContent` does not broaden permissions. On Windows, files inherit
the parent directory ACL, so restrict the content, recovery output and service
temporary directories to the service identity and trusted administrators.

Request bodies larger than 256 KiB, save staging, PostgreSQL content writes and
external file delivery use the system temporary directory. Aspire sets `TMPDIR`,
`TMP` and `TEMP` for its .NET resources to the checkout's `artifacts/tmp` directory.
Set `Parameters__TemporaryDirectory` to override this location. Relative paths
resolve against the checkout root. The HTTP test peer uses the same setting.

Other hosts can set `TMPDIR` on Linux, or `TMP` and `TEMP` on Windows, before
starting the process to select a private, disk-backed directory. A temporary
directory on `tmpfs` uses RAM and counts against the host's memory limit. Writing
to a temporary file does not reduce RAM use on that filesystem. These request,
save and content files close and delete when their operation finishes.

Both a disk-backed `/tmp` and a repo directory store files on disk. The location
also determines who removes files left by interrupted tools. Operating-system
cleanup may remove old files in `/tmp`; `artifacts/tmp` needs explicit cleanup.
Keep reports and saved documents outside both temporary locations.

Before publishing an Office save, CellBridge checks the proposed ZIP package for
`[Content_Types].xml` and the main document entry for its file extension. It reads
each entry and limits the total expanded size. These checks reject some broken
uploads and oversized compressed packages before they replace the current file.
They do not validate the Office XML or prove that Office can open the document.
The current implementation rewinds the stream to check and then store the same
bytes. A temporary file provides that seekable stream without keeping another
full document in RAM. The file itself adds no crash recovery guarantee.

Request parsing still allocates an array for the full body after staging it.
The default eight active requests and 128 MiB body limit therefore allow up to
1 GiB of body arrays alone. Parsing and document graphs need more memory.
`MaxConcurrentRequests` limits request count, not total bytes, and direct document
service calls do not use that HTTP limit. Set request sizes and concurrency for
the host's available memory. Storage quotas do not set a process memory limit.

Use one content configuration for each database. Changing providers requires
an explicit data migration; changing settings alone does not move objects.
Use the [provider portability workflow](provider-portability.md) for validated exports
and atomic recovery into an empty destination. [Revision restoration](revision-history.md)
publishes an older retained version through the regular document service.

## Consume the packages

The eleven shipping packages target .NET 11 and share one version. Install
`CellBridge` for protocol execution and storage contracts, `CellBridge.AspNetCore`
for HTTP endpoints, login integration and dependency registration, and the
provider packages you use. `CellBridge.FssHttpB` remains standalone. Choose state and content
stores from `.PostgreSql`, `.SqlServer`, `.FileSystem` and `.InMemory`; filesystem storage
supplies content only.

For an initialized PostgreSQL database:

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
// Register a real authentication scheme and map validated claims to
// cellbridge:subject. See the authentication guide and NuGet example.
var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
await provider.CheckHealthAsync();
app.MapCellBridge();
app.MapHealthChecks("/health");
await app.RunAsync();
```


To use local filesystem content, replace the content store with
`new PostgreSqlFileSystemContentStore(source, root)` and set
`multipleInstances: false`. Volatile stores require
`AddCellBridge(..., requireDurability: false)`.

For EF configuration, supply `DbContextOptions<CellBridgeDbContext>` built with
`UseNpgsql` to the state store, PostgreSQL content store and migrator. The context
is library-owned and sealed. Runtime stores lease one context per operation from
their private EF context pools. They can share the consumer's configured
`NpgsqlDataSource` and connection pool. Helpers within a transaction share that
operation's context. HTTP request scopes do not determine its lifetime, and
concurrent operations never share one context. Dispose content read streams to
return their context leases. Options containing an explicit `DbConnection` use
unpooled contexts and preserve the configured connection ownership.
Pass the same options instance to both stores for shared storage accounting.
Connection settings, command timeouts, logging and interceptors remain active.
Each store's fixed compiled queries use its configured EF model. A context that
selects a different model uses an ordinary EF query.
Interceptors and custom EF services must support context reuse and concurrent
operations. The context constructor accepts the supplied EF options unchanged;
the stores and migrator configure migration and tracking settings on a copy of
those options. Use
`PostgreSqlStorageMigrator` for initialization and existing-data upgrades.
Configure a non-retrying EF execution strategy. The provider rejects
`EnableRetryOnFailure` because its explicit transactions and receipt checks own
retries after uncertain outcomes.
PostgreSQL row and advisory locks still coordinate document transitions,
namespace changes and shared storage accounting. EF does not replace those
database guarantees.

The host supplies authentication and maps validated principals to CellBridge
subjects. Document grants deny access by default. See
[authentication integration](authentication.md#reusable-hosts) for the claim
contract and the Office sign-in exchange.

`CellBridgeDocumentService.CreateAsync` takes an authenticated actor with
creation permission, sets ownership/authorship and returns null on a conflicting
path or identity. `ImportAsync` takes an explicit owner and importer.
The GUID-first overloads preserve a host's stable resource identity. See
[embedding in another host](package-integration.md) for the parsed-SOAP processor,
request permission limits and accepted-save receipts.
`ResolveAsync` gives supplied ResourceIDs precedence; `ExecuteAsync` handles
supported binary operations. The sample's catalog, Office file generation and
Aspire setup live outside these packages.

Build the core and optional Lists packages, the [consumer example](../examples/NuGetConsumer/README.md)
and its [tests](../tests/CellBridge.Packages.Tests/README.md) with Aspire stopped:

```sh
python3 tools/verify_packages.py
```

The default command builds both package-only document and Lists consumers, runs
non-database checks and reports database skips. Add `--sqlserver` to run them
against separate empty databases in the disposable Aspire SQL Server environment.
Outputs and the isolated feed/cache stay in ignored `artifacts/packages`. See
[package checks](automated-testing.md#package-checks) for evidence and prerequisites.

### Provider composition constraints

Pass consistent `StorageLimits` to the state store, content store's object-size
argument, `StorageProvider` and schema setup. PostgreSQL state/content stores
must share one `NpgsqlDataSource` instance, or the same EF options instance.
SQL Server state/content stores must share the same EF options instance.
Empty in-memory stores composed
through `StorageProvider` share their counters.

`StorageCapabilities.AtomicBudgets` reports coordinated accounting.
Explicit limits reject uncoordinated pairs; default settings permit providers
without that capability. Standalone `FileSystemContentStore` enforces object
size only; its PostgreSQL wrapper adds shared accounting.

## Commit and retry behavior

The document service verifies and combines the retained graph without loading
the previous complete package. Self-contained file saves read retained indexes,
reused incoming identities and the object groups selected by the proposed file.
They preserve every other retained descriptor without reading its payload again.
Inherited, BLOB-backed, multi-cell and other general graph forms use the full
graph path. The service reconstructs the file into an exclusively created
temporary file, checks every ZIP member against the limit on expanded bytes and
rewinds the stream for storage.
The staging file is deleted on success, failure or cancellation.

An ordinary self-contained save does not check unrelated historical graph bodies
for corruption. Those bodies remain referenced and their reads still verify
length and SHA-256, including full graph restore and integrity operations. A
selected or reused payload that fails verification still rejects the save before
staging. Historical package downloads verify the materialized package they read;
they need not read the historical protocol graph.

The service then locks the document row, reads the current state and database
time, rechecks permission, coherency and leases, and saves a snapshot with a
save receipt. File I/O happens outside the lock;
different documents have independent locks.

Receipts bind the proposed storage index to the operation digest, accepted
response and writer. An identical retry of the current content version returns
the accepted response without advancing the version. Conflicting reuse of an
index fails. A delayed retry after another save returns a coherency error.
A lost commit reply triggers receipt lookup before reapplying the operation.
Every retry requires its original writer and current Write access.

Each supported `PutChanges` commits independently. A later failure does not undo
an earlier accepted save. Unsupported partial uploads fail per operation, so
independent queries and complete saves can still succeed. `MultiRequestPutHint`
is a coalescing hint and is accepted for complete saves; it does not enable
staged partial uploads. Explicit binary targets select the file, metadata or
editors partition independently of the SOAP default. Unknown targets fail.

Session and lease updates use the same transaction. Database time is read after
lock acquisition, so waiting cannot preserve an expired lease. Restart does not
extend leases; editor expiry and editor-partition knowledge advance together.

PostgreSQL requires `fsync=on`, forces `synchronous_commit=on`, uses five-second
lock and 30-second statement timeouts, and reads from the primary. Replication
failover needs database settings that prevent loss of acknowledged saves. Storage failures do not
switch the host to volatile storage.

## Filesystem writes and retention

Content writes accept non-seekable streams, check quotas, hash bytes and flush a
temporary file before renaming it to its SHA-256 key.
Linux synchronizes directories and newly created parent entries. Windows uses
write-through flushes and `MoveFileEx` with `MOVEFILE_WRITE_THROUGH`.

Reads verify length and SHA-256. PostgreSQL verifies chunks while streaming;
filesystem reads verify the file before returning a stream, adding a read pass.
Downloads use content and headers from one state snapshot.

Restoration, queries and receipt replay read at most the handle's declared length
plus an EOF probe, verify SHA-256 and honor cancellation. Conflicting handles
sharing a cache key are rejected before caching. Receipt replay requires a complete
successful binary save response without trailing bytes.

PostgreSQL retains the latest 64 state snapshots per document by default,
including lease-only changes. The current state retains its synchronization
graph and receipt identities. Superseded receipt response handles are dropped
from new snapshots, while operation digests remain to reject stale/conflicting
retries. Current-version retries keep their response.

`PartitionState.Elements` is an immutable `GraphElementCollection`. Appended graphs
share their retained prefix, and compact state decoding keeps views into a shared
descriptor table. Normal append histories no longer expand every historical prefix
into a separate array. The persisted encoding remains version 1; existing snapshots,
content identities and retained revisions require no migration. Arbitrary reordered
memberships can still require full membership validation. Explicit expanded JSON and
portability operations can also enumerate complete historical graphs.

The built-in state stores implement `IDocumentPreparationStore`. Save preparation
reads a coherent current state and provider UTC clock without acquiring write
authority. PostgreSQL uses one statement snapshot; SQL Server retains its serializable
read transaction. File saves take that view freshly at save entry, after any earlier
request read or authorization await, and reuse it for preparation. Publication still
rechecks access, receipts, lifecycle, content and coordination under its document
lock. Providers without this optional read retain preparation through a read-only
transition callback.

### Collect orphan content

Failed staging can leave charged orphan objects; interrupted filesystem writes
can leave `.tmp` files. Remove temporary files only with all writers stopped.
Age and the current materialized file are insufficient grounds for deleting
`.blob` objects because later saves may reference older parts.

Report objects unreferenced by every retained snapshot:

```sh
dotnet tools/CellBridge.Storage.Setup/bin/Release/net11.0/CellBridge.Storage.Setup.dll --collect-orphans
```

Supply the database connection and filesystem root through configuration.
Stop every host, reader and writer before adding `--apply --quiescent`.
Database locks cannot protect detached readers or outstanding filesystem writers.
Collection deletes orphan content and releases its charge; it does not prune
the live graph. Filesystem deletion requires Linux directory synchronization.

This collector applies to PostgreSQL state with PostgreSQL content or
PostgreSQL-inventoried filesystem content. It scans every retained state snapshot,
including deletion tombstones and the old resource after explicit recreation.
Current replacement content, historical revisions, retained graph payloads and
receipt responses remain roots. Pending external publications also retain their
content; deletion refuses a document with undelivered entries. See
[conditional deletion and recreation](external-host-reliability.md#deletion-recreation-and-local-eviction).

`LifecycleCollectionTests` runs collection after deletion and again after
recreation for both PostgreSQL and filesystem content. Each sweep removes an unrelated
orphan and verifies retained bytes using fresh provider connections. The tests
also preserve a separate live publication queue. Its saved content has history
references too, so these cases do not establish an exclusive publication pin.
The in-memory provider retains lifecycle state only for the process lifetime and
has no persistent orphan collector. Persistent garbage collection is inapplicable
to it; its deletion/recreation and detached-reader behavior are covered by
`HostLifecycleTests` and `HistoryLifecycleIntegrationTests`.

### Backup and recovery

The [portable archive and recovery tool](provider-portability.md) preserves current
heads, every retained provider snapshot, history, permissions and referenced content
across supported content providers. It rejects incompatible policy/destination
bindings and imports into an empty namespace only. Native database backups remain
a separate environment-specific option.

A PostgreSQL-only backup contains state and content. With filesystem content,
preserve every object referenced by the database recovery point. Append-only
objects allow a content copy completed after the database backup while writes
continue, provided the copy captures complete objects and retention is unchanged.

Keep backup content separate from orphan collection; backup recovery points are
not collector roots. Test restoration with the chosen database/filesystem tools.
Missing referenced content is corruption and is never replaced by reseeding.
The filesystem and storage hardware must honor flush/rename operations;
power-loss and shared-filesystem behavior need deployment-specific qualification.

### Updating a host

1. Record the installed package versions, database schema and content provider.
   Keep the previous binaries and configuration.
2. Stop every writer using the database, including external publication workers.
   Back up PostgreSQL and any filesystem objects referenced by that recovery point.
   Test restoration in a separate environment.
3. Update all CellBridge package references together. Build the application and
   the repository's `CellBridge.Storage.Setup` tool from matching source.
4. Run setup against the stopped database. It migrates storage schema 3, 4 or 5 to 6,
   preserves documents and all retained snapshots, builds the selective read indexes,
   initializes recovery schema 1 and records the document EF migration history.
   Older development schemas need a separate migration or recreation; setup rejects them.
5. Start one upgraded host. Check provider health, document identities,
   permissions, downloads and a save/reopen cycle before enabling other writers.
   Verify publication workers' destination bindings and stored receipts
   before restarting them.

There is no schema-6 downgrade. For rollback, stop upgraded writers and
restore the previous database, matching content objects, binaries and
configuration. This loses changes made after the backup unless you reconcile
them separately. Do not run older writers against the migrated database.

## Implement a provider

Implement `IDocumentStateStore` and `IContentStore`, then compose a
`StorageProvider`. Set storage capability flags only for guarantees provided by
the complete deployed backend.

### State contract

Use `DocumentStateJson` to read and write persisted state and calculate its
encoded size. It supports compact storage encoding 1 and legacy expanded JSON.
The public `DocumentState` records remain logical format 2. Preserve all revision
graph identities and content handles when implementing a provider.

Providers can implement `IDocumentReadStore` for current metadata and paged
revision summaries. Return each selection with the current authorization and
lifecycle metadata from one coherent view. Update any derived read records
atomically with state changes, including recovery import. Without this optional
capability, the service reads complete states.

`TransitionAsync` acquires exclusive coordination for the resource, loads one
coherent snapshot and supplies authoritative time. Invoke the callback once.
Atomically publish `Next` and advance `StateVersion`, or publish nothing for
null/exception. Do not retry callbacks after an unknown commit outcome.
Callbacks perform no I/O; prepare content beforehand.

`IAtomicDocumentSaveStore` optionally publishes prepared immutable content and a
state transition in one transaction. Advertise support only for a content store
that shares the same transaction and quota scope. Keep callback-once semantics,
authoritative time, state validation and receipt recovery. Quota rejection or
cancellation before commit must roll back both content and state. PostgreSQL and
SQL Server use this path for bounded file saves; other pairings use the separate
content-write path. A null next state can still commit staged content and its
quota usage; orphan collection can reclaim content that no state references.

Content stores can implement `IContentBatchReader` and `IContentBatchWriter` to
reduce database round trips. Batch operations retain per-object length and hash
checks, bounded admission and immutable-content verification. A batch write must
roll back all newly inserted objects if quota admission or cancellation fails.

Return immutable detached records, enforce unique paths/resource IDs and forbid
identity/path changes in transitions. Preserve unsigned protocol values, serials,
GUIDs, timestamps and format versions. Path keys use normalized invariant
uppercase with ordinal comparison; percent escapes are decoded once and Unicode
normalization is not applied.

### Generic graph persistence

`DocumentPartition.CaptureGraphAsync` and `RestoreGraphAsync` preserve opaque
graphs separately from application file reconstruction. They reuse document
state format 2 and the existing graph-element records, with no database schema
change. Existing file states retain their legacy reconstruction path. Generic
restore is stricter about mapped revision closure; it rejects incomplete graphs
rather than treating them as opaque file bytes.

Every retained element, including separately referenced BLOBs, has an immutable
verified content handle. Storage-index mapping serials are persisted and checked
against payload bytes on restore. Capture rejects reused IDs with changed type,
payload or conflicting non-null serials. Knowledge and unsigned identities
round-trip unchanged. Existing provider reference enumeration includes these
handles, so state snapshots protect them from orphan collection. Automatic
graph pruning remains disabled.

Capture stages content and returns a detached partition. Its caller owns
authorization, document consistency and atomic publication. It does not update
materialized document content or enable non-file protocol uploads. File
publication uses the separate validated file adapter. Generic restoration
enforces graph/object budgets, integrity and cancellation; permitted object/cell
cycles are bounded separately from invalid revision-base cycles.

### Content and budget contracts

`WriteAsync` stores the content before returning an immutable handle.
Handles are not recycled and remain readable after restart. Open a stream for
each read and report corruption/storage failures explicitly. Content reclamation
requires quiescent maintenance over all retained snapshots.

For atomic budgets, both stores implement `IStorageBudgetParticipant` and expose
one scope object. Charge state JSON, unique content/reservations and document
identities under one atomic admission mechanism. Wrappers preserve this
coordination; the marker alone does not implement accounting.

Repository provider tests cover save, lease, crash and replay behavior. Hardware
flushes and distributed locking still require qualification in the intended
deployment environment.

## Limits and qualification

The sample binds positive `StorageLimits` from `Storage`:

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
| `MaxHistoryRevisions` | 1,000 | Immutable file/application-metadata publications per document; no automatic expiration. |
| `MaxRestoreReceipts` | 1,000 | Stored keyed restore receipts per document; identities are not evicted. |
| `MaxRetiredPathKeys` | 128 | Permanently reserved names per resource after rename; keys are not evicted. |


Accounting includes reservations and charged orphans, but excludes database
row/index overhead, WAL, spool files and allocator overhead. Each content store admits four spool writers. Duplicate content is charged once. A rejected
save preserves the selected revision; content staged before rejection stays
charged until collected.

The creation API returns HTTP 507 for quota failures, file saves return Win32
`ERROR_DISK_FULL` 112, and oversized HTTP bodies return 413. Server/proxy limits
can reject a request earlier.

Requests and graph payloads occupy memory, and graph merging retains defensive
copies. Package reconstruction and ZIP validation use a seekable staging file;
MTOM output streams without a base64 round trip or complete multipart buffer.
Configured defaults are admission limits rather than qualified Office file sizes. Automatic graph
pruning is disabled, so even a constant-size file can accumulate history until a
quota blocks further saves. See [synchronization knowledge and graph retention](protocol-version-decision.md#synchronization-knowledge)
for the graph rules, and [automated testing](automated-testing.md) for measurements.
