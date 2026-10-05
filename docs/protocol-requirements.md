# Protocol requirements ledger

This is the implementation ledger for [the completion plan](protocol-completion-plan.md)
and [issue #18](https://github.com/PatrickMatthiesen/cellbridge/issues/18).
The first audit covers the operation inventory, binary request controls and the
graph/coordination changes in this branch. The remaining attribute, response and
error tables listed below still need individual rows. This document does not
establish exhaustive conformance or desktop OneNote support.

Each row separates the wire requirement from CellBridge's implementation status.
A MUST within an operation defines its behavior when supported; it does not make
all optional protocol capabilities mandatory. Product exceptions describe the
named Microsoft products. They are evidence for a profile decision, not permission
to ignore every extension in every client version.

## Profiles and evidence

| ID | Scope | Decision and evidence | Remaining acceptance |
| --- | --- | --- | --- |
| V01 | SOAP request/response | Default version 2/minor 0; inline base64 and MTOM; `CellStorageRequestParser`, `CellStorageModels`, `MtomResponseMessage`. Covered by `FssHttpTests`, `MtomEndpointTests`, `MtomStreamingTests`. | Audit version rejection and all optional request attributes under #18. |
| V02 | Binary framing | Library defaults 12/11; endpoint serializes `SharePoint13_11`. Request syntax defines 12, 13, 14 and minimum 11. The parser reads supplied versions but does not negotiate a complete version-14 feature set. `SharePoint13ProfileTests` checks output. [Request syntax][Breq]. | Audit negotiation and invalid/unsupported versions under #18. |
| V03 | Product differences | [MS-FSSHTTPB product notes][Bproducts] distinguish version tokens, hashing, priorities and newer upload controls. The profile name does not establish full SharePoint equivalence. | Select and qualify any newer profile before advertising its conditional facilities. |
| V04 | Client qualification | Existing evidence is recorded in [interoperability](interoperability.md). Codec round trips, replay and synthetic two-host HTTP checks have narrower scope than desktop editing. | Actual target OneNote build and discovery workflow: #19/#31. Distinct authenticated desktops: #5. |

Sources in this ledger are Microsoft specifications. Local code and tests are
implementation evidence, not protocol authorities. Fixture locations are
recorded in [automated testing](automated-testing.md) and
[interoperability](interoperability.md); new synthetic tests below are not captures.

## Outer operation inventory

The [outer type enumeration][Otypes] lists fourteen operations. Dispatch is in
[CellBridgeRequestProcessor](../src/CellBridge.AspNetCore/CellBridgeRequestProcessor.cs).
This table inventories all fourteen; each operation's attribute/error audit is a
separate completion gate.

| ID | Operation or subtype | Current behavior and evidence | Classification and next acceptance |
| --- | --- | --- | --- |
| O01 | Cell | Binary execution for file, metadata and editors-table selectors. `CellPartitionSelectorTests`, `CellBinaryRequestExecutorTests`, `PartitionResponseTests`. | Implement remaining selectors/graph modes in #20/#23/#27/#28; see binary rows. |
| O02 | Coauth JoinCoauthoring | Schema lease plus explicit coauthor membership. A genuine one-to-two membership change records a pending transition. `LockTransitionTests`. [Join rules][Ojoin]. | Implemented server behavior; desktop transition/reference qualification remains #29/#5. |
| O03 | Coauth RefreshCoauthoring | Refresh preserves a longer existing expiry; expired membership rejoins. Explicit owner-matching refresh promotes legacy state without membership fields. `LockTransitionPersistenceTests`. [Refresh rules][Orefresh]. | Implemented with documented migration policy; qualify reference traffic. |
| O04 | Coauth ExitCoauthoring | Removes caller's lease/membership and its mirrored editor entry. Leaves other clients intact. `FssHttpLockCoordinatorTests`, `LockTransitionTests`. | Existing strict absent-owner error needs product-specific/idempotency audit under #18/#29. |
| O05 | Coauth ConvertToExclusive | Requires active owned membership, matching schema and valid conversion inputs. Other schema owners block conversion; requested failure release removes only caller. `LockTransitionTests`. [Conversion rules][Oconvert]. | Implemented; source/reference fixtures and HTTP qualification remain #29. |
| O06 | Coauth MarkTransitionComplete | Valid owned member acknowledges the pending transition; repeated acknowledgement succeeds. It emits no CoauthStatus and requires no invented TransitionID request attribute. `LockTransitionTests`. [Mark rules][Omark]. | Implemented; qualify captured transition lifecycle under #29. |
| O07 | Coauth GetCoauthoringStatus / CheckLockAvailability | Status counts coauthors independently of editors-table readers; availability checks locks. `LockTransitionTests`, `FssHttpLockCoordinatorTests`. | Audit optional/required inputs separately; no membership requirement inferred for status. #18/#29. |
| O08 | SchemaLock GetLock / RefreshLock / ReleaseLock / CheckLockAvailability / ConvertToExclusive | Shared leases, exact actor ownership, schema identity and exclusive conversion. `FssHttpLockCoordinatorTests`. | Implemented profile behavior; audit normative timeout/default/error differences under #18/#29. |
| O09 | ExclusiveLock GetLock / RefreshLock / ReleaseLock / CheckLockAvailability | Exclusive lease and actor checks; ordinary GetLock emits no unrelated coauthor status/transition ID. `FssHttpLockCoordinatorTests`, `LockTransitionTests`. | Implemented profile behavior; remaining input/error audit #18. |
| O10 | ExclusiveLock ConvertToSchema / ConvertToSchemaJoinCoauth | Atomic exclusive-to-schema conversion. Only the Join variant creates membership and reports stable file transition identity. Existing foreign editor identity cannot be overwritten. `LockTransitionTests`. [Conversion rules][Otoschema]. | Implemented; full reference/desktop acceptance remains #29. |
| O11 | EditorsTable JoinEditingSession / LeaveEditingSession / RefreshEditingSession / UpdateEditorMetadata / RemoveEditorMetadata | Independent presence/metadata and knowledge advancement, actor-bound GUID editor keys. `EditorsTableStateTests`, authorization tests. | Audit every attribute, timeout and invalid metadata response under #18. |
| O12 | WhoAmI | Uses authenticated subject identity. `AuthorizationEndpointTests`, `LibraryAuthenticationTests`. | Implemented; schema/attribute audit #18. |
| O13 | ServerTime | Reports server time. `FssHttpTests`, `CapturedWordOpenTests`. | Implemented; response schema audit #18. |
| O14 | GetDocMetaInfo | Current document information. `MetadataVersioningResponseBuilderTests`. | Implemented current-state response; optional fields audit #18. |
| O15 | GetVersions | Current entry, versioning disabled. Returned URL uses canonical escaped identity even when a ResourceID lookup carries an obsolete URL. `MetadataVersioningResponseBuilderTests`. | URL defect #8 fixed. Historical versions remain missing #7. |
| O16 | AmIAlone | Stable file TransitionID required; true for one active coauthor without pending transition. Readers do not count. `LockTransitionTests`. | Pending suppressing true is a compatibility interpretation, documented below. Qualify #29/#5. |
| O17 | LockStatus | Reports current shared/exclusive/unlocked state and expiry. `FssHttpLockCoordinatorTests`. | Implemented; response attributes/error audit #18. |
| O18 | FileOperation | Unsupported dispatch. | Missing Rename operation #30; requires NewFileName. [Subtypes][Ofileops]. |
| O19 | Versioning | Unsupported dispatch; internal snapshots are not public history. | Missing GetVersionList and RestoreVersion #30/#7; restoration requires Version. [Subtypes][Overops]. |
| O20 | Properties | Unsupported dispatch. | Missing PropertyEnumerate and PropertyGet #30; PropertyGet requires PropertyIds. [Subtypes][Opropops]. |
| O21 | Dependencies | OnSuccess, OnFail, OnNotSupported and OnExecute determine execution; OnExecute accepts evaluated NotSupported fallback. `DependencyTests`. [Dependency rules][Odependencies]. | Implemented; cycle/token/batching edge audit #18. |
| O22 | ResourceID, URL and GetFileProps | Resource identity wins when requested; unknown identity never creates a document or falls back to URL. GetFileProps supplements binary content. Identity/routing tests and captured Word open. | Implemented; audit other outer Cell attributes under #18. |

### Coordination persistence policy

`CoordinationState` stores coauthor membership and pending transition separately
from `EditorState`. Membership references a schema lease. Permission revocation
removes membership with its lease; expiry removes only expired membership. A
pending transition survives a drop from two members to one until acknowledgement;
complete teardown clears it. The file's ResourceID remains its TransitionID.
`LockTransitionPersistenceTests` exercises detached coordinators across persisted
transactions and old JSON without the additive fields. The PostgreSQL case is
opt-in in the full runner.

Older persisted editors-table entries alone do not prove coauthor membership.
An explicit owner-matching RefreshCoauthoring request promotes a legacy schema
owner into the new tracker. This is migration policy. It avoids silently treating
readers as coauthors. `AmIAlone` returns false while a transition remains pending;
this is a compatibility interpretation of notification behavior, not a literal
extra rule in the [AmIAlone operation][Oalone]. MS-SHDACCWS IsOnlyClient is a
separate operation and is not implemented here.

## Binary operations and request envelope

[The current normative request enumeration][Btypes] contains four operations.
Additional legacy enum values in `Enums.cs` do not establish a current requirement.

| ID | Wire requirement/applicability | Current behavior and evidence | Classification and next acceptance |
| --- | --- | --- | --- |
| B01 | QueryAccess, type 1 | Reports read/write permission separately. `QueryAccessTests`, `AuthorizationTests`. | Implemented. |
| B02 | QueryChanges, type 2 | Mixed/repeated file, metadata and editor queries share a deduplicated package with independent knowledge/errors, per-query and aggregate budgets. Editor snapshot identities derive from the exact stream and persisted document namespace. `RepeatedQueryTests`, `MixedPartitionQueryTests`, `EditorSnapshotTests`, Microsoft-decoded `LiveQueryChangesTests`. | Query assembly implemented #23; filter specialization/reference qualification remain. Metadata graph incomplete #28. |
| B03 | PutChanges, type 5 | Graph-aware file save with coherency, content validation, durable publication and receipts. `FilePartitionSaveTests`, `CapturedSaveTests`, `PublicationTests`. | Staging/non-file modes missing #27. |
| B04 | AllocateExtendedGuidRange, type 11 | Typed request/response codecs; fresh UUID namespace and exact count for 1..100000, with exclusive upper bound in 1000..100000. Requires write access; does not mutate content/state. `GuidAllocationTests`, independently decoded `GuidAllocationInteropTests`. [Allocation rules][Ballocate], [response bounds][Ballocresponse]. | Implemented #25; oversized/zero counts are an explicit local rejection policy. UUID namespaces avoid a persisted global counter; uniqueness has normal UUID collision probability. |
| B05 | Enum-only QueryKnowledge / QueryRawStorage / PutRawStorage / QueryDiagnosticStoreInfo | Explicitly unsupported. Not listed in the current normative four-operation enumeration. | No new required method inferred. Historic-profile applicability still needs audit #18. |
| B06 | UserAgent identity/version | GUID or strict UTF-8 ClientAndPlatform accepted; both accepted on read because simultaneous identity is SHOULD NOT, not MUST NOT. String form replaces GUID on output. Required version, duplicate/length/framing checks. `UserAgentTests`; captured no-UserAgent QueryAccess exception retained. [Request syntax][Breq]. | Compatibility defect #37 fixed; no inference of OneNote qualification. |
| B07 | Optional TargetPartitionId | Typed selector precedes operation data; malformed/duplicate/misplaced objects fail. Explicit known targets select their partition; absent targets inherit the SOAP selector, including after another operation used an explicit target. Unknown selectors get explicit unsupported errors. [Target partition][Btarget]. | Known routing implemented #23/#38. `TargetPartitionTests`, `TargetPartitionExecutionTests`, mixed Microsoft-client interoperability test. Unsupported partition writes remain explicit errors. |
| B08 | Subrequest ID / priority | IDs and priorities retained. Current execution uses wire order, allowed by SharePoint 2013 product behavior. [Subrequest rules][Bsubreq]. | Unique ID and range validation audit remains #18. Ascending priority required when selecting a profile without that exception. |
| B09 | RequestHashOptions | Optional hashing declaration is skipped; storage SHA-256 is not a wire data-element hash. [Hashing][Bhash]. | Conditional capability #26; excluded-data forms and negotiated schema need implementation/evidence before advertisement. |
| B10 | CellRoundtripOptions | Version-token/non-generic hints skipped. SharePoint 2013 product behavior lacks this extension. | Profile audit #18; version-token support #7 before a newer negotiated profile. |
| B11 | Reserved envelope/allocation fields | Allocation writer emits zero; reader ignores reserved byte. `AllocateExtendedGuidRangeTests`. | Other reserved fields and unknown-object handling still need field-level audit #18. |

## Query controls and knowledge

The [QueryChanges rules][Bquery] and [product notes][Bproducts] define the controls.
Code is in `QueryChangesSubRequestData`, `FileQueryResponseBuilder`,
`ClientKnowledge` and `QueryChangesResponseShaper`.

| ID | Control | Current behavior and evidence | Classification / gate |
| --- | --- | --- | --- |
| Q01 | AllowFragments / AllowFragments2 | Parsed as permission; no fragment response or continuation. Budget failures return explicit errors. `QueryChangesResponseShaperTests`. | Missing continuation #24; permission alone does not require fragmentation. |
| Q02 | IncludeFilteredOutDataElementsInKnowledge | Parsed and used when building returned knowledge. `BudgetAndQueryTests`, `RetentionAndKnowledgeTests`. | Implemented. |
| Q03 | RoundKnowledgeToWholeCellChanges | Whole cell retransmission when requested and any visible element is unknown. Knowledge tests. | Implemented. |
| Q04 | ReturnFileHash | Parsed; not emitted. SharePoint 2013 product behavior ignores this control. | Conditional #26; distinguish this profile exception from newer profile expectations. |
| Q05 | UserContentEquivalentVersionOk / response equivalent-version bit | No equivalence-version model. | Profile/product audit #18; version behavior #7. |
| Q06 | IncludeStorageManifest / IncludeCellChanges / CellId | Actual mapped file cells selected from the index/manifest; multi-cell scopes use bounded current dependency closure, per-scope knowledge and waterline zero. `RepeatedQueryTests`, `InheritedFileGraphTests`. | Supported file scope subset #23; filter specialization and reference qualification remain. No invented wire root control. |
| Q07 | Data constraint | Exact serialized byte budgeting; no continuation when too small. `QueryChangesResponseShaperTests`. | Budget behavior implemented; continuation #24. Length measurement without temporary serialization is performance #33. |
| Q08 | QueryChangesVersioning | Unsupported rather than treating legacy Waterline as a version token. | Missing selected history capability #7; SharePoint 2013 ignores the extension. |
| Q09 | All / type / index-reference / cell / custom / ID / hierarchy filters | Parser skips specialization; full response by default; CellBridge returns an error when FailIfUnsupported permits failure. Filtered query tests. | Permitted full fallback; specialization is optional traffic reduction until a target client needs it. #23/#33. |
| Q10 | GUID/serial ranges and mapping knowledge | Authoritative mapping serials, conservative retransmission, bounded entries. Query response decoding retains the validated complete Knowledge bytes, preserving all serial/mapping ranges on reserialization; scalar fields remain a minimal summary. Clear `KnowledgeBytes` to deliberately rebuild from scalar fields. `ClientKnowledge`, `StorageIndexMappingSerials`, `QueryKnowledgeResponseTests`. | Implemented supported forms; no broader response-knowledge parsing claim. Unknown request knowledge forms use validated full-response fallback. |
| Q11 | Waterline, fragment, version-token and other knowledge forms | Waterline alone never suppresses bytes. Specialized forms need further audit. | Valid conservative fallback where permitted; fragment/version semantics depend on #24/#7. #18 tracks exact applicability. |

## Upload controls

[PutChanges][Bput] and [AdditionalFlags][Bputflags] define these fields. Actual
acceptance is in `FilePartitionSaveHandler`, not just the deserializer.

| ID | Control | Current behavior and evidence | Classification / gate |
| --- | --- | --- | --- |
| W01 | Expected storage index / imply-null flag | Graph mapping coherency before atomic publication. File save tests. | Implemented supported file mode. |
| W02 | Partial / PartialLast | Explicit per-operation rejection before publication; independent queries and complete saves continue. `UploadHintTests`. | Missing staging #27. |
| W02a | MultiRequestPutHint | Accepted for complete file saves. This flag reduces automatic coalescing; it does not establish a staged transaction. Normal publication, locks, permissions and receipt retries apply. `UploadHintTests`, including PostgreSQL provider recreation. | Complete-save hint implemented #27; no claim of partial upload staging. |
| W03 | FavorCoherencyFailureOverNotFound | Flag permitted; audit exact error precedence. | Unresolved implementation evidence #18/#27. |
| W04 | AbortRemainingPutChangesOnFailure / ReturnCompleteKnowledgeIfPossible | Accepted without forcing new semantics; specification says server ignores these controls. | Required ignore behavior, not a missing feature. |
| W05 | LastWriterWinsOnNextChange | Explicitly rejected. | Missing alternate coherency #27. |
| W06 | ContentVersionCoherencyCheck / AuthorLogins / reserved byte | Opaque coherency bytes retained then ignored as specified; author strings retained, identity/access still comes from authenticated actor; reserved byte ignored on read. Put codec/save tests. | Field-specific historical applicability audit #18; no client-supplied permission identity. |
| W07 | ReturnAppliedStorageIndexIdEntries | Null applied-index ID when bit A is clear, matching the captured Word saves. With A set, the ID is returned but the requested index data is missing. `SaveAcknowledgementTests`. | Missing returned index data #18/#27; ordinary captured Word saves do not request it. |
| W08 | ReturnDataElementsAdded | Returns newly admitted unique IDs, including when bit B is clear, matching captured SharePoint Word acknowledgements. Retained and duplicate IDs are excluded; durable retries replay the stored acknowledgement. `SaveAcknowledgementTests`, captured HTTP save replay with independent decoder. | Implemented reference profile; Word's intermittent pending-save UI still needs desktop qualification. |
| W09 | CheckForIdReuse | Conflicting reused element IDs rejected before publication. Save tests. | Implemented supported retained graph. |
| W10 | CoherencyCheckOnlyAppliedIndexEntries | Explicit rejection. | Missing mode #27. |
| W11 | FullFileReplacePut | Explicit rejection; optional flag, absent in SharePoint 2013 behavior, and not required for a full save. | Optional/profile capability #18/#27; do not label ordinary full-file saves missing. |
| W12 | RequireStorageMappingsRooted | Explicit rejection. | Missing requested validation mode #27. |
| W13 | AdditionalFlags reserved bits/compact reserved field | Reserved high bits accepted; trailing bytes retained. Excel bit 15 regression passes. | Audit canonical reserved write and full malformed-field handling #18. |
| W14 | Lock ID / client knowledge / diagnostics | Lock token parsed and checked. Other optional trailing objects skipped; diagnostic requests are not diagnostic-store operations. | Codec/response applicability audit #18; knowledge optimization is conditional #33. |

## Graph and application partitions

[The abstract data model][Badm] defines cell-scoped objects, partitions and inherited
revisions. A schema adapter decides how opaque bytes become a usable application
file. The file adapter still validates its schema/root and reconstructed package.

| ID | Requirement | Current behavior and evidence | Classification / gate |
| --- | --- | --- | --- |
| G01 | Multiple roots, cells and object partitions | Immutable generic graph plus format-2 durable capture/restore; file saves and scoped queries preserve cell/partition identity. `GenericGraphPersistenceTests`, `RepeatedQueryTests`. | File host integration implemented #20; general non-file publication and independent graph reference fixtures remain. |
| G02 | Nearest definitions through base revisions | Generic resolver supplies inherited file objects with nearest-definition precedence. Missing/cyclic required ancestry fails before publication; legacy self-contained file path retained. `InheritedFileGraphTests`. | File save/reopen and scoped query integration implemented #21; reference qualification remains. |
| G03 | Opaque objects, BLOB and cell references | Capture/restore retains immutable payload handles, mapping serials and BLOB closure. File publication resolves declared BLOBs by identity; scoped queries follow effective cell references and allow bounded cycles. `GenericGraphPersistenceTests`. | File flow and generic codec implemented #22; non-file publication and reference qualification remain. Automatic pruning stays disabled. |
| G04 | File reconstruction | `PartitionGraphSnapshot` adapts resolved partition-1 objects through `FilePartitionSaveHandler` with ZIP/content validation and graph identity. Metadata partitions cannot satisfy file references; never largest-BLOB recovery. File/replay tests. | Supported file adapter integration #20; no OneNote adapter implied. |
| G05 | Hash-prefixed/excluded-data object groups | Not supported by the new opaque parser. | Conditional negotiated hashing #26. |
| G06 | Application metadata | Existing index placeholder; no complete application metadata graph read/write. | Missing #28. |
| G07 | Native OneNote synchronization | No qualified discovery/open/edit/sync workflow or server adapter. Synthetic generic graph tests establish no client compatibility. | Missing target #19/#31, depends on graph, query and staging work. |
| G08 | Physical .one/.onetoc2 import/export or semantic preview | Separate file codecs/application features. | Outside the chosen desktop synchronization milestone. |

## Audit still required before completing #18

- Expand the inventoried outer subtypes into attribute-level rows and
  enumerate every outer request/response attribute, dependency/token constraint
  and applicable error. Current broad unsupported dispatch is known evidence;
  no subtype requirement is guessed from an enum name.
- Complete response, filter, knowledge and stream-object inventories, including
  hashes, fragments, diagnostic structures and profile-specific ignored fields.
- Add fixture/acceptance links for each supported normative rule. Explicitly mark
  rules with only synthetic tests and reference fixtures still needed.
- Validate advertised discovery capabilities against these rows. Keep version-14
  parsing distinct from version-14 negotiated feature support.

Operational export/recovery/compaction is tracked by #32/#35/#3. Performance work
is tracked by #33. Fuzzing/differential validation is #34. These categories do not
become missing protocol methods just because they improve reliability or speed.

[Breq]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttpb/f44cb4d0-9ce7-48f2-8b19-90785ef70703
[Bproducts]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttpb/d006ebd9-c3df-4ef5-8be0-1c1db78c6d2c
[Btypes]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttpb/4d227fdc-d30c-4f50-8394-76b77f09ff62
[Bsubreq]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttpb/a29b394a-2572-4f60-b032-ce15bf278233
[Btarget]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttpb/bc25b031-1b4f-4c90-bd08-20a844131841
[Ballocate]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttpb/2e79b02f-f9ab-4933-99d4-bc360c27f75d
[Ballocresponse]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttpb/426a9c19-db24-46fd-a97d-83044af1c3c3
[Bquery]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttpb/5b8d1d29-0adf-4b29-b3d1-1a1fe8590642
[Bhash]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttpb/82458bac-fb0f-47d3-a919-9871defdc796
[Bput]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttpb/e4c224ca-0717-4b02-b713-e75d353904bb
[Bputflags]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttpb/57ddba13-18da-452d-8cca-c43ddce28267
[Badm]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttpb/6c7e4447-6ccd-4764-8dbc-17a382fb631d
[Otypes]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttp/a3bb03aa-bbc6-4fab-96f4-4909fb2b813b
[Odependencies]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttp/97e4cd04-106e-459d-89cd-95589978456c
[Ojoin]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttp/87ca4a76-f156-4cc7-b2f2-f7624d5a9887
[Orefresh]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttp/2eca5d82-69b7-4b86-9a59-4809a2c5e342
[Oconvert]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttp/1624294a-f802-4d1e-8727-c0ba29b3e447
[Omark]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttp/c0e4b310-32e4-45c6-9037-ca61d06f0367
[Otoschema]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttp/e9ffd3a6-784e-4f80-8a80-083974b2ce93
[Oalone]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttp/5800d819-5975-433f-b138-0d340703f680

[Ofileops]: https://learn.microsoft.com/th-th/openspecs/sharepoint_protocols/ms-fsshttp/ade57c1d-c752-4a92-bc9e-b4993a00a509
[Overops]: https://learn.microsoft.com/th-th/openspecs/sharepoint_protocols/ms-fsshttp/01aa24ee-02c0-49b9-9f60-837d78d277e6
[Opropops]: https://learn.microsoft.com/th-th/openspecs/sharepoint_protocols/ms-fsshttp/b66e6237-5d97-4792-8f03-cb224f3fa9f5
