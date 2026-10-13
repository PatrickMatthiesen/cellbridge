# Customer workspace list engine implementation plan

Implementation scope for [master issue #76](https://github.com/PatrickMatthiesen/cellbridge/issues/76).
Build a reusable,
opt-in list engine in this repository, alongside the Office document libraries.
Use typed list definitions and PostgreSQL JSONB records to support company
templates X, Y and Z, customer-specific fields, and custom list J. Protect each
customer workspace as an access boundary.

## Scope and repository boundaries

Use one repository and optionally one ASP.NET Core process. List storage must not
depend on document synchronization state, binary serializers or SOAP endpoints.
Use a native `/api/workspaces/{workspaceId}/lists` API. SharePoint list API
compatibility is outside this proposal and the repository's existing protocol scope.

Start with three projects, with names provisional:

- `src/CellBridge.Lists`: models, typed schema validation, operations, authorization
  contracts, query definitions and storage contracts.
- `src/CellBridge.Lists.PostgreSql`: Npgsql persistence, independent database schema,
  migrations, row-level security and bounded query compilation.
- `src/CellBridge.Lists.AspNetCore`: explicit registration and endpoint mapping,
  authenticated actor adaptation, antiforgery handling and HTTP errors.

Add focused unit, PostgreSQL integration and HTTP tests under `tests/`. Add a
customer workspace page in `examples/DocumentLibrary`, enabled by its own
AppHost. Keep the default document demo unchanged.
Package the optional libraries for the package-only document-library example and
verify them through the local package feed. Publishing is a later decision. They
must have list-specific package descriptions and a readme.

The existing `ICellBridgeAuthorizationPolicy` is document-specific. Share the
host's stable identity and permission authority, not this interface or its
document state. Workspace membership is a new host responsibility, not an
existing CellBridge capability.

## First usable release

A company operator registers versioned templates X, Y and Z. A workspace manager
instantiates them for a customer. A list designer can add optional custom fields
and create list J within that workspace. Editors create and update typed records;
readers view records and bounded filtered lists. A scoped integration identity can
use the same APIs to write an external customer identifier into an allowed list.

Initial types are bounded plain text, boolean, integer, decimal, calendar date,
UTC timestamp, single-choice values and same-workspace record references. Specify decimal precision, integer bounds,
date syntax and missing-versus-null semantics before implementation. Choice IDs
are stable; labels can change. Disallow nested objects and arrays initially.

All readers of a list can read all its fields and records. Support workspace and
list roles, not per-record sharing or field-level confidentiality in this release.
Confidential data belongs in a separate restricted list. Filters, sorting and
projections never add access beyond the list grant.

Include field-level merge previews, presentation metadata/tags, automatically
assigned list numbers, same-workspace record links, bounded CSV/XLSX extracts and
an optional read-only OData feed. The feed's protocol is tested locally; live Excel,
Power BI Desktop and hosted refresh compatibility remain separate qualification.
Defer formulas, scripts, arbitrary HTML, customer SQL, outbound webhooks, generic
connectors, arbitrary-column foreign keys, uniqueness declarations, attachments,
global search, bulk imports, automatic template propagation and destructive schema
changes. A new list, a new column and a saved filtered view are supported concepts;
computed or externally synchronized lists require later explicit designs.

## Data model and schema evolution

Use a dedicated PostgreSQL schema such as `cellbridge_lists`. It can share the
development PostgreSQL server, but has its own migrator, credentials, version
table and backup requirements. Never use the document migrator to modify lists.

Store template definitions and immutable template versions in operator-managed
tables. Workspace-scoped tables hold workspace registrations, lists, immutable
list schema versions, records, saved views, quota counters and audit events.
The sample additionally holds workspace memberships and list grants. A workspace
registration maps an opaque host-owned ID; the engine does not own the company's
customer lifecycle.

Each record has `workspace_id`, `list_id`, `record_id`, `schema_version`, a
monotonically increasing revision, timestamps, authenticated actor IDs and
`values jsonb`. JSON keys are server-generated field IDs, not display names.
Scope keys and foreign keys by workspace and list so the database rejects
cross-workspace associations. Never accept actor, revision, provenance or
workspace ownership from the record body.

A template instantiation materializes a pinned effective list schema. Fields have
stable IDs and provenance identifying protected template fields and workspace
extensions. Designers cannot change protected fields. An operator publishes a
new template version without silently updating existing workspaces.

First-release schema changes are additive optional fields and display-label
changes. Field types, required status and choice IDs remain immutable after
initial publication, even if all records are subsequently deleted. Publication
requires the expected current schema version; concurrent edits based on the same
version produce one success and one explicit conflict. Schema versions persist
for decoding old records. Missing optional fields remain absent; introducing a
field does not rewrite records or silently
insert defaults. Record writes must name the current schema version and use the
current record revision. Stale versions return conflicts for new mutations rather
than reinterpret data. Replays of committed creates follow the receipt rules below.
A read returns the schema information needed to interpret its values.

Saved views contain only a validated query definition and display configuration.
They cannot grant access. Saved views use stable field IDs and are validated
against the current effective schema when executed.

## Authorization and database isolation

Use explicit operations such as read list, write records, design list and manage
workspace grants. Reading or designing a list does not imply record-write or
permission-administration rights. Template administration is a separate
company-level operation. Workspace membership permits participation but grants no
list data access. Reading, writing or designing a list also requires an explicit
grant for that list. List discovery returns only lists the actor can access.
Workspace grant administrators can assign list grants, including to themselves;
this trusted power is explicit and audited, not automatic record access. There
are no implicit workspace-reader grants over every list. Define role-to-operation
mappings in tests. Workspace RLS does not enforce these list-level grants;
application authorization must enforce them.

Authentication supplies a stable issuer-qualified subject. Human and integration
identities are distinct. Integration permissions are scoped to named workspaces,
lists and operations; humans do not lend them their cookies. The host validates
credentials. The engine stores no connector secrets and executes no supplied code.

Resolve the route workspace and resource under the caller's authority; never
treat a request workspace ID as evidence of membership. Fail closed when the
authority is missing or unavailable. Authorize metadata and saved views as well
as records. Use a consistent not-found result for absent or invisible resources.
Error messages must not reveal another workspace's names, schemas or counts.

The sample stores memberships, list grants and integration scopes in PostgreSQL.
Use `READ COMMITTED` transactions. Each operation first acquires a shared workspace
authorization lock, then reads authoritative permissions in a separate statement
with a fresh snapshot, and holds the lock through its protected database operation.
Every membership removal, list-grant change and integration-scope reduction takes
the conflicting exclusive lock before updating authority. Do not use a permission
snapshot obtained before waiting for the lock. Acquire locks in the fixed order
workspace authorization, list schema, workspace quota, record; acquire multiple
resources in stable ID order. Set bounded lock/statement timeouts and roll back on
cancellation or timeout without bypassing authorization. Document the resulting
guarantee: operations ordered after a committed revocation are denied, while an
operation ordered before it may finish.
Do not claim to retract data already delivered. External hosts must supply an
equivalent enforceable authorization guard held through the protected operation.
A cached boolean or a revision check followed by an unguarded commit is insufficient.
Adapters with weaker in-flight revocation guarantees remain outside strict-security
qualification. Define this contract before opening adapters to external production
hosts.

All workspace-scoped tables use forced row-level security with read and write
checks. Runtime connections use a non-owner role without superuser, `BYPASSRLS`,
DDL or migration privileges. The store sets verified workspace context using
transaction-local settings and rejects missing context. Never expose raw SQL or
runtime database credentials to users. This database context backs up application
checks; it is not independent authentication and cannot contain arbitrary SQL
execution under the application's role.

Test connection reuse, rollback, cancellation and simultaneous requests for
different workspaces. Ensure pooled connections cannot retain workspace context.
Apply isolation to audit, definitions, views and quotas, not just record rows.
Application queries still include explicit workspace and list predicates.

## Validation, transactions and queries

Use a server-owned typed field-definition model with a restricted set of rules.
Reject unknown fields, duplicate JSON keys, invalid values and over-budget inputs.
Users do not upload arbitrary JSON Schema, regexes or remote schema references.
Decide whether JsonSchema.Net helps validate a generated schema during the first
milestone. Do not add a dependency solely to replace a small fixed type validator.
The same field definitions drive API metadata and sample form controls.

Field display settings include hidden-by-default, presentation read-only, help
text, ordering and descriptive tags. Saved views choose visible fields. These are
bounded inert metadata; hidden/read-only presentation does not restrict API access.

Merge previews compare the client's original values, its proposed field changes
and the current authorized record. Distinguish missing values from explicit null.
Return nonconflicting patches and per-field original/current/proposed values for
conflicts. Clients retain unsaved edits, choose resolutions and submit a conditional
patch against the preview's current revision and schema. A later concurrent change
returns another conflict, never an unconditional whole-record overwrite. The
client-provided original snapshot is comparison input, not authorization or proof
of historical provenance. Full retained record history is not required.

References store the target record UUID, target list and an optional display-field
ID, not a mutable title. Validate target existence and caller access on assignment;
recheck target read access on resolution. Do not expand references automatically
in query/export responses. Cross-workspace links fail. Target deletion is blocked
while referenced, including concurrent assignment/deletion. Opaque record identity
is separate from an immutable server-assigned per-list number and optional display
prefix. Numbers never repeat and committed create retries preserve the assignment;
gap-free fiscal numbering is outside this library's contract.

Schema publication and new record mutation serialize through the current list
schema row. Recheck schema version, authorization, revision and quotas before commit.
Use conditional updates to prevent lost changes. Create IDs are client-generated
UUIDs scoped to a list. Authorize with current permissions first, then look up the
create receipt before applying current-schema validation. Bind the receipt to
workspace, list, record ID, initiating subject, original schema version and a
canonical request digest. Define canonicalization of all accepted types and absent
versus null values in milestone 1. An identical retry by that subject within the
documented retry window returns only the recorded ID and initial revision, even
after schema changes, edits or deletion. It neither restores deleted data nor
returns current values. Changed subjects or payloads conflict; revoked access is
denied before receipt lookup. Validate the current schema only for a new mutation.

Keep a record-ID tombstone for the lifetime of the list so IDs cannot be reused
after deletion or receipt expiry. After receipt expiry, a repeated ID conflicts
instead of being recreated; callers resolve uncertainty through an authorized read.
Tombstones consume quota and count against a total record-identity limit. Retried
updates with stale revisions return a conflict and require rereading. Define
lost-response behavior rather than claiming all writes are exactly-once.

Commit each mutation, quota adjustment and its audit event atomically. Audit
events record actor, action, resource, revision and field IDs, without storing
field values by default. Schema and membership changes are audited too. Audit
access and retention are operator-controlled. Ordinary logs exclude values and
credentials. The initial release has audit history, not full record restoration.

Expose a typed query object with an allowlist of field IDs and operators. Start
with conjunctions of equality and appropriate scalar comparisons. Support only
approved sortable fields and deterministic keyset pagination with a record-ID
tiebreaker. Bind cursors to workspace, list, query and schema, and authenticate
them. Do not accept SQL, arbitrary JSONPath or expression strings. Parameterize
all values and resolve field paths from validated definitions.

Specify null ordering, collation, decimal comparisons and pagination behavior
under concurrent edits. Pagination is not a snapshot across requests. Exclude
unbounded totals and aggregations initially. Index workspace/list/record keys;
benchmark declared JSONB filter and sort operations before selecting additional
indexes. Index creation is an operator action, never user-triggered DDL.

The initial provider may evaluate supported filters/sorts over a bounded list
snapshot of at most 5,000 records. Reject further growth explicitly; do not silently
truncate query inputs. Qualify this limit and document memory/latency tradeoffs.
Database query compilation and larger workloads require measured follow-up work.

Exports use one bounded authorized query snapshot and report their extraction
limit and whether more records remain. CSV protects untrusted text from spreadsheet
formula interpretation; XLSX stores strings as strings rather than formulas.
Default projection omits hidden fields, while an authorized explicit projection
can include them. A read-only per-list OData adapter exposes stable typed field
names, metadata, protected paging and only the native query operator subset.
Reject unsupported OData options. Reporting never receives raw database access.

Set configurable limits for fields, lists per workspace, record bytes, stored
bytes, records, query predicates, page size, execution time and concurrent
requests. Include schemas, views, receipts, tombstones and audit events in retained
storage accounting, not just current record values. Specify logical byte/count
accounting independently of physical database overhead; operators monitor actual
database capacity separately. Set operator-controlled retention and per-workspace
budgets for metadata and audit. Reject mutations before exceeding their budgets.
Never purge a needed schema or a receipt inside its promised retry window.

Permission reductions use a separately reserved security-audit budget and are not
blocked by ordinary workspace quota exhaustion. If that reserve is exhausted or
the authority database is unavailable, fail closed for workspace operations and
provide an operator procedure to restore capacity and revoke access. Do not permit
unaudited permission expansion as an emergency fallback. Choose defaults and the
receipt retention window from the sample workload during milestone 1 and encode
them in tests. Enforce shared storage quotas transactionally, not per process.

HTTP writes using cookies require antiforgery protection. Bearer integrations use
an explicitly configured authentication scheme and permission scope. Bound request
bodies before parsing, encode displayed labels and text, use private/no-store
responses for customer data, and return controlled validation/conflict errors.

## Implementation sequence and acceptance criteria

1. **Contracts and security decisions.** Specify types, query operators, limits,
   role mappings, authorization transaction semantics, error behavior and native
   API examples, canonical create digests and receipt expiry. Resolve null ordering,
   numeric comparisons, collation and concurrent pagination semantics. Record
   dependencies and confirm migration/runtime role separation.
   Acceptance: the complete two-customer scenario can be represented without
   document protocol changes; every proposed operation has an access decision.

2. **Typed list definitions.** Implement immutable schemas, template instantiation,
   protected provenance, additive extensions and record validation in the core
   project. Acceptance: invalid types, unknown fields, duplicate keys, protected
   edits and stale schema versions fail explicitly for new mutations. Concurrent
   schema additions preserve both changes or return an explicit conflict. Unit
   tests cover accepted and rejected cases and all chosen type boundaries.

3. **Durable isolated persistence.** Implement the independent schema/migrator,
   least-privilege runtime setup, forced RLS, atomic record operations, receipts,
   quotas, audit and sample membership authority. Acceptance: direct SQL under
   the runtime role cannot read or write a different selected workspace; missing
   context denies access; stale updates cannot overwrite; concurrent schema changes
   and quota enforcement preserve invariants; revocation races follow the contract
   for membership removal, list-grant removal and integration-scope reduction in
   both race orderings. Verify actual runtime role privileges as well as RLS.
   Test fresh initialization, restart persistence and incompatible-version refusal
   against disposable PostgreSQL, not an in-memory imitation.

4. **Authorized API and bounded queries.** Implement opt-in registration/endpoints,
   typed filters, keyset pages, roles, scoped integration identities and antiforgery.
   Acceptance: two customers can use the same list labels without crossing access
   boundaries through IDs, metadata, records, views, query errors or cursor reuse.
   Ordinary and restricted lists within the same workspace remain isolated for
   discovery, schemas, views and records.
   Missing/unauthorized subjects fail closed. Tests verify injection-shaped values,
   resource limits, stale revisions, duplicate creates and unavailable authority.
   Create retry tests cover schema changes, subsequent edits, deletion, revocation,
   changed subjects/payloads and receipt expiry. Metadata-growth tests cover audit,
   schema history and receipts, including access revocation at ordinary quota limits.
   Include merge previews, display metadata, references, numbering, CSV/XLSX and
   the optional feed. Test repeated merge races, reference/delete races, missing
   target access, immutable numbering and typed spreadsheet/feed output.

5. **Customer workspace sample.** Provide two isolated customer workspaces, X/Y/Z
   templates, an optional custom field on X and custom list J. Include list browsing,
   generated controls, record editing, schema conflict handling and saved views.
   Extend the existing package-only document-library example after Astra implementation
   review, consuming locally verified optional packages. Add task/work-package
   references, a merge flow and extracts. Keep list setup separate from document
   setup and runtime credentials least privileged. Wire the sample into its own
   Aspire host without changing the default Office host resources.
   Acceptance: a browser/API scenario proves isolation, restart persistence and
   revocation; an independent host consumes the module without Office protocols.

6. **Release qualification and documentation.** Add focused CI coverage and bounded
   performance evidence for the chosen limits, backup/restore guidance, retention
   and deployment instructions. Run existing protocol/storage regressions to verify
   isolation of the change. Acceptance: PostgreSQL/security checks pass without
   skips and existing tests pass. Record untested scale and deployment assumptions.
   Public packaging and production claims require a separate decision.

The first end-to-end checkpoint is a durable list with one text field, one numeric
field and two customer workspaces, exercised through the API with real RLS and
revocation checks. Complete that before building the full schema designer.

## Later extensions

Plan destructive schema migration as a separate feature with preflight validation,
explicit data conversion and rollback rules. Extend single-record links with
explicit many-to-many or arbitrary-column key semantics only when required.
Uniqueness needs canonical typed values and a transaction-safe
index/constraint strategy; do not implement it as a pre-write lookup.

Field-level permissions require protecting values in filters, sorting, counts,
exports, audit and derived values, not merely omitting form controls. Generic
integrations require secret storage, outbound network restrictions, destination
validation, retry/idempotency and synchronization conflict rules. Formulas require
a restricted evaluator and execution budgets. None should arrive indirectly
through a schema option in the first release.

Document links can later reuse existing host-owned document authorization. A link
must not grant document access or promise workspace isolation for existing documents
unless the host actually enforces it through the document policy adapter.

## Repository evidence

- [Current architecture](architecture.md) separates protocols, hosting and storage.
- [Host-owned document policy](../src/CellBridge.AspNetCore/DocumentAuthorizationPolicy.cs)
  uses document-specific immutable authorization snapshots.
- [Host authorization example](../examples/HostAuthorization/HostPermissionService.cs)
  demonstrates an adapter but explicitly uses volatile example permissions.
- [Storage setup](storage-providers.md) has an existing document schema/migrator
  that must remain independent of list schema evolution.
- [Testing guidance](automated-testing.md) describes disposable PostgreSQL and
  distinguishes skipped durable checks from passing qualification.
- [PostgreSQL transaction isolation](https://www.postgresql.org/docs/current/transaction-iso.html)
  specifies per-statement snapshots under `READ COMMITTED`, which the proposed
  authorization lock protocol relies on.
- [PostgreSQL row security](https://www.postgresql.org/docs/current/ddl-rowsecurity.html)
  specifies policy enforcement and owner/superuser bypass rules that runtime
  privilege checks must account for.

Implementation must follow the repository's Aspire lifecycle and capture rules.
This planning change runs no app or database and establishes no product support.
