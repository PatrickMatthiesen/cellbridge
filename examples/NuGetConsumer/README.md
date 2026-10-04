# Minimal NuGet consumer

This is a small ASP.NET Core application built from CellBridge NuGet packages.
It registers in-memory state and content storage, maps the protocol/download
endpoints and a health endpoint, and creates `/shared/example.txt` at startup.
It validates bearer tokens from your configured token authority and maps
verified issuer and subject claims into a CellBridge subject.

The example needs neither PostgreSQL nor desktop Office. In-memory storage is
volatile; restarting this application discards document state. For persistent
storage, use the PostgreSQL composition in the
[storage and hosting guide](../../docs/storage-providers.md#consume-the-packages).
The text file demonstrates document creation/download, not desktop Office editing.

With Aspire stopped, build the packages and example from the repository root:

```sh
python3 tools/verify_packages.py
```

Configure `Authentication__Authority`, `Authentication__Audience` and
`Documents__OwnerSubject`. The owner subject is `oidc:` followed by the
Base64 UTF-8 issuer, a colon and the token subject. Run the built example:

```sh
dotnet examples/NuGetConsumer/bin/Release/net10.0/NuGetConsumer.dll --urls http://localhost:5080
```

Download `http://localhost:5080/shared/example.txt` with an Authorization bearer
token matching that owner. `/health` remains anonymous. This example tests
package hosting; desktop Office needs a compatible authentication exchange such
as the sample host's MS-OFBA flow. Stop the application with Ctrl+C.

The verification script also runs the
[package consumption tests](../../tests/CellBridge.Packages.Tests/README.md).
Generated packages and the isolated cache stay under ignored `artifacts/packages`.
