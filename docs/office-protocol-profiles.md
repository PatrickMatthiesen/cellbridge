# Office protocol profiles

CellBridge uses one MS-FSSHTTP/MS-FSSHTTPB execution engine with
different transports and optional collaboration capabilities around it. Personal
OneDrive, SharePoint Server and WOPI Cobalt do not require separate file graph,
history or publication implementations.

This comparison covers the current source, the retained SharePoint reference
fixtures, the personal OneDrive capture and published Microsoft contracts. It
does not establish full protocol conformance or compatibility with a live
SharePoint Online tenant or Office Online Server.

## Protocol layers and versions

| Layer | Responsibility | Current evidence and implementation |
| --- | --- | --- |
| HTTP discovery | Selects the available authoring and synchronization facilities. | Captured personal OneDrive advertises `X-MSFSSHTTP: 1.5`. CellBridge uses that discovery value. The optional native RTC discovery also supplies storage metadata and an RTC endpoint. |
| SOAP envelope | Carries typed file, lock, identity and history operations. | MS-FSSHTTP runs at `/_vti_bin/cellstorage.svc` for native Office. The captured OneDrive exchanges use SOAP 1.1. CellBridge accepts SOAP 1.1 and 1.2 and preserves the namespace and media type in its response. |
| FSSHTTP request/response version | Versions the outer request collection. | `RequestVersion` and `ResponseVersion` are distinct from both SOAP's version and the HTTP discovery value. |
| FSSHTTPB | Synchronizes file graphs, application metadata and editor information. | CellBridge accepts request versions 12, 13 and 14 with minimum 11, using its implemented common subset. The HTTP processor writes the `SharePoint13_11` response profile. |
| RTC | Transfers transient typing, presence and notifications. | The observed service uses SignalR over a WebSocket. CellBridge's relay is enabled by default and implements a bounded observed subset. Multiple hosts require explicit Redis coordination. RTC does not publish file revisions. |
| OCS | Maintains and merges an active collaboration session and periodically saves to the host. | Observed in the Word reference flow. CellBridge does not implement OCS. |
| WOPI | Connects an Office client/service to a host's file, identity and lock operations. | CellBridge supplies execution and shared-authority integration APIs, rather than WOPI HTTP endpoints. |

Examined personal OneDrive exchanges used SOAP 1.1, binary requests with
version 14/11 and binary responses with version 13/11. This is a reference
sample, not an exhaustive inventory of OneDrive protocol behavior.

The observed OneDrive response version therefore agrees with CellBridge's
13/11 response profile. Advertising or serializing 14/11 is not a prerequisite
established by these tests. A future 14/11 response profile needs its own field
coverage and interoperability tests. The published binary response syntax
permits 12, 13 and 14, with minimum version 11.
See [MS-FSSHTTPB response syntax](https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttpb/00134396-bd4f-4d19-a238-62d872abac10)
and the [existing version decision](protocol-version-decision.md).

The capture targets personal OneDrive hosts, including `d.docs.live.net`.
It does not identify SharePoint Online's current wire profile. The committed
[13/11 fixtures](../testdata/sharepoint/fixtures/v13_11/README.md) came from a
SharePoint reference farm and also cannot establish the current cloud profile.

### Observed OCS envelope

The captured Word client offers WPMv10, WPMv11 and `ocsb2`; the service selects
WPMv11. These are separate negotiated formats from FSSHTTPB 13/11. The first
successful Join is followed by Execute requests carrying revision dictionaries,
client clocks and binary blob frames.

The observed envelope has a 16-byte big-endian header containing frame type,
UTF-8 name length, a reserved value and payload length. Type 1 ends the message,
type 2 carries an opaque blob and type 3 carries JSON. A type-2 name contains a
JSON `BlobId` using padded standard Base64; the dictionary's `BlobReference`
encodes the same 16-byte identifier. Numeric `BlobSize` values
match the associated payload lengths in the examined exchanges.

This framing is sufficient for a bounded codec and opaque blob identification.
The codec lives in the standalone `CellBridge.FssHttpB` serialization assembly,
separate from document execution and storage contracts.
It does not define WPM merging or host publication. In an accepted update, the
service changed one 12,800-byte input blob to a different 12,822-byte result;
its revision sequence and the client's clock also advanced independently.
Copying the input dictionary and acknowledging it cannot be claimed equivalent
to that operation. Initial full state used `RevisionContentFormat=2`; the
examined updates and poll deltas used format 1. There is no captured format-2
update establishing its semantics.

## Native synchronization and modern collaboration

The common synchronization engine remains responsible for document identity,
permissions, schema/exclusive locks, file and metadata graphs, editor tables,
saved revisions and atomic publication. A WebSocket cannot replace these rules.

RTC adds immediate communication between clients. Microsoft documents its use
for typing, presence, cursor positions and the coauthor gallery. The
[SignalR Hub Protocol](https://github.com/dotnet/aspnetcore/blob/main/src/SignalR/docs/specs/HubProtocol.md)
defines the outer message framing, not Office's payloads or document authorization.
OCS performs
different work: it holds active document state, merges edits and accumulates
changes before writing them to the host. Microsoft explicitly permits
coauthoring without OCS and describes transitions between OCS and direct host
collaboration. See [CSPP supporting services](https://learn.microsoft.com/en-us/microsoft-365/cloud-storage-partner-program/plus/services).

Native Word, Excel and PowerPoint tests exchanged saved changes between two
authenticated editors. Word cursor indicators and Excel remote selections
appeared, but participant names remained unresolved. See
[client coverage and remaining work](interoperability.md#client-coverage) for
the tested scope and limits.

Protocol admission uses bounded FIFO waits to absorb native opening bursts
while retaining executing-request limits. RTC admission pressure returns a
transient busy result rather than an authorization denial that would remove
a valid participant. See [host request limits](package-integration.md).

Coauthor exit retries retain client/schema/subject keys for the current shared
session. CellBridge limits that session to 128 active and departed participants
to bound the retained replay state. A new shared session resets the keys. This
is a local storage limit, rather than a limit defined by MS-FSSHTTP.

RTC is enabled by default. Hosts can set `EnableOfficeRealTime = false` to disable
RTC discovery and endpoints. OCS remains unimplemented and is not advertised.

Remaining modern-collaboration work includes reliable native startup, resolved
participant identity, token recovery after interruption, server-generated notifications
after publication, measured delivery latency and any OCS behavior actually
required by the supported client workflow. Multiple hosts require explicit
[Redis room coordination](multi-host-rtc.md). Native discovery must describe capabilities the
host actually provides, with its own identity and endpoints.

## Native sharing

The native Share workflow uses SharePoint sharing contracts outside
MS-FSSHTTP. Captured personal OneDrive traffic included calls to the REST
methods `GetObjectSharingSettings` and `GetSharingInformation`, plus requests
to `/_vti_bin/DocumentSharing.svc` and `/_vti_bin/sharedaccess.asmx`. CellBridge
implements only `IsOnlyClient` at the latter route; it does not implement the
document-sharing SOAP service or the CSOM sharing methods.

These contracts cover different operations. MS-CSOMSPT defines
`GetObjectSharingSettings` as data for rendering sharing information and
`GetSharingInformation` as a read of a list item's sharing state. Its
`UpdateDocumentSharingInfo` operation changes recipient roles and requires
ManagePermission. The operation accepts strict or additive grant semantics,
and can request email notifications. MS-DOCSWS defines the SOAP
`DocumentSharing.svc` contract for document sharing and tokenized guest links.
Its `SetLinks` operation requires tokenized-link support and must fail with
SOAP error 17 when the server does not support it. These operations do not
establish that OneDrive and on-premises SharePoint use identical request
sequences or response shapes.

CellBridge stores grants by stable subject identifier and enforces them during
document access. It does not provide a host directory that resolves a typed
email or login to a trusted subject. Its owner-only `GetSharingAsync` and
`SetRecipientGrantAsync` service APIs read and update stored grants with lifecycle
and authority-generation checks. Native sharing endpoints must call these APIs
after resolving a recipient through the host's trusted directory.
External authorization-policy hosts own their permission decisions and
cannot be mutated through CellBridge grants. A native sharing implementation
still needs a host-provided recipient resolver and a protocol adapter around the
owner-only service API before it can add recipients. Link sharing also needs a
separate token store and access path. Until those exist, the host must not
advertise sharing or anonymous-link support.

References: [MS-CSOMSPT `GetObjectSharingSettings`](https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-csomspt/a9d2273e-31ad-40c4-a90b-0a9e8d822c22),
[MS-CSOMSPT `GetSharingInformation`](https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-csomspt/e8604c72-262b-490f-933f-ece357258d63),
[MS-CSOMSPT `UpdateDocumentSharingInfo`](https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-csomspt/fad1d6ca-77e9-42fa-93b3-73d7747fce18),
and [MS-DOCSWS](https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-docsws/78d4c36e-0916-4843-88b4-33f965d0ccd5).

## WOPI integration paths

| Path | Host contract | CellBridge fit |
| --- | --- | --- |
| Ordinary WOPI | File information, file reads/writes, authorization and WOPI locks. | Does not require FSSHTTP merely because it uses WOPI. |
| WOPI Cobalt | `SupportsCobalt` enables `ExecuteCellStorageRequest` and `ExecuteCellStorageRelativeRequest`. The `COBALT` override carries MS-FSSHTTP through a WOPI file route. | Reuse the current FSSHTTP engine through an adapter that supplies the resolved document, actor and transport context. |
| WOPI CSPP Plus coauthoring | Additional coauthor locks/table, chunked file operations, sequence numbers, token metadata and OCS/RTC discovery. | A separate integration feature. The native RTC relay does not implement this contract. |

Microsoft defines the [WOPI Cobalt operation](https://learn.microsoft.com/en-us/openspecs/office_protocols/ms-wopi/5c52df20-c7af-4446-9e65-3a1836528f09)
as an MS-FSSHTTP operation at the WOPI route. Its
[capability documentation](https://learn.microsoft.com/en-us/microsoft-365/cloud-storage-partner-program/rest/files/checkfileinfo/checkfileinfo-response)
distinguishes `SupportsCobalt` from ordinary lock and update support.

The [CSPP Plus contract](https://learn.microsoft.com/en-us/microsoft-365/cloud-storage-partner-program/rest/files/checkfileinfo/checkfileinfo-csppp)
separately requires `SupportsCoauth`, the collaboration service endpoints and
other metadata. Setting it also promises `GetCoauthLock`, `RefreshCoauthLock`,
`UnlockCoauthLock`, `GetCoauthTable`, `GetChunkedFile`, `PutChunkedFile` and
`GetSequenceNumber`. It must not be enabled solely because a native WebSocket
test succeeds.

### WopiHost

WopiHost's proposed [CellBridge integration](https://github.com/petrsvihlik/WopiHost/pull/735)
uses `ICobaltProcessor` for MS-FSSHTTP carried by WOPI for Office Online Server.
The common FSSHTTP engine is the integration point; package compatibility alone
does not establish Office Online Server editing compatibility.

Native URL discovery headers, forms authentication and the optional RTC hub
should not become prerequisites for that processor. A processor-only host can disable RTC.
Enabling it requires stored grants or an immutable versioned authorization policy,
which an existing WOPI adapter may not supply. Optional identity metadata must
come from the host's trusted user context, rather than Microsoft account aliases.

WopiHost still needs to qualify its real OOS request framing and preserve any
required SOAP/MTOM transport context. It also needs one write authority for WOPI
and FSSHTTP and recoverable external publication if its file store is separate.
CellBridge's base host-lock helper excludes conflicting FSSHTTP locks; it does
not implement WOPI shared/extended coauthor locks or cross-protocol conversion.
See [the package boundary](package-integration.md#wopi-integration-boundary) and
[external host reliability](external-host-reliability.md).

An adapter must use shared locks, permission ceilings, conditional external
publication and verified acknowledgements. Use the indexed repository and
service APIs rather than loading complete document state or history.
`SharedDocumentLocks.ReadAsync` provides authorized lock observation.

Content no-ops also require destination verification. An empty outbox alone
does not establish that the external destination still matches the acknowledged
revision. Use indexed delivery and revision reads for that decision. A test
destination does not establish a production revision-store adapter.

## Support direction

Keep the published FSSHTTP contract and existing binary profiles in the shared
engine. Keep native RTC discovery and transport optional. Adapt WOPI Cobalt at
the host boundary, preserving the same document identities, authorization,
revision store and publication fences. Treat CSPP Plus as a distinct optional
host integration if a consumer needs it.

Test these paths separately: native file URL without RTC, native URL with RTC,
WOPI Cobalt against real OOS, and CSPP Plus only when its full host contract is
implemented. A capability can change the client's workflow even when the file
bytes and underlying protocol engine are unchanged.
