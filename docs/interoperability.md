# Interoperability and test coverage

CellBridge is an experimental protocol implementation. Supported package creation,
protocol replay and desktop remote editing are different capabilities. Use this
page to choose the validation needed for a change.

## Client coverage

| Scenario | Coverage |
| --- | --- |
| Anonymous Word remote open, two saves and fresh-process reopen | Verified on Windows with Word `16.0.20430`, using Tailscale HTTPS and PostgreSQL state/content on 2026-10-02. Both file-partition PutChanges responses and final downloaded content were verified. |
| Anonymous Word remote open, ten saves and fresh-process reopen | Verified on 2026-10-03 with Word `16.0.20430`, Tailscale HTTPS and PostgreSQL state/content. Ten accepted file-partition PutChanges responses matched the client's per-edit ETags. All 17 captured binary responses passed the independent Office Inspectors parser. Final server download matched the client's SHA-256 and contained all ten edit markers. |
| Authenticated Word sign-in, save and reader open | Demonstrated manually on one Windows client through Tailscale HTTPS and PostgreSQL on 2026-10-03. MS-OFBA required explicit Office host approval. Two writer file-partition saves and independently downloaded server bytes were verified; the reader opened the saved content read-only. Office build/channel and fresh-process reopen remain to be recorded. |
| Write revocation while Word remains open | Demonstrated in the same authenticated trial. The next binary PutChanges was rejected, Word displayed Upload Failed, and an independent download retained the identical bytes and version. |
| Word and Excel captured save sequences | Automated HTTP replay, graph materialization, retry and download checks |
| Excel remote saving | Demonstrated before the storage-provider migration; repeat desktop validation for the durable host before claiming current client coverage |
| PowerPoint package creation and local open/save/reopen | Package validation; remote saving remains unverified |
| Two service instances using one database | Automated identity, shared lease and protocol routing checks |
| Two desktop clients coauthoring one document | Unverified |
| Distinct authenticated Office users | Implemented with local Identity accounts. Sequential writer and reader requests returned distinct WhoAmI identities in the manual trial; simultaneous use by two desktops remains unverified. |
| Large files and production concurrency | Unqualified; the synthetic benchmark is a measurement tool, not a throughput guarantee |

The Word check observes server bytes independently of Office's cache and requires
a new Word process for reopen. Its server verifier requires a successful binary
file save, not just HTTP or SOAP success. See [automated testing](automated-testing.md)
for reproduction and evidence handling.

## Protocol limits

Supported file-partition saves retain graph identities and validate coherency.
Partial, multi-request and non-file partition uploads return explicit errors.
Repeated binary QueryChanges in one request is unsupported because the response
carries one data package. Full knowledge/filter-based incremental synchronization
and application-specific metadata remain incomplete. No SharePoint list or search
API is provided.

File reconstruction uses the selected revision's object-group references. General
ancestor-revision object lookup and OneNote notebook/page synchronization are not
implemented. Buffered and streaming materialization are checked against the same
reviewed save captures; this does not add desktop-client coverage.

A successful protocol lock transition or editors-table response does not establish
two-desktop coauthoring. Provider durability does not add user authorization.
The consuming host owns its access controls.

## Automated checks

The [test runner](../tools/testing/run.py) builds the main and demo solutions,
starts disposable PostgreSQL and two independent web hosts through Aspire, and
runs protocol, provider, replay, demo and capture checks. The disposable run does
not mount the development document volume.

Provider tests cover save receipts, identical retry, stale retry, concurrent
publication, failed staging, unknown commit acknowledgement, lease expiry after
lock waiting, and process termination before and after publication. Tests also
check immutable content reads, quota and corruption failures. These process tests
do not simulate hardware power loss or asynchronous PostgreSQL failover.

Budget tests also cover cross-provider admission races, deduplicated charging,
bounded metadata history and quiescent orphan collection. Query tests reconstruct
a saved file from prior knowledge plus returned elements, check filtered knowledge
and whole-cell rounding, and round-trip server knowledge through the independent
Microsoft client over HTTP. These checks do not establish Office recovery after
graph eviction; the host does not evict protocol graph or receipt identities.

CI runs the in-repo Office Inspectors parser on Linux and Windows. Its grammar is
separate from the server serializer, and the suite checks reviewed SharePoint
fixtures and generated responses without an external checkout. See
[the parser guide](office-inspectors.md). PowerShell tests with
managed Word stand-ins check wrapper behavior, ownership and cleanup logic only.
Actual Word COM calls require the interactive Windows desktop test.

## Fixtures and generated evidence

Keep only fixtures used by automated tests in `testdata/`. Their README files
record provenance, sanitization and any rebasing needed for a fresh test document.
Sanitized fixtures preserve protocol structure, but do not authenticate the
original capture or establish a current desktop result.

Raw SOAP/MTOM, binary traffic, saved Office files, test reports and benchmark JSON
belong in ignored local output or CI artifacts. Review document text, compressed
editor records and embedded Office metadata before preparing a public fixture.
Removing SOAP URLs alone does not sanitize binary content.

Use [the capture guide](capture-kit.md) for SharePoint comparison. Failed client
runs can reveal a response shape that local parsers accept but Office rejects;
retain their evidence privately rather than turning run journals into user docs.
