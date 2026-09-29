# Word against our API

`word-enable-editing.xml` is the SOAP part logged by the local API during the
2026-09-25 desktop Word attempt. The coauthor join was logged at 10:30:50 UTC.
All three binary Cell requests are inline base64, so this preserves their protocol structure
without needing separate MTOM attachments. The original request used MTOM; the
live regression wraps the extracted envelope in multipart MIME so it exercises
the server's MTOM response path. Binary request data remains inline base64.

Observed outcome: Word opened in Protected View. After Enable Editing it briefly
appeared editable, then became read-only. The API accepted JoinCoauthoring and
returned HTTP 200, followed by direct document GET/HEAD requests. File QueryChanges
returned only an empty storage index. This is a failing-client fixture, not a
known-good server response.

`CapturedWordOpenTests.ActualWordEnableEditingRequestGetsCoauthAndFileGraph`
replays this request against the API and checks the join response and binary
file graph using the independent Microsoft parser. A passing replay does not
prove that desktop Word accepts the document application data or can save.

`word-save-rejected.soap.xml` and `word-save-rejected.response.base64` preserve
the SOAP request and binary response from the first editable API session's save.
Correlation ID: `4891CEFC-8180-4181-BD9F-BDB913210696`, 2026-09-25.
The request's 16,960-byte binary attachment was not retained by API logging;
the SOAP file alone is not a replayable save fixture. The exact response covers
the inspector's error path and confirms Protocol RequestNotSupported, code 4.

`word-save-current.bin` is the complete 16,898-byte binary attachment from the
desktop retry after the save implementation, correlation
`36418428-F0DF-422B-91A0-07C216CC54EF`. Word resumed a pending upload from an
earlier server instance without QueryChanges. Production rejected its stale
expected index. A regression explicitly rebases that index to the test store
and proves the uploaded graph materializes and commits. This rebasing is a test
adaptation, not production behavior.

`word-save-fresh-base.bin` and `word-save-fresh.bin` are sanitized derivatives of the actual file
QueryChanges response and subsequent desktop PutChanges for
`https://localhost:7292/shared/save-test.docx`, captured on 2026-09-25.
There was no server restart between download and upload. The regression loads
the sanitized downloaded graph before applying the matching sanitized upload. It reproduced
the erroneous CoherencyFailure without replacing the expected index, unlike
the older foreign-base save fixtures.

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
