# CellBridge

[![Build and tests](https://github.com/PatrickMatthiesen/cellbridge/actions/workflows/capture-kit.yml/badge.svg?branch=main)](https://github.com/PatrickMatthiesen/cellbridge/actions/workflows/capture-kit.yml)
[![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white)](src/CellBridge.AspNetCore/CellBridge.AspNetCore.csproj)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

Edit Microsoft Office documents in their desktop apps and save them to your own
ASP.NET Core server. CellBridge implements MS-FSSHTTP and MS-FSSHTTPB, the
protocols Office uses to open documents, send changes and coordinate editing.

The repository includes reusable .NET libraries and a sample document library.
Create or import a file, open it in Word, Excel or PowerPoint, and save changes
back to CellBridge. Use the sample to try desktop editing, or add the endpoints
to your own ASP.NET Core application.

CellBridge is experimental. The APIs may change, and production-scale operation
has not been qualified.

## Features

| Feature | Current support |
| --- | --- |
| Word editing | Open, edit, save and reopen documents, including while signed in. |
| Excel editing | Open, edit, save and reopen tested while signed in on the durable host. |
| PowerPoint editing | Save and reopen presentations, including complex Copilot-generated slides. |
| Document library | Create blank Office files, import existing documents and download saved files. |
| Accounts and permissions | Local sign-in, document ownership, read-only/editing access and revocation. |
| Persistent storage | PostgreSQL document state with PostgreSQL or filesystem file content. |
| Revision history | Authorized version listing/download and graph-aware restore through SOAP and reusable APIs. |
| Incremental transfers | Reuse stored file parts and send parts the client does not already have. |
| Coauthoring | Editor sessions and locks are implemented; two-desktop editing remains unverified. |

Some advanced synchronization options and automatic graph pruning remain
unimplemented or disabled. See [revision history](docs/revision-history.md),
[protocol support and compatibility](docs/interoperability.md) and the
[requirements ledger](docs/protocol-requirements.md) for exact coverage and open work.

## Try the demo

Install the .NET 10 SDK, an Aspire CLI compatible with the preview SDK pinned
in `aspire/apphost.cs`, Docker and Python 3.12 or newer.
Desktop editing requires Office on Windows.

From the repository root:

```sh
aspire start --apphost aspire/apphost.cs --non-interactive
aspire wait web --non-interactive
aspire wait demo --non-interactive
aspire ps --non-interactive
```

Aspire starts PostgreSQL, initializes the schema and starts the host and library.
[Provision an account](docs/authentication.md#first-setup), then open
`https://localhost:7292/library`. Sign in, create a document and use its Office
open link to edit and save it.

For another computer, set `Parameters__PublicOrigin` to an HTTPS origin that
Office can reach and trust. The [demo guide](docs/demo-library.md) covers imports
and remote clients.

Stop Aspire before rebuilding locked outputs. If a capture is active, stop it
with `tools/capture/stop.py` first, then run `aspire stop --non-interactive`.

## Use in ASP.NET Core

After configuring authentication and a durable `StorageProvider`, register the
services and endpoints in `Program.cs`:

```csharp
using CellBridge.AspNetCore;

builder.Services.AddCellBridge(provider);
var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.MapCellBridge();
await app.RunAsync();
```

Follow the [hosting guide](docs/storage-providers.md#consume-the-packages) for
provider setup and the [authentication guide](docs/authentication.md#reusable-hosts)
for identity mapping. The [package consumer](examples/NuGetConsumer/README.md)
provides a complete example with bearer authentication and in-memory storage.
The published `0.1.0-beta.1` packages add a reusable parsed-SOAP processor,
host-selected resource GUIDs, request permission limits and accepted-save receipts.
See [embedding in another host](docs/package-integration.md) and the
[beta release record and checklist](docs/beta-release.md). All nine packages and
matching symbols are public on NuGet.org, owned by CellBridge. Fresh remote
consumption passed four consumer tests and the 15 tests of the pinned WopiHost
adapter. Those checks establish package integration, not Office Online Server
or new desktop client qualification.

## Build and test

```sh
dotnet build CellBridge.slnx
dotnet test CellBridge.slnx
dotnet test demo/CellBridge.Demo.slnx
```

CI runs protocol, storage, demo, parser and capture checks on Linux and Windows.
See [automated testing](docs/automated-testing.md) for the disposable two-host
runner and desktop tests. [Live interoperability tests](tests/CellBridge.Interop.Tests/README.md)
are opt-in.

## Documentation

- [Document library and desktop editing](docs/demo-library.md)
- [Authentication and permissions](docs/authentication.md)
- [Storage, hosting and provider authoring](docs/storage-providers.md)
- [Protocol support and compatibility](docs/interoperability.md)
- [Architecture](docs/architecture.md)
- [Protocol scope and references](docs/protocol-version-decision.md)
- [Automated testing](docs/automated-testing.md)
- [SharePoint capture tooling](docs/capture-kit.md)

## License

[MIT](LICENSE). Vendored Microsoft protocol test code retains its
[notice](tests/CellBridge.Interop.Tests/MicrosoftProtocol/FssHttpB/NOTICE.md)
and [license](tests/CellBridge.Interop.Tests/MicrosoftProtocol/FssHttpB/LICENSE-MIT.txt).
