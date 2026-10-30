# Office operation and data catalog

This is a design inventory, checked against Microsoft's published MS-OCPROTO,
MS-FSSHTTP and MS-FSSHTTPB sections on 2026-10-28 and implementation at `f569399`.
It is not a conformance certificate. The normative inventory has **14 outer
operation types, 35 outer leaf operations including Cell, four current binary
operations, seven query filter types and five dependency conditions**. Binary
operations run inside Cell; adding their counts to the outer count double-counts
that layer. HTTP discovery and file delivery are separate entries below.

Microsoft's enumeration, schemas and operation processing rules define the
inventory. Local enums and [the requirements ledger](protocol-requirements.md)
provide implementation evidence, not extra normative methods. The seven subtype
families contain 7 Coauth, 5 SchemaLock, 6 ExclusiveLock, 5 EditorsTable, 1
FileOperation, 2 Versioning and 2 Properties operations. The other seven outer
types have no subtype. [Outer types][O2.2.5.11], [binary types][B2.2.1.6].

## Scope and status

CellBridge advertises HTTP discovery `X-MSFSSHTTP: 1.0`, outer SOAP 2.0, and binary
responses 13/11; its standalone binary library defaults to 12/11. Binary requests
12/13/14 are accepted within the selected common subset, with minimum version 11.
Accepting a version number does not implement every feature of every product
using that version. SharePoint 2010/2013 exceptions determine selected fallbacks,
including current-only binary version queries and priority handling.
[Request syntax][B2.2.2], [product behavior][BAppendix].

`Implemented` below means that a dispatch/handler exists for the described subset.
It does not mean every optional field, error precedence or client interaction has
independent reference coverage. Word, Excel and PowerPoint editing have bounded
manual evidence; desktop OneNote and two-desktop coauthoring remain unqualified.
See [README](../README.md) and [interoperability](interoperability.md).

GetDocMetaInfo and GetVersions are implemented, but their normative client rules
require discovery 1.1 or higher. They are not advertised by the current 1.0
header. Keep implemented, advertised and observed workload sets separate.
[GetDocMetaInfo][O3.1.4.9], [GetVersions][O3.1.4.10].

## How to read sizes

Every operation has a nonzero wire envelope, even an acknowledgement with no
application payload. The tables give **payload ranges**, not a fabricated fixed
HTTP size. Their minimum is the required data named in the request/response
column; optional records can be absent. Errors can replace a successful payload
with a small error record, although diagnostic strings remain variable.

| Symbol | Minimum and maximum interpretation |
| --- | --- |
| `C` | Required control fields through all applicable control fields. No file bytes. IDs, URLs, user names and XML strings contribute their encoded lengths, so a constant field count does not establish a universal byte cap. |
| `S` | Sum of encoded variable strings, identifiers and property values in that row. Zero optional bytes through their supplied/returned lengths; a universal maximum is not specified unless listed below. |
| `K` | Encoded client/server knowledge. No knowledge or minimal valid knowledge through all required ranges/entries for the selected state. Growth depends on serial fragmentation and specialization, not just file size. |
| `G`, `D` | Serialized selected partition graph bytes, and changed/missing graph bytes respectively. `0 <= D <= G` for a fixed graph/representation. A full resync can require `G`; neither equals downloaded file bytes. |
| `I`, `A` | Encoded submitted/returned storage-index mappings and encoded added-element identifiers. They scale with affected mappings/elements. |
| `F`, `V`, `P`, `E` | Materialized file bytes, retained version count, returned property count and active editor count. Their protocol-wide maxima are not specified. |
| `X(E)` | Editors-table XML/ZIP representation, including identities and metadata. Zero entries through all visible entries. ZIP and framing sizes require measurement of the actual contents. |

**Average sizes and request frequencies are not specified by these protocols.**
No arithmetic midpoint is an average. A workload must measure distributions by
operation, document shape, knowledge state, user count, batch and failure mode.
For variable-size rows, the maximum is the symbolic expression shown, not a
universal numerical limit. Store limits below are admission policies.

For `b` binary bytes, inline base64 needs `4 * ceil(b / 3)` ASCII characters before
XML/HTTP overhead. MTOM carries binary parts with MIME boundaries, headers and
XOP references. Binary framing adds its own stream-object headers, compact
integers, GUIDs, serials and knowledge. Neither transport expansion nor a repeated
query response implies new stored content. [Outer Cell][O2.3.1.1],
[transport][O2.1], [binary request][B2.2.2].

## HTTP entry points and batch envelope

| API | Request data | Response data and range | Semantics and scope | Current implementation / normative source |
| --- | --- | --- | --- | --- |
| `OPTIONS` containing folder/document URI | URI and HTTP/authentication headers; `C+S` | Capability/method headers; `C+S`, no file body | Read discovery, folder/host scope | `/shared/{**documentPath}` advertises 1.0; cellstorage OPTIONS returns an empty success. [MS-OCPROTO 2.1.2.1.2][Discover]. |
| HTTP `GET` document | URI, credentials, HTTP headers; `C+S` | File bytes `F` plus metadata headers, or error | Read one current document | Implemented authorized streaming download. OCPROTO explicitly describes GET fallback/open; it does not make GET a binary QueryChanges operation. [Discovery/open][Discover]. |
| HTTP `HEAD` document | Same addressing/authentication as GET | Zero body bytes; metadata headers `C+S` | Read one document's metadata | Implemented alongside GET. HTTP semantics, not an additional FSSHTTP subrequest. [Transport's HTTP dependency][O2.1]. |
| `POST /_vti_bin/cellstorage.svc`, SOAPAction `ExecuteCellStorageRequest` | RequestVersion; correlation ID; one or more document Requests with URL/resource identity, tokens and SubRequests; optional MTOM packages | ResponseVersion, corresponding document/subrequest results and optional binary package; sum of selected row outputs plus shared framing | Mixed reads/mutations; collection can address several documents | Implemented direct SOAP Body and compatibility `ExecuteCellStorageRequest` wrapper. [Transport][O2.1], [request schema][O2.2.2.1], [response schema][O2.2.2.2]. |
| `ExecuteCellStorageQuery` | No separate normative input schema established by the selected specifications | No independent response/size claim | Not a second advertised RPC | An unused SOAPAction constant exists in `CellStorageModels`; the parser does not recognize a Query wrapper. Current §2.1 names only ExecuteCellStorageRequest. Do not infer read-only enforcement or support from the constant. [Transport][O2.1]. |
| Historical URL returned by GetVersions, HTTP GET/HEAD | Version URL plus credentials | GET `F` for that revision; HEAD zero body plus headers | Read one immutable revision and lifecycle generation | Implemented `/_cellbridge/history/...` URL is a host delivery mechanism for the result, not a new normative SOAP operation. [GetVersions][O3.1.4.10]. |

A Request identifies a document; SubRequest tokens correlate operations and are
do not guarantee idempotency. A supplied resource ID must resolve explicitly;
CellBridge does not create documents on a failed lookup or silently substitute the
URL. A collection is not a cross-document transaction. Independent earlier
operations can succeed even if a later operation fails. Binary subrequests share
a Data Element Package, so totals must count shared elements once, while retaining
each operation's status and knowledge. [Outer subrequest][O2.2.4.5],
[binary subrequests][B2.2.2.1].

All five dependency values are parsed and evaluated: `OnExecute`, `OnSuccess`,
`OnFail`, `OnNotSupported`, `OnSuccessOrNotSupported`. They condition execution on
a predecessor's result; skipped work produces dependency results rather than
content. CellBridge preflights duplicate tokens, uses wire order, and does not
roll back independent successes. Binary priorities are distinct from these SOAP
dependencies; the selected SharePoint profile permits its wire-order execution.
[Dependencies][O2.2.5.3], [binary ordering][B2.2.2.1], [product notes][BAppendix].

## All outer leaf operations

All rows inherit document URL/resource identity, request/subrequest tokens,
authenticated actor, optional dependencies and error reporting. `R` means read;
`M` means mutation of the named state. Read operations can cause incidental expiry
cleanup in the implementation without becoming content writes. `C/C` denotes
control request/control response using the size convention above.

### Cell and document information

| Operation | Request data | Response data; payload range | Semantics and scope | Implementation and source |
| --- | --- | --- | --- | --- |
| Cell | Binary request; BinaryDataSize; partition selector; applicable lock/coalesce, Etag and file-property controls | Binary subresponses/package and optional file properties; `C + binary input` / `C + binary output` | R/M document partition according to binary operation | File/metadata/editor reads, complete file/opaque metadata writes; unsupported graph/mode errors. [3.1.4.2][O3.1.4.2], [2.3.1.1][O2.3.1.1]. |
| WhoAmI | No operation-specific data | Current user's identity/friendly name and optional contact fields; `C / C+S` | R actor identity within document request | Authenticated identity implemented. [3.1.4.6][O3.1.4.6], [response fields][O2.3.3.6]. |
| ServerTime | No operation-specific data | Tick count; `C/C` | R server clock | Implemented. [3.1.4.7][O3.1.4.7]. |
| GetDocMetaInfo | No operation-specific data | DocProps/FolderProps key/value collections; `C / C+S(P)` | R document and parent-folder properties | Bounded DocProps set; no claim of full folder/FPSE property coverage; discovery gate above. [3.1.4.9][O3.1.4.9]. |
| GetVersions | No operation-specific data | Version metadata, author/time/size/comments/download URLs, versioning state; `C / C+S(V)` | R document history, not file bodies | Retained servable revisions; discovery gate above. [3.1.4.10][O3.1.4.10]. |
| AmIAlone | TransitionID | Boolean; `C/C` | R document coauthor membership/transition | Implemented with required matching transition ID and pending-transition check. [3.1.4.13][O3.1.4.13], [input][O2.3.1.45]. |
| LockStatus | No operation-specific data | LockType, LockID, LockedBy where applicable; `C / C+S` | R document lock | Implemented shared/exclusive/unlocked result. [3.1.4.14][O3.1.4.14], [fields][O2.3.1.50]. |

Cell options add separate behavior: `GetFileProps` requests creation/modified
times; `Etag` conditions execution; `LastModifiedTime` supplies the committed file
time; `Coalesce` requests persistence into the underlying store;
`CoauthVersioning` affects version-update grouping. `ContentChangeUnit` and
`ClientFileID` identify synchronization/content state; `ExpectNoFileExists`
expresses a creation expectation, not authorization to invent missing documents.
`SchemaLockID`, `ExclusiveLockID`, `BypassLockID`, `Timeout` and binary Lock Id
provide operation-specific write authority. They do not add separate APIs.
CellBridge supports selected controls; field-complete acceptance remains open in
the ledger. [Cell option schema][O2.3.3.1].

### Coauth

These seven values exhaust [CoauthRequestTypes][O2.3.2.2]. Inputs below follow the
[conditional attribute matrix][O2.3.3.3]; every row has `C/C` payload size, with
variable lock/user strings included in `C`. All have implementation handlers;
expiry, absent-member and transition/error details still need independent client
qualification. Membership is not the same as editors-table presence.

There is an input-specification discrepancy for CheckLockAvailability: the
[Coauth][O2.3.1.5] and [SchemaLock][O2.3.1.13] type prose require ClientID for every
subtype, while their operation attribute matrices leave its availability row blank;
SchemaLock's XSD also marks ClientID optional. CellBridge's `TryLockInput` follows
the all-subtype requirement and rejects missing ClientID for both operations.
The rows record that behavior, not a claim that these normative texts agree.

| Subtype | Request data | Response data | Semantics and scope | Processing source |
| --- | --- | --- | --- | --- |
| JoinCoauthoring | ClientID, SchemaLockID, Timeout; optional exclusive fallback and its ExclusiveLockID | LockType, CoauthStatus, transition information where applicable | M document shared lease and membership | [3.1.4.3.1][O3.1.4.3.1] |
| ExitCoauthoring | ClientID, SchemaLockID | Acknowledgement/error | M remove member; release final shared lease | [3.1.4.3.2][O3.1.4.3.2] |
| RefreshCoauthoring | ClientID, SchemaLockID, Timeout | CoauthStatus and applicable lock result | M member lease; expired lease can rejoin | [3.1.4.3.3][O3.1.4.3.3] |
| ConvertToExclusive | ClientID, SchemaLockID, Timeout, ExclusiveLockID, ReleaseLockOnConversionToExclusiveFailure | Acknowledgement/error | M document lock mode and membership; optional release on failure | [3.1.4.3.4][O3.1.4.3.4] |
| CheckLockAvailability | ClientID, SchemaLockID; see discrepancy below | Availability through success/conflict error | R document lock, no reservation | [3.1.4.3.5][O3.1.4.3.5] |
| MarkTransitionComplete | ClientID, SchemaLockID | Acknowledgement without CoauthStatus | M document transition; TransitionID is not an added request field | [3.1.4.3.6][O3.1.4.3.6] |
| GetCoauthoringStatus | ClientID, SchemaLockID | CoauthStatus | R document membership | [3.1.4.3.7][O3.1.4.3.7] |

### SchemaLock and ExclusiveLock

The [SchemaLock][O2.3.2.4] and [ExclusiveLock][O2.3.2.3] enumerations have five and
six values. All eleven have handlers and `C/C` payload size. Lock state can contain
many shared owners even though one response has a bounded number of control
fields. [Schema input matrix][O2.3.3.5], [exclusive input matrix][O2.3.3.4].

| Operation/subtype | Request data | Response data | Semantics and scope | Processing source |
| --- | --- | --- | --- | --- |
| SchemaLock / GetLock | ClientID, SchemaLockID, Timeout; optional exclusive fallback/ExclusiveLockID | LockType and acknowledgement/error | M document shared lease | [3.1.4.4.1][O3.1.4.4.1] |
| SchemaLock / ReleaseLock | ClientID, SchemaLockID | Acknowledgement/error | M caller's document lease | [3.1.4.4.2][O3.1.4.4.2] |
| SchemaLock / RefreshLock | ClientID, SchemaLockID, Timeout | Acknowledgement/error | M caller's document lease | [3.1.4.4.3][O3.1.4.4.3] |
| SchemaLock / ConvertToExclusive | ClientID, SchemaLockID, Timeout, ExclusiveLockID, release-on-failure flag | Acknowledgement/error | M document lock; may release caller on failed conversion | [3.1.4.4.4][O3.1.4.4.4] |
| SchemaLock / CheckLockAvailability | ClientID, SchemaLockID; see discrepancy above | Availability through success/conflict error | R document lock | [3.1.4.4.5][O3.1.4.4.5] |
| ExclusiveLock / GetLock | ExclusiveLockID, Timeout | Acknowledgement/error | M document exclusive lease | [3.1.4.5.1][O3.1.4.5.1] |
| ExclusiveLock / ReleaseLock | ExclusiveLockID | Acknowledgement/error | M document exclusive lease | [3.1.4.5.2][O3.1.4.5.2] |
| ExclusiveLock / RefreshLock | ExclusiveLockID, Timeout | Acknowledgement/error | M document exclusive lease | [3.1.4.5.3][O3.1.4.5.3] |
| ExclusiveLock / ConvertToSchemaJoinCoauth | ExclusiveLockID, SchemaLockID, ClientID, Timeout | CoauthStatus/TransitionID and lock result | M document lease, membership and tracked transition | [3.1.4.5.4][O3.1.4.5.4] |
| ExclusiveLock / ConvertToSchema | ExclusiveLockID, SchemaLockID, ClientID, Timeout | Lock result | M document exclusive-to-shared lease | [3.1.4.5.5][O3.1.4.5.5] |
| ExclusiveLock / CheckLockAvailability | ExclusiveLockID | Availability through success/conflict error | R document lock | [3.1.4.5.6][O3.1.4.5.6] |

### EditorsTable

All five [editor subtypes][O2.3.2.5] have handlers. They mutate presence/metadata,
not file content; binary QueryChanges retrieves the resulting table. Each request
addresses one client's entry within one document. Its GUID/actor binding is an
implementation policy stricter than the schema's string ClientID.

| Subtype | Request data and range | Response data and range | Semantics and scope | Processing source |
| --- | --- | --- | --- | --- |
| JoinEditingSession | ClientID, Timeout, AsEditor; `C` | Acknowledgement/error; `C` | M insert/renew document editor or reader entry | [3.1.4.8.1][O3.1.4.8.1] |
| LeaveEditingSession | ClientID; `C` | Acknowledgement/error; `C` | M remove entry | [3.1.4.8.2][O3.1.4.8.2] |
| RefreshEditingSession | ClientID, Timeout, AsEditor; `C` | Acknowledgement/error; `C` | M renew entry/change presence status | [3.1.4.8.3][O3.1.4.8.3] |
| UpdateEditorMetadata | ClientID, Key, base64 Value; `C + key + 0..1024 decoded bytes` | Acknowledgement/error; `C` | M one metadata pair | [3.1.4.8.4][O3.1.4.8.4], [size bounds][O2.3.1.23] |
| RemoveEditorMetadata | ClientID, Key; `C + key` | Acknowledgement/error; `C` | M remove one metadata pair | [3.1.4.8.5][O3.1.4.8.5] |

Normative bounds are a key of at most 64 bytes, at most four pairs per editor, and
at most 1024 decoded bytes per value. Thus pair data is at most 4352 bytes per
editor before identities/XML/base64/ZIP overhead. Editor timeout is 3600..120000
seconds; 60..3600 receives an implementation default. CellBridge uses 3600 for
lower accepted values. These bounds do not limit total editor count or establish
an average table size. [Input schema and limits][O2.3.1.23].

### FileOperation, Versioning and Properties

The current normative subtype lists contain only the five operations below.
There is no normative delete/move/copy, version-delete or property-set subtype in
these lists. All five have handlers; unsupported values are rejected. This is
not a claim that all resource kinds, optional properties or historical graph
representations work. [File enum][O2.3.2.8], [version enum][O2.3.2.9],
[property enum][O2.3.2.10].

| Operation/subtype | Request data | Response data; payload range | Semantics and scope | Implementation and source |
| --- | --- | --- | --- | --- |
| FileOperation / Rename | NewFileName; optional ExclusiveLockID | Acknowledgement/error; `C+S / C` | M document name plus parent-namespace uniqueness/reservations | Identity-preserving rename implemented through provider atomic rename. Filename must not contain a relative path. [3.1.4.12][O3.1.4.12], [input][O2.3.1.33]. |
| Versioning / GetVersionList | VersioningRequestType | User table and version records; `C / C+S(V)` | R document history | Implemented retained history; server selects how many recent versions to return. [3.1.4.11.1][O3.1.4.11.1], [response][O2.3.1.38]. |
| Versioning / RestoreVersion | Version number | Acknowledgement/error; `C+S / C` | M document current state using a historical graph; internal work can involve `G+F` | Implemented graph-aware restore; repository major `.0` revisions, unsupported legacy metadata restoration cases remain. [3.1.4.11.2][O3.1.4.11.2], [input][O2.3.1.36]. |
| Properties / PropertyEnumerate | Properties subtype | Property IDs; `C / C+S(P)` | R addressed resource's property catalog | Bounded document property set implemented. [3.1.4.15.1][O3.1.4.15.1]. |
| Properties / PropertyGet | PropertyIds list | Returned property IDs/values; `C+S(requested IDs) / C+S(P)` | R addressed resource's selected properties | Bounded document property set implemented; unknown properties omitted; host cap of 256 requested IDs. [3.1.4.15.2][O3.1.4.15.2], [input][O2.3.1.52]. |

## All current binary operations

Each subrequest carries a request ID, type, priority and optional Target Partition
Id. Each response has its own ID/status/error. Partition IDs select file
`00000000-0000-0000-0000-000000000000`, metadata
`383adc0b-e66e-4438-95e6-e39ef9720122`, or editors
`7808f4dd-2385-49d6-b7ce-37aca5e43602`. CellBridge supports these selectors and
rejects unknown selectors; a binary selector overrides that operation's outer
default, not subsequent operations. [Target selector][B2.2.2.1.1],
[outer partition fields][O2.3.3.1].

| Operation/type | Request data; range | Response data; range | Semantics and scope | Implementation and source |
| --- | --- | --- | --- | --- |
| QueryAccess / 1 | No operation-specific data; `C` | Separate read/write access error records; `C` | R authorization for addressed file/partition context | Implemented read/write distinction. [request][B2.2.2.1.2], [response][B2.2.3.1.1]. |
| QueryChanges / 2 | Flags; optional cell scope, data constraint, version selector, ordered filters and knowledge; `C+S+K` | Storage-index ID, knowledge, optional hash/partial flags and shared package; `C+K+D`, up to `C+K+G`; editors up to `C+K+X(E)` | R selected partition/subgraph | Incremental or full file/opaque metadata/editor queries, selected hash representation. Continuation and specialized filter execution unsupported; see below. [request][B2.2.2.1.3], [response][B2.2.3.1.2], [editor representation][B2.2.3.1.2.1]. |
| PutChanges / 5 | Submitted/expected storage-index IDs; package with mappings, manifests, groups/BLOBs; flags, author logins, lock ID, optional knowledge/diagnostics; `C+I+D+K+S`, potentially a full submitted graph | Resultant knowledge, optional applied-index ID/package, added IDs and diagnostics; `C+K+A+I` | M selected partition's mappings and content; compare expected mappings then atomically publish | Complete file and opaque metadata graphs implemented. Editors writes use outer EditorsTable; partial/staged transactions and alternate modes unsupported; Multi-Request Put Hint is accepted for complete saves. [request][B2.2.2.1.4], [response][B2.2.3.1.3], [processing][B3.1.4.3]. |
| AllocateExtendedGuidRange / 11 | Requested count as compact UInt64; `C` | One 16-byte GUID and compact minimum/exclusive maximum integers; `C` | Allocate identity range in namespace; no content write | Implemented fresh UUID namespace, count policy 1..100000; does not store one record per ID. Response exclusive maximum is normatively 1000..100000. [request][B2.2.2.1.5], [response][B2.2.3.1.4]. |

`QueryKnowledge=3`, `QueryRawStorage=6`, `PutRawStorage=7` and
`QueryDiagnosticStoreInfo=8` exist in the local enum. They are historical/diagnostic
names, not entries in the current normative four-operation enumeration. They are
unadvertised and rejected. No request/response schema, supported profile or data
size is inferred from those names. [Current enumeration][B2.2.1.6].

### Query controls and knowledge

For a full graph already known by a client, `D` can be zero; knowledge and framing
still remain. With empty, unusable or unsupported knowledge, `D` can approach `G`.
A small request can therefore generate a large response. File content bytes,
graph membership count and knowledge-range count are independent workload axes.

| Control | Input and size driver | Effect on output / current support | Normative source |
| --- | --- | --- | --- |
| Include Storage Manifest / Include Cell Changes | Flag bits plus optional Cell ID | Selects manifest/cell changes; supported. Cell scope narrows graph traversal. | [Query arguments][B2.2.2.1.3] |
| Maximum Data Elements | Compact UInt64 **byte** budget | Bounds serialized data elements, not an element count or whole SOAP response. Protocol partial results require matching knowledge; CellBridge rejects over-budget results rather than truncating successfully. | [Query constraints][B2.2.2.1.3] |
| Allow Fragments / Allow Fragments 2 | Flag bits | Permit fragments; do not require them. CellBridge sends complete elements. | [Query flags][B2.2.2.1.3] |
| Include filtered-out elements in knowledge / round to whole-cell changes | Flag bits | Can enlarge `K` or `D`; supported selected knowledge/cell behavior. | [Query flags][B2.2.2.1.3] |
| Exclude Object Data / Check For File Exists | Flag bits | Defined as ignored by server; do not assume a cheap probe or guaranteed payload exclusion. | [Query flags][B2.2.2.1.3] |
| Return File Hash | Flag bit | Optional file hash; selected SharePoint profile ignores it. Separate from negotiated data-element hashes. | [Query flags][B2.2.2.1.3], [product notes][BAppendix] |
| QueryChangesVersioning / User Content Equivalent Version Ok | Major/minor or opaque token; optional substitution flag | Current-only selected profile ignores version selector; no historical binary read or waterline-as-version behavior. | [Version fields][B2.2.2.1.3], [product notes][BAppendix] |
| Request hashing schema 1, hashes with/instead of data | Request-level flags/schema; compact declaration | Chosen inline object groups may replace/include data with MS-PCCRC information and excluded-data records. Hash size follows content/block structure, not a constant SHA-256 field. Unselected/BLOB groups remain full. | [Hash declaration][B2.2.2], [hash processing][BHash] |
| Cell Roundtrip Options | Version-token-knowledge request and non-generic-schema hint | Profile-specific options; no advertised higher version-token support. | [Request options][B2.2.2], [product notes][BAppendix] |

All **seven** filters are listed below. Ordered include/exclude operations can
re-include earlier exclusions. CellBridge currently skips every filter's predicate
and returns the result without those predicates, unless `Fail if Unsupported`
requests failure. Other supported scope/knowledge controls still apply.
It does support the separate QueryChanges Cell ID argument. Do not confuse that
argument with implementation of the Cell ID *filter*. The parser drops filter
payloads; `FileQueryResponseBuilder.Supports` rejects the failure flag; `Select`
and `QueryChangesResponseShaper` apply only the separate supported controls.
[Filter processing][B2.2.2.1.3.1].

| Filter/type | Request data and size | Normative matched output / scope | Source |
| --- | --- | --- | --- |
| All / 1 | No predicate payload; `C` | Every selected data element | [2.2.2.1.3.1.1][B2.2.2.1.3.1.1] |
| Data Element Type / 2 | Compact type; `C` | Matching index/manifest/group/fragment/BLOB type | [2.2.2.1.3.1.2][B2.2.2.1.3.1.2] |
| Storage Index Referenced Data Elements / 3 | No specified predicate payload; `C` | Elements referenced by index values; specification itself says server support is absent | [2.2.2.1.3.1.3][B2.2.2.1.3.1.3] |
| Cell ID / 4 | Cell ID; `C` | Elements connected to the cell-rooted graph | [2.2.2.1.3.1.4][B2.2.2.1.3.1.4] |
| Custom / 5 | Schema GUID plus opaque bytes; `C+S` | Application/schema-defined predicate | [2.2.2.1.3.1.5][B2.2.2.1.3.1.5] |
| Data Element IDs / 6 | Extended-GUID array; `C+S(number of IDs)` | Enumerated elements | [2.2.2.1.3.1.6][B2.2.2.1.3.1.6] |
| Hierarchy / 7 | Depth 0..3 and root index key; `C+S` | Index values, first references, one level or deep traversal; up to whole reachable graph | [2.2.2.1.3.1.7][B2.2.2.1.3.1.7] |

Knowledge has a specialized wrapper and five named bodies: Cell, Fragment,
Waterline, Content Tag and Version Token. Cell ranges/entries describe possession;
waterlines are not proof that a client holds every element or a historical token.
CellBridge uses serial ranges, conservatively falls back to full responses for
unsupported specializations, and uses a 10000-entry decoded knowledge policy.
Larger valid knowledge can therefore increase response bytes instead of reducing
them. [Knowledge][B2.2.1.13], [specializations][B2.2.1.13.1],
[implementation](../src/CellBridge.FssHttpB/ClientKnowledge.cs).

### Put controls and response growth

Expected index mappings and `Imply Null Expected if No Mapping` define coherency.
Request size depends on changed graph closure and reused BLOB references, not just
edited Office text. An accepted small delta can require materializing `F` bytes,
retaining a revision and producing `K+A+I` response data. The protocol requires
atomic affected-index updates, not a server-wide mutex. [Put semantics][B2.2.2.1.4].

| Control | Normative meaning and size effect | CellBridge status / source |
| --- | --- | --- |
| Partial / Partial Last | Staged changes across requests; final request supplies the index | Rejected; do not size a complete save as independently committable fragments. [Put][B2.2.2.1.4]. |
| Favor Coherency Failure Over Not Found | Recheck coherency before choosing the failure | Accepted; exact error precedence remains an acceptance gap. [Put][B2.2.2.1.4]. |
| Abort Remaining Put Changes on Failure / Return Complete Knowledge If Possible | Specified ignored controls | Ignored; no batch rollback inferred. [Put][B2.2.2.1.4]. |
| Multi-Request Put Hint / Last Writer Wins On Next Change | Coalescing hint / alternate overwrite semantics | Hint accepted; last-writer mode rejected. [Put][B2.2.2.1.4]. |
| Additional Flags A: Return Applied Storage Index Id Entries | Return applied index ID and mappings, increasing `I` | Implemented. [flags][B2.2.2.1.4.1], [response][B2.2.3.1.3]. |
| Additional Flags B: Return Data Elements Added | Return added-ID collection, increasing `A` | Added IDs returned, also in captured-profile responses with B clear. [flags][B2.2.2.1.4.1]. |
| Additional Flags C: Check for Id Reuse | Attempt reuse detection | Parsed; conflicting content under an existing ID is rejected by graph validation. [flags][B2.2.2.1.4.1]. |
| Additional Flags D/E/F | Coherency Check Only Applied Index Entries; Full File Replace Put; Require Storage Mappings Rooted | All three rejected (`0x38`). These are **not** normative names for buffered/coalescing flags. [flags][B2.2.2.1.4.1]. |
| Author logins, optional Lock Id, client knowledge, diagnostic input/output | Variable author/knowledge bytes; fixed diagnostic control; operation authority | Parsed selected fields; client knowledge does not change the write itself. Diagnostic acceptance remains bounded. [Put][B2.2.2.1.4], [response][B2.2.3.1.3]. |

## Numeric bounds versus host policy

Normative scalar ranges are not file-size guarantees. BinaryDataSize is a required
outer signed-long attribute in `1..9223372036854775807`, which the protocol says
the server ignores. Binary request IDs are unique within the binary request and
less than `0xFFFFFFFF`. A compact UInt64 length/count field is an encoding range,
not a promise that a server accepts an allocation of that size. GUID allocation
returns one range descriptor, even when requesting many IDs.
[BinaryDataSize][O2.3.1.1], [binary IDs][B2.2.2.1], [allocation][B2.2.3.1.4].

The following are **current defaults or implementation caps**, not Microsoft
protocol maxima and not tested capacity promises. Effective admission is the
intersection of wire, parser, graph, storage and host limits.

| Policy | Current value and consequence | Implementation |
| --- | --- | --- |
| HTTP request body / SOAP characters | 128 MiB body; 16 Mi characters SOAP. Inline base64 can hit XML limits before the body cap. | `CellBridgeOptions`, `CellStorageRequestParser` |
| MTOM | 128 parts; 16 KiB part-header budget | `CellBridgeOptions` |
| Materialized document / graph / object | 128 MiB document; 512 MiB graph; 512 MiB object; 100000 graph elements | `StorageLimits` |
| History / receipts | 1000 retained revisions; 10000 save receipts; 1000 restore receipts | `StorageLimits`; no automatic history pruning inferred |
| Stored bytes | 1 GiB per document; no provider-wide byte or document-count cap | `StorageLimits`; provider-wide quotas were removed by the approved policy |
| Knowledge / author lists | 10000 decoded knowledge entries; 1024 author logins and 64 Ki UTF-16 characters combined | `ClientKnowledge`, `PutChangesSubRequestData` |
| Rename / property get | 255-character path segment; 256 requested property IDs | `OuterDocumentOperations` |
| Concurrent request admission / deadline | 8 per process; 2 per subject; 2 minutes | `CellBridgeOptions`; global admission is not a protocol locking requirement |

## Design implications and evidence boundaries

The smallest wire responses can require sensitive coordination: lock conversion,
rename and restore all acknowledge with controls but touch different authority
scopes. Rename needs namespace conflict handling; ordinary document reads and
saves do not imply a shared namespace lock. QueryAccess/ServerTime need no file
BLOB read. Editor refresh needs one session lease, while listing editors scales
with visible sessions. GetVersions scales with retained history even when the
current file is small. PutChanges scales with graph closure and publication work;
QueryChanges scales with selected/missing graph and knowledge fragmentation.
These are design deductions from the catalog, not benchmark results.

Prefer separate measurements for control reads, lease mutations, namespace rename,
history list/restore, full sync, unchanged poll, incremental sync and complete
save. A single average request size or a single document bucket hides these
costs. Stored content, immutable graph/index state, authorization, session leases,
history references and receipts need independent size/accounting dimensions.
Any proposed global lock or serialization point should identify the protocol
invariant requiring that exact scope.

Implementation evidence inspected: [HTTP endpoints](../src/CellBridge.AspNetCore/CellBridgeEndpoints.cs),
[outer parser](../src/CellBridge/FssHttp/CellStorageRequestParser.cs),
[outer execution](../src/CellBridge/Execution/CellBridgeRequestProcessor.cs),
[locks](../src/CellBridge/Execution/FssHttpLockCoordinator.cs),
[history/properties/rename](../src/CellBridge/Execution/OuterDocumentOperations.cs),
[binary parser](../src/CellBridge.FssHttpB/FsshttpbCellRequest.cs),
[binary execution](../src/CellBridge/Execution/CellBridgeDocumentService.cs),
[file saves](../src/CellBridge/Execution/IndexedPartitionSavePreparer.cs),
[metadata saves](../src/CellBridge/Execution/IndexedPartitionSavePreparer.cs),
[query shaping](../src/CellBridge/Execution/QueryChangesResponseShaper.cs), and
[storage limits](../src/CellBridge/Storage/StorageLimits.cs).

Completeness here means operation/subtype enumeration and size-driver coverage.
It does not certify every field/error, specialized application graph, schema GUID,
product exception, continuation protocol or client workflow. The selected scope
does not include the whole Office/SharePoint ecosystem: WOPI, WebDAV, FPSE,
MS-LISTSWS and MS-SHDACCWS APIs are separate protocols even when referenced by
these specifications. Native CellBridge Lists and reusable customer/admin APIs
are outside this Office catalog. Customer create/delete/import/move, external
publication and retention operations can still affect protocol state and must
respect its document identity and publication boundaries.

[O2.2.5.11]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttp/a3bb03aa-bbc6-4fab-96f4-4909fb2b813b
[B2.2.1.6]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttpb/4d227fdc-d30c-4f50-8394-76b77f09ff62
[B2.2.2]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttpb/f44cb4d0-9ce7-48f2-8b19-90785ef70703
[BAppendix]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttpb/d006ebd9-c3df-4ef5-8be0-1c1db78c6d2c
[O3.1.4.9]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttp/55c299d8-9564-4acf-be74-59731f51e97e
[O3.1.4.10]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttp/6e6db998-6014-4eef-b43d-4ff1ce3b4ba5
[O2.3.1.1]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttp/0451c9f4-b7cb-494a-870b-6b0475f0d6f7
[O2.1]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttp/0a18a321-fceb-4e2a-b7cf-78c1cbe51e46
[Discover]: https://learn.microsoft.com/en-us/openspecs/office_protocols/ms-ocproto/2d268a23-420b-4339-8c56-9e4f8a924171
[O2.2.2.1]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttp/8b94c4da-c973-4aa0-994a-f0909627dcf5
[O2.2.2.2]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttp/287516da-82cc-41dc-8834-fb3382afa2a6
[O2.2.4.5]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttp/cb9b782f-0ab4-4d9a-acec-f4c09b2ab028
[B2.2.2.1]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttpb/a29b394a-2572-4f60-b032-ce15bf278233
[O2.2.5.3]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttp/97e4cd04-106e-459d-89cd-95589978456c
[O3.1.4.2]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttp/6772a9e5-f258-44e6-bd9d-f5e6fa6c6544
[O3.1.4.6]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttp/d9681115-a13b-4779-b177-982564d35692
[O2.3.3.6]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttp/d2775fc0-ef57-4944-b265-19ac7e64dd93
[O3.1.4.7]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttp/b13abd22-99aa-41ed-bedb-1032103ecf56
[O3.1.4.13]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttp/5800d819-5975-433f-b138-0d340703f680
[O2.3.1.45]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttp/3a4c75e0-2509-460e-b8c7-d5edaedbfe57
[O3.1.4.14]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttp/b332a77f-a160-4474-8095-c530355cb6a2
[O2.3.1.50]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttp/e82594a0-e5f3-498c-b47b-f9c4e08e4167
[O2.3.3.1]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttp/6d98cb6b-11a7-4c8d-8fbe-5cf291e1c223
[O2.3.2.2]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttp/68042039-4446-49e3-a3ee-7dd124d1876b
[O2.3.3.3]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttp/76429492-884a-4d24-aaf1-bc59d5931598
[O3.1.4.3.1]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttp/87ca4a76-f156-4cc7-b2f2-f7624d5a9887
[O3.1.4.3.2]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttp/2697878c-f39b-48fb-b9e7-e621cf977641
[O3.1.4.3.3]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttp/2eca5d82-69b7-4b86-9a59-4809a2c5e342
[O3.1.4.3.4]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttp/1624294a-f802-4d1e-8727-c0ba29b3e447
[O3.1.4.3.5]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttp/ee189f45-ea6d-4457-91e2-1d604c5eaea9
[O3.1.4.3.6]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttp/c0e4b310-32e4-45c6-9037-ca61d06f0367
[O3.1.4.3.7]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttp/eadbdf48-a8c5-4dfd-a68a-1bba4d21262c
[O2.3.2.4]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttp/a04ed4d2-598b-4daf-8189-7c68c9a836b6
[O2.3.2.3]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttp/2a62c48b-1271-4b8c-a575-b4ff53eef48f
[O2.3.3.5]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttp/110e685c-3494-4c15-b54b-3ff55cefdee0
[O2.3.3.4]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttp/14004b88-4e5a-4276-9e40-d507d602c668
[O3.1.4.4.1]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttp/93fe03d0-63dd-4974-8219-5e8fe5d947dd
[O3.1.4.4.2]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttp/e0b73509-0ef5-473b-a527-a1ce2a3608b2
[O3.1.4.4.3]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttp/41182c0d-fc5a-4bf5-975b-00e84a6cbce6
[O3.1.4.4.4]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttp/da3cecdc-56ea-46c0-8d9c-c5c7ed694c27
[O3.1.4.4.5]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttp/13be418b-010f-46ca-bd2b-9f03ae61d60d
[O3.1.4.5.1]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttp/72cbdece-1eb4-45cc-a937-d1a9283ce20e
[O3.1.4.5.2]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttp/a4a61ed5-440e-4384-beb8-1e9754e0e71a
[O3.1.4.5.3]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttp/a0368679-c332-42cd-b025-c7efe9ad957b
[O3.1.4.5.4]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttp/88974d88-84f8-4928-a7f0-b2a40be31547
[O3.1.4.5.5]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttp/e9ffd3a6-784e-4f80-8a80-083974b2ce93
[O3.1.4.5.6]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttp/45b8642d-f517-419a-aa6e-dc2c7406a414
[O2.3.2.5]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttp/3ead1f51-f5c1-4e7e-a712-5826993b12de
[O3.1.4.8.1]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttp/70fc6695-186f-431a-bee1-ff9d4aaaf00f
[O3.1.4.8.2]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttp/052983ea-be26-4628-875b-ba5ddec63e01
[O3.1.4.8.3]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttp/d164b74f-2800-490c-b505-2de354d663d5
[O3.1.4.8.4]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttp/ad6c203e-3c22-43ec-bd77-bf4f944d438b
[O2.3.1.23]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttp/8258f08c-787f-4e87-8831-5e8430e04fb0
[O3.1.4.8.5]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttp/a7aedd68-f2fc-4ce4-8c24-9a583f3182b0
[O2.3.2.8]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttp/ade57c1d-c752-4a92-bc9e-b4993a00a509
[O2.3.2.9]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttp/01aa24ee-02c0-49b9-9f60-837d78d277e6
[O2.3.2.10]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttp/b66e6237-5d97-4792-8f03-cb224f3fa9f5
[O3.1.4.12]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttp/b1dbc1ae-877e-47c0-b599-c1977e01157e
[O2.3.1.33]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttp/cac57311-5f4f-41c8-aba5-1847da504926
[O3.1.4.11.1]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttp/025134f4-f3a9-48df-a90c-8e0e07b30ae6
[O2.3.1.38]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttp/c9c2eda5-0443-4136-9948-0b113d48506e
[O3.1.4.11.2]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttp/6e8d9f46-fa5a-46fc-a7e2-135c69c937f5
[O2.3.1.36]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttp/a88259ce-695e-4aff-b897-b38c8e2cc2a3
[O3.1.4.15.1]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttp/44bda0b0-d741-472c-baf9-dc7b2aeab815
[O3.1.4.15.2]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttp/4c8dc4d2-aab8-4bcf-89f8-0ee12d574e4e
[O2.3.1.52]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttp/a1f4efe8-dc76-4539-88d8-e10cbc8a0447
[B2.2.2.1.1]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttpb/bc25b031-1b4f-4c90-bd08-20a844131841
[B2.2.2.1.2]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttpb/d4577468-bf48-4e2c-b527-bd22f4758729
[B2.2.3.1.1]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttpb/3cfff6de-3eba-4fa4-b432-19e6fa2aab48
[B2.2.2.1.3]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttpb/5b8d1d29-0adf-4b29-b3d1-1a1fe8590642
[B2.2.3.1.2]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttpb/ddc826d3-48b5-4634-8e48-88ba7721bbab
[B2.2.3.1.2.1]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttpb/f7693c86-679a-4a4a-a5d4-b89b4af54bb1
[B2.2.2.1.4]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttpb/e4c224ca-0717-4b02-b713-e75d353904bb
[B2.2.3.1.3]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttpb/731e4906-b810-430d-8796-a5e1cc8144b4
[B3.1.4.3]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttpb/94a442bc-ce2f-4f25-a245-82976b0b063c
[B2.2.2.1.5]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttpb/9e3c74ec-faa5-4a7f-a690-caf4f7a32f50
[B2.2.3.1.4]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttpb/426a9c19-db24-46fd-a97d-83044af1c3c3
[BHash]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttpb/82458bac-fb0f-47d3-a919-9871defdc796
[B2.2.2.1.3.1]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttpb/70ac7a13-f053-4d8f-96f4-7a77ffde373c
[B2.2.2.1.3.1.1]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttpb/fe237240-7667-4a85-9dc5-b30923258ae3
[B2.2.2.1.3.1.2]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttpb/3e7fcafa-5ac3-45d3-b487-bdd9c7e03598
[B2.2.2.1.3.1.3]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttpb/f5bdac29-ac19-4e1e-b0eb-a29ce69a02e9
[B2.2.2.1.3.1.4]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttpb/65b2e89a-9c3f-4263-b8d2-99e59691b11e
[B2.2.2.1.3.1.5]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttpb/8fc945d1-9e5f-4cff-acd0-02d27a794c71
[B2.2.2.1.3.1.6]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttpb/72604452-285e-4090-8803-28b65260d226
[B2.2.2.1.3.1.7]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttpb/67a39be6-8037-42b3-89b4-6185e6fc49f3
[B2.2.1.13]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttpb/d6414c58-95ba-40ab-be03-17faf38b6bde
[B2.2.1.13.1]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttpb/093cc11c-485b-4843-82a4-31d26f5da7f0
[B2.2.2.1.4.1]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttpb/57ddba13-18da-452d-8cca-c43ddce28267
[O2.3.1.5]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttp/49764dab-6d2f-4e5d-bfd7-ae9b3c8cd3dd
[O2.3.1.13]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttp/01ec6017-9744-4a59-8db0-6c2deb36995d
