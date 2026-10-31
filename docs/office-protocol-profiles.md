# Office protocol profiles

CellBridge should keep one MS-FSSHTTP/MS-FSSHTTPB execution engine and support
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
| RTC | Transfers transient typing, presence and notifications. | The observed service uses SignalR over a WebSocket. CellBridge's default-off, single-host relay implements a bounded observed subset. It does not publish file revisions. |
| OCS | Maintains and merges an active collaboration session and periodically saves to the host. | Observed in the Word reference flow. CellBridge does not implement OCS. |
| WOPI | Connects an Office client/service to a host's file, identity and lock operations. | CellBridge supplies execution and shared-authority integration APIs, rather than WOPI HTTP endpoints. |

On 2026-10-31, a scan of 1,103 captured personal OneDrive HTTP exchanges found
90 FSSHTTP POSTs, 897 OCS POSTs and separate RTC discovery/transport exchanges.
All 90 captured FSSHTTP requests and responses used SOAP 1.1. Extracted binary
headers contained 86 request occurrences of **14/11** and 86 response occurrences
of **13/11**. The body hashes were checked before extraction. These are header
occurrences, not a count of independent edits or a performance measurement.
The continuing capture has an abnormal WebSocket-end health flag, so these
completed exchanges do not establish capture completeness.

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

## Native synchronization and modern collaboration

The common synchronization engine remains responsible for document identity,
permissions, schema/exclusive locks, file and metadata graphs, editor tables,
saved revisions and atomic publication. A WebSocket cannot replace these rules.

RTC adds immediate communication between clients. Microsoft documents its use
for typing, presence, cursor positions and the coauthor gallery. OCS performs
different work: it holds active document state, merges edits and accumulates
changes before writing them to the host. Microsoft explicitly permits
coauthoring without OCS and describes transitions between OCS and direct host
collaboration. See [CSPP supporting services](https://learn.microsoft.com/en-us/microsoft-365/cloud-storage-partner-program/plus/services).

The current native evidence is mixed. Two-editor Excel and PowerPoint cases
worked with a replayed reference discovery profile. With CellBridge's own
discovery identity, two fresh Word clients exchanged saved edits in both
directions, and a separate server download retained both markers. Peer cursors
appeared, but names remained unresolved. Fresh Excel and PowerPoint opens also
produced intermittent client-side busy errors. Matching PowerPoint server
responses granted read/write access and did not show HTTP 503. Those results
do not qualify reliable startup, typing latency or complete native presence.

Coauthor exit retries retain client/schema/subject keys for the current shared
session. CellBridge limits that session to 128 active and departed participants
to bound the retained replay state. A new shared session resets the keys. This
is a local storage limit, rather than a limit defined by MS-FSSHTTP.

Remaining modern-collaboration work includes reliable native startup, resolved
participant identity, token renewal/reconnection, server-generated notifications
after publication, measured delivery latency and any OCS behavior actually
required by the supported client workflow. The relay currently implements no
multi-host room coordination. Native discovery must describe capabilities the
host actually provides, with its own identity and endpoints.

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

[WopiHost PR #735](https://github.com/petrsvihlik/WopiHost/pull/735), verified open
and draft on 2026-10-31, integrates CellBridge behind `ICobaltProcessor` as an
alternative to `Microsoft.CobaltCore`. Its stated target is MS-FSSHTTP carried
by WOPI for Office Online Server. It parses raw binary or SOAP bodies and does
not yet establish real OOS editing compatibility. This makes the common
FSSHTTP engine the appropriate integration point.

Native URL discovery headers, forms authentication and the optional RTC hub
should not become prerequisites for that processor. RTC remains off by default.
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
