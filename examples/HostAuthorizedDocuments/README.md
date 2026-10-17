# Async document authority example

This executable uses a host-owned PostgreSQL permission table and either an in-memory or PostgreSQL document store. It demonstrates async authority without Lists, historical permission snapshots or permission-writer coordination. The host provider is a normal `IDocumentAuthorizationProvider`; caching is a separate optional decorator.

Set `ConnectionStrings__authority` to a disposable PostgreSQL database, then run:

```sh
dotnet run -c Release --project examples/HostAuthorizedDocuments
dotnet run -c Release --project examples/HostAuthorizedDocuments -- --cache
dotnet run -c Release --project examples/HostAuthorizedDocuments -- --durable
dotnet run -c Release --project examples/HostAuthorizedDocuments -- --durable --cache
```

The example creates its own `example_document_permissions` table and tests two subjects with different permissions. It checks creation despite the actor's `CanCreate` flag, read-only access, transport-neutral protocol metadata, shared locks, and 100 whole-file saves with changing content and idempotent operation keys. It verifies each saved revision's bytes. A second workload interleaves reads across 64 resources with eight concurrent callers, exceeding the optional cache's capacity of 32.

After revocation and optional invalidation, history, a new save, receipt replay, lock refresh and protocol access must deny, without changing content or receipts. The example also checks workspace isolation, a new grant after a cached denial, and the fact that the revoked editor's retained lock can still block another editor. Every assertion runs in both cache modes; failure produces a nonzero exit.

For bounded scaling measurements, add `--saves=10`, `--saves=20`, `--saves=40` or `--saves=100`. The default remains 100 changed saves. `--payload-bytes=1048576` adds one MiB of deterministic varied text to each synthetic DOCX before ZIP compression. This tests larger Office-shaped content without claiming that Office opened it. Historical byte checks compare the original, first, middle and latest revisions after all saves.

JSON output reports checked scenarios, elapsed times, managed allocation, actual provider calls, resource count, concurrency, store and cache configuration. It also reports revision count, encoded storage bytes and expanded public-record JSON bytes so repeated-edit growth is visible. The optional codec-size probe runs after allocation and live-heap measurements. The hot-resource groups are sequential; the mixed-resource group has concurrency eight. `--durable` uses the same disposable database for CellBridge state/content and host permissions and initializes the document schema. These are synthetic consumer measurements, not an Office, external-publishing, crash-recovery or production-load qualification. Run modes on the same machine and database when comparing them; one timing trial is not a performance guarantee.

`--measure-gc` runs a full GC only after all workload operations and reports the live managed heap while the service, stores, current state, packages and expanded denial-check JSON remain owned. It is an optional measurement, not a storage fix. Total allocation includes the host permission provider and other process activity during the workload; it excludes startup and generation of the optional large text. Durable output also reports the current persisted JSON size, retained JSON bytes and state-version count for the changing document, and total physical bytes for the fresh database's public tables and indexes. Physical database size includes other fixture documents and free space. Measure process RSS externally and separately from managed heap or serialized state size.

Host permission fixture rows are removed in `finally`, including on failure. The empty permission table remains. Durable document state, content and receipts remain in the disposable database until you discard it; the example does not reset other documents. The default in-memory document store does not persist receipts across process restarts.

For interactive authenticated HTTP flows, use [DocumentLibrary](../DocumentLibrary/README.md), with its documented local package feed until these APIs are released. See [the authorization guide](../../docs/async-document-authorization.md) for migration and revocation semantics.
