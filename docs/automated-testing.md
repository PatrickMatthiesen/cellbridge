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
container, waits for `web`, `web-peer` and `demo`, and runs:

- Binary and SOAP unit tests.
- Provider tests against real PostgreSQL, including process termination before
  and after publication, retries, concurrent updates and lease timing.
- HTTP replay of captured Word/Excel saves, Word open exchanges, shared locks
  across two hosts, and HTTPS origin forwarding through a loopback proxy.
- Demo, capture-tool and Office evidence checks.

It writes TRX files, a `summary.json`, and opt-in wire evidence beneath
`artifacts/testing/<timestamp>`, then stops its AppHost. It never mounts the
developer's `cellbridge-storage-data` volume. The Windows-only Office Inspectors
adapter runs when this command runs on Windows, and in Windows CI.
All local raw capture artifacts stay outside Git.

## Performance measurements

```sh
python3 tools/testing/run.py --performance --sizes 1,10 --clients 1,8 --iterations 5
```

This adds PostgreSQL content and filesystem content measurements, both with
PostgreSQL document state. Each client edits its own document. Reports include
save/download p50, p95 and p99, throughput, allocation counts, retained graph
counts and process peak memory. Accepted uploads and downloads must succeed.
The temporary filesystem content directory is removed after each run.

These measurements use synthetic complete DOCX graphs in the document service.
They do not include HTTP, desktop rendering, small Office deltas or network
latency. Startup and initial document creation contribute to overall throughput.
Iterations reuse the same package bytes with fresh protocol graphs, so content
deduplication is part of the measured path. These results do not measure a stream
of unique file-content writes. Peak memory is cumulative within each benchmark
process. Small samples cannot establish reliable p99 latency.

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
`dev-machine.example.ts.net`. Start Aspire with the public origin and server
wire capture enabled. The usual persistent development database is appropriate
for a laptop session; do not set `CELLBRIDGE_TEST_RUN=1` here.

```sh
export CELLBRIDGE_PUBLIC_ORIGIN='https://dev-machine.example.ts.net'
export CELLBRIDGE_WIRE_CAPTURE="$PWD/artifacts/office-wire"
export CELLBRIDGE_SKIP_SHAREPOINT_CAPTURE=1
aspire start --apphost aspire/apphost.cs --non-interactive
aspire wait web --non-interactive
aspire wait demo --non-interactive
tailscale serve --bg --https=443 http://127.0.0.1:5181
tailscale serve --bg --https=8443 http://127.0.0.1:5281
tailscale serve status
```

The collaboration server is `https://dev-machine.example.ts.net`. The optional
demo is `https://dev-machine.example.ts.net:8443`. Keep document and SOAP paths
at their original roots. The demo sends Office to the collaboration origin on
443. The web and demo hosts accept forwarded HTTPS/Host information from trusted
loopback proxies using ASP.NET Core's defaults. An integration test verifies the
SOAP origin through Aspire's HTTP proxy.

Use Serve for this private test environment. The current server has no distinct
authenticated users, so restrict tailnet access to the test devices. Tailscale
identity headers do not implement Office user authentication in CellBridge.

From the laptop, verify that this succeeds with the normal Windows trust store:

```powershell
Invoke-RestMethod https://dev-machine.example.ts.net/health
```

## Run real desktop Word

Copy `tools/testing/word-smoke.ps1` to the laptop or use a checkout. Open Word
once to finish its account and first-run setup, then close Word. Run the check
from a logged-in Windows desktop:

```powershell
powershell.exe -NoProfile -STA -ExecutionPolicy RemoteSigned `
  -File .\tools\testing\word-smoke.ps1 `
  -BaseUrl https://dev-machine.example.ts.net `
  -OutputDirectory .\artifacts\office-laptop-01
```

The script first checks local Word startup, temporary document close and quit
through real COM. A failure here stops before creating a remote document. The
JSON report records the current phase and whether this preflight passed. The
tests with managed Word stand-ins check script logic only; they cannot validate
native COM calls.

The script creates a fresh blank DOCX through the demo API and downloads an
initial snapshot. Desktop Word opens the remote URL and performs two separate
edits and saves in the same session. After each save, the script independently
downloads the server's DOCX and requires all markers in `word/document.xml` and
a changed ETag. It retains both saved snapshots with SHA-256 hashes. A fresh Word
process then reopens the document and checks both edits. The report records Word
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
a two-edit, fresh-process reopen test with Word `16.0.20430` and PostgreSQL. See
[interoperability coverage](interoperability.md).

For cleanup, disable only the two Serve listeners added for this session:

```sh
tailscale serve --https=443 off
tailscale serve --https=8443 off
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
