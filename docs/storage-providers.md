# Storage providers and reusable hosting

See [tested environments and recovery limits](supported-environments.md) and the
[beta.1 upgrade procedure](beta-2-upgrade.md) before upgrading a durable host.

CellBridge stores document state separately from file content. PostgreSQL holds
document identities, current revisions, synchronization graphs, save receipts,
sessions and leases. Content uses PostgreSQL chunks or immutable filesystem
objects. In-memory stores are available for disposable development.

## Run the durable sample

With Aspire stopped, build and start from the repository root:

```sh
dotnet build CellBridge.slnx
aspire start --apphost aspire/apphost.cs --non-interactive
aspire wait web --non-interactive
aspire wait demo --non-interactive
```

Aspire uses the `cellbridge-storage-data` PostgreSQL volume. The `storage-init`
resource initializes fresh/current storage, and the host checks both stores
before serving requests. [Provision an account](authentication.md#first-setup)
to use the library. Removing the volume removes its persisted data.

Outside Aspire, build and run `tools/CellBridge.Storage.Setup` with
`ConnectionStrings__cellbridge` before starting the host. Initialization accepts
a fresh database or storage schema version 4, and explicitly migrates version 3
to 4 while preserving documents. Stop hosts using that database before migration;
schema-3 writers fail closed afterward. Older schemas are rejected without
modifying them. Initialization sets authoritative byte/document limits. Existing
data stays intact when a limit is reduced. See [external host reliability](external-host-reliability.md#storage-compatibility-and-validation).

### Configuration

| Setting | Meaning |
| --- | --- |
| `Storage:Provider` | `PostgreSql` or explicitly volatile `InMemory`. Aspire selects PostgreSQL; the sample executable's standalone default is InMemory. |
| `ConnectionStrings:cellbridge` | Required PostgreSQL connection string, supplied by Aspire in local development. |
| `Storage:ContentProvider` | `PostgreSql`, the default for PostgreSQL state, or `FileSystem`. |
| `Storage:ContentRoot` | Absolute filesystem content directory, required for FileSystem. |
| `Storage:MaxObjectBytes` | Maximum bytes per binary object, default 512 MiB. This is a quota, not a tested document-size promise. |
| `Storage:MultipleInstances` | Require shared state and content; reject incompatible provider combinations. |
| `Storage:SharedContent` | Operator declaration that the filesystem root is shared with verified flush/rename semantics. Default false. |
| `Protocol:MaxRequestBytes` | Maximum SOAP/MTOM request size, default 128 MiB. Requests and retained graph payloads still occupy memory. |
| `Protocol:MaxConcurrentRequests` | Maximum cellstorage requests per host, including response delivery, default 8; excess requests return HTTP 503. |
| `Protocol:MaxMtomParts` | Maximum MIME parts per request, default 128. |
| `Protocol:MaxMtomHeaderBytes` | Maximum header bytes per MIME part, default 16 KiB. |
| `Protocol:CaptureDirectory` | Opt-in directory for correlated raw requests/responses. Default disabled. Restrict access and manage retention. |


Environment variables use double underscores, such as
`Storage__ContentProvider=FileSystem`. Set `AppHost__PeerEnabled=true` to add a
second host, then start it with:

```sh
aspire resource web-peer start --apphost aspire/apphost.cs --non-interactive
aspire describe --non-interactive
```

Both hosts use the same database. Discover assigned ports from Aspire.

### Deployment constraints

Filesystem content must be visible to every host and recovery environment.
Separate local directories on separate machines cannot provide shared content.
`SharedContent` declares an actual shared root with suitable flush/rename
semantics; it does not make local content shared.

Use one content configuration for each database. Changing providers requires
an explicit data migration; changing settings alone does not move objects.
Use the [provider portability workflow](provider-portability.md) for validated exports
and atomic recovery into an empty destination. [Revision restoration](revision-history.md)
publishes an older retained version through the regular document service.

## Consume the packages

The reusable packages target .NET 10. Version `0.1.0-beta.1` and its matching symbols
are public on NuGet.org under the CellBridge owner. See the
[release evidence](beta-release.md#published-release). `CellBridge.AspNetCore`
provides endpoint registration and save orchestration. Choose state and content
stores from `.PostgreSql`, `.FileSystem` and `.InMemory`; filesystem storage
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

Build the nine packages, the [consumer example](../examples/NuGetConsumer/README.md)
and its [tests](../tests/CellBridge.Packages.Tests/README.md) with Aspire stopped:

```sh
python3 tools/verify_packages.py
```

Outputs and the isolated package cache stay in `artifacts/packages`.

### Provider composition constraints

Pass consistent `StorageLimits` to the state store, content store's object-size
argument, `StorageProvider` and schema setup. PostgreSQL state/content stores
must share one `NpgsqlDataSource` instance. Empty in-memory stores composed
through `StorageProvider` share their counters.

`StorageCapabilities.AtomicBudgets` reports coordinated accounting.
Explicit limits reject uncoordinated pairs; default settings permit providers
without that capability. Standalone `FileSystemContentStore` enforces object
size only; its PostgreSQL wrapper adds shared accounting.

## Commit and retry behavior

A durable save verifies and combines the retained graph without loading the previous
complete package. It reconstructs into an exclusively created temporary file,
validates every ZIP member under the expanded-byte budget and rewinds for content
storage. Graph payloads still load eagerly. The staging file is deleted on every
exit path. The save then locks the document row, reads
the current state and database time, rechecks permission, coherency and leases,
and publishes a snapshot with a save receipt. File I/O happens outside the lock;
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
failover needs deployment-specific durability settings. Storage failures do not
switch the host to volatile storage.

## Filesystem durability and retention

Content writes accept non-seekable streams, check quotas, hash bytes and flush a
temporary file before publishing its SHA-256 key with a durable rename.
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

### Collect orphan content

Failed staging can leave charged orphan objects; interrupted filesystem writes
can leave `.tmp` files. Remove temporary files only with all writers stopped.
Age and the current materialized file are insufficient grounds for deleting
`.blob` objects because later saves may reference older parts.

Report objects unreferenced by every retained snapshot:

```sh
dotnet tools/CellBridge.Storage.Setup/bin/Release/net10.0/CellBridge.Storage.Setup.dll --collect-orphans
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
recreation for both durable content backends. Each sweep removes an unrelated
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

## Implement a provider

Implement `IDocumentStateStore` and `IContentStore`, then compose a
`StorageProvider`. Advertise durability/shared flags for guarantees provided by
the complete deployed backend.

### State contract

`TransitionAsync` acquires exclusive coordination for the resource, loads one
coherent snapshot and supplies authoritative time. Invoke the callback once.
Atomically publish `Next` and advance `StateVersion`, or publish nothing for
null/exception. Do not retry callbacks after an unknown commit outcome.
Callbacks perform no I/O; prepare content beforehand.

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

`WriteAsync` completes durable publication before returning an immutable handle.
Handles are not recycled and remain readable after restart. Open a stream for
each read and report corruption/storage failures explicitly. Content reclamation
requires quiescent maintenance over all retained snapshots.

For atomic budgets, both stores implement `IStorageBudgetParticipant` and expose
one scope object. Charge state JSON, unique content/reservations and document
identities under one atomic admission mechanism. Wrappers preserve this
coordination; the marker alone does not implement accounting.

### Conformance checks

Run `CellBridge.Storage.Conformance.ProviderConformance.VerifyAsync(provider)`
against a disposable backend; it retains its test records and objects.
Also run the repository's save, lease, crash and replay cases. Verify hardware
flushes and distributed locking in the intended deployment environment.

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
| `MaxRestoreReceipts` | 1,000 | Durable keyed restore receipts per document; identities are not evicted. |
| `MaxRetiredPathKeys` | 128 | Permanently reserved names per resource after rename; keys are not evicted. |


Accounting includes reservations and charged orphans, but excludes database
row/index overhead, WAL, spool files and allocator overhead. Each durable content
store admits four spool writers. Duplicate content is charged once. A rejected
save preserves the selected revision; content staged before rejection stays
charged until collected.

The creation API returns HTTP 507 for quota failures, file saves return Win32
`ERROR_DISK_FULL` 112, and oversized HTTP bodies return 413. Server/proxy limits
can reject a request earlier.

Requests and graph payloads occupy memory, and graph merging retains defensive
copies. Durable package reconstruction/ZIP validation use a seekable staging file;
MTOM output streams without a base64 round trip or complete multipart buffer.
Configured defaults are admission limits rather than qualified Office file sizes. Automatic graph
pruning is disabled, so even a constant-size file can accumulate history until a
quota blocks further saves. See [synchronization knowledge and graph retention](protocol-version-decision.md#synchronization-knowledge)
for the graph rules, and [automated testing](automated-testing.md) for measurements.
