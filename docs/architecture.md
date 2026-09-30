# Architecture

These notes include historical implementation status. See the root [README](../README.md)
for the current capabilities and known limitations.

## Overview

CellBridge implements the two layers of the Office file collaboration
protocol stack so that Word desktop can open, edit, save, and coauthor a
`test.docx` against this server instead of SharePoint:

```
┌────────────────────────────────────────────────────────────┐
│ Word desktop                                               │
└──────────────┬─────────────────────────────────────────────┘
               │ HTTPS (SOAP + base64 binary payloads)
┌──────────────▼─────────────────────────────────────────────┐
│ CellBridge.Web                                     │
│  GET  /shared/test.docx        → file download             │
│  OPTIONS /_vti_bin/cellstorage.svc → capability discovery   │
│  POST  /_vti_bin/cellstorage.svc   → ExecuteCellStorage…   │
└──────────────┬─────────────────────────────────────────────┘
               │
   ┌───────────▼────────────┐   ┌──────────────────────────┐
   │ CellBridge.    │   │ CellBridge.      │
   │ FssHttp (SOAP layer)   │──▶│ FssHttpB (binary layer)  │
   │ CellStorageRequest/    │   │ QueryAccess/QueryChanges/│
   │ Response, SubRequests  │   │ PutChanges, Stream Object│
   └───────────┬────────────┘   │ Headers, DataElements    │
               │                └──────────────────────────┘
   ┌───────────▼────────────┐
   │ CellBridge.    │
   │ Storage                │
   │ DocumentStore, coauth  │
   │ sessions               │
   └────────────────────────┘
```

## The two protocol layers

See [protocol-version-decision.md](protocol-version-decision.md) for the full
analysis. In short:

1. **MS-FSSHTTP (SOAP)** — the outer envelope. `CellStorageRequest` /
   `CellStorageResponse` with `SubRequest` elements typed `Cell`, `Coauth`,
   `ExclusiveLock`, `SchemaLock`, `WhoAmI`, `ServerTime`, etc. Endpoint:
   `/_vti_bin/cellstorage.svc`.
2. **MS-FSSHTTPB (binary)** — the base64 payload inside the `Cell`
   subrequest's `SubRequestData`/`SubResponseData`. Uses `Query Access` /
   `Query Changes` / `Put Changes` framed with Stream Object Headers.

## Project layout

| Project | Purpose |
|---|---|
| `src/CellBridge.Web` | ASP.NET Core host: file download, OPTIONS, cellstorage.svc, request logging |
| `src/CellBridge.FssHttp` | SOAP layer: request parser, response serializer, subrequest model |
| `src/CellBridge.FssHttpB` | Binary layer: Stream Object Headers, Compact64bitInt, ExGuid, SerialNumber, DataElementPackage, request/response structures |
| `src/CellBridge.Storage` | In-memory document store with content versioning and coauthoring sessions |
| `tests/CellBridge.FssHttpB.Tests` | Tests for the binary library, editors partition, FSSHTTPD graph, and response inspector |
| `tests/CellBridge.FssHttp.Tests` | Tests for SOAP/MTOM, editors state, and binary operation dispatch |
| `aspire/` | Aspire AppHost wiring the Web project |

## Key wire facts

- SOAP action: `http://schemas.microsoft.com/sharepoint/soap/ICellStorages/ExecuteCellStorageRequest`
- FSSHTTPB request signature: `0x9B069439F329CF9C`; response: `0x9B069439F329CF9D`
- FSSHTTPB protocol version: 12 (minimum 11)
- Stream Object Header Start: 2-bit type (0=16-bit, 2=32-bit), 1-bit compound,
  6-bit or 14-bit object type, 7-bit or 15-bit length
- Compact64bitInt: k zero bits + a 1 bit prefix, then 7·(k+1) value bits
- ExGuid: 2/5/6/7 zero bits + a 1 bit, then 5/10/17/32-bit index value, then a 16-byte GUID
- In FSSHTTP 2.0, SOAP selects the Cell partition. The default Cell partition
   contains the document bytes; `383ADC0B-E66E-4438-95E6-E39EF9720122` is the
   metadata partition; `7808F4DD-2385-49D6-B7CE-37ACA5E43602` is the editors
   table partition. Each partition must retain separate FSSHTTPB identity and
   synchronization knowledge.
- `ServerTime` is a positive integer containing UTC ticks since year 1. File
   property `CreateTime` and `LastModifiedTime` use Windows FILETIME values.

## Current status vs success criteria

| # | Criterion | Status |
|---|---|---|
| 1 | Serve test.docx at stable HTTPS URL | ✅ HTTP + HTTPS (trusted dev cert) |
| 2 | Respond to Office OPTIONS discovery | ✅ |
| 3 | Cause Word to select MS-FSSHTTP | ✅ Word sends `ExecuteCellStorageRequest` |
| 4 | Implement cellstorage.svc | ✅ (Cell, Coauth, EditorsTable, SchemaLock, WhoAmI, ServerTime) |
| 5 | Parse and log every FSSHTTP request | ⏳ QueryChanges arguments/knowledge parsing is incomplete |
| 6 | Return valid protocol responses | ⏳ File and editors partition graphs exist; metadata still needs its captured application-specific graph |
| 7 | Word opens test.docx | ⏳ Word falls back to direct GET and opens read-only |
| 8 | Word saves a modification | ⏳ PutChanges is rejected until graph-aware updates are implemented; no successful save claim |
| 9 | Two Word instances coauthor | ⏳ Session state exists, but Word has not completed an editable coauthoring session |
| 10 | Change by A visible to B | ⏳ Requires successful editable coauthoring |

## Next steps

The optional [capture kit](capture-kit.md) records application-level HTTP/HTTPS
exchanges under Aspire or standalone. Its local integration tests do not establish
Word/SharePoint authentication compatibility. Capture a successful reference
open/save session before relying on those recordings as protocol fixtures.

Binary Cell dispatch now preserves request IDs across supported operations.
It supports one QueryChanges per Cell payload and rejects additional queries
explicitly until independent filters and snapshots are implemented.
The unsafe largest-BLOB save heuristic has been removed. MTOM parsing checks
delimiter lines and rejects duplicate content IDs, and the HTTP host parses MIME
only once. Full knowledge-based synchronization, atomic document snapshots, SOAP
dependency execution, and lock/coauthoring transitions remain unfinished.

1. Capture the corresponding three Cell responses from the known-good
   SharePoint farm, then compare them with the canonical response-inspector
   dump now logged for every generated Cell response.
2. Validate the dedicated editors partition against SharePoint. It now emits
   schema-ordered editor XML, the zip-stream header, DEFLATE data, and a
   reconstructable FSSHTTPD root, leaf, and raw-data graph.
3. Capture the application-specific metadata partition payload from SharePoint
   and add a dedicated metadata serializer.
4. Finish QueryChanges parsing, especially Knowledge, versioning, filters, and
   the maximum data-element constraint; then return incremental state.
5. Live test Word against `https://web-aspire.dev.localhost:7292/shared/test.docx`
   after each change. A direct GET immediately after the CellStorage POST means
   Word has rejected the FSSHTTP response.
