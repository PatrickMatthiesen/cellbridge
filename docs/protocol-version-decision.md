# Protocol Version Decision

**Date:** 2026-08-28
**Status:** DECIDED (corrected)

## The Problem

While implementing the `CellBridge.FssHttpB` binary serialization
library, a subagent flagged that the 2024 MS-FSSHTTPB spec appeared to be a
different protocol than expected. This was verified against the spec text and
the Interop-TestSuites reference implementation.

## The Finding (CORRECTED)

The two protocols **work together** — they are NOT competing alternatives.

### 1. MS-FSSHTTP (SOAP) — the OUTER protocol Word desktop uses

- Endpoint: `/_vti_bin/cellstorage.svc`
- SOAP action:
  `http://schemas.microsoft.com/sharepoint/soap/ICellStorages/ExecuteCellStorageRequest`
- SOAP envelope with `CellStorageRequest`/`CellStorageResponse`,
  `SubRequestType` (Cell, Coauth, ExclusiveLock, SharedLock, SchemaLock,
  WhoAmI, ServerTime, EditorsTable, GetDocMetaInfo, GetVersions,
  FileOperation, Versioning, AmIAlone, LockStatus, Properties), and
  `SubRequestData`/`SubResponseData` elements.

### 2. MS-FSSHTTPB (2024 rev, v20240820) — the BINARY payload inside the SOAP Cell subrequest

- The `Cell` subrequest's `SubRequestData`/`SubResponseData` contains
  base64-encoded binary data defined by MS-FSSHTTPB.
- Uses `Query Access` / `Query Changes` / `Put Changes`
- Framed with `Stream Object Header` (32-bit)
- Has `Protocol Version` (2 bytes) and `Signature` (8 bytes:
  `0x9B069439F329CF9C`)

## Evidence

`Select-String` on `%TEMP%\fsshttp-official-research\MS-FSSHTTPB.txt` (241KB,
117 pages, v20240820) found:
- `Query Access`, `Query Changes`, `Put Changes`
- `Protocol Version` (2 bytes), `Signature` (8 bytes: `0x9B069439F329CF9C`)
- `Query Changes Request`, `Query Changes Filter`, `Cell ID`,
  `Stream Object Header`
- References to `[MS-FSSHTTP]` for the SOAP-level coauth/lock structures.

`Select-String` on `%TEMP%\fsshttp-official-research\MS-FSSHTTP.txt` (559KB)
found the SOAP structures:
- `CellStorageRequest`, `SubRequestType`, `CellSubRequestType`,
  `CoauthSubRequestType`, `ExclusiveLockSubRequestType`, etc.
- Endpoint `/_vti_bin/cellstorage.svc`
- SOAP action `.../ICellStorages/ExecuteCellStorageRequest`
- References to `[MS-FSSHTTPB]` for the binary payloads.

## Confirmed by Interop-TestSuites test suite

`%TEMP%\ITS\FileSyncandWOPI\Source\MS-FSSHTTP-FSSHTTPB\TestSuite\` has BOTH:
- S01-S10: SOAP-level subrequests (Cell, Coauth, SchemaLock, ExclusiveLock,
  WhoAmI, ServerTime, EditorsTable, GetDocMetaInfo, GetVersions,
  MultipleSubRequests)
- S11-S13: Binary sub-requests (QueryAccess, QueryChanges, PutChanges)
- S14-S20: AllocateExtendedGuidRange, CreateFile, Versioning, FileOperation,
  AmIAlone, LockStatus, Properties

## Decision

**Build BOTH layers:**
- `CellBridge.FssHttp` — the SOAP layer (CellStorageRequest SOAP
  message, cellstorage.svc endpoint, OPTIONS capability discovery).
- `CellBridge.FssHttpB` — the binary serialization library
  (QueryAccess/QueryChanges/PutChanges with Stream Object Headers), used as
  the payload of the SOAP Cell subrequest.

## QueryChanges interoperability fix (2026-09-01)

Word was accepting the SOAP request/response transport and HTTP 200 status, but
repeating the same JoinCoauthoring, SchemaLock, and QueryChanges batch instead
of becoming editable. The server's QueryChanges payload was incomplete: it
returned a response header and a StorageManifestRoot declaration, while the
MS-FSSHTTPB response format requires a storage-index extended GUID, flags byte,
and a complete `Knowledge` compound object.

The response now emits the required tree:

- Cell Knowledge specialized serialization, including a Cell Knowledge Range.
- Waterline Knowledge specialized serialization, including a Waterline Entry.
- Correct typed compound-object start/end headers and the protocol's fixed
  Cell Knowledge and Waterline Knowledge GUIDs.

The data element package remains the carrier for the storage manifest. The
QueryChanges payload does not repeat that manifest root declaration. The
implementation uses the generated cell-storage extended GUID consistently for
the storage index, cell knowledge, and waterline values.

The response serializer has a focused round-trip parser so malformed or
structurally incomplete Knowledge payloads fail locally rather than being sent
to Word. The next validation milestone is a real Word session that accepts the
response and emits `PutChanges`; unit tests alone do not prove Word save or
coauthoring interoperability.

## SOAP negotiation fixes (2026-09-01)

The open-document request includes a normal FSSHTTPB `QueryChanges` payload
even when the SOAP `Cell` request has `GetFileProps="true"`. The response now
preserves that binary response and adds the SOAP file-property attributes
required by the open-document flow: `Etag`, `CreateTime`,
`LastModifiedTime`, `CoalesceHResult`, `ContainsHotboxData`, and `ModifiedBy`.
The timestamps are emitted as UTC Windows file times.

The same request batch includes a `SchemaLock` fallback with
`DependencyType="OnNotSupported"` and `DependsOn` pointing to the successful
`JoinCoauthoring` request. Since coauthoring is supported, the server now
returns `DependentOnlyOnNotSupportedRequestGetSupported` with HRESULT
`2147500037` instead of granting a second schema lock.

The response shape is now closer to the published MS-FSSHTTP example. Build
and all 71 tests pass. A fresh Word probe exercised both fixes, but Word still
performed a subsequent document GET and did not yet send `PutChanges`; further
FSSHTTPB state/manifest compatibility work remains.

## Reference implementation

Interop-TestSuites cloned at `%TEMP%\ITS\FileSyncandWOPI\Source\`:
- `Common\Proxy\MS-FSSHTTP\MS-FSSHTTP.cs` (177KB) — WSDL-generated SOAP proxy
  (XML serialization) for the classic protocol.
- `SharedTestSuite\SharedAdapter\Stack\FSSHTTPB\` — the binary serialization
  stack (Request/Response/BasicType/Knowledge/DataPackage).
- `MS-FSSHTTP-FSSHTTPB\TestSuite\` — the test suite (S01-S20).
