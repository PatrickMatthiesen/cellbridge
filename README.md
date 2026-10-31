# CellBridge

[![Build and tests](https://github.com/PatrickMatthiesen/cellbridge/actions/workflows/capture-kit.yml/badge.svg?branch=main)](https://github.com/PatrickMatthiesen/cellbridge/actions/workflows/capture-kit.yml)
[![.NET 11](https://img.shields.io/badge/.NET-11-512BD4?logo=dotnet&logoColor=white)](src/CellBridge/CellBridge.csproj)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

Edit Microsoft Office documents in their desktop apps and save them to your own
ASP.NET Core server. CellBridge implements MS-FSSHTTP and MS-FSSHTTPB, the
protocols Office uses to open documents, send changes and coordinate editing.

The repository ships eleven NuGet packages, one development AppHost and three
examples. The shipping packages are `CellBridge`, `CellBridge.FssHttpB`,
`CellBridge.AspNetCore`, `CellBridge.Storage.InMemory`,
`CellBridge.Storage.PostgreSql`, `CellBridge.Storage.SqlServer`,
`CellBridge.Storage.FileSystem`, `CellBridge.Lists`,
`CellBridge.Lists.PostgreSql`, `CellBridge.Lists.SqlServer` and
`CellBridge.Lists.AspNetCore`. The `CellBridge` core package contains SOAP/MTOM handling, document
execution and storage contracts. `CellBridge.AspNetCore` adds HTTP endpoints,
login integration and dependency registration. Storage providers depend on
core, not the HTTP package. `CellBridge.FssHttpB` remains standalone.

The examples cover document hosting with NuGet packages, Lists-only hosting
with HostAuthorizedLists, and Documents plus Lists composition with
DocumentLibrary. WebDemo is the richer development application for desktop
editing and manual protocol checks.

CellBridge is experimental. The APIs may change, and CellBridge has not been
tested at production scale.

Optional [customer workspace list libraries](docs/lists.md) provide typed schemas,
company templates, custom fields, record merge previews, references and authorized
extracts. They use independent PostgreSQL storage and a native HTTP API. Hosts
provide their own customer lifecycle and user interface.

## Features

| Feature | Current support |
| --- | --- |
| Word editing | Open, edit, save and reopen tested while signed in. |
| Excel editing | Open, edit, save and reopen tested while signed in on the PostgreSQL-backed host. |
| PowerPoint editing | Save and reopen presentations, including complex Copilot-generated slides. |
| Document library | Create blank Office files, import existing documents and download saved files. |
| Accounts and permissions | Local and optional [Microsoft Entra sign-in](docs/authentication.md), document ownership, read-only/editing access and revocation. |
| Persistent storage | PostgreSQL document state with PostgreSQL or filesystem file content. |
| Revision history | Native Word, Excel and PowerPoint version listing, historical open and restore tested on Microsoft 365 16.0.20527. Authorized SOAP and reusable APIs use the same persisted history. |
| Incremental transfers | Reuse stored file parts and send parts the client does not already have. |
| Coauthoring | Two Windows desktops exchanged Word, Excel and PowerPoint changes under distinct accounts with AutoSave. Independent downloads retained both editors' changes. See the bounded [client coverage](docs/interoperability.md#client-coverage). |
| Real-time collaboration | RTC is enabled by default. Multiple hosts require [Redis coordination](docs/multi-host-rtc.md). Set `CellBridgeOptions.EnableOfficeRealTime = false` to disable it, or `Protocol:EnableOfficeRealTime=false` in WebDemo. Remote Excel selections and Word cursor indicators were observed on a single host. Participant names, typing latency and reliable startup across all three apps remain unqualified. OCS is not implemented. |

Some advanced synchronization options and automatic graph pruning remain
unimplemented or disabled. See [revision history](docs/revision-history.md),
[protocol support and compatibility](docs/interoperability.md) and the
[requirements ledger](docs/protocol-requirements.md) for exact coverage and open work.

## Try the demo

The development application `src/CellBridge.WebDemo` hosts the protocol endpoints
and a Razor Pages library at `/library`. It creates blank documents and uses
the sample Identity accounts.

Install the .NET 11 SDK pinned in `global.json`, Docker and Python 3.12 or newer.
Use an Aspire CLI compatible with the preview packages pinned in
`src/CellBridge.AppHost/CellBridge.AppHost.csproj`.
Desktop editing requires Office on Windows.

From the repository root:

```sh
aspire start --isolated --apphost src/CellBridge.AppHost/CellBridge.AppHost.csproj --non-interactive
aspire wait webdemo --apphost src/CellBridge.AppHost/CellBridge.AppHost.csproj --non-interactive
aspire ps --non-interactive
```

Aspire starts PostgreSQL with persistent storage, initializes the schema and
then starts WebDemo.
[Provision an account](docs/authentication.md#first-setup), then open
the HTTPS endpoint shown for `webdemo`, followed by `/library`. Sign in, create
a document and use its Office open link to edit and save it.

For native Version History, configure one stable catalog GUID through
`Parameters__DocumentLibraryId`. See [revision history](docs/revision-history.md)
for reusable-host configuration, retention limits and tested client behavior.

For another computer, set `Parameters__PublicOrigin` to an HTTPS origin that
Office can reach and trust. The [demo guide](docs/demo-library.md) covers imports
and remote clients.

Stop Aspire before rebuilding locked outputs. If a capture is active, stop it
with `tools/capture/stop.py` first, then run `aspire stop --non-interactive`.

## Use in ASP.NET Core

After configuring authentication and a `StorageProvider`, register the
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
For standard browser login pages, use `AddCellBridgeIdentityLogin<TUser>`
with existing ASP.NET Core Identity accounts, including authenticator and recovery-code
sign-in. For a custom account system, `AddCellBridgeCookieLogin` registers a cookie and the
standard pages with a credential checker. Use `AddCellBridgeLogin` for an existing cookie.
Custom HTML is optional. These helpers handle sign-in and CSRF. Enable the Office
forms challenge/completion flow separately with
`AddCellBridgeOfficeFormsAuthentication` for the selected cookie. OAuth-to-forms
fallback requires another explicit opt-in and defaults to disabled. See
[login integration](docs/package-integration.md#use-the-built-in-login-page).

For applications with their own document permissions, start with
[async host authorization](docs/async-document-authorization.md).
The [HostAuthorizedLists example](examples/HostAuthorizedLists/README.md) shows
Lists-only host authorization. DocumentLibrary shows how one ASP.NET Core host
maps both endpoint families and derives their actors from the same identity.
The [integration guide](docs/package-integration.md) covers parsed SOAP requests,
resource IDs and save receipts. To publish saves to an external file store or
share locks with WOPI, follow [external host reliability](docs/external-host-reliability.md).

## Build and test

```sh
dotnet build CellBridge.slnx
dotnet test CellBridge.slnx -m:1
```

CI runs protocol, PostgreSQL, WebDemo and parser checks on Linux, and capture
checks on Linux and Windows.
See [automated testing](docs/automated-testing.md) for disposable PostgreSQL
and replay fixtures and desktop tests. [HTTP interoperability tests](tests/CellBridge.Interop.Tests/README.md)
own disposable hosts by default. Remote and desktop checks remain opt-in.

## Documentation

- [Provider export, migration and recovery](docs/provider-portability.md)
- [Document library and desktop editing](docs/demo-library.md)
- [Authentication and permissions](docs/authentication.md)
- [Storage and hosting](docs/storage-providers.md)
- [Document paths and migration](docs/document-paths.md)
- [Protocol support and compatibility](docs/interoperability.md)
- [Architecture](docs/architecture.md)
- [Protocol scope and references](docs/protocol-version-decision.md)
- [Automated testing](docs/automated-testing.md)
- [SharePoint capture tooling](docs/capture-kit.md)

## License

[MIT](LICENSE). Vendored Microsoft protocol test code retains its
[notice](tests/CellBridge.Interop.Tests/MicrosoftProtocol/FssHttpB/NOTICE.md)
and [license](tests/CellBridge.Interop.Tests/MicrosoftProtocol/FssHttpB/LICENSE-MIT.txt).
