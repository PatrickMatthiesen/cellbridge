# Negotiated protocol hashing

CellBridge accepts request-wide schema-1 hashing negotiation for its binary 12/11
library format and 13/11 endpoint format. Request versions 12, 13 and 14 share
this negotiation syntax. Accepting version 14 does not advertise every feature
of Office 2016 or newer SharePoint products.

The [requirements ledger](protocol-requirements.md#advertised-profile-matrix)
records the exact selected profile, flags, errors and remaining audit. Authorities
are [MS-FSSHTTPB request syntax](https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttpb/f44cb4d0-9ce7-48f2-8b19-90785ef70703),
[Query Changes processing](https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttpb/82458bac-fb0f-47d3-a919-9871defdc796)
and [MS-PCCRC schema 1](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-pccrc/36171657-7f15-419e-973b-6612b1799117).

## Negotiation and group selection

`RequestHashOptions` contains schema 1 and two flags. C, `0x04`, requests hashes
instead of object bytes. D, `0x08`, requests hashes alongside bytes. C applies
even if D is clear. Reserved bits are ignored when reading and zero when writing.
Absent negotiation, or both flags clear, returns ordinary data.

The default configuration selects complete inline object groups with a nonempty
concatenation of object bytes. Each selected group receives a Data Element Hash.
For C, every declaration has an excluded-data record preserving references, cell
references and the original byte size. Excluded bytes are cache information and
cannot be passed to graph materialization or accepted as a complete upload.
Hash-bearing uploads also remain explicitly rejected. Cache injection is outside
this response implementation.

BLOB groups and empty concatenations stay full and unhashed. The specification
permits the server to choose which groups to hash. An element shared with a save
response stays full and unhashed to preserve the save acknowledgement. These
fallbacks do not indicate a staged upload or continuation capability.

Hash input contains only Object Data binary-item bytes, sorted by unsigned
extended-GUID value, GUID Data1, Data2, Data3, each remaining GUID byte, and finally
unsigned 64-bit object partition. Declarations, references, framing and metadata
do not enter that input. Wire declaration/data order remains unchanged.

Schema 1 contains SHA-256 block hashes, segment hashes and segment secrets, with
64-KiB blocks and 32-MiB segments. It is a structured content-information value,
not the SHA-256 of a file or a content-store address.

## Configuration and budgets

The default `ProtocolHashingOptions` generates a separate server secret once per
process. A host can supply a stable 32-byte SHA-256 server secret to retain stable
segment secrets across service recreation and process restarts. Treat it as secret
configuration. The constructor copies it and exposes no mutable secret buffer.

```csharp
builder.Services.AddCellBridge(provider, configure: options =>
    options.Hashing = new ProtocolHashingOptions(serverSecret));
```

Use `ProtocolHashingOptions.Disabled` to select no groups while still validating
negotiation. Direct document-service and standalone-executor callers can supply
the same immutable configuration. No secret is written to protocol diagnostics.
Storage integrity checks and durable receipt identities continue to use their own
SHA-256 calculations independently.

Response assembly compares the original immutable elements before projection.
It checks the projected union against the server budget and rechecks every admitted
query's byte budget when a later operation changes its final representation.
Save preflight uses the same candidate before publication. A later rejected query
preserves earlier saves and payloads. Hashes can enlarge small groups.

The existing query shaper also checks the full-data size before projection. A query
that exceeds that initial limit still fails, even when a hash-only response would
fit. Continuation remains unsupported, and this implementation does not promise
that hashing will make every constrained query succeed.

## Evidence and limits

[Independent vectors](../testdata/protocol-hashing/README.md) use Python rather
than the .NET codec. They cover full encoded bytes for three-byte and 125-KB
inputs and fixed encoded digests around 64-KiB and 32-MiB boundaries. Tests check
split inputs, unsigned ordering, partition ties, excluded sizes, duplicate identities,
malformed negotiation, disabled negotiation and query/save budget interactions.

The pinned OfficeDev parser decodes full-data hash responses and its client sends
negotiated requests in the disposable HTTP suite. Its full object-group parser
has no excluded-data branch. Excluded records are independently decoded using its
bit-field, GUID-array, cell-array and compact-integer codecs. This is narrower than
full independent decoding of a hash-only response.

Storage tests check mixed file/metadata queries, service recreation, receipts and
content integrity on in-memory and disposable PostgreSQL providers. These are
synthetic protocol checks. No new SharePoint capture, desktop hash negotiation,
OneNote, two-desktop editing or external-host qualification is claimed.

`ReturnFileHash` and `QueryChangesVersioning` use the separate SP2010/2013 ignore
exceptions in [product notes 13 and 17](https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttpb/d006ebd9-c3df-4ef5-8be0-1c1db78c6d2c).
Those exceptions do not remove the conditional MUSTs for selected hashed groups.
