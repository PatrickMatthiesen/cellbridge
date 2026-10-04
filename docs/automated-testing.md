# Automated protocol, storage and desktop Office checks

We can simulate recorded Office exchanges and concurrent saves on Linux. Real
Word still makes client decisions that a replay cannot exercise. Use both, and
keep their results separate.

## Repeatable local checks

Install .NET 10, the compatible Aspire CLI, Docker, and the capture test
dependencies. From the repository root:

```sh
python3 -m venv tools/capture/.venv
tools/capture/.venv/bin/python -m pip install -r tools/capture/requirements-dev.txt
python3 tools/testing/run.py
```

Stop a running Aspire app before building. If a SharePoint capture is active,
stop and validate it as described in [the capture guide](capture-kit.md) first.
The runner refuses to interrupt an already running app.

The runner builds Release binaries, starts Aspire with a disposable PostgreSQL
container, explicitly starts the .NET test-account seed job and peer host, waits
for `web`, `web-peer` and `demo`, creates its three test documents through the
authenticated API, and runs:

- Binary and SOAP unit tests.
- Explicit operator import and repeat-import checks against the disposable store.
- Provider tests against real PostgreSQL, including process termination before
  and after publication, retries, concurrent updates and lease timing.
- HTTP replay of captured Word/Excel saves, Word open exchanges, shared locks
  across two hosts, and HTTPS origin forwarding through a loopback proxy.
- Demo, capture-tool and Office evidence checks.

It writes TRX files, a `summary.json`, and opt-in wire evidence beneath
`artifacts/testing/<timestamp>`, then stops its AppHost. It never mounts the
developer's `cellbridge-storage-data` volume. The in-repo .NET 10 Office Inspectors
parser checks run on Linux and Windows, including both CI jobs. They require no
external checkout or desktop runtime. See [the parser guide](office-inspectors.md).
All local raw capture artifacts stay outside Git.

To verify the packed libraries, run `python3 tools/verify_packages.py` with
Aspire stopped. It builds the minimal NuGet consumer example and runs the
[package consumption tests](../tests/CellBridge.Packages.Tests/README.md) against
a fresh local package feed/cache. The example contains application setup;
test-server, SOAP probes and provider conformance checks live in the test project.

## AppHost configuration

AppHost inputs use standard configuration. Values under `Parameters` appear as
Aspire parameter resources; environment overrides use names such as
`Parameters__PublicOrigin`. An ignored `aspire/apphost.settings.json` can keep
non-secret local values between runs. Environment values override that file.
Store secrets through Aspire's secret configuration rather than in the file.

| Parameter | Default | Purpose |
| --- | --- | --- |
| `StorageVolume` | `cellbridge-storage-data` | Durable development database volume; omitted in disposable test mode |
| `PublicOrigin` | `https://localhost:7292` | HTTPS origin reachable by Office and browsers |
| `WireCaptureDirectory` | Empty | Optional absolute server capture directory |
| `TestPassword` | Required in test mode | Secret password supplied to the explicitly started .NET seed resource |

`Testing__Enabled=true` selects the disposable test model: no durable volume or
SharePoint capture resource. Its documents belong to the signed-in integration user.
The runner supplies a temporary secret and starts its required resources.
`AppHost__PeerEnabled=true` adds an explicitly started second host in ordinary
development. `Testing__RunStorageTests=true` adds an explicitly started storage
test command. These model choices use configuration rather than parameter
resources because they determine which resources exist. The SharePoint capture
proxy is declared in ordinary development and starts only when requested; see
[capture instructions](capture-kit.md).

Earlier `CELLBRIDGE_*` AppHost switches and parameter names are replaced by the
configuration above. `LegacyOwner` and `ImportOwner` are removed from the AppHost;
Legacy ownership upgrades are unsupported; recreate outdated development
databases. Use the explicit [import command](authentication.md) to load files.
Update local launch scripts before restarting. Disposable
runs clear inherited AppHost settings and ignore the local settings file.

## Performance measurements

```sh
python3 tools/testing/run.py --performance --sizes 1,10 --clients 1,8 --iterations 5
```

This adds PostgreSQL content and filesystem content measurements, both with
PostgreSQL document state. Each client edits its own document. Reports include
save/download p50, p95 and p99, throughput, allocation counts, retained graph
counts and process peak memory. Accepted uploads and downloads must succeed.
The temporary filesystem content directory is removed after each run.
Each durable benchmark creates and drops its own database. The supplied PostgreSQL
role needs `CREATE DATABASE`; benchmark documents and content backends never mix
with host data. Usage totals are cumulative across size/concurrency scenarios in
one benchmark invocation.

These measurements use synthetic complete DOCX graphs in the document service.
They do not include HTTP, desktop rendering, small Office deltas or network
latency. Startup and initial document creation contribute to overall throughput.
Iterations reuse the same package bytes with fresh protocol graphs, so content
deduplication is part of the measured path. These results do not measure a stream
of unique file-content writes. Peak memory is cumulative within each benchmark
process. Small samples cannot establish reliable p99 latency.

The benchmark also supports `changed-full`, `delta`, `delta-chain` and `captured`
workloads. `delta` reuses file objects; `delta-chain` additionally links every
previous revision through the immutable current index. `captured` replays the two
reviewed save fixtures and requires exactly two iterations. `--seed` controls
synthetic file bytes and save identities, `--retry` checks accepted retries, and
`--query-every` samples empty/prior/current client knowledge. `--sizes 0` uses a
small deterministic DOCX. Example service-level runs after a Release build:

```sh
dotnet tools/CellBridge.Storage.Benchmark/bin/Release/net10.0/CellBridge.Storage.Benchmark.dll --content memory --sizes 0 --clients 1 --iterations 1000 --workload full --retry --output artifacts/full.json
dotnet tools/CellBridge.Storage.Benchmark/bin/Release/net10.0/CellBridge.Storage.Benchmark.dll --content memory --sizes 0 --clients 1 --iterations 1000 --workload delta-chain --retry --output artifacts/delta-chain.json
dotnet tools/CellBridge.Storage.Benchmark/bin/Release/net10.0/CellBridge.Storage.Benchmark.dll --content memory --sizes 0 --clients 1 --iterations 2 --workload captured --retry --output artifacts/captured.json
Storage__MaxGraphElements=30 dotnet tools/CellBridge.Storage.Benchmark/bin/Release/net10.0/CellBridge.Storage.Benchmark.dll --content memory --sizes 0 --clients 1 --iterations 100 --allow-quota --output artifacts/quota.json
```

Budget settings use `Storage__...` environment variables, matching the sample.
Quota runs verify that the rejected save preserves content and both versions.
Ordinary runs fail on any rejection. Reports separate retained history from
required closure, include analysis blockers, and sample live managed memory.
Allocation totals include workload setup, queries and retention diagnostics;
forced collections affect throughput. These are diagnostic growth measurements,
not isolated save allocations or proof that memory reaches a plateau.

On 2026-10-03, Linux/.NET 10.0.12 with four logical processors completed these
offline workloads with a constant 2,197-byte file and identical retries:

| Workload | Saves | Retained elements, first → last | Required elements, first → last |
| --- | --- | --- | --- |
| Complete graphs | 1,000 | 10 → 5,005 | 5 → 5 |
| Linked delta revisions | 1,000 | 9 → 4,005 | 6 → 1,005 |

The linked chain's required bytes grew from 2,911 to 154,759; retained payloads
reached 30,897,259 bytes. Automatic compaction therefore cannot guarantee ongoing
bounded editing for every supported graph. After typed change-frequency parsing
and scoped reference validation, both captured save graphs pass retention
analysis and require 20 elements each. A regression test compacts both graphs,
checks exact file bytes, and protects the previous index explicitly. The host
still keeps graph history until base-admission and receipt-retention policies are
qualified with live Office.

After separate mapping knowledge and server serial allocation, the second
captured save's empty/prior/current-knowledge binary queries were
33,502 / 12,391 / 237 bytes. The updated 128-save linked-chain check still grew
its required closure from 6 to 133 elements. These offline checks validate
query reduction while the
[retention gates](storage-providers.md#limits-and-qualification) remain open.

The two-host runner covers captured HTTP/MTOM saves, independent Microsoft
knowledge queries, atomic quota races, metadata retention and quiescent cleanup.
Packed-consumer tests cover known-length and streaming request rejection. On
2026-10-03, desktop Word completed ten saves and a fresh-process reopen against
the changed knowledge behavior. Each captured file save matched the client's
ETag; all 17 binary responses passed the independent Office Inspectors parser,
and final server bytes matched the client's SHA-256. Graph pruning and receipt
eviction remained disabled during this check.

The `Storage performance report` GitHub workflow supports manual runs and a
weekly schedule after merging. Each provider job gets a fresh PostgreSQL service
and uploads its JSON result. It reports performance rather than enforcing a
hardware-independent latency threshold. Compare repeated runs on the same
machine before setting a regression budget. Large graph processing still makes
many copies; see [storage limits](storage-providers.md#limits-and-qualification).

## Connect the Windows laptop through Tailscale

The client requires Windows, desktop Word and Tailscale. Install and sign in to
Tailscale on the Linux dev machine using the same tailnet. This step requires
the user's account login. Enable tailnet HTTPS if Serve prompts for it.
[Tailscale Serve](https://tailscale.com/docs/reference/tailscale-cli/serve)
provides a valid HTTPS certificate and limits access to the tailnet.

On the dev machine, inspect existing Serve routes before assigning ports:

```sh
tailscale status
tailscale serve status
```

Use the machine's full `*.ts.net` DNS name, for example
`dev-machine.example.ts.net`. Choose a free HTTPS port. Start Aspire with that
public origin and server wire capture enabled. A separate persistent volume
keeps this authentication trial independent of other checkouts. Do not set
`Testing__Enabled=true` for a laptop session.
Choose a different volume name for each concurrent trial. Aspire's `--isolated`
randomizes ports but does not separate explicitly named Docker volumes.

```sh
export Parameters__PublicOrigin='https://dev-machine.example.ts.net:8444'
export Parameters__StorageVolume=cellbridge-ofba-storage-data
export Parameters__WireCaptureDirectory="$PWD/artifacts/office-wire"
aspire start --isolated --apphost aspire/apphost.cs --non-interactive
python3 tools/testing/tailscale.py --https-port 8444
tailscale serve status
```

The exported capture setting lasts only for that shell session. To keep capture
enabled after a restart, create the ignored `aspire/apphost.settings.json` with an
absolute directory on the dev machine:

```json
{
  "Parameters": {
    "WireCaptureDirectory": "/absolute/path/to/CellBridge/artifacts/office-wire"
  }
}
```

`Parameters__WireCaptureDirectory` overrides this setting. Disposable automated runs ignore
the local file. Restart Aspire after changing capture configuration, then check
that a SOAP request produces request, response and summary files before starting
the desktop test. Remove the setting when the capture session is finished.

The library is `https://dev-machine.example.ts.net:8444/library`; documents,
authentication and SOAP use that same origin. The web host proxies the library
to the internal demo. The helper discovers Aspire's current HTTP port, waits
for both hosts, checks the configured authentication origin and refuses to
overwrite an unrelated Serve listener. Run it again after restarting isolated
Aspire because its local ports change. Serve may require `sudo`; the helper
tries noninteractive sudo when the daemon denies configuration access.

Keep document and SOAP paths at their original roots. The web and demo hosts
accept forwarded HTTPS/Host information from trusted loopback proxies using
ASP.NET Core's defaults. An integration test verifies the SOAP origin through
Aspire's HTTP proxy. Tailscale identity headers do not authenticate CellBridge
users. Provision local accounts and document grants using the
[authentication setup](authentication.md). Use an operator-owned document with
Write granted to a writer and Read granted to a reader so revocation can be
tested. Owners always retain Write.

From the laptop, verify that this succeeds with the normal Windows trust store:

```powershell
Invoke-RestMethod https://dev-machine.example.ts.net:8444/health
```

## Run real desktop Word

Copy `tools/testing/word-smoke.ps1` to the laptop or use a checkout. Open Word
once to finish its account and first-run setup, then close Word. Run the check
from a logged-in Windows desktop:

The current host requires authentication. Privately supply
`CELLBRIDGE_INTEROP_COOKIE` and `CELLBRIDGE_INTEROP_CSRF` from a signed-in creator
account for the script's HTTP creation and verification calls. Include the
paired antiforgery cookie and a token issued after sign-in. These credentials
do not sign desktop Office in. First validate Office's own MS-OFBA exchange
against the same origin using the [authentication checklist](authentication.md).
Until that succeeds, this unattended script is not evidence of authenticated
Office editing. Do not put cookie or token values in reports or command arguments.

```powershell
powershell.exe -NoProfile -STA -ExecutionPolicy RemoteSigned `
  -File .\tools\testing\word-smoke.ps1 `
  -BaseUrl https://dev-machine.example.ts.net:8444 `
  -OutputDirectory .\artifacts\office-laptop-01
```

The script first checks local Word startup, temporary document close and quit
through real COM. A failure here stops before creating a remote document. The
JSON report records the current phase and whether this preflight passed. The
tests with managed Word stand-ins check script logic only; they cannot validate
native COM calls.

The script creates a fresh blank DOCX through the demo API and downloads an
initial snapshot. Desktop Word opens the remote URL and performs the requested
number of edits and saves in the same session, defaulting to two. After each save,
the script independently
downloads the server's DOCX and requires all markers in `word/document.xml` and
a changed ETag. It retains each saved snapshot with a SHA-256 hash. A fresh Word
process then reopens the document and checks all edits. The report records Word
version/build and open, save, verification and reopen timings. Use `-Edits` to
choose between one and ten edits.

The parent process enforces a five-minute timeout and records the PID of the Word
instance it owns. It refuses to start with an existing Word process and does not
kill other Word processes. It preserves normal Office trust settings and restores
the background-save preference. Relative output paths follow PowerShell's current
location. The Windows PowerShell worker uses `RemoteSigned` for its own process;
the script does not change persistent execution policies. Worker errors appear
in the console and remain in the evidence directory. Unblock a downloaded script
with `Unblock-File` before running it. Keep the desktop visible for first-run or
account dialogs. Microsoft documents the interactive desktop assumptions of
[Office automation](https://learn.microsoft.com/en-us/office/client-developer/integration/considerations-unattended-automation-office-microsoft-365-for-unattended-rpa).

Copy `word-result.json` back to the dev machine, then require a successful binary
file-partition save in the matching server capture:

```sh
python3 tools/testing/verify_office.py \
  --capture artifacts/office-wire \
  --word-result artifacts/office-laptop-01/word-result.json \
  --output artifacts/office-laptop-01/verified.json
```

The verifier combines the laptop success flags with at least one matching
successful captured file-content PutChanges. It does not independently correlate
each edit with a separate capture or fetch server bytes to compare hashes. Retain
the per-edit snapshots and inspect each capture when qualifying multiple saves.
Only a zero exit code from this final command is a passing desktop test. A local
Word save, HTTP 200, failed PutChanges or metadata-partition write cannot pass.
The server emits `.summary.json` beside the raw request/response bytes only when
capture is enabled. Unique document names keep separate runs' evidence apart.

The Word script uses `Documents.Open` on the HTTPS URL. Office may choose a
different transport than the demo's `ms-word:ofe|u|` link. The capture requirement
detects that difference; if the script fails while a manual demo open works,
capture both flows before changing the protocol. The current script has passed
a ten-edit, fresh-process reopen test with Word `16.0.20430` and PostgreSQL. See
[interoperability coverage](interoperability.md).

For cleanup, disable only the Serve listener added for this session. Leave the
app running until the laptop session finishes. Stopping preserves the named
PostgreSQL volume, documents and accounts:

```sh
tailscale serve --https=8444 off
aspire stop --apphost aspire/apphost.cs --non-interactive
```

## Turn a desktop run into replay fixtures

Keep the laptop output directory and matching server captures together. The
useful evidence is the Word build, exact edit steps, initial DOCX, both saved
DOCX snapshots, raw SOAP/MTOM request and response bodies, their content types,
and the observed outcome. Preserve failed runs too; an Office dialog or a missing
save can identify a response that Word rejected even when parsers accepted it.

Decode the exchange and extract each binary Cell request and response. Sanitize
document text and personal metadata before committing a fixture, preserving graph
references consistently. Binary payloads contain document content. The sanitizing
step needs review; stripping SOAP URLs alone does not remove private data.

Replay against a fresh document with its actual resource ID, leases and storage
index. Update the expected storage index from each accepted server response, as
the existing `CapturedSaveTests` does. Do not hardcode session IDs from a previous
run or rewrite unknown identities to pass. Partial graph updates may reference
earlier revisions, so retain the full starting graph and all preceding saves.

Assert protocol results and independently materialize the final file. Compare
that file with the captured server snapshot and require the expected edited text.
Add retry and restart variants once the ordinary sequence works. Repeat the same
decoded workload with many isolated documents for HTTP/performance testing.
For known workloads, this moves routine checks to Linux without asking someone
to open Word after every change.

A fixed replay cannot observe what Word would choose after a different response.
Keep an occasional real desktop run when changing discovery, response framing,
locks, knowledge or upload support. A dedicated Windows test desktop can run the
Word script automatically for those checks.

## Further coverage

A dedicated Windows VM can run the same check repeatedly in a logged-in desktop.
Task Scheduler should use "Run only when user
is logged on". Keep desktop Word tests out of ordinary headless CI jobs.

Additional scenarios include service restart after a confirmed save, Office retry
after a dropped connection, longer edit sequences, and two independent
Windows desktops editing the same file. Two-host HTTP checks already exercise
shared storage and locks. They do not demonstrate two-desktop coauthoring or
distinct authenticated identities. For performance, add captured small-delta
workloads through HTTP and profile graph allocation before testing very large
files or dozens of clients.
