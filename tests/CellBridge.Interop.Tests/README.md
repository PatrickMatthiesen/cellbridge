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

Run the offline checks with:

```powershell
dotnet test tests/CellBridge.Interop.Tests/CellBridge.Interop.Tests.csproj
```

For a disposable Aspire database and two web hosts, use
`python3 tools/testing/run.py` from the repository root. This enables all HTTP
tests and runs provider and protocol checks too. See
[automated testing](../../docs/automated-testing.md) for the Windows Word script,
Tailscale setup and performance runs.

To test an already running Aspire app, set the full SOAP endpoint. The peer
endpoint is optional and enables the two-host storage/lock test:

```powershell
$env:OFFICECOLLABSERVER_INTEROP_ENDPOINT = 'http://localhost:5181/_vti_bin/cellstorage.svc'
$env:OFFICECOLLABSERVER_INTEROP_PEER = 'http://localhost:5182/_vti_bin/cellstorage.svc'
dotnet test tests/CellBridge.Interop.Tests/CellBridge.Interop.Tests.csproj
```

Live checks include QueryAccess, QueryChanges, captured Word open exchanges,
Word and Excel PutChanges, identity routing, session visibility, shared exclusive
locks and forwarded HTTPS origins. Unconfigured HTTP checks report skipped.
The proxy-origin test assumes a trusted loopback connection to the local host.
It should run locally rather than targeting a remote proxy from another machine.
