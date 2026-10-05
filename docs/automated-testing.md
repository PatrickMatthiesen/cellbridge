# Automated testing

CellBridge has protocol/unit tests, live HTTP checks, provider tests and an
interactive Windows Word check. Use the commands below for the part you are
changing. [Client coverage](interoperability.md#client-coverage) records desktop
results.

## Repeatable local checks

Install .NET 10, the compatible Aspire CLI, Docker and capture test dependencies.
From the repository root:

```sh
python3 -m venv tools/capture/.venv
tools/capture/.venv/bin/python -m pip install -r tools/capture/requirements-dev.txt
python3 tools/testing/run.py
```

On Windows, use `tools/capture/.venv/Scripts/python.exe` for the virtual
environment's Python. Stop this workspace's Aspire app before building.
Finish any active capture using the [capture guide](capture-kit.md).
The runner refuses to interrupt an existing app.

The runner builds Release binaries, starts disposable PostgreSQL and two hosts
through Aspire, provisions an integration account and creates test documents.
It runs:

- Binary, SOAP and independent Office Inspectors parser tests.
- Operator import/re-import checks and provider save, retry, concurrency,
  process-termination and lease-timing checks against PostgreSQL.
- Captured Word/Excel HTTP replay, shared locks and loopback HTTPS forwarding.
- Demo, capture tooling and Office evidence-verifier tests.

Results are TRX files, `summary.json` and wire captures under
`artifacts/testing/<timestamp>`. The runner stops its AppHost on completion
and uses no persistent development volume.

For packed-library consumption, with Aspire stopped:

```sh
python3 tools/verify_packages.py
```

This packs the libraries, builds the [consumer example](../examples/NuGetConsumer/README.md)
and runs [package tests](../tests/CellBridge.Packages.Tests/README.md) using an
isolated local feed/cache. Verification clears its own package output, checks
all nine beta packages and symbol packages, validates embedded readmes, license,
repository metadata and internal versions, and writes `artifacts/packages/manifest.json`.
The package tests compare the reusable processor's response with the HTTP endpoint
and exercise host-selected identities and read-only request limits.

## AppHost configuration

Inputs use standard configuration. Aspire parameters can be overridden with
environment variables such as `Parameters__PublicOrigin`. Keep non-secret local
values in ignored `aspire/apphost.settings.json`; environment values override
the file. Use Aspire's secret configuration for secrets.

| Parameter | Default | Purpose |
| --- | --- | --- |
| `StorageVolume` | `cellbridge-storage-data` | Persistent development volume |
| `PublicOrigin` | `https://localhost:7292` | Origin reached by Office and browsers |
| `WireCaptureDirectory` | Empty | Absolute server capture directory |
| `TestPassword` | Required in test mode | Secret for the test-account seed job |

`Testing__Enabled=true` selects disposable mode, omitting the persistent volume
and SharePoint proxy. The runner clears inherited AppHost/test settings, the
database connection string and live-test endpoints. It ignores local AppHost
settings and supplies its own temporary account secret. Inherited `Storage__*`
settings remain in effect.

`AppHost__PeerEnabled=true` adds an explicitly started peer in development.
`Testing__RunStorageTests=true` adds an explicitly started storage test command.
These switches choose the resource model; they are not Aspire parameters.
Normal development includes the explicitly started [SharePoint proxy](capture-kit.md).

## Performance measurements

```sh
python3 tools/testing/run.py --performance --sizes 1,10 --clients 1,8 --iterations 5
```

The runner measures PostgreSQL and filesystem content, both with PostgreSQL
state. Each benchmark creates/drops a separate database and uses a temporary
filesystem directory. The database role needs `CREATE DATABASE`.
Each client edits its own document.

Reports include save/download p50/p95/p99, throughput, allocations, retained graph
counts and process peak memory. The default workload repeats package bytes with
fresh complete DOCX graphs. It measures the document service and includes
deduplication, setup and initial creation; HTTP, network latency and Office
rendering are excluded. Peak memory and usage totals accumulate within an
invocation. Use enough samples for percentile comparisons.

### Workloads and options

These options apply to the benchmark executable shown below. The Python runner
exposes sizes, clients and iterations; use the executable for custom workloads.

| Option | Behavior |
| --- | --- |
| `--workload full` | Complete graph for each save; default |
| `--workload changed-full` | Changes package bytes for each complete save |
| `--workload delta` | Reuses existing file objects |
| `--workload delta-chain` | Also links each earlier revision through the current index |
| `--workload captured` | Replays two reviewed saves; requires `--iterations 2` |
| `--sizes 0` | Small deterministic DOCX |
| `--seed` | Controls synthetic bytes and save identities |
| `--retry` | Checks identical accepted-save retries |
| `--query-every` | Samples empty/prior/current client knowledge |
| `--allow-quota` | Permits quota rejection and verifies unchanged content/versions |

After a Release build, run a service-level growth check:

```sh
dotnet tools/CellBridge.Storage.Benchmark/bin/Release/net10.0/CellBridge.Storage.Benchmark.dll --content memory --sizes 0 --clients 1 --iterations 1000 --workload delta-chain --retry --output artifacts/delta-chain.json
```

Budget overrides use `Storage__...`. Ordinary runs fail on rejection.
Reports distinguish retained history from the graph closure needed to
reconstruct a file and list blockers to retention analysis. Allocation totals
include setup, queries and retention diagnostics; forced collections affect
throughput. A constant-size file can still accumulate required history.
See [storage limits](storage-providers.md#limits-and-qualification).

The `Storage performance report` GitHub workflow runs manually or weekly and
uploads JSON from a fresh database per provider. Compare repeat runs on the same
machine; it does not enforce a fixed latency threshold.

## Connect the Windows laptop through Tailscale

Install Word and Tailscale on Windows. Sign in to Tailscale on both client and
development machine using the same tailnet; enable tailnet HTTPS if prompted.
[Tailscale Serve](https://tailscale.com/docs/reference/tailscale-cli/serve)
provides HTTPS restricted to that tailnet.

Inspect existing listeners before choosing a port:

```sh
tailscale status
tailscale serve status
```

Use the machine's full `*.ts.net` name and a free HTTPS port. Start Aspire with
that origin, a distinct persistent volume and server capture enabled:

```sh
export Parameters__PublicOrigin='https://dev-machine.example.ts.net:8444'
export Parameters__StorageVolume=cellbridge-ofba-storage-data
export Parameters__WireCaptureDirectory="$PWD/artifacts/office-wire"
aspire start --isolated --apphost aspire/apphost.cs --non-interactive
python3 tools/testing/tailscale.py --https-port 8444
tailscale serve status
```

Use a different named volume for each concurrent trial. Aspire `--isolated`
randomizes ports but does not isolate explicit Docker volume names.
Use ordinary development mode, not `Testing__Enabled=true`, for laptop sessions.

The helper waits for web/demo, discovers the current HTTP port and verifies the
configured public origin. It refuses unrelated listeners and tries noninteractive
sudo if the daemon denies access. Rerun it after restarting isolated Aspire,
whose ports change. The library, sign-in, documents and SOAP share
`https://dev-machine.example.ts.net:8444`.

### Keep server capture enabled

Shell exports last for the shell session. To retain the capture setting across
restarts, add an absolute directory to ignored `aspire/apphost.settings.json`:

```json
{
  "Parameters": {
    "WireCaptureDirectory": "/absolute/path/to/CellBridge/artifacts/office-wire"
  }
}
```

Restart Aspire and verify a SOAP request creates request/response/summary files.
Environment overrides take precedence; disposable runs ignore the file. Remove
the setting when finished.

Keep routes at their original roots. The host accepts forwarded HTTPS/Host
headers from trusted loopback proxies. Tailscale headers do not sign users into
CellBridge. [Provision local accounts and grants](authentication.md#first-setup).
For revocation tests, use a document owned by the operator and grant Write to a
different writer; owners retain Write.

From Windows, check HTTPS using its normal trust store:

```powershell
Invoke-RestMethod https://dev-machine.example.ts.net:8444/health
```

## Run real desktop Word

Use a logged-in Windows desktop. Open Word once to finish account/first-run
setup, then close every Word process. Copy `tools/testing/word-smoke.ps1` to the
laptop or use a checkout.

Privately set `CELLBRIDGE_INTEROP_COOKIE` and `CELLBRIDGE_INTEROP_CSRF` from a
signed-in creator account for the script's HTTP creation/verification calls.
Include the paired antiforgery cookie and a token issued after sign-in.
These credentials are separate from [Office's sign-in exchange](authentication.md#desktop-validation),
which must work against the same origin. Keep credential values out of reports
and command arguments.

```powershell
powershell.exe -NoProfile -STA -ExecutionPolicy RemoteSigned `
  -File .\tools\testing\word-smoke.ps1 `
  -BaseUrl https://dev-machine.example.ts.net:8444 `
  -OutputDirectory .\artifacts\office-laptop-01
```

The script checks local Word COM startup/cleanup before creating a remote file.
It creates a blank DOCX, opens its URL and makes two edits/saves by default.
Use `-Edits` for one to ten edits. After every save, it independently downloads
the DOCX and checks every marker in `word/document.xml` plus a changed ETag.
It retains snapshots and SHA-256 hashes, then starts a new Word process to reopen
and verify the edits. Reports include build, phase, preflight result and timings.

The wrapper owns one Word PID, refuses existing Word processes and enforces a
five-minute timeout. It preserves trust settings and restores the background-save
preference. Relative output paths follow PowerShell's current directory.
`RemoteSigned` applies to its worker only; persistent policy is unchanged.
Use `Unblock-File` for a downloaded script if needed and keep the desktop
visible for dialogs. See Microsoft's [Office automation guidance](https://learn.microsoft.com/en-us/office/client-developer/integration/considerations-unattended-automation-office-microsoft-365-for-unattended-rpa).

### Verify the server save

Copy `word-result.json` back and combine it with server captures:

```sh
python3 tools/testing/verify_office.py \
  --capture artifacts/office-wire \
  --word-result artifacts/office-laptop-01/word-result.json \
  --output artifacts/office-laptop-01/verified.json
```

A zero exit code requires successful laptop flags and a matching binary
file-content `PutChanges`. HTTP/SOAP success alone is insufficient.
The verifier matches at least one save; it does not independently correlate each
edit or compare server-download hashes. Keep per-edit snapshots and captures
when checking multiple saves.

Server `.summary.json` files are emitted only with capture enabled. Unique
document names distinguish runs. `Documents.Open` can choose a different
transport from a manual `ms-word:ofe|u|` launch. If one works and the other fails,
compare both captures before changing protocol responses.

### Cleanup

Finish the laptop session, remove only its Serve listener and stop Aspire:

```sh
tailscale serve --https=8444 off
aspire stop --apphost aspire/apphost.cs --non-interactive
```

The named PostgreSQL volume retains documents and accounts.

## Turn a desktop run into replay fixtures

Keep the Office build, edit steps, initial/saved files and matching SOAP/MTOM
request/response captures together in ignored output. Decode each binary Cell
payload and sanitize document text, usernames and embedded metadata before
committing a reviewed fixture. Preserve references consistently.

Replay with a fresh document's actual resource ID, leases and storage index.
Use each accepted response's storage index for the next save, as
`CapturedSaveTests` does. Keep the starting graph and preceding saves because
changed file parts can reference earlier revisions.

Assert protocol outcomes, materialize the final file independently and compare
it with the captured server snapshot and edit markers. Add retry/restart cases
after the ordinary sequence works. The [capture guide](capture-kit.md) covers
extraction and validation.

## Further coverage

The runner and replay tests execute without Office. PowerShell stand-ins check
wrapper logic; real COM tests need an interactive desktop. A scheduled Windows
test should use "Run only when user is logged on".

Use a desktop run when changing Office discovery, framing, locks, knowledge or
upload behavior. Fixed replays cannot observe a client's response to changed
server behavior. Two-host HTTP tests exercise storage/locks;
[client coverage](interoperability.md#client-coverage) tracks desktop coauthoring
and other unverified scenarios.
