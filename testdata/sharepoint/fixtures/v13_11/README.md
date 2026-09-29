# SharePoint FSSHTTPB 13/11 fixtures

These base64 files are the four binary subresponses extracted from the successful Word-open response in `word-login-and-edit.pcapng`.

| Fixture | SOAP token | Meaning | Decoded bytes |
| --- | ---: | --- | ---: |
| `editors-table-token-8.fsshttpb.b64` | 8 | EditorsTable Cell partition, QueryChanges | 1,342 |
| `metadata-token-7.fsshttpb.b64` | 7 | Metadata Cell partition, QueryChanges | 364 |
| `file-contents-token-6.fsshttpb.b64` | 6 | Default FileContents Cell partition, QueryChanges | 151 |
| `query-access-token-13.fsshttpb.b64` | 13 | QueryAccess | 140 |

All payloads advertise protocol version 13 and minimum version 11. Office Inspectors consumes the three partition payloads completely. Store fixtures as base64 so diffs remain reviewable and tests can load them without a packet-capture dependency.

The source capture contains an NTLM exchange and must not be committed to a public repository. The public payloads omit HTTP authentication headers and replace captured lab display and login names.

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
