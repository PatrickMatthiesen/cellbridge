# Word open, save and reopen exchanges

These four paired SOAP/MTOM fixtures come from local run
`20260925T084814Z-session-f088f59e` against the SharePoint reference farm.

| File | Flow | Scenario |
| --- | ---: | --- |
| open.json | 35 | OpenFile_Open |
| save-first.json | 46 | AutoSaveFile_Save |
| save-second.json | 59 | AutoSaveFile_Save |
| reopen.json | 71 | OpenFile_Open |

The user confirmed editing, saving, closing, reopening and further editing.
Each selected exchange is complete; both bodies were checked against recorded
lengths and SHA-256 hashes before extraction. The source run as a whole is
incomplete and is not a validated session fixture. These are selected exchanges,
not a complete stateful replay or a reconstructed before/after document pair.

JSON contains the source sequence, timestamp, Content-Type, SHA-256 and sanitized
base64 body bytes for each side. HTTP authentication and cookie headers are not
included. Generic application test content is retained; lab identities are replaced.

`SharePointSaveReopenTests` checks body hashes, MTOM extraction, SOAP request
parsing, response-token correspondence, successful SOAP responses, full binary
request consumption, and PutChanges versus QueryChanges operation presence.

These fixtures exposed two parser failures, now covered by regression tests:

- DataElement parsing scanned for a 16-bit end marker instead of following stream
  object lengths and compound boundaries. Real save payloads use 8-bit endings,
  and arbitrary object bytes can also resemble end markers. The parser now walks
  the framing. See [MS-FSSHTTPB data element package](https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttpb/99a25464-99b5-4262-a964-baabed2170eb)
  and [object group elements](https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttpb/21404be6-0334-490e-80b5-82fccb9c04af).
- Word's captured inline QueryAccess request omits the UserAgent compound block.
  The decoder now accepts that observed compatibility form while retaining
  validation of a block when present. This is capture-derived compatibility,
  not a claim that the specification declares the whole block optional.

Passing these tests does not implement server-side saves: PutChanges still
returns RequestNotSupported. Next work is applying the partition object graph
and returning resulting knowledge, using the paired successful save responses
as evidence. Do not restore the largest-BLOB extraction heuristic.

Validation: 122 offline .NET tests passed, with two opt-in live tests skipped.
Both live interop tests then passed against the running Aspire server at
https://localhost:7292. The generated web-aspire.dev.localhost name does not
resolve through this laptop's OS DNS; no hosts or trust settings were changed.

## Public fixture sanitization

These fixtures preserve protocol framing and generic document test content, but
are sanitized derivatives of private captures. Author, company and lab account
values were replaced in SOAP, compressed editors tables and embedded OOXML
metadata. They are no longer byte-for-byte copies of the original captures.

Replacement text preserves uncompressed byte lengths. Recompressed DEFLATE
streams use trailing zero padding to preserve the original compressed sizes,
object lengths and ZIP offsets. ZIP CRCs and JSON body SHA-256 hashes describe
the sanitized data. The original capture bodies remain private.

Protocol object IDs and captured content signatures remain as opaque fixture
identifiers. These fixtures test parsing, graph materialization and save behavior;
they do not validate the original content signatures or authentication.
