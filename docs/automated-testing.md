# Automated testing

## Generated files and worktree cleanup

Report routine results in the conversation. A successful check or cleanup does
not need a separate report file. The agent that creates temporary task files must
remove them when the task finishes.

Preserve only files still needed for ongoing work, such as uncommitted patches,
captures for an open issue, or raw benchmark results awaiting comparison. Keep
them under the primary checkout's ignored `artifacts/<task>/` before removing a
task worktree. Remove them when that work finishes or replaces them. Do not
archive all ignored files or copy package caches and full build trees by default.
`artifacts/` has no automatic cleanup and must not be force-added to Git.

| Files | Location and cleanup |
| --- | --- |
| Request bodies and proposed save files | A private temporary directory. Each operation deletes its files when it closes them. A disk-backed `/tmp` is suitable on Linux. |
| Disposable tools and scratch output | `artifacts/tmp/<task>/`, or a private task directory under disk-backed `/tmp`. Remove the directory when the task finishes. |
| Requested reports, uncommitted patches, captures and raw results still needed | `artifacts/<task>/` in the primary checkout. The agent completing the work removes files no longer needed. |
| Build output | Normal `bin` and `obj` directories in the checkout. Rebuild them when needed and remove them with the worktree. |
| Saved documents and databases | The configured storage directory or volume. Never put these in a temporary directory. |

Aspire's .NET resources use `artifacts/tmp` as their system temporary directory.
`Parameters__TemporaryDirectory` selects another location, including a path
relative to the checkout root. Packages used outside Aspire follow the host's
normal `TMPDIR`, `TMP` and `TEMP` settings. Stop the resource before clearing its
temporary folder. A temporary folder must not hold the database or saved content.
For example, set `Parameters__TemporaryDirectory=/tmp/cellbridge-<user>` to use a
private directory on a disk-backed `/tmp`. Check the filesystem first; `/tmp` can
also use RAM. The operating system's cleanup rules vary. The repo's
`artifacts/tmp` directory has no automatic age-based cleanup, and tools must
remove their task files in either location.

Reuse a suitable existing checkout for continued work. Read-only reviews usually
need no separate worktree. An isolated delegated task creates a new worktree;
retries can therefore leave more than one. Reuse one task checkout for its related
experiments rather than creating a checkout for each experiment.

T3 worktrees can remain after their threads finish or are deleted. Before removing
one, check that no thread or process still uses it, preserve uncommitted work and
any ignored files still needed, and confirm that its commits are saved or merged. A clean
`git status` does not show ignored reports, captures or databases. Use
`git worktree remove <path>` after those checks. `git worktree prune` removes stale
Git metadata; it does not remove old checkout directories. Do not use forced
removal as a substitute for checking their contents.

## Routine Linux checks

Install .NET 11, Docker and an Aspire CLI compatible with the pinned Aspire packages.
Build and run the main solution from the repository root:

```sh
dotnet build CellBridge.slnx
dotnet test CellBridge.slnx -m:1 --logger trx --results-directory artifacts/testing
```

Protocol, replay, provider and API tests run without a Windows desktop.
PostgreSQL suites reuse `src/CellBridge.AppHost` through Hosting.Testing.
Fixtures remove the persistent database mount and capture resource before
starting their own instance. Provider tests omit WebDemo; HTTP tests retain it
and add their account and peer. Recovery tests configure the same database
resource for controlled interruption. These changes live in the fixtures; the
development AppHost has no test topology switches.

Direct protocol behavior tests use `CellBridgeDocumentService.ExecuteAsync` with
both InMemory and PostgreSQL. They check decoded responses and resulting files,
partition graphs and receipts. Codec and response-assembly tests keep independent
binary and reference assertions.

Fixtures apply the existing initialization and dispose their own application.
They do not use the development database or volume. Storage tests always own
their database. Lists accepts an explicitly supplied
`CELLBRIDGE_LISTS_TEST_CONNECTION_STRING` for an externally managed test database.

Run a focused suite with `dotnet test tests/CellBridge.Storage.Tests`,
`dotnet test tests/CellBridge.Lists.PostgreSql.Tests`,
`dotnet test tests/CellBridge.Interop.Tests` or
`dotnet test tests/CellBridge.WebDemo.Tests`. WebDemo checks cover authenticated
pages, API authorization and antiforgery. The interop fixture hosts WebDemo peers
against PostgreSQL and replays reviewed Office requests. Real desktop Office and
remote SharePoint checks remain separate opt-in workflows.

Fixture generators live in the non-shipping `tests/CellBridge.TestFixtures`
project. Tests that need only document bytes do not reference an application.
`tests/CellBridge.TestInfrastructure` owns reusable Aspire environments.
Record TRX results and qualification reports under ignored `artifacts/`.
See [recovery environments](supported-environments.md) and
[bounded protocol validation](bounded-protocol-validation.md).

Capture and desktop evidence tooling retains its Python dependencies:

```sh
python3 -m venv tools/capture/.venv
tools/capture/.venv/bin/python -m pip install --require-hashes -r tools/capture/requirements-dev.txt
tools/capture/.venv/bin/python -m pytest tools/capture tools/testing -q
```

## SQL Server engine checks

Run the SQL Server tests with .NET 11, Docker on Linux x86-64 and an Aspire CLI
compatible with the pinned hosting packages:

```sh
dotnet test tests/CellBridge.SqlServer.Tests --logger trx --results-directory artifacts/sqlserver
```

The fixture starts a disposable SQL Server 2022 Developer database. Allow at
least 2 GiB for SQL Server plus memory for Aspire, .NET and Docker. The
integration accepts Microsoft's license for the nonproduction Developer
edition. Review the [container license and platform requirements](https://learn.microsoft.com/en-us/sql/linux/sql-server-linux-docker-container-deployment?view=sql-server-ver17)
before running it elsewhere.

The suite checks the document and Lists migrations, runtime permissions, document
storage and recovery, and Lists authorization and operations. It does not test
live Office editing or establish SQL Server performance or full provider parity.
Fixture startup and health failures are setup failures, not test results.

## Package checks

```sh
python3 tools/verify_packages.py
python3 tools/verify_packages.py --sqlserver
python3 tools/check_release.py --version <version> --commit <sha>
```

The verifier packs the shipping libraries, builds package-only examples and runs
[consumer checks](../tests/CellBridge.Packages.Tests/README.md) against an isolated
local feed and cache. It validates the eleven shipping packages and symbols, readmes,
license, repository metadata and package versions, then writes
`artifacts/packages/manifest.json`.

The default command builds document and Lists package consumers and reports
database checks as skipped. `--sqlserver` runs them against separate disposable
databases using the SQL Server fixture above. Each consumer runs its package's
migrator with setup credentials, then tests with restricted runtime credentials.

The package-only test projects stay outside `CellBridge.slnx` because restore
requires the packed feed. Outputs go under ignored `artifacts/packages`. A skipped
check is not a pass. Package checks do not test live Office editing or SQL Server
performance.

A release requires reviewed committed source and
`sourceDirty: false`. The [publish workflow](../.github/workflows/publish-nuget.yml)
validates from `main`; use a new version for changed source.

## Development AppHost

```sh
aspire start --isolated --apphost src/CellBridge.AppHost/CellBridge.AppHost.csproj --non-interactive
aspire wait webdemo --apphost src/CellBridge.AppHost/CellBridge.AppHost.csproj --non-interactive
aspire describe --apphost src/CellBridge.AppHost/CellBridge.AppHost.csproj --non-interactive
aspire stop --apphost src/CellBridge.AppHost/CellBridge.AppHost.csproj --non-interactive
```

The development AppHost starts PostgreSQL, schema/authentication
initialization, WebDemo and an explicitly started SharePoint capture resource.
It starts no tests or benchmarks. Aspire allocates ports and passes connection
and endpoint references. `Parameters__PublicOrigin` supplies an external HTTPS
origin when desktop Office connects through another hostname. Its local default
is WebDemo's allocated HTTPS endpoint.

PostgreSQL uses the `cellbridge-storage-data` volume. Set
`Parameters__StorageVolume` to use another existing volume or to give concurrent
checkouts separate databases. Test fixtures remove this volume mount and use
temporary storage.

Other external inputs are `Parameters__ApplicationName`,
`Parameters__WireCaptureDirectory`, `Parameters__CaptureUpstream`,
`Parameters__CaptureOutput` and `Parameters__CaptureUpstreamCa`.
Non-secret local values can live in ignored `src/CellBridge.AppHost/apphost.settings.json`;
environment variables override the file. These inputs do not select resources.
Never stop another worktree's AppHost. Finish an active capture with
`tools/capture/stop.py` before stopping its AppHost.

## Performance measurements

Performance projects live under `benchmark/`. Correctness assertions live under
`tests/`, while each benchmark still rejects failed operations and invalid results.
See [list measurements](list-performance.md) and
[isolated allocation measurements](allocation-measurements.md).

Use the standard BenchmarkDotNet entry point to list or run all maintained cases:

```sh
dotnet run -c Release --project benchmark/CellBridge.Benchmarks -- --list flat
dotnet run -c Release --project benchmark/CellBridge.Benchmarks
```

The unified parent process starts one disposable PostgreSQL environment through
Aspire, passes connections to BenchmarkDotNet's child processes, and disposes it
after the run. Measured child processes do not host Aspire health checks or
telemetry. Each case resets its test data before use. Preparation and result
checks run outside the measured method. A Dry job checks
setup and cleanup but does not provide reliable performance numbers.
The performance workflow runs the BenchmarkDotNet suite weekly and on manual
request. Its benchmark artifacts expire after 14 days.

NBomber runs all provider and HTTP load scenarios through the same entry point:

```sh
dotnet run -c Release --project benchmark/CellBridge.Benchmarks -- --load-test --config benchmark/CellBridge.Benchmarks/load-smoke.json
```

This bounded configuration covers Lists provider/HTTP and document memory,
PostgreSQL, filesystem and HTTP workflows. It checks execution, not capacity.
NBomber owns load scheduling, warm-up, statistics and thresholds. Aspire owns
PostgreSQL, disposable databases, setup, test account and WebDemo. Scenario initialization prepares test data. HTTP scenarios
use normal cookie authentication and CSRF protection. No separate machine,
manual database or running development app is needed.

The performance workflow runs both BenchmarkDotNet and this load check weekly
and on manual request. Reports expire after 14 days. See
[list measurements](list-performance.md#load-tests) for configuration, license
requirements and result limits.

Document load scenarios retain full, changed-full, delta, delta-chain and reviewed
capture requests, identical retries, change queries, graph retention and download
checks. HTTP request construction uses owned-provider reads in a separate step.
It is not a complete Office client emulator. Final probes run outside load timing;
preparation, retry and inspection steps remain visible in workflow timing.
Detailed correctness assertions remain in the test projects.

Use supported storage limits and do not interpret quota rejection as a successful
save. Larger retained graphs and multiple clients need separate qualification.
The [storage guide](storage-providers.md#limits-and-qualification) describes limits.

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
that origin and server capture enabled:

```sh
export Parameters__PublicOrigin='https://dev-machine.example.ts.net:8444'
export Parameters__WireCaptureDirectory="$PWD/artifacts/office-wire"
aspire start --isolated --apphost src/CellBridge.AppHost/CellBridge.AppHost.csproj --non-interactive
python3 tools/testing/tailscale.py --https-port 8444
tailscale serve status
```

The development AppHost uses a volume that retains data between runs. Integration
fixtures own separate disposable databases.

The helper waits for WebDemo, discovers the current HTTP port and verifies the
configured public origin. It refuses unrelated listeners and tries noninteractive
sudo if the daemon denies access. Rerun it after restarting isolated Aspire,
whose ports change. The library, sign-in, documents and SOAP share
`https://dev-machine.example.ts.net:8444`.

### Keep server capture enabled

Shell exports last for the shell session. To retain the capture setting across
restarts, add an absolute directory to ignored `src/CellBridge.AppHost/apphost.settings.json`:

```json
{
  "Parameters": {
    "WireCaptureDirectory": "/absolute/path/to/CellBridge/artifacts/office-wire"
  }
}
```

Restart Aspire and verify a SOAP request creates request/response/summary files.
Environment overrides take precedence. Test fixtures load this file but disable
server capture and remove the SharePoint capture process before starting. Remove
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
aspire stop --apphost src/CellBridge.AppHost/CellBridge.AppHost.csproj --non-interactive
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

The native fixtures and replay tests execute without Office. PowerShell stand-ins check
wrapper logic; real COM tests need an interactive desktop. A scheduled Windows
test should use "Run only when user is logged on".

Use a desktop run when changing Office discovery, framing, locks, knowledge or
upload behavior. Fixed replays cannot observe a client's response to changed
server behavior. Two-host HTTP tests exercise storage/locks;
[client coverage](interoperability.md#client-coverage) tracks desktop coauthoring
and other unverified scenarios.
