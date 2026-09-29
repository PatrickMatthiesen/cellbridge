# Modern S11/S12 checks

This SDK-style .NET 10 xUnit project runs the S11 QueryAccess and S12
QueryChanges request/response checks without the legacy Protocol Test
Framework. The `MicrosoftProtocol/FssHttpB` folder contains the minimal
Interop-TestSuites stack, with the original `Microsoft.Protocols.TestSuites`
namespace kept intact because its type maps use `Type.GetType`.

Run the local checks with:

```powershell
dotnet test tests/OfficeCollabServer.Interop.Tests/OfficeCollabServer.Interop.Tests.csproj
```

To run the live SOAP/MTOM check against Aspire, set the full
`cellstorage.svc` endpoint first:

```powershell
$env:OFFICECOLLABSERVER_INTEROP_ENDPOINT = 'https://web-aspire.dev.localhost:7292/_vti_bin/cellstorage.svc/CellStorageService'
dotnet test tests/OfficeCollabServer.Interop.Tests/OfficeCollabServer.Interop.Tests.csproj --filter LiveQueryChangesTests
```

Both parser-gate tests now pass through the unmodified Microsoft parser. The
opt-in live test also passes for QueryAccess and baseline QueryChanges against
the Aspire server. QueryChanges filters and PutChanges are the next additions.
