# Async document authority example

This executable uses a host-owned PostgreSQL permission table and an in-memory document store. It demonstrates async authority without Lists, historical snapshots or permission-writer coordination. The host provider is a normal `IDocumentAuthorizationProvider`; caching is a separate optional decorator.

Set `ConnectionStrings__authority` to a disposable PostgreSQL database, then run:

```sh
dotnet run -c Release --project examples/HostAuthorizedDocuments
dotnet run -c Release --project examples/HostAuthorizedDocuments -- --cache
```

The example creates its own `example_document_permissions` table, registers one DOCX, reads history, executes transport-neutral protocol metadata requests, takes a shared host lock and saves whole files with durable operation keys. It then revokes the permission, optionally invalidates the cache, and verifies a later operation denies access. It removes its permission row afterward. The empty table remains.

JSON output reports elapsed times and actual provider calls for each workload. Each run uses one subject/resource, sequential execution, a local in-memory document store, and one indexed PostgreSQL query per provider call. The first whole-file write changes content; later writes are no-ops with distinct receipts. It does not measure durable document write throughput, remote policy latency, many-workspace contention, HTTP transport or Office interoperability. Run both modes on the same machine and database when comparing them.

The in-memory document store is intentionally disposable. Production hosts should select a durable provider. See [the authorization guide](../../docs/async-document-authorization.md) for migration and revocation semantics.
