# CellBridge customer workspace lists

Optional .NET 10 libraries for typed customer-workspace lists. These libraries
use a native list API and are independent of CellBridge's Office synchronization
protocols.

- `CellBridge.Lists` defines schemas, templates, validation, typed queries and
  three-way merge previews.
- `CellBridge.Lists.PostgreSql` stores JSONB records with workspace isolation,
  explicit list grants or an asynchronous host authority, conditional updates,
  references, numbering and audit.
- `CellBridge.Lists.AspNetCore` provides opt-in HTTP endpoints, protected paging,
  CSV/XLSX extracts and a restricted read-only OData feed.

The host supplies authentication and maps its validated principal to a stable
`ListActor`. Database setup uses separate operator credentials; runtime uses a
non-owner role subject to row-level security. Workspace membership alone does
not grant access to a list.

Hidden fields and read-only display hints are presentation settings. They do
not restrict API permissions. A merge preview never authorizes an unconditional
overwrite; submit the resolved patch with the preview's current revision.

The initial provider evaluates queries over bounded list snapshots. Use the
documented limits and qualification evidence before choosing deployment scale.
The OData adapter is intended for Power Query exploration; native desktop and
hosted scheduled-refresh compatibility require separate qualification.

See the repository's [list guide](https://github.com/PatrickMatthiesen/cellbridge/blob/main/docs/lists.md)
and [host authorization guide](https://github.com/PatrickMatthiesen/cellbridge/blob/main/docs/host-list-authorization.md)
and [document-library example](https://github.com/PatrickMatthiesen/cellbridge/tree/main/examples/DocumentLibrary)
for configuration, supported operations and limits. These packages are experimental.
