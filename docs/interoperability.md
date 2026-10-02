# Interoperability and test coverage

CellBridge is an experimental protocol implementation. Supported package creation,
protocol replay and desktop remote editing are different capabilities. Use this
page to choose the validation needed for a change.

## Client coverage

| Scenario | Coverage |
| --- | --- |
| Word remote open, two saves and fresh-process reopen | Verified on Windows with Word `16.0.20430`, using Tailscale HTTPS and PostgreSQL state/content on 2026-10-02. Both file-partition PutChanges responses and final downloaded content were verified. |
| Word and Excel captured save sequences | Automated HTTP replay, graph materialization, retry and download checks |
| Excel remote saving | Demonstrated before the storage-provider migration; repeat desktop validation for the durable host before claiming current client coverage |
| PowerPoint package creation and local open/save/reopen | Package validation; remote saving remains unverified |
| Two service instances using one database | Automated identity, shared lease and protocol routing checks |
| Two desktop clients coauthoring one document | Unverified |
| Distinct authenticated Office users | Unimplemented; the sample returns a shared identity |
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

On Windows, CI also runs the Office Inspectors adapter. PowerShell tests with
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
