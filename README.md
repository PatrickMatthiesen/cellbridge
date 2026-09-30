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

This is a protocol experiment, not a production document service. Documents,
versions, resource IDs, and sessions live in memory and reset on restart.
Distinct authenticated user identities and durable storage are not implemented.
Partial and unsupported uploads return protocol errors.

## Requirements

- .NET 10 SDK.
- Aspire CLI compatible with the preview SDK pinned in `aspire/apphost.cs`.
- Python 3.12 or newer for the bundled SharePoint capture proxy.
- Desktop Office on Windows for manual interoperability testing.

## Build and test

```sh
dotnet build CellBridge.slnx
dotnet test CellBridge.slnx
dotnet test demo/CellBridge.Demo.slnx
```

The Office Inspectors adapter requires Windows. Three existing URL/path tests
also fail on Linux; see the [validation notes](docs/publication-audit.md).
Live interoperability tests are opt-in. See the
[interop test instructions](tests/CellBridge.Interop.Tests/README.md).

## Run locally

Start the app from the repository root. Aspire's Python integration prepares
the capture proxy environment and installs its dependencies:

```sh
aspire start --apphost aspire/apphost.cs
aspire wait web --apphost aspire/apphost.cs
aspire ps
```

Open the `demo` endpoint shown in the Aspire dashboard. Desktop Office must be
able to reach and trust the server's HTTPS endpoint. The default Office/browser
origin is `https://localhost:7292`; configure `collab-public-url` for other clients.
Stop the AppHost with `aspire stop` before rebuilding if an executable is locked.

Aspire also starts a SharePoint capture proxy. Its upstream is an example domain;
configure `capture-upstream` for your own test farm before using capture features.
See the [capture guide](docs/capture-kit.md) for configuration and certificates.

## Protocol and implementation

- `CellBridge.FssHttp` parses SOAP and MTOM requests and serializes responses.
- `CellBridge.FssHttpB` contains binary framing, object graphs, and synchronization messages.
- `CellBridge.Web` serves documents, capability discovery, and `/_vti_bin/cellstorage.svc`.
- `CellBridge.Storage` tracks documents, versions, locks, and sessions in memory.

The protocol references are MS-OCPROTO, MS-FSSHTTP, and MS-FSSHTTPB. SOAP and
binary structures work together; see the [protocol notes](docs/protocol-version-decision.md).
The [OfficeDev test suites](https://github.com/OfficeDev/Interop-TestSuites)
provide an independent reference implementation.

## Documentation

- [Demo and document library](docs/demo-library.md)
- [Architecture and historical development notes](docs/architecture.md)
- [Capture tooling](docs/capture-kit.md)
- [Publication and fixture validation](docs/publication-audit.md)

Recorded fixtures are sanitized regression inputs. Their READMEs explain which
properties they test and how their bytes differ from the original captures.
Keep raw traffic, private keys, and local configuration outside Git.

## License

[MIT](LICENSE). Vendored Microsoft protocol test code retains its
[notice](tests/CellBridge.Interop.Tests/MicrosoftProtocol/FssHttpB/NOTICE.md)
and [license](tests/CellBridge.Interop.Tests/MicrosoftProtocol/FssHttpB/LICENSE-MIT.txt).
