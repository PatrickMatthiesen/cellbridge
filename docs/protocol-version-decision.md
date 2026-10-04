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

The independent Microsoft parser and sanitized fixtures check serialization and
materialization. They cannot prove that a particular desktop Office build accepts
all responses or that two users can coauthor. See
[interoperability coverage](interoperability.md) and
[testing instructions](automated-testing.md).

## Authorities

The [protocol completion plan](protocol-completion-plan.md) distinguishes known
implementation gaps, valid fallbacks, unverified clients and performance work,
and orders the remaining work including OneNote desktop synchronization.

- [MS-OCPROTO](https://learn.microsoft.com/en-us/openspecs/office_protocols/ms-ocproto/)
- [MS-FSSHTTP](https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttp/)
- [MS-FSSHTTPB](https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttpb/)
- [OfficeDev Interop-TestSuites](https://github.com/OfficeDev/Interop-TestSuites)

Protocol types stay in the standalone serialization libraries, with unit tests.
Changes to supported wire behavior should add a focused regression and, when
client decisions may change, a real desktop test with correlated server capture.
