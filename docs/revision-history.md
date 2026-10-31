# Recoverable revision history

CellBridge stores immutable document publications alongside its current state.
Each publication records the resource GUID, lifecycle generation, history revision
number, file content version, authenticated author, authoritative timestamp,
materialized file handle and file/application-metadata protocol partitions.
Editor sessions, permissions and leases remain current state. Historical access
always uses current permissions, including trusted-host request limits and revocation.

History revision numbers advance on committed file saves, application-metadata
publications and restores. Metadata-only publications can share a file content
version. Retries and editor/lease changes do not create history. Creation records
the initial revision. An older format-2 document without history seeds only its
known current state before its next publication; earlier edits cannot be recovered.

## Read and restore

Reusable hosts can call `CellBridgeDocumentService.ListRevisionsAsync`,
`GetRevisionAsync` and `RestoreRevisionAsync`. A restore requires the selected
revision number, expected current history revision and a caller-supplied operation
key. Reuse the same key and arguments after an uncertain commit result. The
receipt returns the original committed revision even if a later save has occurred.
Changing the arguments for an accepted key fails. Receipts belong to an
authenticated subject and lifecycle generation; a revoked caller cannot replay them.

`GetVersions` and `Versioning/GetVersionList` list the same records. Every
`GetVersions` URL addresses an authenticated GET/HEAD endpoint:

```text
/_cellbridge/history/{resourceId}/{lifecycleGeneration}/{revisionNumber}
```

`GetVersions` also requires the host's document-library identity. Configure
`CellBridgeOptions.DocumentLibraryId` with one nonempty GUID for the catalog and
keep it unchanged across documents, hosts and restarts. Without this setting,
`GetVersions` returns `RequestNotSupported`; the reusable revision API and
`Versioning/GetVersionList` remain available. The response includes the library
GUID and the authenticated `/_cellbridge/history/settings` page, with the host's
mount path and validated public origin. That read-only page describes the actual
publication limit and does not offer settings changes or automatic pruning.

For WebDemo, set `Protocol:DocumentLibraryId`. Through the Aspire AppHost, set
`Parameters__DocumentLibraryId`. Generate the catalog GUID once, for example with
PowerShell's `[guid]::NewGuid()`, and retain it in the host configuration.

The resource and generation identify the document independently of renames.
Unavailable revisions return 404 and denied access returns 403. Downloads carry
private/no-store caching, an immutable revision ETag and the recorded timestamp.
Versioning is enabled only for documents whose revision repository has been initialized.
The legacy `BuildGetVersions(StoredDocument, ...)` API still describes current-only
storage with versioning disabled; the request processor uses `DocumentState`.

Restore stages and verifies both historical content and graphs before an atomic
publication. It creates fresh cell manifests, revision wrappers, storage index
identities and serials above retained mapping watermarks. Wrappers inherit the
selected historical objects and repeat their roots. The new index selects historical
cells; cells added in later revisions are not selected. Retained current and inherited
records remain available. Restore preserves live security, editors and coordination,
advances the file content version, appends a new history record and queues external
delivery when the current document has a stored destination binding.
An empty historical metadata index publishes a fresh empty index without inventing
a storage manifest. [Storage index mappings are optional][index]. A present
[storage manifest must declare at least one root][storage-manifest].

The commit rechecks access, lifecycle and coordination generation, expected history
revision and both selected partition generations. Concurrent save/restore operations
from the same expected graph have one winner. Changing permissions, locks or metadata
during preparation cannot be overwritten. Restoring the current revision still
creates a new publication.

SOAP `Versioning/RestoreVersion` accepts numeric major/minor version strings and
returns `VersionNotFound` for a well-formed unavailable version. Published versions
currently use `revisionNumber.0`. SOAP request/subrequest tokens correlate messages;
the specification does not establish them as globally unique retry keys.
Each SOAP restore request is a new restore. The keyed reusable API provides
retry guarantees. No automatic retry of an uncertain SOAP restore should be inferred.

## Outer document operations

`FileOperation/Rename` requires a filename without a relative path and write access.
It preserves the resource GUID, content, graph and history. Every document provider
implements the typed `IDocumentRepository.ChangePathAsync` operation. The affected
document lock and a unique destination path claim make the move atomic, including
case-only rename. Existing protocol locks remain authoritative. Paths use the same
decoded storage convention as creation; public URLs escape each segment once.

Released canonical path keys remain permanently reserved to that resource, including
after deletion. Old-path lookup remains a miss; the same resource may rename back.
Other resources cannot create or rename into a reserved name. Reactivating an owned
path does not allocate another reservation or consume another retired-path slot.
The default cap is 128 retired keys per resource. Admission fails without evicting
names when the cap would be exceeded.

Ordinary rename and lifecycle operations do not scan retained snapshots or take a
provider-wide namespace lock. Recreation uses the retired document row and its
specific path claim. It preserves the tombstone and creates a new resource identity
with the next lifecycle generation, one initial revision and no copied receipts.
Recreated resources require explicit matching `UseResourceID` for SOAP mutations.

`Properties/PropertyEnumerate` lists the derived document property IDs.
`Properties/PropertyGet` accepts `PropertyIds/PropertyId` elements with lowercase
`id` attributes and returns available values with lowercase `id`/`value` attributes.
Unknown IDs are omitted. These read operations do not implement SharePoint lists,
search, application-metadata mutation or arbitrary property storage.

`QueryChangesVersioning` selects the retained file and application-metadata graphs.
The documented eight-byte numeric major/minor form uses `N.0` for revision N and
`0.0` for current state. Microsoft 365 16.0.20527 instead sends a compact version
token for the numbers in `Versioning/GetVersionList`. CellBridge accepts the
observed canonical form (compact major, compact minor, zero) and rejects other
token formats. An unavailable revision returns an error; it never falls back to
the current graph. Editors and permissions remain current state. Legacy Waterline
remains knowledge and cannot be serialized as a version selector. CellBridge does
not issue Version Token Knowledge or accept arbitrary server-issued token formats.

The observed native `RestoreVersion` request supplies the reserved schema marker
as `LockID`, without its editing session's schema ID or client ID. For that exact
request form, CellBridge resolves one active schema lease owned by the authenticated
subject. It then validates the actual schema and client IDs through the existing
save preparation and atomic publication checks. Multiple active clients, another
subject's lease, host locks and exclusive locks do not use this resolution path.
Ordinary requests still require their explicit lock identifiers.

## Retention and upgrades

History has no automatic expiration in this batch. The defaults allow 1,000 history
revisions and 1,000 keyed restore receipts per document. Existing document/global
stored-byte limits also charge unique historical content handles and repeated JSON
snapshot metadata. Admission rejects a publication atomically when a limit would be
exceeded. Staged unreferenced objects can remain charged until existing quiescent
maintenance reclaims them. History, current graphs, inherited records and pending
external delivery all contribute references to content collection.
Reservations cannot recover paths released before this feature was installed.

Graph element, byte and revision-depth limits still apply. Restore explicitly fails
for an incomplete graph or unsupported transition from legacy inline metadata to an
opaque application-metadata graph. It does not recover by guessing BLOBs. Live graph
pruning remains separate work. The [provider recovery tool](provider-portability.md)
exports/imports retained history and provides an operator restore-version command.

The JSON state codec remains format 2 with additive defaults. New binaries read
old state and preserve history/lifecycle/publication fields through detached capture.
Upgrade all hosts together: old binaries can discard unknown JSON fields when writing.
Rolling downgrade compatibility is not supported.

## Evidence and remaining gates

`RevisionHistoryTests`, `RestoreFencingTests`, `OuterDocumentOperationTests`,
`HistoricalGraphRestorerTests`, `NativeHistoryRestoreTests` and `HistoryProfileFallbackTests` cover publications,
keyed retries, lost commit replies, authority changes, quotas, codec round trips,
wire XML, inherited BLOBs/multiple cells and canonical history downloads.
PostgreSQL tests recreate independent providers; `RevisionHistoryInteropTests`
exercises history across two isolated HTTP hosts. These are synthetic server tests.
`HistoryLifecycleIntegrationTests` exercises actual rename, deletion and recreation
with the shared lifecycle providers, including old history retention, foreign alias
reservation, clean replacement history and recreated SOAP mutation fencing.
`CombinedPublicationTests` checks metadata and file publication with queued external publication,
restoration, exact retries, atomic quota rejection and recreated resources on both providers.
Native Word, Excel and PowerPoint `16.0.20527` listed persisted revisions, opened
older content and restored it through their Version History controls. Independent
downloads verified the restored content and retained history after a server
restart. Fresh Office processes repeated listing and historical open. See
[client coverage](interoperability.md#client-coverage) for the tested scope.

Independent decoded SharePoint version/rename/property reference traces and
qualification on other Office builds remain open in [#7][history-issue] and
[#30][outer-issue]. No OneNote or two-desktop coauthoring compatibility follows
from these checks.

[products]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttpb/d006ebd9-c3df-4ef5-8be0-1c1db78c6d2c
[index]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttpb/f5724986-bd0f-488d-9b85-7d5f954d8e9a
[storage-manifest]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttpb/a681199b-45f3-4378-b929-fb13e674ac5c
[history-issue]: https://github.com/PatrickMatthiesen/cellbridge/issues/7
[outer-issue]: https://github.com/PatrickMatthiesen/cellbridge/issues/30

Normative operation and schema references: [restore][restore], [version list][list],
[file version data][data], [rename input][rename], [properties input][properties],
[property ID][id], [property value][value] and [revision manifest][manifest].

[restore]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttp/6e8d9f46-fa5a-46fc-a7e2-135c69c937f5
[list]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttp/025134f4-f3a9-48df-a90c-8e0e07b30ae6
[data]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttp/3b1395c6-5f4c-4125-9efa-f8fb8bf5cf74
[rename]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttp/cac57311-5f4f-41c8-aba5-1847da504926
[properties]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttp/a1f4efe8-dc76-4539-88d8-e10cbc8a0447
[id]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttp/1c2c25ec-dbf5-464a-bee3-516dff4b41d5
[value]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttp/a2b4a5d3-2362-43fd-a2a5-13ffe7ddb25f
[manifest]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttpb/eb3351db-8626-4804-a35b-f3eeda13c74d
