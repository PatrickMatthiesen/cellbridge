# Minimal NuGet consumer

This is a small ASP.NET Core application built from CellBridge NuGet packages.
It registers in-memory state and content storage, maps the protocol/download
endpoints and a health endpoint, and creates `/shared/example.txt` at startup.
By default it validates bearer tokens from your configured token authority and
maps verified issuer and subject claims into a CellBridge subject. An optional
Entra mode uses the packaged native Office authentication flow and an explicit
host-owned account mapping, without the demo database.

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
dotnet examples/NuGetConsumer/bin/Release/net11.0/NuGetConsumer.dll --urls http://localhost:5080
```

Download `http://localhost:5080/shared/example.txt` with an Authorization bearer
token matching that owner. `/health` remains anonymous. This example tests
package hosting. That default bearer-only mode does not implement native Office
sign-in. Stop the application with Ctrl+C.

The verification script also runs the
[package consumption tests](../../tests/CellBridge.Packages.Tests/README.md).
Generated packages and the isolated cache stay under ignored `artifacts/packages`.

## Optional native Office Entra mode

Instead of `Authentication__Authority` and `Authentication__Audience`, configure:

```text
Entra__Enabled=true
Entra__TenantId=TENANT_GUID
Entra__ClientId=API_APPLICATION_GUID
Entra__UserObjectId=LINKED_USER_OBJECT_GUID
PublicOrigin=https://office.example.test
Documents__OwnerSubject=example:owner
```

`ConfiguredEntraAccountMapper` accepts only that tenant/object pair and maps it
to the configured existing owner subject. It never creates accounts or links by
email. Real consumers replace it with a mapper that queries their accounts and
checks current account status, or use `UseIdentity<ApplicationUser>` for the
default Identity adapter. CellBridge validates the native token before invoking
the mapper. The cookie is explicitly configured by this host as `SPOIDCRL`.

Follow the [Entra API registration instructions](../../docs/authentication.md#microsoft-entra-sign-in)
for the public origin, delegated scope and authorized Office client. This example
uses fixed-home-tenant mode by default and needs no client secret. To follow
Azure's supported account types, additionally set `Entra__DiscoverAccountTypes=true`
and supply `Entra__ClientSecret` through a secret store for the read-only Graph
lookup. Keep `Entra__TenantId` set to the app registration's home tenant. The
registration must request v2 access tokens. The policy refreshes on demand after
30 seconds by default; an expired lookup failure returns 503 instead of accepting
an expired policy. See [Azure-driven account policy](../../docs/authentication.md#follow-azures-supported-account-types).

The example mapper still permits only its one configured tenant/object link;
allowing more account types in Azure does not create other local account links.
Direct personal Microsoft accounts are currently unsupported in CellBridge's
native Office integration, including when Azure's audience permits them.
Run behind HTTPS at the configured origin;
trusted proxy configuration and certificates belong to the host. This example
has no browser login or account-linking pages. React, Blazor, MVC or another UI
can supply them in a real application. `MapCellBridge()` maps native discovery
and cookie exchange without a separate authentication mapping call.

The example still creates only a text file, so it does not establish live Office
editing support. Automated package checks use signed test tokens and exercise
the exchange/discovery sequence; actual Office open, save and reopen validation
remains separate. Adding Google browser sign-in would not establish native
Google authentication in Office.
