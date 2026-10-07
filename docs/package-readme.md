# CellBridge beta

CellBridge implements MS-FSSHTTP and MS-FSSHTTPB for ASP.NET Core hosts on .NET 10.
It is experimental software. The `0.1.0-beta.2` candidate packages expose the protocol
libraries, reusable host and storage providers; API and storage compatibility
may change before a stable release.

Start with `CellBridge.AspNetCore` and a storage provider. PostgreSQL is the
sample host's durable default; `CellBridge.Storage.InMemory` is volatile and
intended for disposable development. Applications provide authentication and
document permissions. `AddCellBridgeIdentityLogin<TUser>` reuses existing Identity
accounts and cookies, with packaged browser and Office pages, authenticator codes
and recovery codes. `AddCellBridgeLogin` connects custom account systems through
`ICellBridgeLoginAuthenticator`. Both supply CSRF protection and the Office
challenge/completion flow. Custom HTML is optional.
`AddCellBridgeOfficeFormsAuthentication` can connect an existing complete login flow.
The sample account database is not packaged.

Hosts can map the HTTP endpoints or execute parsed SOAP requests through
`CellBridgeRequestProcessor`. They can supply GUID resource identities, apply
document-scoped request permission limits and inspect accepted-save receipts.
A request permission limit only restricts the configured access evaluator;
it cannot grant access by itself. Receipts identify committed revisions, but
do not replace external publication. Opt-in ordered delivery requires a destination
with atomic revision comparison and durable operation receipts.

Desktop Word and Excel have been tested for authenticated editing, saving and
reopening. OneNote desktop synchronization, two-desktop coauthoring and Office
Online Server integration remain unqualified. True partial and unsupported non-file uploads
are rejected. Shared base-lock authority, recoverable external publication and
conditional document lifecycle APIs are available for hosts to adopt. Their
presence does not qualify every external backend or desktop client.

PostgreSQL setup explicitly migrates schema 3 to 4 with hosts stopped. Back up
state and content before upgrading. See the upgrade and tested recovery limits
in the repository guides before changing a durable installation.

- [Hosting and provider contracts](https://github.com/PatrickMatthiesen/cellbridge/blob/main/docs/storage-providers.md)
- [Embedding in another host](https://github.com/PatrickMatthiesen/cellbridge/blob/main/docs/package-integration.md)
- [Capabilities and limitations](https://github.com/PatrickMatthiesen/cellbridge/blob/main/README.md)
- [Package consumer example](https://github.com/PatrickMatthiesen/cellbridge/tree/main/examples/NuGetConsumer)

Licensed under MIT.
