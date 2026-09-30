# API comparison with captured Word requests

Replayed the four `save_reopen` fixtures against the running CellBridge
HTTP endpoint on localhost:5181. Only the SOAP document URL was retargeted to
`/shared/test.docx`; the captured binary requests were preserved. This is an API
probe using reference traffic, not a fresh desktop Word acceptance test or a
stateful replay of the SharePoint document graph.

## Observed results before the partition fix

| Request | SharePoint | CellBridge |
| --- | --- | --- |
| Open/reopen Cell tokens 1, 2, 3 | SOAP Success | SOAP Success |
| Open/reopen Cell token 6, inline QueryAccess without PartitionID | Success | InvalidArgument |
| Open/reopen GetVersions token 4 | Success with nested version results | NotSupported |
| Open/reopen ServerTime token 5 | Success | Success |
| Both saves | SOAP Success | SOAP Success wrapping binary RequestNotSupported |

HTTP 200 and SOAP Success alone do not prove save success. The binary executor
explicitly rejects PutChanges and leaves the document unchanged.

## Fix and verification

Extracted Cell partition selection into `CellPartitionSelector`. Missing SOAP
PartitionID and explicit all-zero GUID select FileContents. GetFileProps now
requests response properties without overriding an explicit partition selector.
Malformed or unknown explicit IDs remain rejected.

The default GUID and GetFileProps semantics are documented in
[MS-FSSHTTP CellSubRequestDataOptionalAttributes](https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttp/6d98cb6b-11a7-4c8d-8fbe-5cf291e1c223).
The omitted selector on inline QueryAccess is present in both captured open
fixtures. Binary Target Partition Id handling remains separate unfinished work;
the decoder currently skips that extension.

Seven selector cases cover missing, zero, metadata, editors, malformed and unknown
IDs, including interaction with GetFileProps. All 129 offline .NET tests pass;
three opt-in live tests are skipped in that run. Added a live regression test
that replays both actual Word open messages and checks each Cell response with
the independent Microsoft binary response parser, including token 6.

The web resource was stopped for rebuilding. Automatic approval review rejected
the subsequent restart-and-live-test command with `blocked by policy`, so this
turn has no live verification of the new fix and no desktop Word result. The
previous branch baseline had passed two live interop tests before this fix.

## Next protocol work, in evidence order

1. **QueryChanges response graph and filtering.** File and metadata handlers
   always return a storage index, regardless of request knowledge or filters.
   SharePoint file responses in the paired fixtures are 1,334 bytes on open and
   14,486 on reopen. Byte lengths are diagnostic evidence, not expected constants
   for our different document. Implement requested manifests/objects and resulting
   knowledge rather than sending a fixed response shape.
2. **PutChanges graph application.** Apply incoming data elements to the correct
   partition and validate knowledge/coherency; produce resulting knowledge and
   materialize document content from the graph. Largest-BLOB selection is invalid.
   The new saved-request fixtures now parse completely and are available for this.
3. **Discovery-dependent SOAP operations.** GetVersions and GetDocMetaInfo are
   unsupported. The current API advertises X-MSFSSHTTP 1.0, which gates these out
   for compliant clients; the SharePoint fixtures use the richer operation set.
   Implement their real response models before raising the advertised capability.
   GetVersions uses nested GetVersionsResponse/GetVersionsResult/results, not an
   empty SubResponseData. See [MS-FSSHTTP GetVersions](https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttp/6e6db998-6014-4eef-b43d-4ff1ce3b4ba5).
4. **Coauthoring and locks.** Session/editor operations exist, but SchemaLock
   grants a synthetic lock without checking ownership or conflict. ConvertToExclusive
   and MarkTransitionComplete currently report success without a state transition.
   Implement actual lock state before claiming multi-client correctness. AmIAlone,
   LockStatus, ExclusiveLock, Versioning, Properties and FileOperation have no
   handlers beyond NotSupported; add the operations exercised by the next Word
   session in order, with state-transition tests.

The baseline transport and parser tests are useful, but do not establish that
Word accepts our generated document graph. Next desktop test should use
`https://localhost:7292/shared/test.docx` once the web resource is running; the
Aspire-generated hostname does not resolve through this laptop's OS DNS.

## Desktop Word attempt after the default-selector fix

The web resource was subsequently started successfully using a separate Aspire
CLI command. The capture replay passed against that running API. Desktop Word
opened `https://localhost:7292/shared/test.docx` in Protected View, then briefly
appeared editable after Enable Editing before reverting to read-only.

The API logged a successful Coauth join at 10:30:50 UTC. Word sent the three
partition queries, editor metadata updates, WhoAmI and ServerTime. The file
response contained only an empty storage index, 149 bytes. The metadata response
was 202 bytes with a null manifest mapping. Word then performed direct GET and
HEAD requests. No authentication rejection or PutChanges was observed in this
sequence. This establishes that Word attempted the collaboration protocol; it
does not prove why Word abandoned it.

The actual SOAP request is preserved in `testdata/api/word-enable-editing.xml`
and now has a live regression alongside the SharePoint open/reopen fixtures.
Raw console evidence remains local at
`tools/capture/captures/api-word-readonly-20260925.json`.

Authentication remains a separate experiment. MS-FSSHTTP assumes authentication
is supplied by underlying protocols and leaves authorization to the storage
service. Our API currently grants the lab identity access without challenging
the caller. Word reached JoinCoauthoring in this configuration, so lack of an
HTTP challenge did not prevent this attempt from reaching the session endpoint.
That is narrower than proving anonymous editing works end to end. See
[MS-FSSHTTP prerequisites](https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttp/750596b8-c711-4da5-89ae-993b01bf6eca).

## Corrections deployed for the next attempt

- File QueryChanges returns manifests, inline object data and a storage index
  with manifest, cell and revision mappings. Fixed the revision ID and root
  object references. Removed the unreferenced duplicate BLOB.
- QueryChanges respects the two manifest inclusion flags. Its data constraint
  is a byte budget. A budget requiring pagination gets an explicit unsupported
  response until partial knowledge and continuation are implemented.
- GetVersions returns the current version in the nested protocol XML shape.
  It reports versioning disabled because the store has no retained history.
  GetDocMetaInfo derives properties from the document and uses captured constants
  for stream schema and item level.
- Schema/exclusive locks have leases and conflict checks. Coauthor join, refresh
  and exit update shared lock ownership and document sessions together. Added
  LockStatus and AmIAlone handlers. Coauth ConvertToExclusive and
  MarkTransitionComplete now explicitly reject unsupported transition behavior.
- PutChanges decoding retains storage index, expected index, flags, coherency
  bytes and authors. Fixed request serializer payload boundaries. This does not
  apply saves yet.
- MTOM content IDs are URI-decoded before attachment lookup; binary decode
  failures retain their exception in the logs.
- The Office Inspectors adapter resets and serializes access to the reference
  parser's static state. Without that isolation, a previous editors-table parse
  caused an unrelated file response to be treated as compressed editor XML.

Validation: 145 offline tests passed in the full solution run, followed by one
additional passing coauthor lifecycle regression. The restarted API passed all
four live tests, including SharePoint open/reopen and the actual Word
Enable Editing request. The latter checks coauthor success and the returned file
graph. A desktop Word retry is still needed to establish client acceptance.

Remaining work is substantial: PutChanges application and document materialization,
metadata application data, complete knowledge/filter/continuation handling,
transition state, and the unsupported Versioning/Properties/FileOperation
operations. No end-to-end save or two-client convergence against our API is
claimed by these tests.

## Word diagnostics narrowed the failure to multipart framing

The next desktop attempt still became read-only. The user pointed out Word's
own logs at `%LOCALAPPDATA%/Temp/Diagnostics/WINWORD`, which had not yet been
inspected. In
`Primary1790333788925450600_14803E78-61D6-4302-BAA8-92BB925178EA.log`,
at 10:56:30.170 UTC, correlation `6D5AA89E-BE8E-4242-A54E-3286C7F60706`, Word
reported `Completed reading stream {"Binary stream count":2,"Xml stream exists":false}`.
It immediately raised `1629 [c04b1]` and failed JoinCoauthoring, SchemaLock and
all Cell downloads. It then recorded CSI fallback and opened the direct download
read-only.

This corrects the earlier inference: a successful join in the API's own state
does not mean Word recognized the join response. Word failed to identify the SOAP
part before it could consume the response graph. The diagnostic does not prove
that authentication is sufficient in every mode, but it identifies a concrete
response-framing failure in this attempt.

Compared with successful SharePoint responses, our MTOM output omitted the
initial CRLF and used different root-part header formatting/content IDs. The
writer now matches the captured preamble and header layout. Four fixture-backed
tests compare the exact opening boundary and root headers, normalizing only the
boundary value. All 60 SOAP/API tests pass. This framing correction is deployed;
Word confirmation is pending. The new node-based file builder is experimental
and is not wired into the running API yet.

The 11:02:49 UTC retry in
`Primary1790334168592557700_E75A2076-F06E-47B2-B514-A5454184D5A2.log`
confirmed the framing fix: three binary streams, `Xml stream exists:true`, and
roundtrip `Success`, correlation `7CE97E86-9FFE-4027-B78F-C1D394953194`.
Word recognized JoinCoauthoring with status Alone and briefly set read-only to
false. It then reported an empty DefaultPartition base/download revision,
missing version token, and failed document load with `117 [8ffru]`.
This proves the client reached the coauthor response without adding auth.
It does not establish a valid file graph or a successful editable document.

The same attempt also probes `/_vti_bin/sharedaccess.asmx` with SOAPAction
`IsOnlyClient`; our API returns 404 and Word records `2001 [c2sow]`.
That is distinct from the FSSHTTP AmIAlone subrequest. It remains an observed
unsupported probe, not proof that it causes the empty document revision.

The next build also replaces the opaque DOCX root object with intermediate,
leaf and content objects. The graph reader reconstructs generated bytes and the
captured first save by following ordered references. The second save fixture is
a delta and requires retained prior objects; it is not a standalone document.
The reader rejects missing references and cycles. Save application is still not
wired into the API.

Validation after these changes: 158 offline tests passed, plus both captured
live replay tests. The actual Word replay now uses multipart MIME and checks
the response preamble, rather than bypassing that path with plain SOAP.
The API was restarted after replay to clear test coauthor sessions before the
next desktop attempt.

The user confirmed the resulting build opens for editing and supplied screenshots
showing inserted paragraphs. Saving produces Word's Upload Failed banner.
This is the editable-open checkpoint; it does not establish successful upload.

## First editable session save failure

Checkpoint commit: `d580285`. API logs show the save correlation
`4891CEFC-8180-4181-BD9F-BDB913210696`, Office session
`F8B7CD4A-B111-4CE0-A8C8-D9B843BF1B3C`. Word sent an 18,420-byte MTOM request
with a 16,960-byte binary attachment. The server decoded a FileContents
PutChanges operation and deliberately returned Protocol RequestNotSupported,
code 4. This explains Upload Failed; successful SOAP/HTTP status does not make
the nested binary operation successful. Document storage was not updated.

The response inspector skipped error payloads incorrectly and then tried to
read the error header as a SubResponse end. It now decodes and prints errors.
The actual 111-byte rejection response is a regression fixture; all 78 binary
tests pass. The SOAP request is also retained, but API logging did not preserve
its binary attachment, so it cannot be replayed by itself.

Other observed failures are 404 responses to sharedaccess.asmx IsOnlyClient,
root OPTIONS, and sharing-related APIs. Sharing APIs are outside this project's
protocol scope. These probes are separate from the confirmed PutChanges
rejection. Current Word diagnostic files are exclusively locked while Word
runs, including when opened with read/write/delete sharing; this review used
API logs and the user's screenshots without closing Word.

The next save implementation must retain and merge partition object graphs,
resolve the manifest-selected root, validate expected storage index and locks,
atomically update document bytes, and return resulting knowledge. The graph
reader and captured save fixtures provide a foundation, but that storage
transaction is not implemented by the editable-open checkpoint.

## File save implementation

`PartitionGraphSnapshot` resolves the proposed index through storage, cell and
revision manifests to the named file root. It retains earlier data elements for
delta uploads. `FilePartitionSaveHandler` validates the expected index, rejects
conflicting identifier reuse, materializes and validates the DOCX, then commits
content and graph under the document lock. SOAP lock validation surrounds the
transaction. Repeating the same save does not advance the document version twice.
Downloads return the stored graph rather than regenerating unrelated identities.

Incoming null serials use known mapping serials where available; remaining new
elements receive monotonically increasing server serials. The response supplies
serial reassignment records and Knowledge ranges for the elements actually held.
Ranges do not bridge gaps. The wire error for a failed save is now a Cell error;
the earlier Protocol error type was wrong for CoherencyFailure. The response parser
also no longer overwrites the storage index with the waterline storage identifier.
File properties are returned after commit so their ETag and timestamp reflect
the saved version.

Validation: 175 offline tests pass. The live captured-save test submits both
SharePoint saves, validates responses with Microsoft's parser, and compares each
HTTP GET with the reopened graph's materialized bytes. Saved sizes are 13,506 and
14,005 bytes. Both live open/reopen tests also pass. A new desktop Word save test
is requested after resetting the replay data and restarting the API.

Limits: this implements complete file-partition uploads and retained-object
deltas, not partial/multi-request uploads, arbitrary alternate coherency modes,
metadata uploads or persistent disk storage. A stale expected index is rejected;
automatic conflict merging is not implemented. Desktop save acceptance remains
to be confirmed separately from replay acceptance.

### Desktop retry and cached upload

The first desktop retry after implementation still failed. API logs show no
QueryChanges download; Word joined using the prior process's ResourceID and
resumed its pending PutChanges. The new server correctly returned Cell
CoherencyFailure because its in-memory base had changed. The complete 16,898-byte
upload is retained in `testdata/api/word-save-current.bin`. Its regression first
checks stale-base rejection, then explicitly rebases the foreign expected index
for the test and successfully materializes/commits the uploaded document.

An initial expected-index check accepted a different index identifier only when
its complete resolved mapping matched the current revision. The fresh desktop
capture below exposed that check as too strict. A fresh `/shared/save-test.docx`
is seeded for desktop testing without Word's pending upload cache for test.docx.
GET/HEAD and document-prefixed CellStorage routes support the seeded filenames.
The running API is ready for that fresh-URL save/reopen test; desktop confirmation
is pending.

The 12:07 UTC desktop attempt still targeted `/shared/test.docx` in every SOAP
request. Word's diagnostic file
`Primary1790338037379692000_FA0B1D37-579F-4378-84CF-9272E5BACE4F.log`
reports `IsOpeningOfflineCopy: true`. Correlation
`5078F966-0152-4389-8080-04CA3FD71FE7` also reports a Cobalt parsing failure
`0c19`, size 111, after receiving the coherency error. This does not establish
whether the fresh document can save. The error response format needs separate
investigation. Keep the server running during the next desktop attempt so its
in-memory base does not change.

### Fresh desktop save coherency fix

The subsequent confirmed attempt opened `save-test.docx`, downloaded its graph,
and uploaded without any intervening restart. It reproduced CoherencyFailure.
The exact response and request are now paired fixtures `word-save-fresh-base.bin`
and `word-save-fresh.bin`. The regression installs that exact base and applies
the unchanged upload, with no replacement of its expected index.

The expected index references the same manifest and cell as our response but
uses Word's serial numbers. Our old check compared those serials with server
serials and demanded a complete manifest chain. [MS-FSSHTTPB section 2.2.2.1.4](https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttpb/e4c224ca-0717-4b02-b713-e75d353904bb)
requires checking the mapping values for the keys being updated. The new check
compares mapped data-element IDs, evaluates absent expected keys using the
Imply Null Expected flag, and keeps the comparison and commit under the document
lock. The exact desktop upload now commits changed bytes in the regression.

The Word error-path message alone did not establish malformed error framing;
the CellError structure matches the independent parser's format. No speculative
error fields were added. A new `save-check.docx` seed avoids the failed upload
cached for the earlier filenames after the required server restart.

Validation after this fix: 180 offline tests passed, five live tests skipped
in the offline run. The live two-save SharePoint replay then passed against the
restarted API and verified HTTP downloads against the reopened partition graph.
Desktop confirmation of the successful response remains pending.

### Desktop save confirmed

The user confirmed that Word saved `save-check.docx` after `f08a0bc`. API logs
show `Cell save completed: version=2 bytes=12214 errors=0`. A separate HTTP GET
downloaded the DOCX and its `word/document.xml` contained both the seed text
`Save check for CellBridge` and the added text `asd`. This confirms
desktop save acceptance and server-side content update. Word close/reopen has
not yet been independently confirmed; neither has two-client coauthoring.
The server remains running because its document store is still in-memory.

## Manifest identity correction

The interop reference and captured SharePoint EditorsTable response both use
protocol identities for the manifest graph. The storage-manifest schema GUID is
`0EB93394-571D-41E9-AAD3-880D92D31955`. Storage and revision root declarations
use `ExGuid(2, 84DEFAB9-AAA3-4A0D-A3A8-520C77AC7073)`, and the root CellID uses
`ExGuid(1, 84DEFAB9-AAA3-4A0D-A3A8-520C77AC7073)` together with
`ExGuid(1, 6F2A4665-42C8-46C7-BAB4-E28FDCE1E32B)`. The captured EditorsTable
payload contains these GUIDs at the expected schema/root/cell positions.

The server had been emitting an arbitrary schema GUID and random root/cell
identities. The builders and document identity now use the fixed protocol
values while retaining document-specific data-element and revision IDs. The
binary regression checks these identities; 74 FSSHTTPB tests and five
partition interop tests pass after the correction.
