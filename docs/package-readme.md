# CellBridge beta

CellBridge implements MS-FSSHTTP and MS-FSSHTTPB for ASP.NET Core hosts on .NET 10.
It is experimental software. The `0.1.0-beta.1` packages expose the protocol
libraries, reusable host and storage providers; API and storage compatibility
may change before a stable release.

Start with `CellBridge.AspNetCore` and a storage provider. PostgreSQL is the
sample host's durable default; `CellBridge.Storage.InMemory` is volatile and
intended for disposable development. Applications provide authentication and
an access evaluator. The sample authentication library is not packaged.

Hosts can map the HTTP endpoints or execute parsed SOAP requests through
`CellBridgeRequestProcessor`. They can supply GUID resource identities, apply
document-scoped request permission limits and inspect accepted-save receipts.
A request permission limit only restricts the configured access evaluator;
it cannot grant access by itself. Receipts identify committed revisions, but
do not provide a reliable external write-back queue.

Desktop Word and Excel have been tested for authenticated editing, saving and
reopening. OneNote desktop synchronization, two-desktop coauthoring and Office
Online Server integration remain unqualified. True partial and non-file uploads
are rejected. Shared WOPI/FSSHTTP locks, reliable external write-back and document
deletion are not provided by this beta.

- [Hosting and provider contracts](https://github.com/PatrickMatthiesen/cellbridge/blob/main/docs/storage-providers.md)
- [Embedding in another host](https://github.com/PatrickMatthiesen/cellbridge/blob/main/docs/package-integration.md)
- [Capabilities and limitations](https://github.com/PatrickMatthiesen/cellbridge/blob/main/README.md)
- [Package consumer example](https://github.com/PatrickMatthiesen/cellbridge/tree/main/examples/NuGetConsumer)

Licensed under MIT.
