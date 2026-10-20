# Protocol replay and live HTTP checks

This SDK-style .NET 10 xUnit project runs the S11 QueryAccess and S12
QueryChanges request/response checks without the legacy Protocol Test
Framework. The `MicrosoftProtocol/FssHttpB` folder contains the minimal
Interop-TestSuites stack, with the original `Microsoft.Protocols.TestSuites`
namespace kept intact because its type maps use `Type.GetType`.

The portable tests parse SOAP/MTOM and binary responses with the vendored
Microsoft parser. Captured Word and Excel save regressions validate graph-aware
materialization. These tests exercise protocol exchanges, not a running Office
desktop.

Run the parser/replay and HTTP checks from the repository root:

```sh
dotnet test tests/CellBridge.Interop.Tests
```

The HTTP collection owns a project-based Aspire fixture through Hosting.Testing.
It creates disposable PostgreSQL, runs the current schema initialization, seeds
a temporary Identity account, and starts two WebDemo instances against that
same database. The fixture uses real login and antiforgery flows and creates its
documents through the authenticated API. It disposes only its own resources.
.NET 10, the repository's Aspire CLI/SDK and a Docker-compatible container runtime
are required. No Windows desktop or continuously running development app is needed.
See [automated testing](../../docs/automated-testing.md) for other checks.

To test an already running Aspire app, set the full SOAP endpoint. Supply both hosts when running the complete suite:

```powershell
$env:OFFICECOLLABSERVER_INTEROP_ENDPOINT = 'https://localhost:7292/_vti_bin/cellstorage.svc'
$env:OFFICECOLLABSERVER_INTEROP_PEER = 'http://localhost:5182/_vti_bin/cellstorage.svc'
$env:CELLBRIDGE_INTEROP_PUBLIC_ORIGIN = 'https://localhost:7292'
dotnet test tests/CellBridge.Interop.Tests/CellBridge.Interop.Tests.csproj
```

Live checks include QueryAccess, QueryChanges, captured Word open exchanges,
Word and Excel PutChanges, identity routing, session visibility, shared exclusive
locks and forwarded HTTPS origins. The default run supplies both hosts.
`CELLBRIDGE_INTEROP_PUBLIC_ORIGIN` is the fixed origin advertised in protocol
responses. If omitted, the tests use the SOAP endpoint origin. The proxy-origin
test assumes a trusted loopback connection to the local host. It should run
locally rather than targeting a remote proxy from another machine.

Live HTTP tests require `CELLBRIDGE_INTEROP_COOKIE` and `CELLBRIDGE_INTEROP_CSRF`
from an ordinary signed-in creator account. Cookies must include the paired
antiforgery cookie; the token must be issued after sign-in. Keep these values
out of logs and captures. The default Hosting.Testing fixture supplies both
privately after signing into its temporary account. A shared account across hosts tests cookie and storage
sharing, not distinct authenticated desktop coauthoring.
