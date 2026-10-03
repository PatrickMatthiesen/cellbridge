# Office Inspectors parser

`CellBridge.OfficeInspectors` is the in-repo .NET 10 port of the binary parser
from Microsoft's Office Inspectors for Fiddler. It runs on Linux and Windows
without Fiddler, WinForms, desktop Office or a sibling source checkout.
The [source notice](../src/CellBridge.OfficeInspectors/NOTICE.md) records the
upstream commit, retained license and port changes.

The port contains the FSSHTTPB request/response, FSSHTTPD node/ZIP and OneStore
property grammars used by the former adapter. The server does not depend on it.
Its parsing code has no dependency on `CellBridge.FssHttpB`, so the tests compare
server-generated responses against a separately maintained parser. The existing
Interop-TestSuites checks provide another independent comparison.

## Source layout

Each top-level parser type has its own file under
`src/CellBridge.OfficeInspectors/Parsers`:

| Directory | Contents |
| --- | --- |
| `Common` | Base reader, bit attributes, string encoding, stream context and utilities |
| `FssHttpB` | Basic types, framing, data elements, knowledge, requests, responses, errors and editors-table models in separate subdirectories |
| `FssHttpD` | Node objects and ZIP records in `Nodes` and `Zip` |
| `OneStore` | Basic types, property values and object streams in separate subdirectories |

All types retain the `CellBridge.OfficeInspectors.Parsers` namespace and their
upstream names. The directories organize the source without changing the API.

## Run the checks

```sh
dotnet test tests/OfficeInspectors.Adapter
```

The main solution, disposable Aspire test runner and Windows/Linux CI jobs all
include these checks. Fixtures copy to the test output directory; no source-tree
search or external checkout is needed. The suite compares reviewed SharePoint
13/11 payloads and generated responses, verifies storage-index and manifest
references, rejects truncated messages and checks concurrent editors-table parses.

To inspect an extracted binary Cell response:

```sh
dotnet run --project tools/OfficeInspectors.Dump -- artifacts/response.fsshttpb
```

The tool prints version, status, consumed bytes, data-element types and
subresponse counts. It exits with code 1 if any input fails parsing or has
unconsumed bytes. Extract binary payloads from SOAP/MTOM first; the tool accepts
response bytes, not an entire HTTP capture.

## Compatibility limits

The port supports both 16-bit and 32-bit data-element framing. Put Changes
decodes the serial reassignment records handled by Microsoft's OfficeDev test
suites, including element IDs, unsigned serial values and declared-length checks.
These records are absent from the public August 2024 Put Changes description;
their grammar follows the OfficeDev implementation and observed Word traffic.
It decodes the
standard QueryAccess read/write results and retains additional access results as
opaque objects. Consuming an unknown extension does not establish its semantics.
OneStore interpretation is opt-in through `OfficeInspector.ParseResponse`.

Each input stream owns its parser state. Editors-table decompression reads the
complete XML rather than assuming a fivefold expansion, with a 16 MiB decoded
limit. Truncation and unsupported grammar return an error and consumed offset.
The inherited grammars do not cover every Office format or every protocol
extension. This diagnostic parser does not replace the server's request validation.

A passing parse or fixture replay does not establish desktop open/save/reopen or
coauthoring. Those still need the evidence described in
[interoperability coverage](interoperability.md).
