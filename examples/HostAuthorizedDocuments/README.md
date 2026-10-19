# Host-authorized documents

A console example of `IDocumentAuthorizationProvider` backed by a host-owned PostgreSQL permission table. It creates a document, saves an edited Office file with a retry key, reads revision summaries and the original version's bytes, then revokes access. CellBridge enforces the permissions returned by the host provider.

Use a disposable PostgreSQL database and two local versions of the same Word document:

```sh
export ConnectionStrings__authority='Host=localhost;Port=5432;Database=example;Username=example;Password=...'
dotnet run --project examples/HostAuthorizedDocuments -- original.docx edited.docx
```

Add `--cache` to wrap the authority in `CachedDocumentAuthorizationProvider`. The example commits revocation before invalidating its local cache. A host with multiple instances must notify each instance when permissions change.

Document state and content use in-memory storage by default. Add `--durable` to use the same PostgreSQL database for CellBridge storage. Both options can be combined.

Each run creates a new resource and removes only its own host permission row on exit. Durable documents remain in the disposable database; discard that database when finished. The example does not modify the input files or remove other documents.

See [the authorization guide](../../docs/async-document-authorization.md) for provider configuration and [DocumentLibrary](../DocumentLibrary/README.md) for an authenticated web host.
