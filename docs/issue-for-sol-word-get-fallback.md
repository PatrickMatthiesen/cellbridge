# Issue for Sol: Word falls back to GET after FSSHTTP negotiation

**Date:** 2026-09-01
**Server:** Custom ASP.NET Core implementing MS-FSSHTTP (SOAP) + MS-FSSHTTPB (binary) to replace SharePoint for Word desktop coauthoring.
**Client:** Word 16.0.3844.3804, Windows, `UserAgentClient="msword"`, `UserAgentPlatform="win"`.

## Symptom

Word selects MS-FSSHTTP, POSTs `ExecuteCellStorageRequest` to `/_vti_bin/cellstorage.svc/CellStorageService`, we return HTTP 200 with a well-formed SOAP envelope, but Word **still falls back to a plain `GET /shared/test.docx`** and never sends `PutChanges`. Document opens read-only.

## The single request Word sends (all 10 subrequests)

```
POST /shared/test.docx/_vti_bin/cellstorage.svc/CellStorageService
SOAPAction: http://schemas.microsoft.com/sharepoint/soap/ICellStorages/ExecuteCellStorageRequest
```

Request XML (abridged to the subrequest list):

| Token | Type | DependsOn | DependencyType | Key attributes |
|-------|------|-----------|----------------|----------------|
| 1 | Coauth | – | – | `CoauthRequestType="JoinCoauthoring"` `SchemaLockID="{29358EC1-...}"` `ClientID="{58F4014B-...}"` `Timeout="2580"` `AllowFallbackToExclusive="true"` `ExclusiveLockID="{58F4014B-...}"` |
| 5 | SchemaLock | 1 | OnNotSupported | `SchemaLockRequestType="GetLock"` `SchemaLockID="{29358EC1-...}"` `ClientID="{58F4014B-...}"` `Timeout="2580"` |
| 8 | Cell | 5 | OnExecute | `PartitionID="7808F4DD-2385-49D6-B7CE-37ACA5E43602"` `BinaryDataSize="104"` |
| 7 | Cell | 5 | OnExecute | `PartitionID="383ADC0B-E66E-4438-95E6-E39EF9720122"` `BinaryDataSize="104"` |
| 6 | Cell | 5 | OnExecute | `GetFileProps="true"` `BinaryDataSize="109"` |
| 4 | EditorsTable | 1 | OnSuccess | `UpdateEditorMetadata` `Key="SupportsRename"` |
| 3 | EditorsTable | 1 | OnSuccess | `UpdateEditorMetadata` `Key="Capabilities"` |
| 2 | EditorsTable | 1 | OnSuccess | `UpdateEditorMetadata` `Key="Platform"` |
| 9 | WhoAmI | – | – | – |
| 10 | ServerTime | – | – | – |

Request-level attrs: `Url="https://web-aspire.dev.localhost:7292/shared/test.docx"` `UseResourceID="true"` `UserAgent="{1984108C-4B93-4EEB-B320-919432D6E593}"` `Build="16.0.3844.3804"` `MetaData="1031"` `ClientIsReviewOnlyTrusted="true"`.

## The three Cell binary payloads (base64, as sent)

**Cell token 8** (PartitionID `7808F4DD-...`), 104 bytes:
```
DgALAJzPKfM5lAabBgIAAO4CAACqAiAAjBCEGZNL606zIJGUMtblk1oEFgANbXN3b3JkB3dpbnoCCABG8HA7dwEWAgYAAwUAigIEAADw2gIGAAMAAMoCCAAIAIADhABBCwGsAgBVAwE=
```

**Cell token 7** (PartitionID `383ADC0B-...`), 104 bytes:
```
DgALAJzPKfM5lAabBgIAAO4CAACqAiAAjBCEGZNL606zIJGUMtblk1oEFgANbXN3b3JkB3dpbnoCCABG8HA7dwEWAgYAAwUAigIEAADw2gIGAAMAAMoCCAAIAIADhABBCwGsAgBVAwE=
```

**Cell token 6** (GetFileProps=true), 109 bytes:
```
DgALAJzPKfM5lAabBgIAAO4CAACqAiAAjBCEGZNL606zIJGUMtblk1oEFgANbXN3b3JkB3dpbnoCCABG8HA7dwFqBAIAARYCBgADBQCKAgQAAPDaAgYAAwAAygIIAAgAgAOEAEELAawCAFUDAQ==
```

> Note: tokens 8 and 7 have **identical** 104-byte payloads. Token 6 differs only in the QueryChanges request-data region (bytes after the UserAgent compound object).

## What our decoder reports for all three

All three decode successfully to a single FSSHTTPB subrequest:
```
RequestId=1  Type=QueryChanges  Priority=0
UserAgentGuid=1984108c-4b93-4eeb-b320-919432d6e593  UserAgentVersion=997257286
```
`QueryChangesSubRequestData` optional args (StorageManifestRoot, CellId, MaxDataElements, Waterline) all parse as **null** — our `Deserialize` reads only the `QueryChangesRequest` header and skips the rest.

## Raw stream-object walk of the request (after the 12-byte header)

```
pos=12 START type=0x40 (Request) len=0 compound=1
  pos=16 START type=0x5D (UserAgent) len=0 compound=1
    pos=20 START type=0x55 (UserAgentGUID) len=16
    pos=40 START type=0x8B (UserAgentClientandPlatform) len=11
    pos=55 START type=0x4F (UserAgentVersion) len=4
    pos=63 END type=0x5D
  pos=65 START type=0x42 (SubRequest) len=3 compound=1
    pos=69 END type=0x140
  pos=71 START type=0x00 len=69 compound=0   <-- QueryChanges request data (type 0x00 = QueryChangesRequest)
```

The 69-byte QueryChanges request-data region is where tokens 8/7 (104-byte payloads) and token 6 (109-byte payload) differ. We currently **skip** this region entirely.

## What we return (all three Cells get the SAME QueryChanges response)

We treat all three Cells identically: same `StorageManifestBuilder.BuildQueryChangesResponse` producing a full data-element graph (Storage Manifest → Cell Manifest → Revision Manifest → Object Group → Object Data BLOB) with the file bytes, plus a `QueryChangesSubResponseData` (StorageIndexExtendedGuid, CellKnowledgeTo, Waterline).

SOAP-level differences we apply:
- **Tokens 8 & 7** (PartitionID): `CoalesceHResult="0"` `ContainsHotboxData="True"` + full binary graph.
- **Token 6** (GetFileProps): `Etag="\"{B5F9B4EF},1\""` `CoalesceHResult="0"` `ContainsHotboxData="False"` `CreateTime=134327260782108919` `LastModifiedTime=134327260782118860` `ModifiedBy="officelab"` + **the same full binary graph**.

Other responses:
- Coauth: `CoauthStatus="Alone" LockType="SchemaLock" TransitionID="dde04eb0-974a-4199-a6ea-49f8f3cde81b" SchemaLockID="{29358EC1-...}" Timeout="2580"`
- SchemaLock (token 5): `ErrorCode="DependentOnlyOnNotSupportedRequestGetSupported"` `HResult="2147500037"` + empty `<SubResponseData/>`
- EditorsTable: `EditorsTablePresent="1"`
- WhoAmI: `UserId/UserName/UserLogin="officelab"`
- ServerTime: `ServerTime="2026-09-01T08:49:14.3852014Z"`

All subresponses carry `ErrorCode="Success" HResult="0"` except the SchemaLock fallback.

## Update after partition-routing changes

The server no longer treats the three Cell requests as one document partition.
The latest Aspire log shows:

| Token | Selected partition | Binary response size |
|-------|--------------------|----------------------|
| 8 | EditorsTable | 779 bytes |
| 7 | Metadata | 749 bytes |
| 6 | FileContents | 4061 bytes |

The server now returns `ServerTime` as UTC ticks since `0001-01-01` and returns
only `UserName` and `UserLogin` for `WhoAmI`. The request still completes with
HTTP 200 and no application exception. Word immediately issues:

```
GET /shared/test.docx
HEAD /shared/test.docx
```

It does not issue `PutChanges` and opens the file read-only.

The token 7 response visibly carries placeholder metadata XML:

```
<Metadata ContentVersion="1" Modified="639238560467165507" />
```

Token 8 carries UTF-8 XML compressed with DEFLATE and prefixed with the
eight-byte Editors Table Zip Stream Header. It is not yet serialized through
the MS-FSSHTTPD chunking path. Both partitions currently use the generic
StorageManifestBuilder response graph.

## Current hypotheses / questions

1. **The three Cells are not equivalent.** The two `PartitionID` cells and the `GetFileProps` cell now get independent storage identities and content. The remaining question is the required FSSHTTPB graph and payload shape for the metadata and editors-table partitions.

2. **QueryChanges parsing is incomplete.** The parser now reads flags, request arguments, `CellId`, and data constraints, but it still skips the actual Knowledge, versioning, filters, storage root, and waterline. The response does not yet honor those values.

3. **The GetFileProps cell** still returns SOAP file properties and a full QueryChanges binary graph. Should it return a different FSSHTTPB response type, or SOAP properties with no or empty binary data?

### Specific questions
1. For the two `PartitionID` cells versus the `GetFileProps` cell, what should each binary response contain?
2. For the editors-table partition, is MS-FSSHTTPD chunking required before the compressed zip stream enters the FSSHTTPB graph? Which data elements contain that stream?
3. What is the expected metadata partition representation? Is the metadata an application-specific stream, and which manifest or object types must reference it?
4. Should the server honor the client's QueryChanges knowledge, versioning, filters, storage root, cell ID, and waterline rather than returning a complete graph?
5. Which identities, serials, or manifest relationships does Word validate before it silently chooses the direct GET fallback?
