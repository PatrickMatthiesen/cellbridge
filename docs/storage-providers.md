# Storage providers and reusable hosting

CellBridge stores canonical protocol graphs and typed document metadata. A
`DocumentPackage` describes a derived Office file by its immutable graph root,
reconstruction version, length and SHA-256. A changed file save does not store a
second complete downloadable package. Downloads reconstruct and verify the
selected version.

The `CellBridge` package contains the protocol and storage contracts.
`CellBridge.AspNetCore` supplies HTTP endpoints and hosting registration.
PostgreSQL and SQL Server providers use the same normalized repository operations
with their own EF mappings and migrations. Document and Lists packages have
independent dependencies and schemas. All shipping CellBridge packages use the
same beta version and target .NET 11.

See [tested environments](supported-environments.md), the
[design decision](office-storage-design.md) and the [host update procedure](#updating-a-host).
Database and filesystem durability still require qualification on the deployment's
actual storage. Live Office editing against SQL Server remains unverified.

## Consume the packages

Initialize the database with setup credentials and all hosts stopped. Constructors,
registration, health checks and requests do not create or migrate schemas.

For PostgreSQL:

```csharp
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.PostgreSql;
using Npgsql;

var limits = new StorageLimits();
await using var setupSource = NpgsqlDataSource.Create(setupConnectionString);
await new PostgreSqlStorageMigrator(setupSource, limits).MigrateAsync();
```

The runtime host uses its own credentials:

```csharp
using CellBridge.AspNetCore;

var builder = WebApplication.CreateBuilder(args);
var source = NpgsqlDataSource.Create(runtimeConnectionString);
builder.Services.AddSingleton(source);
var content = new PostgreSqlContentStore(source, limits.MaxObjectBytes);
var documents = new PostgreSqlStateStore(source, limits, content);
var provider = new StorageProvider(documents, content, limits);
builder.Services.AddCellBridge(provider, multipleInstances: true,
    configure: options => options.EnableOfficeRealTime = false);

// Register authentication and map validated claims to cellbridge:subject.
var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
await provider.CheckHealthAsync();
app.MapCellBridge();
app.MapHealthChecks("/health");
await app.RunAsync();
```

See [authentication integration](authentication.md#reusable-hosts) for the subject
claim and Office sign-in exchange. Stored document grants deny access by default.
The host must authorize document creation and provide the authenticated actor.
GUID-first creation/import overloads preserve a host's stable resource identity.
Unknown resource IDs never fall back to a path or create a document.

For SQL Server, initialize separately:

```csharp
using CellBridge.Storage.SqlServer;
using Microsoft.EntityFrameworkCore;

var setupOptions = new DbContextOptionsBuilder<SqlServerDocumentDbContext>()
    .UseSqlServer(setupConnectionString).Options;
await new SqlServerStorageMigrator(setupOptions, limits).MigrateAsync();

var runtimeOptions = new DbContextOptionsBuilder<SqlServerDocumentDbContext>()
    .UseSqlServer(runtimeConnectionString).Options;
var content = new SqlServerContentStore(runtimeOptions, limits.MaxObjectBytes);
var documents = new SqlServerStateStore(runtimeOptions, limits, content);
var provider = new StorageProvider(documents, content, limits);
```

SQL Server requires `DELAYED_DURABILITY = DISABLED` and
`ALLOW_SNAPSHOT_ISOLATION = ON`. Explicit initialization enables snapshot isolation.
Runtime credentials need access to the normalized document tables, immutable
objects/chunks and recovery receipts, and read access to the schema version.
Initialization and maintenance use separate credentials. The setup tool grants
runtime access without permission to migrate schemas or delete content objects.

Use a non-retrying EF execution strategy. `EnableRetryOnFailure` is rejected
because CellBridge owns transactions and resolves uncertain outcomes through
receipts. Supplied options retain connection settings, timeouts, logging and
interceptors; providers normalize migration and tracking settings on a copy.
Each operation owns its context. Do not share one context across concurrent
operations, and dispose content read streams. Database chunks and their repository
must use the same database and schema configuration.

### Provider composition constraints

`StorageProvider.Documents` is the required `IDocumentRepository`.
`StorageProvider.Content` stores immutable physical objects, and
`StorageProvider.Packages` reads verified derived packages. Every repository must
provide the typed read, publication, control, lifecycle, delivery and complete
recovery operations. There is no optional aggregate callback path.

Pass consistent `StorageLimits` to the repository, content object's size limit,
provider and initializer. Pass the actual content reader to the repository when
physical objects use another backend. This lets publication verify physical
payloads and their indexed metadata before acquiring a document guard.

For PostgreSQL-inventoried filesystem content:

```csharp
var content = new PostgreSqlFileSystemContentStore(source, root,
    limits.MaxObjectBytes, shared: false);
var documents = new PostgreSqlStateStore(source, limits, content);
var provider = new StorageProvider(documents, content, limits);
builder.Services.AddCellBridge(provider, multipleInstances: false);
```

The same reader is required for upgrades of existing filesystem-backed data:

```csharp
await new PostgreSqlStorageMigrator(source, limits, content).MigrateAsync();
```

A missing filesystem reader fails initialization and rolls back its database
changes. Changing configuration does not move existing objects. Use an explicit
[provider portability workflow](provider-portability.md).

For disposable development:

```csharp
using CellBridge.Storage.InMemory;

var content = new InMemoryContentStore(limits.MaxObjectBytes);
var provider = new StorageProvider(new InMemoryDocumentRepository(content, limits),
    content, limits);
builder.Services.AddCellBridge(provider, requireDurability: false);
```

In-memory state and content disappear when the process stops. Registration with `requireDurability: true` rejects providers whose data cannot
survive a restart. Multi-instance registration also requires shared storage.
Filesystem storage supplies content only. Separate local directories on separate
machines are not shared storage.

## Run the PostgreSQL sample

Stop Aspire before building outputs used by its processes:

```sh
dotnet build CellBridge.slnx
aspire start --isolated --apphost src/CellBridge.AppHost/CellBridge.AppHost.csproj --non-interactive
aspire wait webdemo --apphost src/CellBridge.AppHost/CellBridge.AppHost.csproj --non-interactive
```

The AppHost retains PostgreSQL data in its development volume and runs explicit
storage initialization before WebDemo. Imports create only missing documents;
saves do not write back to the import directory. Removing the database volume
removes its persisted data. See [account setup](authentication.md#first-setup).

Outside Aspire, run `tools/CellBridge.Storage.Setup` with the setup connection
string before starting the host. PostgreSQL initialization accepts supported
schemas 3 through 9. SQL Server's current document schema is 4. Initializers
preserve document data, old recovery snapshots, physical objects and consumer
business tables. Unknown schemas or migration histories fail without resetting
the database.

### Configuration

Environment variables use double underscores, such as
`Storage__ContentProvider=FileSystem`.

| Setting | Meaning |
| --- | --- |
| `Storage:Provider` | `PostgreSql`, `SqlServer` or explicitly volatile `InMemory`. Aspire defaults to PostgreSQL. |
| `ConnectionStrings:cellbridge` | PostgreSQL connection string, supplied by Aspire in local development. |
| `ConnectionStrings:documents` | SQL Server document connection string. |
| `Storage:ContentProvider` | `PostgreSql` or `FileSystem` for PostgreSQL repository storage. |
| `Storage:ContentRoot` | Absolute filesystem content directory, required for filesystem objects and their migration. |
| `Storage:MultipleInstances` | Require shared repository and content guarantees. |
| `Storage:SharedContent` | Operator declaration that the filesystem root is actually shared and has verified flush/rename behavior. Default false. |
| `Protocol:MaxRequestBytes` | Maximum SOAP/MTOM body, default 128 MiB. |
| SOAP XML limit | Fixed 16 Mi characters, independent of the HTTP body limit. Inline base64 reaches this ceiling before a 128 MiB transport body. |
| `Protocol:MaxConcurrentRequests` | Cellstorage POSTs and current/history document GETs share eight active requests per process, including reconstruction and delivery. Excess requests return HTTP 503. |
| `Protocol:MaxConcurrentRequestsPerSubject` | Shared active-request limit per authenticated subject, default 2. Excess requests return HTTP 429. |
| `Protocol:RequestTimeout` | Absolute deadline through request reading, download reconstruction and response delivery, default `00:02:00`. |
| `Protocol:PublicOrigin` | Optional fixed external HTTPS origin. Without a fixed Office/Protocol origin, configure specific remote `AllowedHosts`. |
| `Protocol:MaxMtomParts` | Maximum MIME parts per request, default 128. |
| `Protocol:MaxMtomHeaderBytes` | Maximum header bytes per MIME part, default 16 KiB. |
| `Protocol:CaptureDirectory` | Optional correlated raw traffic capture directory. Disabled by default. |
| `Protocol:MaxCaptureBytes` | Per-process capture limit, including existing evidence, default 1 GiB. Use a separate directory per process. |

### Deployment constraints

`SharedContent` does not turn a local directory into shared storage or change its
permissions. Every host and recovery environment must see the same objects.
New content directories use Unix mode `0700`; new content, spool and recovery
files use `0600`. Existing permissions are preserved. Shared filesystem hosts
should use the same operating-system identity, or explicitly configured restricted
ACLs that also cover newly created files. On Windows, files inherit the parent
ACL. Restrict content, recovery output and service temporary directories.

Request bodies larger than 256 KiB, save preparation, content writes, derived
packages and external delivery can use temporary files. Aspire points `TMPDIR`,
`TMP` and `TEMP` at `artifacts/tmp` by default; `Parameters__TemporaryDirectory`
overrides it. Other hosts can set the operating system's temporary directory
before starting the process. Use a private disk-backed directory. A `tmpfs`
temporary directory consumes RAM. Closing operation scratch files removes them;
interrupted processes can leave files that require explicit cleanup.

Body staging does not eliminate parsing allocations. Eight requests at the
128 MiB body limit can allocate 1 GiB for bodies alone, before graph and parsing
objects. Request concurrency is not a process memory limit, and direct service
calls do not use HTTP admission limits. Set sizes and concurrency for the host's
available memory.

Document GETs retain their admission permit until the reconstructed package
stream closes. A slow transfer therefore occupies its slot until completion,
disconnect or the request deadline. Choose document size and concurrency limits
for temporary disk capacity as well as memory. At the default limits, eight
128 MiB downloads can retain 1 GiB of reconstructed package files. Direct
package/service calls need their own caller-managed concurrency and cancellation.

Before accepting a file publication, CellBridge reconstructs a seekable package,
checks `[Content_Types].xml` and the extension's main document entry, reads ZIP
members and limits expanded bytes. These checks reject some broken or oversized
packages. They do not validate Office XML or prove desktop Office compatibility.

## Commit and retry behavior

Save preparation reads a coherent current control view, selected bindings and
explicitly affected mapping keys. Immutable elements and metadata use scoped,
targeted lookups. Ordinary controls, metadata reads and package downloads do not
decode a retained `DocumentState` aggregate or enumerate application history.

Immutable physical input is staged and verified before the affected document's
publication guard. Publication obtains provider time and checks current authority,
lifecycle, receipts, coordination and mapping fences. It atomically accepts the
new roots, indexed records, document counters, receipt and any outbox entry.
PostgreSQL uses affected-head row locks and repeatable-read snapshot reads.
SQL Server uses affected-head update locks and snapshot reads. Ordinary operations
do not take provider-wide publication or namespace gates.

Storage-index wire elements are transmission envelopes. Key updates apply
atomically without replacing unrelated mapping keys. Providers keep current
mapping values and historical visibility intervals, with logical root identities
for selected versions. Raw incoming index envelopes do not accumulate as canonical
graph objects.

Current bindings support latest-version reads. Immutable effective changes and
synchronously prepared checkpoints support historical reads. The shipping policy
bounds a replay tail at 128 publication changes and 1 MiB of encoded changes.
Providers reject a publication that requires a checkpoint but omits it. These
prototype bounds require measurement before tuning; they are not a throughput
qualification.

Receipts bind an operation to its digest, actor, lifecycle and accepted result.
A current accepted retry reuses the receipt without publishing another version.
Protocol replay rejects a superseded file content version or metadata index.
Whole-file and restore receipts retain their accepted immutable revision.
Unknown commit outcomes require receipt lookup before retrying. Current access is required for replay; an old
accepted save does not restore revoked permissions. Genuine ownerless legacy
receipts remain preserved and cannot authorize replay.

A stale preparation returns a reprepare result. Explicit client coherency
preconditions still produce their protocol error. Metadata publications can add
an application revision while retaining file bytes, file content version and file
authorship/modification fields. Changed file publications advance the file version
and enqueue delivery when a destination is bound. A whole-file unchanged retry
can record its receipt without adding an application revision.

Supported operations commit independently. A later failing operation does not
undo an earlier accepted save. Unsupported partial upload modes remain explicit
errors; hints do not enable partial-upload support. Session, permission and lease
changes use typed controls under the document guard. Waiting does not extend a
lease because the provider reads authoritative time after acquiring the guard.

PostgreSQL requires `fsync=on`, sets `synchronous_commit=on`, and uses five-second
lock and 30-second statement timeouts. Read from the primary. Replication failover
must preserve acknowledged commits. SQL Server requires delayed durability to be
disabled. A storage failure never switches the host to volatile storage.

## Filesystem writes and retention

Physical writes accept non-seekable streams, enforce the object limit and publish
complete immutable objects under their SHA-256 keys. Filesystem writes flush a
temporary file before rename. Linux synchronizes directories and newly created
parent entries; Windows uses write-through flushes and `MOVEFILE_WRITE_THROUGH`.
Reads verify length and SHA-256. Filesystem reads verify before returning a stream;
PostgreSQL verifies chunks while streaming. Dispose every returned stream.

Application revisions, immutable protocol records, operation receipts and pending
deliveries have separate purposes. Controls update small current records instead
of appending complete provider snapshots. Historical packages select their
immutable graph root and resolved bindings. Retained legacy aggregate rows exist
for migration evidence and offline retention; ordinary exports use normalized
records and never return those stale aggregates as current state.

### Collect orphan content

When SQL state and content use the same configured database, Office, whole-file
and restore saves buffer up to 8 MiB and 64 provisional objects in owned memory.
The provider validates their graph records before opening the write transaction,
inserts the payloads, then locks the affected document row and publishes its
records, limits, receipt and delivery together. Rejection, replay or cancellation
before commit rolls back tentative content. An uncertain commit still resolves
through the operation receipt. Physical content and its document ownership charge commit together.

External content stores and saves that exceed the provisional buffer instead
reserve capacity in the affected document before writing new physical content. Each unique handle and its ownership record
count toward `MaxDocumentStoredBytes`, including when the save later fails.
Reservation takes that document's row lock and does not advance its version.
Repeated attempts with the same handle reuse the charge. Publication completes
the ownership records in its transaction without charging the payload again.
No provider-wide counter or bucket participates.

A pending reservation may have complete bytes, partial external staging, or no
physical object after cancellation or a failed write. Recovery preserves its
descriptor and charge without treating it as published content. Only offline,
quiescent collection releases unused reservations. Raw `IContentStore` writes
are trusted storage operations outside this document admission contract. Fresh
document creation remains a separate host-authorized operation with no global
document-count limit.

Failed staging can leave unreferenced physical objects. Interrupted filesystem
writes can leave `.tmp` files. File age and the latest package alone cannot
establish that an object is unreferenced.

Preview PostgreSQL-backed inventory:

```sh
dotnet tools/CellBridge.Storage.Setup/bin/Release/net11.0/CellBridge.Storage.Setup.dll --collect-orphans
```

Supply the connection string and filesystem root through configuration. Stop every
host, reader, writer and delivery worker before applying collection with
`--apply --quiescent`. There is no ordinary global gate that makes an online sweep
safe. Collection preserves normalized document-owned physical references and
retained legacy snapshot roots, including tombstones and recreated ancestors.
It checks complete normalized metadata and reference inventories before deleting
orphan candidates. Missing reference records fail collection before any deletion.
Deletion and recreation reject a document with pending delivery entries.

SQL Server exposes the same explicit operation:

```csharp
var maintenance = new CellBridge.Storage.SqlServer.StorageMaintenance(setupOptions, limits);
var preview = await maintenance.CollectOrphansAsync();
var applied = await maintenance.CollectOrphansAsync(apply: true, quiescent: true);
```

Apply releases pending reservations and deletes only unreferenced objects/chunks.
It does not prune graph history.
Use maintenance credentials with content deletion rights. In-memory storage has
no persistent collector.

### Backup and recovery

`IDocumentRepository` requires complete provider recovery. A coherent capture
contains every normalized identity, including retired documents; immutable graph
records and intervals; mappings and logical roots; binding changes/checkpoints;
application revisions; permissions; receipts; outbox records; path claims and
physical references. Capturing only current heads and application revisions would
lose intermediate protocol publications.

Import validates complete normalized bundles and referenced content. It publishes
all document state and the archive operation's stored receipt atomically into an
empty namespace. A repeated accepted archive operation returns its receipt; a
conflicting fingerprint fails. A lost commit response is resolved through that
ledger. Recovery-only coordination does not participate in ordinary requests.
Current portable recovery rejects host-owned authorization bindings and active
editor sessions or leases. See [portable recovery](provider-portability.md).

Native database backups are a separate operational choice. With filesystem
content, preserve every immutable object referenced by the database recovery
point. Backup recovery points are not collector roots. Test the chosen restore
process, retain matching content and never replace missing objects by reseeding.

### Updating a host

1. Record package versions, database schema, content configuration and credentials.
   Keep the prior binaries and configuration.
2. Stop all writers and delivery workers. Back up the database and referenced
   filesystem objects. Test restoration separately.
3. Update shipping CellBridge packages together and build matching setup tooling.
4. Run explicit initialization. PostgreSQL schema 8 and SQL Server schema 3 remove
   the old global quota tables. PostgreSQL schema 9 and SQL Server schema 4 create
   normalized records and backfill current controls and retained application
   revisions from validated legacy data. Filesystem upgrades require the matching
   content reader/root. PostgreSQL schema 10 and SQL Server schema 5 add charged
   staging reservations to document content ownership. Existing data and consumer
   tables are preserved.
5. Start one host and verify health, identities, permissions, downloads and a
   save/reopen cycle before enabling other writers or delivery workers.

Initialization does not downgrade databases. Rollback requires stopped writers
and restoration of the matching prior database, content, binaries and settings.
Do not run old writers against the migrated schema.

## Implement a provider

Implement `IDocumentRepository` and `IContentStore`, then compose a
`StorageProvider`. Repository durability and sharing flags describe actual backend
guarantees. The complete configuration must satisfy restart-persistence and multi-host registration requirements.

### Repository contract

Return detached typed records. Keep the current `DocumentHead` bounded; application
history, receipts, graph records and outbox entries have indexed records of their
own. Multi-record reads must be coherent. Identity lookup has one explicit target;
unknown resource IDs cannot resolve through a URL fallback.

Dispatch typed controls through `DocumentControlReducer`. Publication must honor
actor/authority generations, lifecycle, coordination, prepared file versions,
operation receipts and affected mapping preconditions. Invoke no arbitrary
whole-history callback. `ReserveContentAsync` checks current write authority and
charges new handles under the affected document guard before physical staging.
Publication takes that guard again and commits the document records together.
Matching SQL providers can also implement `IAtomicDocumentPublisher` to insert
owned provisional content and publish the document in one transaction. They must
roll back those inserts on every rejected or replayed publication, validate the
supplied bytes before taking the document guard, and preserve content-key lock
ordering. This optional operation does not replace external staging admission.
Preserve pending reservations after cancellation or an uncertain commit; their
bytes may already exist. Do not retry an arbitrary mutation after an unknown
commit outcome.

Mapping serial claims retain their scoped key/target meaning. Mapping updates
change only requested keys. Persist the current catalog's visibility intervals,
current resolved bindings and historical effective changes/checkpoints. Historical
reads replay a bounded tail from a checkpoint. Full mapping enumeration is an
explicit query/export operation; ordinary preparation reads only affected keys.
Keep package readers and control-only operations independent of retained history.

### Content and package contracts

`IContentStore.WriteAsync` returns a handle only after the provider stores the complete immutable
object according to its declared persistence guarantees. Never recycle keys or
hide corruption. Check all aliases before caching or deduplicating a key.
`IContentBatchReader` and `IContentBatchWriter` may batch object operations while
preserving length/hash checks, cancellation and atomic batch failure.

Stores used for ordinary document saves must return the lowercase SHA-256 digest
as both `Key` and `Sha256` for new content. This lets the service reserve a known
handle before any committed write. All bundled stores follow this rule. Trusted
offline recovery can still remap provider-specific keys, and retained historical
handles remain readable; that does not qualify an arbitrary-key writer for new
document saves.

`IDocumentPackageReader` reproduces one exact immutable package version and verifies
its recorded digest and length. Package descriptors are not physical content keys.
The canonical graph retains opaque protocol identities and payloads; file
reconstruction remains a separate validated adapter. Unsupported non-file uploads
do not become supported merely because a package can be created or downloaded.

### Recovery contract

`IIndexedRepositoryRecovery` includes whole-provider capture, receipt lookup and
atomic import. `NormalizedDocumentSnapshot` has an explicit format version and
contains provider-independent domain records. Do not export EF rows or silently
omit history. Validate duplicate/cross-scope identities, intervals, bindings,
immutable metadata, package roots, physical references and counters before import.
The receipt ledger commits with every imported head.

## Limits and qualification

`StorageLimits` bounds individual operations and documents. Providers do not cap
total stored bytes or document count, and independent documents update no shared
usage counter or allowance bucket.

| Setting | Default | Scope |
| --- | --- | --- |
| `MaxDocumentBytes` | 128 MiB | Derived package and expanded ZIP bytes. |
| `MaxGraphBytes` | 512 MiB | Retained canonical element payload bytes per document. |
| `MaxGraphElements` | 100,000 | Retained canonical element records per document. |
| `MaxDocumentStoredBytes` | 1 GiB | Canonically charged metadata and unique document-owned physical references. |
| `MaxObjectBytes` | 512 MiB | One physical content object. |
| `MaxSaveReceipts` | 10,000 | Protocol operation receipts per document. |
| `MaxHistoryRevisions` | 1,000 | Application revisions per document. |
| `MaxRestoreReceipts` | 1,000 | Keyed restore receipts per document. |
| `MaxWholeFileSaveReceipts` | 10,000 | Keyed whole-file save receipts per document. |
| `MaxRetiredPathKeys` | 128 | Permanently claimed aliases per resource. |
| `MaxPendingExternalRevisions` | 128 | Pending delivery entries per document. |
| `MaxPendingExternalBytes` | 1 GiB | Pending derived package lengths per document. |

Metadata charges use UTF-8 domain JSON, owned path strings and unique physical
reference lengths. The head's own `StoredBytes` field is serialized as zero when
charging the head, so its decimal representation does not change its own charge.
Current mapping values and their immutable intervals each occupy a charged record.
Row/index overhead, WAL, scratch files and process allocation overhead are outside
these logical limits. Legacy aggregate decoding retains separate graph-membership
bounds; it does not define normal repository reads.

Creation maps quota failures to HTTP 507; file saves use Win32 `ERROR_DISK_FULL`
112. Oversized HTTP bodies return 413, and a proxy can reject a request earlier.
Defaults are admission bounds rather than qualified Office file-size claims.
Automatic graph/history pruning remains disabled. See
[protocol retention](protocol-version-decision.md#synchronization-knowledge) and
[automated testing](automated-testing.md) for constraints and evidence.
