# Compare conflicting Exit behavior with SharePoint SE

This procedure separates server behavior from current Office behavior. A synthetic
request returning Success does not establish that Office sends that request or
requires that result. Keep CellBridge's current behavior until trace evidence
supports a change. A no-op Success must never release another client's lock.

## Independent expectations

[MS-FSSHTTP 3.1.4.3.2](https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttp/2697878c-f39b-48fb-b9e7-e621cf977641)
recommends a lock error for conflicting Exit. Its
[Appendix B footnote 45](https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttp/9ad3bd41-0f08-4f8b-ad2d-a894196a7e2e)
documents different product behavior. Record these predictions before inspecting
traces:

| Setup before a different-schema Exit | Documented SE response | Current CellBridge regression | Required ownership observation |
| --- | --- | --- | --- |
| Exclusive lock held by owner | Success | FileAlreadyLockedOnServer | Original lock refresh succeeds; another exclusive ID cannot acquire |
| Valid shared session with one participant | Success | FileAlreadyLockedOnServer | Owner remains Alone and refreshes; different schema cannot join |
| Valid shared session with two participants | Success | FileAlreadyLockedOnServer | Both remain Coauthoring and refresh; different schema cannot join |

Footnote 45 describes exclusive locks and differing-schema shared sessions with
multiple clients for several SharePoint versions. Its additional one-client
Success behavior names SE and 2019. These are documented server behaviors, not
measurements of the lab's exact patch level or proof of current Office needs.
Upstream S02 TC16 concerns exclusive locks, TC17 one shared participant and
TC18 two participants. See the [adaptation notice](../tests/CellBridge.Interop.Tests/NOTICE-Interop-TestSuites.md).

## Close regression assumptions

`DelayedCloseExitAndSchemaReleaseCannotUnlockANewOwner` orders A's Exit, B's
acquisition and A's stale retries deterministically. It uses coordinator state,
not independent wire decoding, and does not test parallel scheduling or native
Office. Its Exit Conflict assertions may change for documented SE Exit no-op
Success; that does not establish a different SchemaLock Release result. Preserving
B's lease and fencing other acquisitions must not change for either operation.

Historical Word COM build `16.0.20430` save/reopen recordings from 2026-10-02/03
contain Exit followed by an `OnNotSupported` schema-release fallback. Exit
succeeded and the fallback was skipped. The test's standalone Release is a
stronger synthetic probe. Those recordings do not establish conflicting-lock
behavior, a current Office requirement or SE behavior. See the
[scenario provenance and divergences](../tests/CellBridge.Interop.Tests/NOTICE-Interop-TestSuites.md).

## Record versions and access

Use only an explicitly authorized disposable lab and separate test documents.
Record each Office application's executable path, FileVersion, ProductVersion,
bitness, Click-to-Run VersionToReport and UpdateChannel (or MSI/LTSC update
identity), Windows build and UTC time. Record SE's farm BuildVersion from an
authorized administrator's `Get-SPFarm` or Central Administration, installed
CU/KB identity and evidence source. HTTP version headers alone cannot establish
edition or patch level. Record CellBridge commit plus local diff/hash, .NET,
provider, authentication mode and origin.

Record account aliases and which session owns each document/client ID. Compare
the same principal first; separately test authorized distinct accounts. The
synthetic script below uses one authenticated principal. `-ExitClientIdentity Owner` (default) reuses the first owner's ClientID with the conflicting schema;
`-ExitClientIdentity Outsider` sends a new ClientID. Run both variants with fresh
documents and retain their identities in the results. The Owner variant still
uses the same authenticated account, unlike upstream TC17/18's different
account reusing the owner's ClientID. Neither variant establishes distinct-user
SE behavior. Do not launch Office where the operator has other documents open. `office-repeat.ps1` and
`word-smoke.ps1` use CellBridge APIs and cannot be pointed unchanged at SE.
They do not construct these conflicting-lock conditions.

## Run the synthetic server probe

Create a fresh disposable document through the server's supported UI/API, close
Office for that document, and confirm no other participant uses it. Use a fresh
document for each case. Preserve independent downloads and SHA256 before and
after the trial. The probe modifies only locks, never file content. It does not
create or discover documents or infer authorization from a URL.

Prepare a new output path under a private parent directory. Windows ACLs must
already restrict the parent. The script does not change Windows ACLs, proxy,
certificate trust or firewall settings. Raw SOAP may contain URLs and identity
metadata; keep output out of source control.

From an authorized Windows account with existing SE lab access:

```powershell
./tools/testing/compare-conflicting-exit.ps1 `
  -TestDocumentUrl 'https://se-lab.example.test/Shared%20Documents/exit-exclusive.docx' `
  -Case exclusive -ServerProduct SharePointSE `
  -ServerBuild '<verified farm build>' -ServerBuildSource '<farm/admin evidence>' `
  -OutputDirectory 'C:\private-evidence\se-exclusive-01' -TestDocumentConfirmed
```

Repeat with `-Case shared-one` and `-Case shared-two` and fresh documents.
For CellBridge use its test URL, `-ServerProduct CellBridge`, verified candidate
build/diff source and privately supply `CELLBRIDGE_INTEROP_COOKIE`. Default
Windows credentials are used only for SharePointSE. `-AllowLoopbackHttp`
permits an explicit local fixture HTTP origin. Redirects are disabled and TLS
validation remains enabled. Credentials and response headers are not recorded.

Each run saves numbered raw request/response XML and `result.json`. Inspect the
first Exit, repeated Exit and subsequent owner refresh/status and contender
acquisition results. `ownershipChecksCompleted` does not claim SE parity; read
the actual Exit codes. A setup or transport failure is an incomplete run.
Cleanup targets only random IDs generated by that invocation, including
attempted acquisitions whose acknowledgements might have been lost. If cleanup
fails, retain evidence and inspect that lab file through normal administration;
do not issue broad unlocks.

## Run the disposable CellBridge regression traces

The fixture-managed HTTP tests can record their synthetic SOAP bodies locally:

```sh
CELLBRIDGE_STATEFUL_TRACE_DIRECTORY="$PWD/artifacts/private-exit-trace" \
  dotnet test tests/CellBridge.Interop.Tests --filter FullyQualifiedName~StatefulCoauthoringTests
```

Use a new private directory. This fixture owns its disposable test accounts,
documents and PostgreSQL instance; it does not contact SE or launch Office.
The trace directory contains raw request/response XML without authentication
headers. The one-principal synthetic client IDs are not substitutes for the
upstream TC17/18 distinct-account setup. Inspect these local regression results
separately from native-client and SE observations.

## Trace native Office separately

Follow the [capture guide](capture-kit.md) using already approved routing/trust,
or obtain explicit approval before changing certificates, proxies, alternate
access mappings or firewall rules. Compare direct and intercepted baseline
behavior before accepting a trace. Do not target employer production.

For Word, Excel and PowerPoint separately, first capture a fresh open/edit/save/
close/reopen control against each server. Then reproduce all three lock setups
with known test participants. Verify ownership immediately before and after the
conflicting Exit. If a synthetic injector is needed to create stale schema
state, label its requests as injected and keep native requests distinguishable.
Do not attribute injected Exit responses to Office. Do not force expiry or
terminate processes unless separately authorized.

Retain a single UTC timeline for each trial:

1. Exact versions, account aliases, document identity and request IDs, plus
   evidence that the exclusive/shared lock preconditions actually held.
2. Paired SOAP/MTOM request and response bodies with tokens and HTTP status,
   exact Exit ErrorCode, surrounding Join/Refresh/GetCoauthoringStatus/lock
   operations and all subsequent native retries/fallbacks. Capture through the
   final save and close. Record absent native Exit as absent.
3. Visible dialogs, editable/read-only state, saved/unsaved indication and any
   intervention. Derive timings from trace timestamps. Do not infer UI state
   from SOAP success.
4. Independent downloads after every save and reopen, SHA256 and retained edit
   markers. Verify unrelated participants/locks remain and owner refresh/save
   still works, regardless of Exit response code.

Stop and validate capture before stopping its host. Keep raw evidence private;
sanitize identities/content before adding fixtures. Checked-in open/save
fixtures are selected sanitized exchanges and do not establish a complete
conflicting-lock sequence or a verified current SE build.

Change production only when paired traces explain why SE's result matters to
current Office or the intended server compatibility policy. Add regressions
proving no-op Success preserves unrelated locks, coauthor membership and owner
refresh/save ability. If native Office does not emit the conflicting request,
record that limitation and reason separately about server parity. Do not change
implementation merely to match an old test assertion.
