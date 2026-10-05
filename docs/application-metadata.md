# Complete graph uploads and application metadata

The durable document service accepts complete, non-coalescing application metadata
graphs through the existing Cell SOAP endpoint and the application metadata
partition selector. The file adapter and EditorsTable remain separate. Metadata
bytes are opaque object partitions; no Office ZIP format is imposed on them.

## Publication and queries

An uploaded storage index is a patch. The service checks each changed mapping
against the supplied expected index and merges untouched mappings from the current
partition inside the atomic provider transition. An expected index must be present
in the request package. A stale value for an affected key returns coherency failure;
changes to independent keys can proceed without overwriting each other. These rules
follow [MS-FSSHTTPB Put Changes, 2.2.2.1.4](https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttpb/e4c224ca-0717-4b02-b713-e75d353904bb).

Graphs must contain their declared roots, cells, revisions, inherited object groups
and referenced immutable BLOBs. Existing IDs cannot acquire different bytes or
types. Missing dependencies and conflicting duplicate elements fail before
publication. An empty partition can use an empty index without a manifest.
A present manifest requires at least one root, as specified by the
[Storage Manifest format](https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttpb/a681199b-45f3-4378-b929-fb13e674ac5c).
The [Storage Index format](https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttpb/f5724986-bd0f-488d-9b85-7d5f954d8e9a)
allows absent mapping entries. An empty patch against a populated partition
therefore retains its mappings.

Metadata queries return the selected graph and mapping knowledge. Scoped queries
use the requested cell's dependency closure. Put and Query waterlines use the same
stable partition identity. Mixed and repeated file, metadata and editor queries
share a deduplicated response package with independent operation results.

Metadata publication preserves current file content, file ContentVersion and
editor state. File publication preserves current opaque metadata and its knowledge.
Metadata commits call the shared revision-history publication hook and do not emit
a file AcceptedSave or enqueue external file bytes. Capture uses the source state
with updated fields so unrelated lifecycle, history and delivery fields survive.

## Applied indexes and retries

When Return Applied Storage Index Id Entries is set, successful file and metadata
saves return the applied index data element with its accepted serial and bytes.
The metadata applied patch can have a different ID from the merged selected index.
This implements [Additional Flags A](https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttpb/57ddba13-18da-452d-8cca-c43ddce28267)
and the [Put Changes response](https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttpb/731e4906-b810-430d-8796-a5e1cc8144b4).
Mandatory data is checked against response-package identity and byte limits before
commit. The complete persisted acknowledgement also must fit MaxObjectBytes,
which is the same bound used when loading it for replay.

Durable receipts are scoped by partition and owner. Metadata retry identity uses
the uploaded index ID plus a normalized operation digest. Exact retries reproduce
the stored acknowledgement after provider recreation. Reusing an accepted identity
for different changes fails; superseded metadata receipts fail coherency. File and
metadata publication retain each other's current receipts. Legacy receipt JSON
defaults to file semantics without changing its positional public constructor.

Publication checks access and lock authority at provider time before preparation,
then rechecks current access, lifecycle generation, the original coordination epoch,
effective lease authority and partition coherency at commit. Retry response loading
ends with an authoritative access, generation and matching-receipt check.
Unpublished immutable content is never exposed as the selected partition.

## Limits and remaining work

Graph bytes, element count, object bytes, durable receipt count, history count and
stored bytes use the existing StorageLimits admission checks. Automatic pruning
remains disabled. There is no accepted partial staging state, expiry or abort API.
Partial and PartialLast, alternate coherency modes, coalescing metadata writes and
buffered metadata writes remain explicit unsupported operations.

The Partial flags specify nil versus supplied storage indexes. Neither their
field definition nor [server processing, 3.1.4.3](https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttpb/94a442bc-ce2f-4f25-a245-82976b0b063c)
establishes an association key for retained fragments. ClientKnowledge does not
affect Put Changes, and MultiRequestPutHint is a coalescing hint. Safe staged uploads
still require normative or reference evidence for association, retry identity,
out-of-order fragments, commit, abort and retention before defining quotas/expiry.
No transaction token is inferred from those fields.

Tests in AppliedStorageIndexTests, StorageIndexPatchTests, MetadataPublicationTests
and GraphUploadInteropTests cover independently decoded wire output, key patches,
inherited BLOB closure, permissions, stale coherency, duplicate fragments, provider
recreation, PostgreSQL process death and two HTTP hosts. These are synthetic
protocol/storage checks. SharePoint reference captures and native OneNote
open/edit/sync and two-desktop coauthoring qualification remain outstanding.
