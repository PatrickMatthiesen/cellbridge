# CellBridge

CellBridge implements MS-FSSHTTP and MS-FSSHTTPB for ASP.NET Core hosts on .NET 11.
It is experimental software. The packages contain the protocol
libraries, reusable host and storage providers. API and storage compatibility
may change before a stable release.

Install `CellBridge`, `CellBridge.AspNetCore` and a storage provider. Core
contains SOAP/MTOM handling, document execution and storage contracts.
`CellBridge.AspNetCore` contains HTTP, login and dependency-registration code.
Providers depend on core, not the HTTP package. `CellBridge.FssHttpB` is a
standalone binary protocol package. PostgreSQL is the
sample host's default storage. `CellBridge.Storage.InMemory` loses its contents
when the process stops. Applications provide authentication and
document permissions. `AddCellBridgeIdentityLogin<TUser>` reuses existing Identity
accounts and cookies, with packaged browser pages, authenticator codes
and recovery codes. `AddCellBridgeCookieLogin` sets up a cookie and connects custom account systems
through `ICellBridgeLoginAuthenticator`. Use `AddCellBridgeLogin` when a host
already registers that cookie. These integrations supply CSRF protection. Custom HTML
is optional. Enable MS-OFBA separately with
`AddCellBridgeOfficeFormsAuthentication` for the same cookie scheme, using the
packaged pages or an existing complete login flow. OAuth-to-MS-OFBA fallback is
a separate opt-in and defaults to disabled. WebDemo explicitly enables forms
authentication for testing; its configuration is not the library default.
The sample account database is not packaged.

`CellBridge.Storage.SqlServer` supplies document state and content. It is
independent of PostgreSQL and `CellBridge.Lists.SqlServer`. Use the same version
for all CellBridge packages. Configure
`DbContextOptions<SqlServerDocumentDbContext>` with EF Core `UseSqlServer`, then
call `await new SqlServerStorageMigrator(setupOptions, limits).MigrateAsync()`
from a separate initializer with setup credentials. Construct both runtime stores
with the same restricted runtime options instance.
Registration, health checks and requests never migrate schemas. The hosting guide
below has complete examples and permission requirements. SQL Server has not been tested with live Office editing or every provider contract.

Hosts can map the HTTP endpoints or execute parsed SOAP requests through
`CellBridgeRequestProcessor`. They can supply GUID resource identities, apply
document-scoped request permission limits and inspect accepted-save receipts.
A request permission limit only restricts the configured access evaluator;
it cannot grant access by itself. Receipts identify committed revisions, but
do not replace external publication. Opt-in ordered delivery requires a destination
with atomic revision comparison and stored operation receipts.

The core `CellBridge.OfficeDocumentTemplates` API creates blank Office files:
`CreateWordDocument()`, `CreateExcelWorkbook()` and
`CreatePowerPointPresentation()`. Each returns an independently owned `byte[]`
for `documentService.CreateAsync(path, bytes, actor, cancellationToken)` with the
current authenticated actor. Word uses an empty paragraph and compatibility mode
15, the Word 2013+ model supported by current Word. Excel has one empty `Sheet1`;
PowerPoint has one blank slide with its master, layout and theme. File compatibility
metadata is independent of negotiated Office protocol versions. Imported files
and custom templates remain unchanged. See the embedding guide below for usage.

Desktop Word, Excel and PowerPoint have been tested for authenticated editing,
saving and reopening on a PostgreSQL-backed host. Bounded two-desktop tests
verified saved-change propagation; they do not establish reliable startup,
character-by-character delivery or production concurrency. RTC is enabled by
default. Multiple hosts require Redis coordination and shared document storage;
multi-host validation uses simulated protocol/SignalR traffic rather than native
Office. OneNote desktop synchronization and Office Online Server integration
remain unqualified. True partial and unsupported non-file uploads are rejected.
Shared base-lock authority, recoverable external publication and
conditional document lifecycle APIs are available for hosts to adopt. Their
presence does not qualify every external backend or desktop client.

Existing database installations require explicit migration with hosts stopped.
Back up state and content before upgrading. Follow the current provider upgrade
instructions and tested recovery limits in the hosting guide before changing an
installation with stored documents. Do not run old writers against a migrated schema.

- [Hosting and provider contracts](https://github.com/PatrickMatthiesen/cellbridge/blob/main/docs/storage-providers.md)
- [Embedding in another host](https://github.com/PatrickMatthiesen/cellbridge/blob/main/docs/package-integration.md)
- [Capabilities and limitations](https://github.com/PatrickMatthiesen/cellbridge/blob/main/README.md)
- [Multi-host RTC setup and limits](https://github.com/PatrickMatthiesen/cellbridge/blob/main/docs/multi-host-rtc.md)
- [Package consumer example](https://github.com/PatrickMatthiesen/cellbridge/tree/main/examples/NuGetConsumer)
- [HostAuthorizedLists example](https://github.com/PatrickMatthiesen/cellbridge/tree/main/examples/HostAuthorizedLists)
- [DocumentLibrary example](https://github.com/PatrickMatthiesen/cellbridge/tree/main/examples/DocumentLibrary)

Licensed under MIT.
