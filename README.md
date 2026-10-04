# CellBridge

An experimental ASP.NET Core server for opening, saving, and coauthoring
Microsoft Office documents through **MS-FSSHTTP** and **MS-FSSHTTPB**.

CellBridge implements the SOAP endpoint and binary synchronization protocol
used by desktop Office. A Razor Pages demo lets you create, browse, download,
and open documents in their desktop applications.

## Status

Desktop Word editing and saving, and Excel saving, have been demonstrated in
local development. Blank Word, Excel, and PowerPoint packages can be created.
PowerPoint package open/save/reopen was tested locally; remote PowerPoint saves
and two-desktop live coauthoring remain unverified.

Aspire uses PostgreSQL to persist documents, resource IDs, retained graphs,
versions, save receipts and unexpired sessions. Binary content can use PostgreSQL
or a filesystem provider. Local Identity accounts and per-document Read/Write
permissions are implemented. A manual Windows Word trial on 2026-10-03
demonstrated authenticated writer saves, reader read-only open and Write
revocation with unchanged server bytes. MS-OFBA required explicit Office host
approval; Office build qualification and authenticated fresh-process reopen
remain pending. On 2026-10-03, desktop Word completed ten remote saves and
a fresh-process reopen against the PostgreSQL-backed host through Tailscale.
Each binary file save was matched to the client's ETag, all 17 captured binary
responses passed the independent Office Inspectors parser, and the final server
bytes matched the client's SHA-256. See the
[interoperability coverage](docs/interoperability.md).
Partial and unsupported uploads return protocol errors.

Storage and request budgets reject growth before publishing a new revision.
Knowledge-aware file queries avoid reading payloads the client already knows.
Automatic graph pruning remains disabled pending reference and recovery coverage;
see [limits and retention](docs/storage-providers.md#limits-and-qualification).

## Requirements

- .NET 10 SDK.
- Aspire CLI compatible with the preview SDK pinned in `aspire/apphost.cs`.
- Docker for Aspire's PostgreSQL container and persistent volume.
- Python 3.12 or newer for the bundled SharePoint capture proxy.
- Desktop Office on Windows for manual interoperability testing.

## Build and test

```sh
dotnet build CellBridge.slnx
dotnet test CellBridge.slnx
dotnet test demo/CellBridge.Demo.slnx
```

Protocol, provider and the in-repo Office Inspectors parser tests run on Linux
and Windows. Desktop Office checks require Windows.
Live interoperability tests are opt-in. See the
[interop test instructions](tests/CellBridge.Interop.Tests/README.md).

For a disposable PostgreSQL database and two live hosts, run
`python3 tools/testing/run.py`. Add `--performance` for provider measurements.
The [automation guide](docs/automated-testing.md) covers prerequisites, the
Windows desktop Word check and connecting the laptop through Tailscale Serve.

## Run locally

Start the app from the repository root. Aspire starts PostgreSQL with a persistent
volume, initializes the current schema, then starts the web host. Its Python integration
prepares the capture proxy environment and installs its dependencies:

```sh
aspire start --apphost aspire/apphost.cs
aspire wait web --apphost aspire/apphost.cs
aspire ps
```

Provision an account using the [authentication setup](docs/authentication.md).
Create documents from the library or import a directory with the explicit admin command. Open `/library` on the web
HTTPS endpoint and sign in. Desktop Office must be
able to reach and trust the server's HTTPS endpoint. The default Office/browser
origin is `https://localhost:7292`; configure `Parameters__PublicOrigin` for other clients.
Stop the AppHost with `aspire stop` before rebuilding if an executable is locked.

Aspire also declares an explicitly started SharePoint capture proxy. Its upstream
is an example domain; configure `Parameters__CaptureUpstream` for your own test
farm before starting the resource.
See the [capture guide](docs/capture-kit.md) for configuration and certificates.

## Protocol and implementation

- `CellBridge.FssHttp` parses SOAP and MTOM requests and serializes responses.
- `CellBridge.FssHttpB` contains binary framing, object graphs, and synchronization messages.
- `CellBridge.AspNetCore` provides document execution and reusable HTTP endpoint registration.
- `CellBridge.Storage.Abstractions` defines immutable state and streaming content contracts.
- `CellBridge.Storage` restores and prepares protocol document state.
- `CellBridge.Storage.PostgreSql`, `.FileSystem` and `.InMemory` implement storage providers.
- `CellBridge.Web` is the sample host, with imports and demo JSON APIs.

The protocol references are MS-OCPROTO, MS-FSSHTTP, and MS-FSSHTTPB. SOAP and
binary structures work together; see the [protocol notes](docs/protocol-version-decision.md).
The [OfficeDev test suites](https://github.com/OfficeDev/Interop-TestSuites)
provide an independent reference implementation.

## Documentation

- [Authentication and document permissions](docs/authentication.md)
- [Demo and document library](docs/demo-library.md)
- [Storage setup, NuGet consumption and provider authoring](docs/storage-providers.md)
- [Automated checks, performance and desktop Word over Tailscale](docs/automated-testing.md)
- [Architecture](docs/architecture.md)
- [Interoperability and test coverage](docs/interoperability.md)
- [Capture tooling](docs/capture-kit.md)

Recorded fixtures are sanitized regression inputs. Their READMEs explain which
properties they test and how their bytes differ from the original captures.
Keep raw traffic, private keys, and local configuration outside Git.

## License

[MIT](LICENSE). Vendored Microsoft protocol test code retains its
[notice](tests/CellBridge.Interop.Tests/MicrosoftProtocol/FssHttpB/NOTICE.md)
and [license](tests/CellBridge.Interop.Tests/MicrosoftProtocol/FssHttpB/LICENSE-MIT.txt).
