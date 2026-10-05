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
key. Reuse the same key and arguments after an uncertain commit result. The durable
receipt returns the original committed revision even if a later save has occurred.
Changing the arguments for an accepted key fails. Receipts belong to an
authenticated subject and lifecycle generation; a revoked caller cannot replay them.

`GetVersions` and `Versioning/GetVersionList` list the same records. Every
`GetVersions` URL addresses an authenticated GET/HEAD endpoint:

```text
/_cellbridge/history/{resourceId}/{lifecycleGeneration}/{revisionNumber}
```

The resource and generation identify the document independently of renames.
Unavailable revisions return 404 and denied access returns 403. Downloads carry
private/no-store caching, an immutable revision ETag and the recorded timestamp.
Versioning is enabled only for documents whose revision repository has been initialized.
The legacy `BuildGetVersions(StoredDocument, ...)` API still describes current-only
storage with versioning disabled; the durable request processor uses `DocumentState`.

Restore stages and verifies both historical content and graphs before an atomic
publication. It creates fresh cell manifests, revision wrappers, storage index
identities and serials above retained mapping watermarks. Wrappers inherit the
selected historical objects and repeat their roots. The new index selects historical
cells; cells added in later revisions are not selected. Retained current and inherited
records remain available. Restore preserves live security, editors and coordination,
advances the file content version, appends a new history record and queues external
delivery when the current document has a durable destination binding.
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
the specification does not establish them as durable globally unique retry keys.
Each SOAP restore request is a new restore. The keyed reusable API provides durable
retry guarantees. No automatic retry of an uncertain SOAP restore should be inferred.

## Outer document operations

`FileOperation/Rename` requires a filename without a relative path and write access.
It preserves the resource GUID, content, graph and history. Providers opt into
`IAtomicDocumentRenameStore`; the in-memory and PostgreSQL providers coordinate
state and destination-path uniqueness atomically, including case-only rename.
Other providers return `NotSupported`. Existing protocol locks remain authoritative.
Paths use the same decoded storage convention as creation; public URLs escape each
segment once.
Released canonical path keys remain permanently reserved to that resource, including
after deletion. Old-path lookup remains a miss; the same resource may rename back.
Other resources cannot create or rename into a reserved name. Providers own these
reservations and reject callbacks that remove them. The default cap is 128 keys per
resource; admission fails without evicting names when the cap would be exceeded.
PostgreSQL serializes namespace changes with one transaction advisory lock acquired
before document/path locks. Its initial reservation lookup scans current state JSON,
including tombstones, within document-count and metadata-byte budgets. Large catalogs
can make this scan expensive. Lifecycle recreation uses the same namespace guard
and preserves the old tombstone's reservations. A replacement accepts empty history
or one coherent current snapshot for its new resource identity, with no copied
restore receipts. Recreated resources require explicit matching `UseResourceID`
for SOAP mutations. A case-only rename keeps the existing canonical path ownership.

`Properties/PropertyEnumerate` lists the derived document property IDs.
`Properties/PropertyGet` accepts `PropertyIds/PropertyId` elements with lowercase
`id` attributes and returns available values with lowercase `id`/`value` attributes.
Unknown IDs are omitted. These read operations do not implement SharePoint lists,
search, application-metadata mutation or arbitrary property storage.

The server follows the SharePoint 2010/2013 profile for `QueryChangesVersioning`:
it ignores the bounded extension and queries current state. This is the product
behavior in [MS-FSSHTTPB Appendix B, notes 4 and 17][products]. It does not implement
Version Token Knowledge or higher-profile historical binary queries. Legacy
Waterline remains knowledge and cannot be serialized as a version selector.

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
pruning, provider export/import and migration tooling remain separate work.

The JSON state codec remains format 2 with additive defaults. New binaries read
old state and preserve history/lifecycle/publication fields through detached capture.
Upgrade all hosts together: old binaries can discard unknown JSON fields when writing.
Rolling downgrade compatibility is not supported. Existing public constructors and
legacy receipt semantics remain available; no beta package is republished by this change.

## Evidence and remaining gates

`RevisionHistoryTests`, `RestoreFencingTests`, `OuterDocumentOperationTests`,
`HistoricalGraphRestorerTests` and `HistoryProfileFallbackTests` cover publications,
keyed retries, lost commit replies, authority changes, quotas, codec round trips,
wire XML, inherited BLOBs/multiple cells and canonical history downloads.
PostgreSQL tests recreate independent providers; `RevisionHistoryInteropTests`
exercises history across two isolated HTTP hosts. These are synthetic server tests.
`HistoryLifecycleIntegrationTests` exercises actual rename, deletion and recreation
with the shared lifecycle providers, including old history retention, foreign alias
reservation, clean replacement history and recreated SOAP mutation fencing.
`CombinedPublicationTests` checks metadata and file publication with queued external publication,
restoration, exact retries, atomic quota rejection and recreated resources on both providers.
Independent SharePoint version/rename/property traces and desktop version restoration
qualification remain open in [#7][history-issue] and [#30][outer-issue]. No OneNote or
two-desktop coauthoring compatibility follows from these checks.

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
