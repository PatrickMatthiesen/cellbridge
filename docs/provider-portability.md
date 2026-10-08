# Provider export, migration and recovery

CellBridge can export a complete provider recovery point, validate it locally and
atomically import it into an empty destination. PostgreSQL state supports either
PostgreSQL chunks or the accounted filesystem content store. In-memory state and
content support disposable API tests. Filesystem content alone is not a state provider.

This workflow copies provider state. It never changes the source or overwrites a
destination document. Use [revision restoration](revision-history.md) to publish
an older version of an existing document.

## Prepare the environments

Build the current source with the hosts in that checkout stopped:

```sh
dotnet build CellBridge.slnx -c Release
```

Initialize the destination with `CellBridge.Storage.Setup` and its own connection
string. Recovery uses storage schema 4 plus recovery schema 1. Explicit setup adds
the recovery tables to an existing schema-4 database and preserves existing state.
Export/import never initialize a database implicitly. Missing, future or multiple
recovery schema versions fail before import writes content.

Keep destination hosts stopped through validation and import. Stop source writers
before the final migration export and leave them stopped after cutover. Online
exports are coherent recovery points, but subsequent source writes are absent
from that recovery point. This release has no incremental migration or live cutover.

The archive preserves editor sessions, all leases and coordination generations
exactly, including their absolute UTC expiration times. Import rejects any current
document with an unexpired editor session, schema lease, exclusive lease or host
lease. Wait for expiration and retry the same archive; do not edit the archive to
remove coordination. Publication checks expiration again using destination time.
Recovery does not extend a lease or invent a new editor graph.

## Authorization and external destinations

Supply trusted source and destination configuration through an explicit context
file, kept outside the repository. A stored-grant example is:

```json
{
  "Authorization": {
    "Mode": "stored-grants",
    "ContractVersion": 1,
    "Domain": "my-application-stable-subject-namespace"
  },
  "ExternalDestinations": []
}
```

Both environments must use the same authorization contract and stable subject
namespace. Context equality is an operator attestation. It does not establish that
accounts or permissions in another system were backed up. The archive preserves
`DocumentSecurity`, ownership, grants and recorded authors exactly. It does not
contain sample Identity accounts, passwords, authentication cookies or protection
keys. Recover those separately and verify the same stable subjects before serving.

Version 1 supports stored-grants authorization contract 1. External-policy modes,
unknown context versions and unknown persisted fields fail closed. Arbitrary
external permission systems cannot be serialized by this workflow. Supporting
host-policy recovery requires a coordinated policy recovery point and an explicit
recovery contract; matching subject names alone is insufficient.
The host-policy contract uses `DocumentSecurity.AuthorizationPolicy` with
`DocumentAuthorizationBinding.PolicyDomain`, `ContractVersion` and `Revision`.
A non-null binding rejects portable recovery in current and retained snapshots.
This workflow does not define another permission schema.

For every external delivery binding, including a binding with no queued revisions,
both context files must contain the same entry:

```json
{
  "BindingId": "00000000-0000-0000-0000-000000000001",
  "Destination": "application-document-store",
  "ReceiptDomain": "the-same-durable-external-delivery-ledger"
}
```

Add entries to `ExternalDestinations` only when the destination name resolves to
the same external system and durable delivery receipt ledger. Unknown, absent or
foreign bindings reject export/import. The archive preserves binding IDs, expected
external revisions, operation IDs, sequences, blocked state and queued content
pins. It does not create an external system or reconstruct its delivery ledger.
Recover that system separately before starting a publisher.

## Operator commands

Set `ConnectionStrings__cellbridge` privately and use the selected provider's
`Storage__ContentProvider`, `Storage__ContentRoot`, `Storage__SharedContent` and
`Storage__...` admission limits. Do not change the content configuration of an
existing database to simulate migration.

```sh
dotnet tools/CellBridge.Storage.Portability/bin/Release/net10.0/CellBridge.Storage.Portability.dll export --archive /backups/cellbridge.zip --context /private/source-context.json
dotnet tools/CellBridge.Storage.Portability/bin/Release/net10.0/CellBridge.Storage.Portability.dll validate --archive /backups/cellbridge.zip --context /private/destination-context.json
dotnet tools/CellBridge.Storage.Portability/bin/Release/net10.0/CellBridge.Storage.Portability.dll import --archive /backups/cellbridge.zip --context /private/destination-context.json
```

`validate` uses disposable local staging and needs no database or content provider.
`import` uses the configured destination. It requires a namespace with no current
or deleted resources, retained state rows, retired aliases or prior recovery
operations. Preexisting unreferenced content can be reused after verification and
still counts against destination storage limits. Identity/path collisions reject
the entire import. There is no merge, replace or force option.

For a provider copy in one invocation, configure `ConnectionStrings__source` and
`ConnectionStrings__destination`. Use `source__Storage__...` and
`destination__Storage__...` for each provider's settings:

```sh
dotnet tools/CellBridge.Storage.Portability/bin/Release/net10.0/CellBridge.Storage.Portability.dll migrate --archive /backups/migration.zip --context /private/context.json
```

This exports a fresh archive and imports it. The archive path must be new. If the
copy is interrupted after the archive exists, use `import` with that same artifact
and destination configuration to resume. A new export is a different recovery
operation and cannot overwrite a previous destination.

After recovering the provider, an authorized actor can restore a retained version:

```sh
dotnet tools/CellBridge.Storage.Portability/bin/Release/net10.0/CellBridge.Storage.Portability.dll restore-version --resource-id GUID --revision 3 --expected-revision 8 --operation-key stable-recovery-key --subject stable-writer-subject
```

The operator supplies the actor's stable subject. The regular document service
still requires its current Write access and checks lifecycle, locks and expected
history. Restore creates a new publication and durable restore receipt; it does
not replace history or bypass permissions. Reuse the same key after an uncertain
commit. Existing unsupported legacy metadata/graph restoration cases remain
explicit errors, as described in the revision guide.

## Archive and publication rules

Archive version 1 is a ZIP with `manifest.json` and ordinal `objects/00000000`
entries. Provider keys are data in the manifest and never become filesystem paths.
The manifest carries:

- A new archive operation ID and versioned authorization/external context.
- Explicit current heads and every retained provider state snapshot, including
  tombstones, replacement links, retired names, histories and durable receipts.
- All referenced immutable content handles, including handles used only by an old
  provider snapshot, retained binary response, historical graph or external pin.
- Previous recovery receipts, so repeated recovery remains recognizable after
  another provider move.

All resource and graph IDs, unsigned serials and knowledge values, metadata,
versions, authors, timestamps and security records remain unchanged. Destination
content stores return their own immutable handles. Import rewrites every handle
reference, including historical partitions and external pins, while retaining
the content length and SHA-256.

PostgreSQL export reads heads, all retained state rows and recovery receipts in
one repeatable-read transaction. Immutable content remains available during saves,
renames, recreation and restoration. Exclude quiescent orphan collection for the
entire export. An archive is not a collector root in the source database; keep its
object bytes with its manifest.

Validation rejects unknown/duplicate JSON fields, unsupported archive/document
formats, duplicate/unexpected ZIP entries, missing objects, inconsistent handle
aliases and incorrect lengths/hashes. It validates every retained snapshot and
history revision, mapping serials, receipt responses and lifecycle namespace.
File reconstruction follows `PartitionGraphSnapshot` and must reproduce the
recorded Office package. It never selects a BLOB by size. Opaque metadata graphs
use generic graph validation. The existing self-contained legacy file allowance
for unused missing ancestors remains; recovery does not claim that every such
graph supports historical rebase. DOCX, XLSX and PPTX packages are supported.

Import completes validation in temporary local files before writing destination
content. Only after every required immutable object is durable and verified does
the provider publish all snapshots, heads and one recovery receipt in an atomic
operation. Readers observe the prior empty namespace or the complete import.
A competing creation either commits before recovery, causing recovery to reject,
or occurs after the complete publication and follows normal collision rules.

The archive operation ID and SHA-256 of the complete artifact identify retries.
That digest includes its exact policy and external context. A matching receipt
returns the original result even after later document changes. Reusing an operation
ID with another artifact or context fails. Copy the exact archive when resuming;
repacking identical logical contents changes its fingerprint.

## Failure, quotas and upgrades

Malformed/incompatible input performs zero destination content writes. Cancellation,
content-store failure, quota rejection or interruption before commit publishes no
document. A commit whose reply is lost is resolved by importing the same archive
again. The provider checks its durable receipt before destination emptiness. Do
not treat cancellation or a transport failure as evidence that commit rolled back.

Import leaves validated staged immutable objects charged when later publication
fails. Abort means leaving the destination offline and discarding local staging,
which the tool removes on normal exception/cancellation. Process termination can
leave temporary `cellbridge-recovery-*` files; remove only those from a stopped
recovery run. Use existing [quiescent maintenance](storage-providers.md#collect-orphan-content)
to reclaim unreferenced destination objects. Never remove referenced objects or
retry receipts to make a different archive fit.

Destination object/document/graph/history/receipt and stored-byte limits all apply.
Every imported snapshot and recovery receipt consumes metadata bytes. Recovery
receipts are retained with a 1,000-entry limit. Independent archive defaults cap
input and expanded bytes at 12 GiB, manifest bytes at 128 MiB, snapshots at 640,000
and ZIP entries at 1,000,001. Override them through `Recovery__...`; these are
admission limits, not qualified production dataset sizes. Graph decoding remains
memory bounded by the configured graph/object budgets, and local staging needs
space for all objects plus one reconstructed package.

Document format 2 retains its established additive defaults for legacy histories
and lifecycle fields. Required constructor fields and mapping serial metadata must
be present. Unknown newer fields fail instead of being silently discarded.
Archive and recovery schema versions are independent of database/document versions.
This release implements no archive converter or downgrade path. Upgrade all hosts
together, with old writers stopped, as the existing state codec requires.

## Evidence and supported environments

`PortabilityTests` and `PortabilityProviderTests` exercise retained identities,
history, restoration, corrupt/incomplete input, concurrent source changes,
competing creation, cancellation, uncertain commit retry and schema/context/quota
rejection. The isolated runner exercises PostgreSQL chunks to accounted filesystem
content to PostgreSQL chunks, including content referenced only by an old state
snapshot. These are server recovery checks, not desktop compatibility evidence.

The supported durable path uses initialized PostgreSQL and Windows/Linux filesystem
stores with their existing flush/rename requirements. Power-loss, PostgreSQL
replication/failover, arbitrary shared filesystems, external policy-system recovery
and real external-host adoption require environment-specific qualification. This
workflow adds no OneNote or two-desktop coauthoring claim and does not publish new
NuGet packages.

For the separate quiescent `pg_dump`/`pg_restore` procedure, controlled database
outages and independent raw-table/content verification, see
[controlled database recovery](supported-environments.md#controlled-database-interruption-and-logical-restore).
That runner uses disposable local resources and does not qualify a deployed
backup, shared filesystem or failover configuration.
