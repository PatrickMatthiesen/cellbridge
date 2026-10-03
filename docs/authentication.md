# Authentication and document permissions

The sample host uses operator-provisioned ASP.NET Core Identity accounts in PostgreSQL. Browser and Office requests use a Secure, HttpOnly cookie. Documents default to denying access; each has an owner and explicit subject grants. The owner can read and write, and Write implies Read. Library creation is a separate account permission.

Server authentication and authorization tests do not establish desktop Office compatibility. The sample implements the [MS-OFBA challenge](https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-ofba/c2c4baef-c611-4e7b-9a4c-d009e678e3d2). A manual Windows Word trial on 2026-10-03 demonstrated sign-in, two writer saves, reader read-only open and Write revocation during an open editing session. Office required explicit forms-sign-in host approval. Office build/channel and authenticated fresh-process reopen remain to be recorded. See [interoperability coverage](interoperability.md) for the scope of that evidence.

## First setup

Aspire migrates the document schema to version 3 and creates authentication schema version 1 before starting web instances. Existing documents without authenticated ownership (storage schema version 1 or main’s version 2) require `CELLBRIDGE_LEGACY_OWNER`, a stable subject such as `local:operator`. Stop all writers before that migration. It assigns every historical snapshot to that owner, preserves file graphs, versions and receipts, clears anonymous sessions and leases, and leaves legacy authorship unknown. Unowned legacy receipts cannot return an authenticated successful retry.

Start Aspire with `CELLBRIDGE_SKIP_SHAREPOINT_CAPTURE=1` when capture is unnecessary. Imports are skipped until an import owner is configured. Retrieve `ConnectionStrings__cellbridge` privately from the web resource's Aspire environment and supply it to the operator tool. Do not copy the connection string into logs or committed configuration.

```sh
# Password is read from stdin, not passed as a command argument.
dotnet run --project tools/CellBridge.Admin -- create-user --id operator --login operator --display-name Operator --can-create true
dotnet run --project tools/CellBridge.Admin -- create-user --login reader --display-name Reader
```

Use a password satisfying Identity's policy: at least 12 characters with uppercase, lowercase, digit and nonalphanumeric characters. There is no public registration. Five failed attempts lock the account for 15 minutes.

After provisioning, stop Aspire and start it with `CELLBRIDGE_IMPORT_OWNER=local:operator`. An import owner must be a provisioned account. Imports create missing documents only, record `system:imports` as creator and initial modifier, and never overwrite saved content. An existing migration owner can be provisioned with the matching `--id` after migration. Configure `CELLBRIDGE_PUBLIC_ORIGIN` as the HTTPS origin reachable and trusted by Windows. Its default is `https://localhost:7292`.

Open `https://localhost:7292/library`, sign in, and create or download an allowed document. The web host proxies the library to the internal demo. Web instances and demo share the PostgreSQL account store, Data Protection keys and cookie settings. Demo sends only the current request's authentication cookies to the fixed web endpoint, including chunked cookies. It forwards the paired antiforgery cookie and form token for creation. It has no service account or shared cookie jar.

## Manage access

Obtain the resource GUID from an authorized FSSHTTP response or an operator database query. The catalog intentionally exposes no hidden document IDs or counts. Commands require operator database credentials; protocol and library users cannot grant access.

```sh
dotnet run --project tools/CellBridge.Admin -- grant --resource-id GUID --subject local:USER_ID --access read
dotnet run --project tools/CellBridge.Admin -- grant --resource-id GUID --subject local:USER_ID --access write
dotnet run --project tools/CellBridge.Admin -- grant --resource-id GUID --subject local:USER_ID --access none
dotnet run --project tools/CellBridge.Admin -- set-owner --resource-id GUID --subject local:USER_ID
dotnet run --project tools/CellBridge.Admin -- set-user --subject local:USER_ID --enabled false
dotnet run --project tools/CellBridge.Admin -- set-user --subject local:USER_ID --can-create false
```

Owners retain Read and Write until ownership transfers. Transfer does not create a grant for the former owner. Account IDs are stable across display-name changes. Account updates change the Identity security stamp. Existing cookies are rejected at the next one-minute stamp check in each host. Document grants are read from authoritative document state on each operation, independently of cookies.

Revoking Write atomically downgrades editor sessions to readers, advances editor-table knowledge, and removes that subject's editing leases. Revoking Read also removes its sessions. Changes use the same document transaction as publication. A save rechecks current Write permission and lease ownership before publishing staged bytes. Receipts require their original writer and current Write permission on every retry path.

## Protocol behavior

Authentication runs before SOAP or MTOM parsing. Anonymous Office discovery and file requests receive HTTP 403 with fixed MS-OFBA login and completion URLs. JSON APIs return 401. Browser library requests redirect to login. Authenticated denials do not trigger another login challenge.

| Operation | Required permission |
| --- | --- |
| Catalog, GET, HEAD, metadata, versions, QueryChanges, lock status | Read |
| QueryAccess | Reports Read and Write separately |
| PutChanges, acquire or refresh editing locks, JoinCoauthoring | Write |
| Join or refresh EditorsTable as reader | Read |
| Join or refresh EditorsTable as editor | Write |
| Release own lock or leave own session | Read and matching authenticated owner |
| Create document | Account creation permission |

SOAP denials use `FileUnauthorizedAccess`. Binary denials use HRESULT `E_ACCESSDENIED`, including each denied QueryAccess field. Responses to callers without Read omit resource IDs, canonical document URLs and GetFileProps metadata. Each executable SOAP dependency is independently authorized. Matching ClientID or lock GUIDs cannot impersonate another subject. Office saves omitting ClientID can use a lease only when the authenticated subject owns it.

WhoAmI reports the caller. Author metadata reports the persisted creator; ModifiedBy reports the last successful content writer. Readers and session joins never replace authorship. Unknown legacy authors stay unknown.

Login, logout and document creation require antiforgery validation. SOAP endpoints accept XML or MTOM and reject browser simple content types and cross-origin Origin headers. Health checks remain anonymous. Account passwords, cookies, tokens and connection strings must not enter shared capture evidence. Database access grants access to cookie protection keys, so restrict and protect that database and its backups.

## Reusable hosts

`CellBridge.AspNetCore` owns no authentication provider. Register authentication middleware and map a validated principal to an authority-qualified `cellbridge:subject`, with optional `cellbridge:display-name` and `cellbridge:create=true` claims. Arbitrary NameIdentifier, email, ClientID and forwarded user headers are never used as identity. Middleware must authenticate before authorization and endpoint execution.

Request-facing service methods require an explicit `CellBridgeActor`. `CreateAsync` sets that actor as owner and creator. Trusted import code supplies an explicit owner and importer to `ImportAsync`. `ResolveAsync` still honors ResourceID precedence. A custom `ICellBridgeAccessEvaluator` can restrict the stored grants; it must be deterministic and perform no I/O inside document transactions. Catalog filtering uses stored grants before applying that evaluator. Storage and binary serialization remain independent of ASP.NET identity types.

The sample authentication library is an executable-project dependency, not part of the nine reusable NuGet packages. See the [authenticated package consumer](../examples/NuGetConsumer/README.md).

## Desktop validation

The [Tailscale setup](automated-testing.md#connect-the-windows-laptop-through-tailscale)
uses a private HTTPS listener, an isolated Aspire run and a named persistent
database volume. Browser sign-in verifies the web form and library. To test
Office's own sign-in, start in a Windows profile without cached credentials for
the test origin and launch the document directly:

```powershell
Start-Process 'ms-word:ofe|u|https://dev-machine.example.ts.net:8444/shared/ofba-laptop.docx'
```

Office may block the MS-OFBA prompt with "Office has blocked this content because
it uses a sign-in method that may be insecure." This happened in the laptop
trial on 2026-10-03. The server received Word's `OPTIONS /shared/` and returned
the HTTP 403 challenge; that attempt had no subsequent login request.
After explicit host approval, the user signed in and completed the writer,
reader and revocation checks described above.

For a controlled trial, open Word's File > Options > Trust Center > Trust Center
Settings > Form-based Sign-in. Select "Ask me what to do for each host", retry,
and allow the exact test host when prompted. Record this explicit trust decision
as a prerequisite of the result. Organization policy may prevent changing the
setting. Use host-specific approval rather than allowing every sign-in prompt.
This setting is separate from macro and Protected View settings.

Word must receive the HTTP 403 challenge, show the CellBridge sign-in form,
reach `/auth/complete` and retry with the authentication cookie. Record whether
Word shows that dialog; opening a document after browser login alone cannot
establish this exchange. Save two unique text markers, download the server
document independently and inspect both markers, close Word completely, then
reopen the same remote URL. A marker visible only in Word can be cached locally.
Preserve the matching file-partition PutChanges captures as save evidence.

The manual trial observed distinct writer and reader identities, successful
writer publication, reader editing denial and a rejected save after Write
revocation. Remaining checks include recording the Office build/channel,
confirming a fresh Word process for authenticated reopen, account disablement
and comparison with denied SharePoint reference traffic. Server tests already
exercise the cookie, antiforgery, authorization, ownership and publication
rules. An AD domain is unnecessary for this local-account MS-OFBA trial.

Use separate Windows profiles or machines for two accounts because Office may share cached credentials. Record the Office build and the browser/Office authentication exchange. Verify authenticated OPTIONS, GET, HEAD and both SOAP route shapes; distinct WhoAmI results; a reader denied locks and saves; a writer save followed by a fresh-process reopen; lease impersonation denied; and permission revocation before publication. Repeat against the Windows-resolvable remote HTTPS origin if remote editing is required. Sanitize cookies and credentials before retaining fixtures. Do not report authenticated desktop support or two-desktop coauthoring from synthetic HTTP tests alone.
