# Document paths and migration

CellBridge's standard endpoints serve flat and nested paths beneath `/shared`.
For example, `/shared/team/folder/Project%20plan.docx` and
`/shared/another-team/Project%20plan.docx` identify separate documents.
GET and HEAD download the current file. Authenticated OPTIONS discovery works
on documents and directories, with or without a trailing slash. The root
`/_vti_bin/cellstorage.svc` endpoint and document-relative endpoints such as
`/shared/team/folder/Project%20plan.docx/_vti_bin/cellstorage.svc` accept the
existing SOAP/MTOM requests and endpoint suffixes. Directory discovery does not
list documents or disclose their permissions.

Resource IDs remain independent of paths. Downloads and protocol operations
evaluate the resolved document's current authorization policy. When
`UseResourceID` is true and the request supplies a `ResourceID`, CellBridge
looks up that ID only. An explicitly empty, malformed or unknown ID does not
fall back to the URL. If the optional `ResourceID` attribute is omitted, the
required URL identifies the initial request and a successful response returns
the stable ID. A URL miss still does not create a document.

## Move an existing resource

Call the document service instead of importing another document:

```csharp
var committed = await documents.MoveDocumentAsync(
    resourceId, "/shared/team/Project plan.docx", actor,
    cancellationToken: cancellationToken);
// Persist committed.Path and committed.StateVersion in host metadata.
```

The destination is an application-rooted, decoded path, not an HTTPS URL. Move
does not decode it again. Spaces, Unicode, `#` and literal percent characters
remain literal, including a name such as `Budget%20plan.docx`. Escape each
segment once when building its public URL, so that name becomes
`Budget%2520plan.docx`. CreateAsync and ImportAsync retain their existing
escaped-path input contract.

Move rejects empty or dot segments, a trailing slash, backslashes, control
characters, segments longer than 255 characters and the filename characters
`/`, `:`, `?`, `*`, `"`, `<`, `>` and `|`. The route segments `_vti_bin`,
`_vti_inf.html` and `_cellbridge` are reserved, ignoring case. Move preserves
Unicode spelling; path conflicts use the provider's existing case-insensitive
PathKey rules. URL lookup decodes once and rejects encoded separators,
malformed percent escapes and traversal. A literal `%2F` name is supported
through `%252F`; an encoded slash `%2F` does not create another directory.

The provider checks permissions, locks and namespace conflicts atomically.
Move returns the committed DocumentState and preserves its ResourceId,
lifecycle generation, content, content version, revisions, permissions,
custom authorization-policy binding and coordination state. RenameDocumentAsync
and Office's protocol Rename still change only the basename within the current
directory. Moving to the same exact path checks authorization and locks and
returns the current state without incrementing StateVersion or adding a
reservation. A case-only spelling change updates the path. Moving back to a
path reserved by this same resource is allowed.

Invalid paths raise ArgumentException, denied access raises
UnauthorizedAccessException, missing resources raise KeyNotFoundException,
lock failures raise DocumentOperationLockException, and occupied or foreign
retired paths raise InvalidOperationException. A provider without atomic rename
raises NotSupportedException. Reservation limits can raise
StorageQuotaExceededException. Failed moves leave the committed document intact.

## Old links and open Office sessions

There are no aliases or redirects. After rename or move, the old path-only GET
and HEAD links return 404, and path-only SOAP lookup fails. A request to an old
document-relative cellstorage endpoint can still resolve the unchanged resource
ID. It enforces the resource's current permissions and returns its current URL.
Unknown IDs cannot use the stale URL to target another document.

Tested Word, Excel and PowerPoint sessions continued saving by resource ID after
a move, as recorded below. This does not guarantee that every already-open Office
session survives a move.
Office may issue another request using only the old path. Such a client must
reopen the document at its current URL. Resource-ID requests continuing after a
move have both automated protocol coverage and the bounded live evidence below.

Retired paths remain reserved against use by a different resource, including
after soft deletion. A host cannot rename `a.docx` to `b.docx` and then give
`a.docx` to a new document. Existing durable retired-path reservations remain
effective without a schema migration. Hosts that need name reuse should choose
an additional stable namespace in their paths and explain these restrictions
to users. Do not reuse a reserved URL to bypass the protection.

## Host responsibilities

The host allocates workspace directories, authorizes access to the destination
namespace and handles naming conflicts. A path move does not transfer workspace
membership or change permissions. Manage an actual workspace transfer separately.

Store the resource ID as the stable identity. CellBridge state and a host's
metadata database do not share a transaction. A move may commit before the host
updates its metadata or crashes. Read `Provider.State.FindByResourceIdAsync`
to recover the authoritative Path and StateVersion, then reconcile the host's
stored URL and display name. Retrying the completed move to the same path is
safe. Reconcile by ID before retrying a stale operation after another rename.
Hosts must also reconcile changes made through Office's protocol Rename; updating
metadata only after their own MoveDocumentAsync calls misses those changes.
Polling authoritative state is sufficient; no rename notification is required.

For an application mounted at a PathBase, storage paths remain application
relative. The HTTP endpoints strip the mount prefix during SOAP lookup and add
it to discovery, document and historical revision URLs. Direct processor users
can pass the decoded `applicationPathBase` argument to ExecuteAsync while keeping
publicOrigin as the scheme and authority. Configure forwarded headers or the
Office forms authentication PublicOrigin for the public HTTPS origin. Existing
Office login completion URLs retain their mounted completion-path contract.

## Validation evidence and limits

Endpoint tests cover routing, escaping, authorization and stale URL behavior.
Storage tests cover atomic moves and PostgreSQL persistence, including provider
recreation and permission revocation. Desktop Word, Excel and PowerPoint checks
have exercised saves by resource ID after a move. They used one authenticated
user. Normal-close behavior, live permission revocation during an open session,
other Office builds and mounted applications remain unqualified. Follow the
[desktop validation guide](demo-library.md) when qualifying another host or
client configuration.

When `UseResourceID="true"` arrives without a `ResourceID`, the URL identifies
the initial request and the successful response returns the stable ID, following
[MS-FSSHTTP section 2.2.3.2](https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttp/91fb9fd5-ef2d-4484-b57c-f0aedf7d0f00).
Explicit empty, malformed and unknown IDs still fail without URL fallback.
