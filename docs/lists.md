# Customer workspace lists

The experimental `CellBridge.Lists`, `CellBridge.Lists.PostgreSql` and
`CellBridge.Lists.AspNetCore` packages add typed records to customer workspaces.
They are optional and independent of Office document storage, SOAP and binary
synchronization. They do not implement SharePoint list APIs. The
[implementation plan](list-engine-implementation-plan.md) records the broader
scope and qualification criteria; this guide describes the current libraries.

## Library and host responsibilities

`CellBridge.Lists` defines schemas, templates, validation, bounded queries and
three-way merge previews. `IListEngine` is the authorized operation contract.
The PostgreSQL provider stores list state, memberships, grants, receipts and
audit events in its own `cellbridge_lists` schema. The ASP.NET Core adapter maps
native JSON endpoints, exports and a limited read-only OData feed.

The host authenticates callers and maps each validated identity to a stable,
issuer-qualified `ListActor.Subject`. It owns workspace registration, account
lifecycle, authentication schemes, integration identities, UI rendering and
operator setup. A caller-supplied subject or workspace ID never proves access.
Document permissions do not grant list permissions. An integration identity
needs its own workspace membership and explicit grants to its allowed lists.
The current authority has no separate OAuth scope registry.

Every operation requires current workspace membership. List grants are flags,
not a hierarchy:

| Operation | Additional authority |
| --- | --- |
| Discover lists | Any explicit grant on each returned list |
| Read schema, record, query, view, export or feed | `Read` |
| Create or delete a record | `Write` |
| Patch a record or preview a merge | `Read` and `Write` |
| Publish schema or save a view | `Design` |
| Create a list | Membership's `CanCreateLists`; the creator receives an explicit `Read`, `Write`, `Design` grant |
| Change membership or list grants; read audit | Membership's `CanManageGrants` |

A grant administrator can assign grants to themselves. That power does not
implicitly grant record access. All list readers can read every field and record
in that list. Put confidential fields in a separately restricted list.

The provider takes a shared workspace authorization lock before reading
permissions and holds it through the transaction. Authority changes take the
conflicting exclusive lock. Operations ordered after a committed revocation are
denied; an operation ordered before it may finish. Revocation cannot retract
already delivered data. Workspace RLS backs up these application checks and does
not replace list grants.

## Definitions, values and presentation

Each field has a stable UUID. Record value keys must be its exact lowercase,
hyphenated `D` representation, such as
`30000000-0000-0000-0000-000000000000`. Unknown fields fail validation. HTTP parsing
rejects duplicate JSON keys, unknown envelope properties and null required
collections. Nested objects and arrays are not record values.

| Type | Accepted value |
| --- | --- |
| `Text` | JSON string, at most `MaxLength` UTF-16 code units; default 1,024, configurable from 1 to 65,536 |
| `Boolean` | JSON `true` or `false` |
| `Integer` | JSON integer from `-9223372036854775808` through `9223372036854775807`; fractional or exponent spellings are rejected |
| `Decimal` | JSON number exactly representable within 28 decimal digits and scale 12 after insignificant trailing zeros; exponent notation is accepted within those bounds |
| `Date` | Valid calendar date string exactly `yyyy-MM-dd` |
| `Timestamp` | Canonical UTC string `yyyy-MM-ddTHH:mm:ssZ` or with up to seven fractional digits before `Z`; omit trailing fractional zeros |
| `Choice` | String matching a declared stable choice ID, not its label |
| `Reference` | Nonempty target record UUID string in canonical lowercase `D` form |

An optional value can be absent or explicitly `null`. Both are valid but remain
distinct in stored records and merge previews. Required values reject absence and
null. `FieldChange.Remove=true` removes an optional key; assigning `Value=null`
stores an explicit null. Duplicate changes to one field and removal with a value
are invalid. Record limits measure UTF-8 bytes of the serialized JSON object,
including keys and escaping.

List and field names are nonblank, at most 200 characters and contain no control
characters. IDs and field names must be unique within a definition. Choice fields
have 1 to 256 choices, with unique IDs of at most 64 characters and labels of at
most 200. Display metadata allows help text up to 2,048 characters, order from
-1,000,000 through 1,000,000, and at most 16 unique tags of 64 characters each.
Help text may contain line breaks and tabs. Metadata is plain data; the host must
encode it when displaying HTML.

`Hidden`, `ReadOnly`, help, order and tags configure presentation. Saved views
select visible field IDs. Hidden fields remain accessible through authorized
native record reads and explicit export/feed projection. Presentation read-only
fields remain writable through the API. Neither setting is a security rule.

Operators register immutable `ListTemplate` versions. To instantiate one,
`CreateListRequest` supplies its ID/version and workspace extension fields; the
provider copies template fields as protected and pins that version. Custom lists
omit the template identity. `ListTemplates.Instantiate` also provides the core
copy-and-validate operation. Publishing a later template never updates existing
lists automatically.

Schema publication requires `ExpectedVersion`. Existing fields cannot be removed
or change their type, required status, maximum length, choice IDs, reference
target or protected provenance. Unprotected field names, choice labels and
presentation metadata can change. Protected template fields cannot change.
Published additions must be optional, unprotected workspace fields. Schema
versions advance by one, remain stored, and do not rewrite old records or add
defaults. New creates and patches require the current schema version.

References declare a target list and optionally a display field. Assignment
requires the target record to exist in the same workspace and the caller to have
target-list read access. Resolution rechecks that access and returns the target
UUID, number and optional label. Queries and extracts retain the UUID without
expanding related records. Deletion fails while another record references the
target; provider mutation locks serialize assignment against deletion.
An unrelated patch preserves stored references without requiring target read
access again. Assigning a reference in a patch still checks that access.

Each successful create gets an immutable server-assigned per-list `Number`,
starting at 1. `NumberPrefix`, at most 32 characters, is presentation metadata and
cannot change after publication. UUIDs identify records; numbers never repeat.
This is not a contract for gap-free fiscal numbering.

## Conditional saves and merge previews

Keep the original record snapshot and the user's local changes separately. Save
only field patches with the loaded revision and current schema version. A stale
revision returns `409`; do not discard the draft or overwrite the whole record.

For example, Alice edits the title while Bob changes the deadline. Alice's
original values and proposed patch can be sent to the preview endpoint:

```http
POST /api/workspaces/{workspaceId}/lists/{listId}/records/{recordId}/merge-preview
Content-Type: application/json

{
  "schemaVersion": 3,
  "originalValues": {
    "30000000-0000-0000-0000-000000000000": "Original title",
    "40000000-0000-0000-0000-000000000000": "2026-10-20"
  },
  "changes": [
    { "fieldId": "30000000-0000-0000-0000-000000000000", "value": "Alice's title" }
  ]
}
```

The preview returns `currentRevision`, `schemaVersion`, `suggestedChanges` and
`fields`. Field entries compare original/current/proposed values and presence,
including fields changed by others that Alice did not edit. Bob's deadline change
appears with `conflict=false` and produces no suggested patch. An identical
current/proposed value also needs no patch. Competing edits to the same field
require Alice to choose a resolution. The supplied original snapshot is
comparison input, not proof of history or authority.

After presenting those differences, combine `suggestedChanges` with explicit
user resolutions and save against the returned revision. If it is 8:

```http
PATCH /api/workspaces/{workspaceId}/lists/{listId}/records/{recordId}
Content-Type: application/json

{
  "expectedRevision": 8,
  "schemaVersion": 3,
  "changes": [
    { "fieldId": "30000000-0000-0000-0000-000000000000", "value": "Alice's title" }
  ]
}
```

This preserves Bob's deadline. Another intervening save returns `409` again;
retain the draft and request another preview. Cookie requests need the host's
antiforgery token/header in addition to the JSON shown above.

Create IDs are client-generated UUIDs. Receipts last seven days by default and
bind workspace, list, ID, actor, original schema version and SHA-256 digest. The
digest sorts field keys and canonicalizes accepted numeric scalars; absence and
null differ. An authorized identical retry returns the original ID, revision and
number, even after edits, schema changes or deletion. It does not restore data or
return current values. Changed payloads or actors conflict. Authorization occurs
before receipt lookup. Replays validate field constraints against the retained
original schema, and PostgreSQL's clock determines expiry. Expiry does not
permit ID reuse; lifetime tombstones retain
every used record ID. Updates have conditional revisions, not create-style retry
receipts.

## Queries and reporting

`ListQuery` supports conjunctions of `Equal`, `NotEqual`, `GreaterThan`,
`GreaterOrEqual`, `LessThan` and `LessOrEqual`. Boolean, choice and reference fields
support only equality and inequality. Null supports only equality and inequality;
missing values compare as null in queries. Relational comparisons never match a
null value. Text uses ordinal comparison, numbers use their declared numeric type,
and timestamps sort chronologically. Nulls sort first ascending and last
descending. Default ordering is the canonical record UUID; field sorting uses that
UUID as its tie breaker. Descending reverses both comparisons.

The native query endpoint accepts this envelope:

```json
{
  "query": {
    "filters": [],
    "sortFieldId": null,
    "descending": false,
    "limit": 100
  },
  "cursor": null
}
```

POST it to `/api/workspaces/{workspaceId}/lists/{listId}/records/query`. Repeat the
same query with the response's `nextCursor`. HTTP clients cannot supply
`afterRecordId` directly. Protected cursors expire after one hour and bind actor,
workspace, list, schema, query and adapter purpose. Persist/share ASP.NET Core Data
Protection keys when restart or multiple-instance cursor continuity is required.
Pages are not snapshots across requests. Concurrent edits can move records between
pages; a deleted anchor or one no longer matching the filter rejects pagination,
so restart the query.

The initial PostgreSQL provider reads at most 5,000 candidate records per list
and evaluates filters/sorting in memory. It rejects excessive candidates instead
of truncating them. Workspace mutations serialize. Raising limits does not qualify
larger workloads; database query compilation, indexing and measured latency/memory
work remain follow-up tasks.

GET `/{listId}/export.csv` or `/{listId}/export.xlsx` below the list API root.
Only `limit` and `fields` query parameters are supported; `fields` is a
comma-separated list of field UUIDs. The current extract endpoint returns one
bounded default-ordered page, without filter/sort parameters or continuation.
Response headers `X-CellBridge-Export-Limit`, `X-CellBridge-Export-Has-More` and
`X-CellBridge-Schema-Version` disclose its bounds. Default projection omits hidden
fields. CSV prefixes formula-triggering text; XLSX stores text as strings,
booleans as boolean cells and dates/timestamps as date cells where representable.
Numbers with more than 15 significant digits become text to preserve precision.

XLSX rejects an export with HTTP 413 if a selected text cell exceeds 32,767
UTF-16 units before XML escaping. This conservative bound follows
[Excel's cell character limit](https://support.microsoft.com/en-us/excel/excel-specifications-and-limits).
The adapter never truncates cell text. Use CSV, native reads or a narrower field
projection for those values. Hidden fields omitted by the projection do not block
the export.

Choice values and references retain stable IDs. Neither format is a backup or
lossless absent-versus-null transport.

The optional OData 4.0 subset is enabled by default; set `EnableOData=false` to
hide it. Each list has `/odata`, `/odata/$metadata` and `/odata/Records`. Typed
properties use `f_` plus the field's UUID without hyphens, alongside `Id`, `Number`
and `Revision`. Supported options are `$top`, `$select`, `$filter`, `$orderby` and
protected `$skiptoken`. Filters use the native comparison subset joined by `and`;
ordering permits one field. Unsupported options, functions, expansion, counts and
writes fail. Hidden fields are omitted from default rows but remain in metadata
and explicit projections. This is a reporting adapter, not a full OData service.
Explicit `$top` limits the total response and does not create a next link; omit it
to use bounded server paging. `$select` controls returned properties. Request
`IEEE754Compatible=true` in the JSON Accept header to transport Int64 and decimal
values as strings. Decimal metadata describes precision 28 with variable scale
and a maximum-scale-12 annotation. These follow the
[OData CSDL facets](https://docs.oasis-open.org/odata/odata-csdl-xml/v4.01/cs01/odata-csdl-xml-v4.01-cs01.html)
and [JSON numeric representation](https://docs.oasis-open.org/odata/odata-json-format/v4.01/cs01/odata-json-format-v4.01-cs01.html).
Live Excel/Power BI Desktop imports and hosted scheduled refresh remain separate
qualification; the library supplies no refresh credentials or connector service.

## Setup, operation and checks

Provision separate PostgreSQL setup and runtime credentials. The setup role owns
DDL and template/workspace provisioning. The runtime role must not own or inherit
ownership of list objects, be a superuser, have `BYPASSRLS`, or have schema creation
privileges. Provision its password through the host's secret system. For example,
an operator can create a dedicated role before setup:

```sql
CREATE ROLE cellbridge_lists_runtime LOGIN NOSUPERUSER NOCREATEDB
  NOCREATEROLE NOINHERIT NOBYPASSRLS;
```

Keep that role out of privileged memberships and review database/public schema
grants separately. `InitializeAsync` installs the independent schema and grants
only its required list access; it does not create the login. All workspace tables
have forced RLS with matching read/write checks. The provider sets verified
workspace context transaction-locally and checks schema version/runtime privileges
before use. Runtime credentials cannot register operator templates.

Run provisioning separately from the web host using the actual public setup APIs:

```csharp
using CellBridge.Lists;
using CellBridge.Lists.PostgreSql;
using Npgsql;

await using var setup = NpgsqlDataSource.Create(setupConnectionString);
await PostgreSqlListSetup.InitializeAsync(setup, "cellbridge_lists_runtime");
await PostgreSqlListSetup.RegisterWorkspaceAsync(
    setup, workspaceId, "Customer A", administratorSubject);
await PostgreSqlListSetup.RegisterTemplateAsync(setup,
    new ListTemplate(templateId, "X", 1,
        [new ListField(titleFieldId, "Title", ListFieldType.Text, Required: true)]));
```

Workspace registration bootstraps a member with create-list and grant-management
rights. Use the same subject mapping as the authenticated host. Bootstrap is
operator provisioning; use `IListEngine.SetMembershipAsync` and
`SetListGrantAsync` for subsequent changes under the authorization lock and audit.
There is no public membership or audit HTTP endpoint in the current adapter.

Runtime registration uses only the runtime connection:

```csharp
using CellBridge.Lists;
using CellBridge.Lists.AspNetCore;
using CellBridge.Lists.PostgreSql;
using Npgsql;

var limits = new ListLimits();
builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(runtimeConnectionString));
builder.Services.AddSingleton<IListEngine>(services =>
    new PostgreSqlListEngine(services.GetRequiredService<NpgsqlDataSource>(), limits));
builder.Services.AddCellBridgeLists(options =>
{
    options.Limits = limits;
    options.ActorResolver = context => ResolveHostActor(context.User);
    options.IntegrationAuthenticationScheme = "ListsBearer";
});
// The host registers authentication and validates cookie/bearer identities.
var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.MapCellBridgeLists();
```

`ResolveHostActor` is a host-supplied mapping of validated identities, not a library
method. Omit `IntegrationAuthenticationScheme` when no integration scheme exists.
The named bearer scheme must authenticate the same actor and the request must
have no Cookie header to bypass antiforgery. Every other non-GET request,
including query/preview POSTs, requires antiforgery. Configure its header name and
issue tokens in the host UI. Authentication scheme selection and bearer scope
validation remain host responsibilities. Responses use `private, no-store`.
HTTP results are 400 for validation, 401 for missing authentication, 404 for absent
or unauthorized resources, 409 for conflicts, 413 for limits and 503 for
unavailability; detailed store messages are not returned.

Record reads retain their stored schema version. For patches and merge previews,
use the current `ListDefinition.SchemaVersion` while preserving the loaded
record's original values and revision. Text must contain valid Unicode and cannot
include U+0000, which PostgreSQL text/JSONB cannot store.

Default budgets are 64 fields, 100 lists per workspace, 64 KiB record JSON,
5,000 lifetime record identities per list, 64 MiB logical workspace storage,
500 rows per page, 12 predicates, 32 views per list, 128 schema versions,
100,000 ordinary audit events and 10,000 reserved security events. HTTP bodies
are bounded at 128 KiB; SQL statement/lock timeouts default to 10 seconds.
The HTTP adapter admits at most eight concurrent requests by default through
`MaxConcurrentRequests`, including response serialization and extraction.
`MaxConcurrentRequestsPerActor` defaults to two across all workspaces, and
`MaxConcurrentRequestsPerWorkspace` defaults to four across all actors. Each
partition limit is capped by the global limit. Admission has no waiting queue;
excess requests return 503. Only active requests retain partition entries.

`RequestTimeout` defaults to 30 seconds and starts before admission. It covers
body reading, authentication checks inside the adapter, backend calls and response
execution. Expiry cancels the request and aborts its connection. A timed-out write
may already have committed; retry a create with the same ID and payload to recover
its receipt. Hosts still own connection/header limits and any distributed rate
limiting. Direct engine consumers must bound their own concurrency.

Custom engine and response implementations must honor cancellation. If custom
work ignores it, the adapter waits for that work to finish and retains its lease
so outstanding work stays bounded and request dependencies remain alive.
Deleting records frees their value bytes, not lifetime identity capacity.

Logical accounting includes value bytes and defined charges for retained schemas,
views, receipts, tombstones and ordinary audit metadata. It is not a PostgreSQL
physical-size estimate. Membership and grant removal releases their row charges,
including grants cascaded by membership removal. Retained audits keep their charges.
Monitor table/index/WAL growth and actual disk separately.
Permission reductions use their separately bounded security-audit reserve rather
than ordinary storage/audit capacity. Exhausting that reserve prevents further
audited reductions and fails closed for subsequent workspace data operations.
Do not bypass the authority with unaudited grant changes.
Plan operator capacity restoration before exhaustion.

There is no automatic receipt/audit retention job or public maintenance API yet.
Operator retention work must preserve the promised receipt window, lifetime
identities and schemas needed by records, and update logical counters
transactionally. Do not delete retained rows casually to reclaim quota. Back up
and restore the complete list schema and template versions together, separately
from document setup; qualify restore and maintenance procedures for your host.
Audit records actors, actions, revisions and field IDs, not value history.

Focused checks run independently of Office protocol tests:

```sh
dotnet test tests/CellBridge.Lists.Tests/CellBridge.Lists.Tests.csproj
dotnet test tests/CellBridge.Lists.AspNetCore.Tests/CellBridge.Lists.AspNetCore.Tests.csproj
node tools/testing/test-list-example-ui.cjs
dotnet build tests/CellBridge.Lists.PostgreSql.Tests/CellBridge.Lists.PostgreSql.Tests.csproj
aspire start --apphost tests/CellBridge.Lists.PostgreSql.Tests/aspire/apphost.cs --non-interactive
aspire resource lists-tests start --apphost tests/CellBridge.Lists.PostgreSql.Tests/aspire/apphost.cs --non-interactive
aspire describe --apphost tests/CellBridge.Lists.PostgreSql.Tests/aspire/apphost.cs --non-interactive
aspire stop --apphost tests/CellBridge.Lists.PostgreSql.Tests/aspire/apphost.cs --non-interactive
```

The example regression script uses Node 24 and checks retained drafts, exact
numeric values and workspace targeting across delayed or failed requests.
Both Linux and Windows CI run it.

Wait for the explicit `lists-tests` resource to finish before stopping. Its TRX
results are written under ignored `artifacts/lists-tests`; the dedicated AppHost
requires the real database rather than accepting skipped durable checks. Stop
Aspire before rebuilding locked outputs. The
[package-only example](../examples/DocumentLibrary/README.md) consumes the optional
packages through its configured NuGet feed; keep its list setup and runtime
credentials separate.

The libraries remain experimental. Per-record sharing, field confidentiality,
formulas, customer SQL, uniqueness, arbitrary-column relationships, attachments,
bulk import, destructive migrations and full record restoration are not
implemented. High-scale operation and live reporting-client compatibility are not
established by local protocol tests.
