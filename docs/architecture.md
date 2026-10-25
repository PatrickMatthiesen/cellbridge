# Architecture

CellBridge implements the Office file synchronization protocols in reusable
.NET libraries. WebDemo provides a development environment;
[interoperability coverage](interoperability.md) describes the tested client
behavior and unsupported operations.

## Request flow

```mermaid
flowchart TD
    Office[Desktop Office] -->|HTTPS discovery and download| Host[CellBridge.AspNetCore endpoints]
    Office -->|SOAP or MTOM| Host
    Host --> Http[CellBridge.AspNetCore]
    Http --> Core[CellBridge core]
    Core --> Soap[MS-FSSHTTP SOAP/MTOM]
    Core --> Binary[CellBridge.FssHttpB]
    Core --> Service[CellBridgeDocumentService]
    Service --> State[IDocumentStateStore]
    Service --> Content[IContentStore]
    State --> Database[PostgreSQL or SQL Server document state]
    Content --> Blobs[Database chunks or filesystem objects]
```

The `CellBridge` package handles `/_vti_bin/cellstorage.svc`, document execution
and storage contracts. `CellBridge.AspNetCore` owns HTTP routing, authentication
integration and dependency registration. Cell subrequests carry MS-FSSHTTPB
requests inline as base64 or in MTOM attachments.
`CellBridgeRequestProcessor` executes parsed SOAP requests for the mapped endpoint
and direct callers. It resolves document identity and sends binary Cell requests
to `CellBridgeDocumentService.ExecuteAsync`. Direct binary integrations use that
same service. The service validates the request, selects each operation's target
partition and runs the operations in request order. Internal handlers prepare
file and metadata graphs and build query responses. InMemory, PostgreSQL and SQL
Server use this execution path.

The endpoint serializes each operation's protocol response. HTTP 200 alone does
not indicate save success.
See [protocol scope](protocol-version-decision.md).

Authentication runs before parsing. The sample supplies PostgreSQL Identity
accounts, MS-OFBA challenges and shared cookie protection keys. Each operation
checks the caller's stable subject against the resolved document's ownership
and grants. Sessions, leases and receipts retain their authenticated owner.
See [authentication and permissions](authentication.md).

## Libraries and executables

The eleven shipping packages are `CellBridge`, `CellBridge.FssHttpB`,
`CellBridge.AspNetCore`, `CellBridge.Storage.InMemory`,
`CellBridge.Storage.PostgreSql`, `CellBridge.Storage.SqlServer`,
`CellBridge.Storage.FileSystem`, `CellBridge.Lists`,
`CellBridge.Lists.PostgreSql`, `CellBridge.Lists.SqlServer` and
`CellBridge.Lists.AspNetCore`.

| Project | Responsibility |
| --- | --- |
| `CellBridge` | SOAP/MTOM parsing, document execution, storage contracts, document state and persistence codecs |
| `CellBridge.FssHttpB` | Binary framing, manifests, graph data and synchronization messages |
| `CellBridge.Storage.PostgreSql` | EF Core document persistence, explicit migrations, per-document coordination and chunked content |
| `CellBridge.Storage.SqlServer` | Independent EF Core document persistence, explicit migrations, coordinated state and chunked content |
| `CellBridge.Storage.FileSystem` | Immutable binary content with file and directory flushes |
| `CellBridge.Storage.InMemory` | Volatile provider for development and tests |
| `CellBridge.AspNetCore` | HTTP endpoints, login integration and dependency registration |
| `CellBridge.Lists` | Typed workspace lists, schema validation, merge comparisons and bounded queries |
| `CellBridge.Lists.PostgreSql` | Independent EF Core list persistence and migrations, JSONB records, workspace isolation and grants |
| `CellBridge.Lists.SqlServer` | List persistence, migrations, SQL queries, workspace authorization, views and audit |
| `CellBridge.Lists.AspNetCore` | Opt-in list APIs, protected paging, CSV/XLSX and read-only OData subset |
| `CellBridge.WebDemo` | Non-packable development host, Razor Pages, package generation and catalog APIs |
| `CellBridge.Authentication` | Development Identity account store and key protection |
| `tools/CellBridge.Admin` | Operator account provisioning, explicit file imports and document permission updates |
| `tools/CellBridge.Storage.Setup` | Current-schema initialization and quiescent object maintenance |
| `src/CellBridge.AppHost/CellBridge.AppHost.csproj` | PostgreSQL, schema initialization, WebDemo and optional capture proxy |

The binary library is independent of ASP.NET Core and storage. Storage providers
depend on `CellBridge` and do not depend on `CellBridge.AspNetCore`. One AppHost
starts WebDemo, PostgreSQL, initialization and optional capture.

The optional [list engine](lists.md) uses separate database tables, setup and
runtime credentials. Hosts supply validated identities and customer workspaces.
Its APIs and state are independent of Office document synchronization.
PostgreSQL and SQL Server use the same document and Lists operations. Each
provider owns its mappings, migrations, security settings and SQL. Required
database locking stays enabled. Document and Lists packages have independent
storage dependencies.

SQL Server consumers explicitly call the document or Lists migrator with setup
credentials before starting stores or engines with restricted runtime
credentials. Registration and requests never apply migrations. See
[document setup](storage-providers.md) and [Lists setup](lists.md#sql-server-setup-and-runtime).

## Document identity and partitions

A document has a stable resource ID and a normalized path key. A supplied
resource ID takes precedence over a URL; an unknown ID returns an explicit
lookup failure. Lookup does not create a document or redirect to a matching path.

File contents, application metadata and the editors table have independent
partition identities, serials and synchronization knowledge. File saves merge
against the retained graph and reconstruct the Office package through
`PartitionGraphSnapshot`. Reconstruction follows graph references rather than
choosing an individual binary object by size.

## Saving a document

The document service reconstructs the package into an exclusive temporary file.
It checks every ZIP member against the limit on expanded bytes before storing
immutable content.
The staging file is deleted on success, failure or cancellation. InMemory,
PostgreSQL and SQL Server use this preparation before updating state.

The service then reads the current state and database time, rechecks Write permission,
coherency and leases, and commits the selected revision with a save receipt.
Different documents can progress independently. Sessions use the same coordination.

Cancellation after an established save stops remaining operations and returns an
incomplete execution with `IsCancelled` and known file receipts. The SOAP
processor preserves that result. Cancellation before acceptance can throw;
completed saves are never rolled back. See [accepted saves](package-integration.md#accepted-saves-and-external-content)
for uncertain commit outcomes and HTTP delivery limits.

Downloads take content and headers from one snapshot. Receipts identify duplicate
saves; shared budgets reject excessive growth before selecting a new revision.
Prior file parts remain available for subsequent edits and detached readers.

[Storage providers](storage-providers.md) explains publication, retries, maintenance
and provider contracts. The [protocol guide](protocol-version-decision.md#synchronization-knowledge)
explains changed-part selection, graph retention and materialization constraints.

MTOM responses stream one prepared message with matching capture bytes and exact
content length. Incoming requests and retained graph payloads still occupy memory.
See [response serialization](protocol-version-decision.md#response-serialization)
for buffer ownership and XML precedence.
