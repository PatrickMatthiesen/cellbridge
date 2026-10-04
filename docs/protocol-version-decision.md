# Protocol scope

CellBridge implements MS-FSSHTTP and MS-FSSHTTPB together. MS-OCPROTO describes
how Office discovers and uses these protocols. SharePoint lists, search and
unrelated APIs are outside this project's scope.

## Outer SOAP protocol

MS-FSSHTTP defines the endpoint `/_vti_bin/cellstorage.svc`, the
`CellStorageRequest` and `CellStorageResponse` envelopes, and typed subrequests
such as Cell, Coauth, SchemaLock, EditorsTable, WhoAmI and ServerTime.

The SOAP action is
`http://schemas.microsoft.com/sharepoint/soap/ICellStorages/ExecuteCellStorageRequest`.
The server supports inline base64 and MTOM/XOP binary payloads. Dependency
conditions determine whether dependent subrequests execute.

`OnExecute` permits continuation after an evaluated `OnNotSupported` fallback.
It excludes failures caused by `OnSuccess`, `OnFail` or `OnExecute` dependencies,
as defined by [DependencyTypes](https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttp/97e4cd04-106e-459d-89cd-95589978456c).
This distinction preserves the Coauth → SchemaLock fallback → Cell open sequence
used by Word.

`GetFileProps` supplements the selected Cell response with file properties; it
does not replace its binary graph. The default Cell selector is file contents.
The explicit metadata selector is `383ADC0B-E66E-4438-95E6-E39EF9720122`; the
editors-table selector is `7808F4DD-2385-49D6-B7CE-37ACA5E43602`.

## Inner binary protocol

MS-FSSHTTPB defines QueryAccess, QueryChanges and PutChanges, framed with Stream
Object Headers. The request signature is `0x9B069439F329CF9C` and the response
signature is `0x9B069439F329CF9D`. The standalone library defaults to binary
protocol version 12 with minimum version 11. The HTTP endpoint uses the
`SharePoint13_11` compatibility profile and writes version 13 with minimum 11.

A successful QueryChanges response includes the storage index and complete
Knowledge structures as well as its data-element package. Each partition retains
its own identities and serials. File-partition PutChanges applies graph changes
and validates coherency before publication. Unsupported upload modes return
explicit protocol errors.

See [protocol support and compatibility](interoperability.md) for implemented
operations and tested clients.

## Synchronization knowledge

File queries compare client GUID/serial ranges against stored metadata before
loading payloads. Mapping serials identify mappings independently of their target
elements. New elements receive server serials above the stored high-water mark;
retries preserve existing serials. Reusing a nonnull mapping serial for a different
key or target fails before publication. Snapshots retain this metadata and reject
incomplete state. Ambiguous element serials cause conservative retransmission.
These rules follow [data-element serial assignment](https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttpb/9db15fa4-0dc2-4b17-b091-d33886d8a0f6)
and [storage-index mapping knowledge](https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttpb/f5724986-bd0f-488d-9b85-7d5f954d8e9a).
The PutChanges writer retains the observed legacy serial-reassignment stream shape.

Waterline-only knowledge does not establish possession. Whole-cell rounding returns
all visible elements when any are unknown. Filtered knowledge excludes withheld
elements unless the request includes them explicitly. Unknown knowledge
specializations or more than 10,000 decoded entries fall back to a complete
response after validating the exchange. Partial ranges never suppress payloads;
malformed framing fails. Optional unsupported filters are ignored unless
`FailIfUnsupported` requires an error. See the [Query Changes rules](https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttpb/5b8d1d29-0adf-4b29-b3d1-1a1fe8590642).

## Graph retention

The host keeps graph history because later edits can reference earlier revisions.
Even a constant-size file can have a growing chain of required parts. Object-group
metadata records [change frequencies](https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttpb/507c6b42-2772-4319-b530-8fbbf4d34afd)
and carries no references. `AnalyzeRetention` resolves references within the
declaring revision and its explicit base chain, following the
[protocol data model](https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttpb/6c7e4447-6ccd-4764-8dbc-17a382fb631d).
Incomplete analysis reports a lower bound only.

The standalone `Compact` API rejects incomplete analysis. The HTTP host does
not invoke it; saves reject at a storage quota rather than deleting required
history. See [storage limits](storage-providers.md#limits-and-qualification).

## Authorities

- [MS-OCPROTO](https://learn.microsoft.com/en-us/openspecs/office_protocols/ms-ocproto/)
- [MS-FSSHTTP](https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttp/)
- [MS-FSSHTTPB](https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttpb/)
- [OfficeDev Interop-TestSuites](https://github.com/OfficeDev/Interop-TestSuites)

Protocol types stay in the standalone serialization libraries, with unit tests.
Changes to supported wire behavior should add a focused regression and, when
client decisions may change, a real desktop test with correlated server capture.
