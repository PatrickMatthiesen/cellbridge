# Office Inspectors parser port

The parsers under `Parsers/Common`, `Parsers/FssHttpB`, `Parsers/FssHttpD` and
`Parsers/OneStore` derive from Microsoft's
[Office Inspectors for Fiddler](https://github.com/OfficeDev/Office-Inspectors-for-Fiddler)
at commit `b526e008f46e0c974485555e70d4f715d2c861ff`. The upstream MIT license
is retained in `LICENSE-MIT.txt`, along with source copyright notices.

Upstream's `BaseStructure.cs`, `FSSHTTPB.cs`, `FSSHTTPD.cs` and `ONESTORE.cs`
are split into one file per top-level type, grouped by protocol area. The port
retains the original type names, namespace and declaration comments.

CellBridge maintains this .NET 10 port. It includes the binary request/response,
FSSHTTPD node/ZIP and OneStore property grammars used by the FSSHTTP inspector.
WinForms rendering and hex-view helpers were removed. Parser state belongs to
each input stream. No Fiddler assemblies or external checkout are required.

The port handles both 16-bit and 32-bit data-element framing. Additional
QueryAccess results retain their payload and child objects as opaque extensions;
full byte consumption does not establish their semantics. Reads require complete
input and compressed editors tables have a 16 MiB decoded limit.
Put Changes also decodes OfficeDev-compatible serial reassignment records before
Knowledge, retaining typed element IDs and serials and checking declared lengths.

Object Data BLOB elements read opaque bytes using the stream-object header length,
as required by MS-FSSHTTPB 2.2.1.12.8. The imported `BinaryItem Data` field remains
a compatibility container; its length is derived from the header. Element metadata,
BLOB lengths and the element end type are checked.

The library has no reference to `CellBridge.FssHttpB`. Keeping its grammar separate
allows comparisons between server serialization, this parser, the vendored
Interop-TestSuites parser and reviewed SharePoint traffic. It is a diagnostic
parser, not a server request validator or proof of desktop Office interoperability.
