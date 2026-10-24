# Automated testing

## Routine Linux checks

Install .NET 10, Docker and an Aspire CLI compatible with the pinned Aspire packages.
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
node tools/testing/test-list-example-ui.cjs
```

## SQL Server engine checks

Run the independent SQL Server tests with .NET 10, Docker on Linux x86-64 and an
Aspire CLI compatible with the pinned hosting packages:

```sh
dotnet test tests/CellBridge.SqlServer.Tests --logger trx --results-directory artifacts/sqlserver
```

The fixture reuses the main AppHost through Hosting.Testing, removes its default
resources before startup and adds native Aspire SQL Server database resources.
It starts SQL Server 2022 Developer with a 2048 MiB engine memory limit,
disposable storage, random credentials and allocated endpoints, then disposes
only its own application. Leave additional memory for Aspire, .NET and Docker. The default
development AppHost and PostgreSQL volume remain unchanged. Running this fixture
uses the native integration's `ACCEPT_EULA=Y` setting for the nonproduction
Developer edition. Review Microsoft's [container license and platform requirements](https://learn.microsoft.com/en-us/sql/linux/sql-server-linux-docker-container-deployment?view=sql-server-ver17)
before running it in another environment.

Content checks cover explicit and concurrent migrations, restricted runtime
credentials, deduplicated writes from separate store instances, shared quota
admission, cancellation during chunk insertion, rollback, streamed integrity
verification and early disposal. Lists checks exercise its separate migrations,
workspace filter/block policies, immutable authorization anchors, exact persisted
subject identities, JSON/Unicode round trips and context cleanup across a
single-connection pool. Cleanup-failure checks also use an externally owned open
connection and inject SQL and non-SQL failures after commit. Both must discard
the old workspace session without turning the committed mutation into a failure.
Restricted-runtime inserts must reject null subjects in both identity tables.
An upgrade check repairs the earlier nullable schema while preserving valid rows.
Lists engine checks cover built-in and host authority, exact typed SQL filtering
and ordering, paging, create retry receipts, conditional mutations, references,
schema changes, saved views and audit. Runtime health requires the exact
[Lists permissions](lists.md#sql-server-setup-and-runtime), including schema
`VIEW DEFINITION`, migration-history `SELECT` and query-function `EXECUTE`.

Document checks run the public provider conformance contract and the canonical
document service. They cover inherited file graphs, competing saves, receipts,
replay after a lost commit reply, cancellation after acceptance, restoration,
post-lock database time, selective reads without snapshot/content loads,
rename/delete/recreate, path reservations, snapshot/history limits, exact byte
accounting, persisted quota admission and atomic recovery import with retry.
Outer-request checks cover coauthoring with skipped schema-lock fallback, saves
and replay, read-only authorization and resource-ID lookup without URL fallback.
Independent providers also exercise host-lock refresh/replacement/fencing and
coauthor membership transitions.
Namespace checks cover repeated recreation through renamed lifecycle ancestors,
unrelated retired reservations, and hash/key/missing-row corruption. Health,
path misses and admission reject damaged namespace indexes without reading
content objects or chunks.
Runtime logins cannot create tables or change persisted quota limits.
These checks do not establish live Office interoperability or full provider
parity or qualify SQL Server performance.
Fixture startup/health failures are setup failures and do not count
as product test results.

Quiescent collection checks cover dry-run/apply reports, retained-only save
receipts, history/graph roots, deleted and recreated documents, pending external
deliveries, imported recovery snapshots and receipt replay. Corrupt inventories
abort before deletion. Faults and cancellation during a sweep roll back chunks
and accounting; cancellation after commit preserves the completed report.
The engine also checks that collection blocks content quota admission, document
publication and recovery while holding its transaction lock. Such tests exercise
transaction ordering at the maintenance boundary, not live collection during
multi-step Office saves. An explicit quiescent apply is still required.

The Lists transaction helper binds the workspace in `CONTEXT_INFO`, then clears
it before returning the connection. A failed reset clears its pool and closes the
session even when the consumer owns the open connection. This avoids
the documented [`SESSION_CONTEXT` parallel-plan issue](https://learn.microsoft.com/en-us/sql/t-sql/functions/session-context-transact-sql?view=sql-server-ver17).
Workspace context selects a database scope; host authorization must still approve
each Lists operation. The runtime user must not own the schema or have permission
to alter security policies. Tests use separate setup and runtime logins.

## Package checks

```sh
python3 tools/verify_packages.py
python3 tools/verify_packages.py --sqlserver
python3 tools/check_release.py --version <version> --commit <sha>
```

The verifier packs the libraries, builds package-only examples and runs
[consumer checks](../tests/CellBridge.Packages.Tests/README.md) against an isolated
local feed/cache. It validates all fourteen packages and symbols, readmes, license,
repository metadata and internal versions, then writes
`artifacts/packages/manifest.json`. The inventory includes
`CellBridge.Storage.SqlServer` and `CellBridge.Lists.SqlServer`, at the same
`0.1.0-beta.4` version as the other packages. Both remain independent of each
other and PostgreSQL.

The default command builds both package-only document and Lists test consumers,
runs non-database checks and reports database skips. `--sqlserver` opts into the
existing disposable Aspire `SqlServerEnvironment` through source test
orchestration, with the same SQL Server prerequisites as the engine checks above.
It launches separate package-only document and Lists test processes against
separate empty databases. Each consumer explicitly migrates its own schema with
setup credentials and uses restricted runtime credentials afterward. Source
orchestration supplies the environment; the consumer processes use package
references. Existing package and consumer checks still run.

The package-only test projects stay outside `CellBridge.slnx` because their packed
feed must exist before restore. Ignored `artifacts/packages` retains manifests,
the isolated feed/cache, consumer assembly identities and hashes, TRX results and
the SQL engine version for database runs. A skipped check is not a pass.
Environment startup failures are setup failures. Package checks do not establish
full provider parity, SQL Server performance or live Office interoperability.

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

Run the benchmark executables directly. A PostgreSQL run without a supplied
connection string starts a disposable database through the shared AppHost fixture:

```sh
dotnet run -c Release --project benchmark/CellBridge.Storage.Benchmark -- --content postgresql --sizes 1 --clients 1,8 --iterations 64 --workload delta-chain --output artifacts/storage-growth.json
dotnet run -c Release --project benchmark/CellBridge.Lists.Benchmark -- --clients 1,8 --workspaces 1,16 --iterations 200 --output artifacts/lists-performance/report.json
```

Explicit `ConnectionStrings__cellbridge` or `ConnectionStrings__lists` values
select an external administrative connection. The benchmark creates a new
database there and removes only that database when it finishes. The Lists
benchmark also creates and removes its own database role. The memory storage
workload needs no database.

Each run retains workload, setup, warm-up and measured-interval metadata.
Storage reports retain retry mode and initial/final file hashes. After timing,
the runner reloads final state and checks receipts, graph materialization and
expected file bytes. These probes do not count toward workload allocations.
Steady-state timings exclude environment startup/schema setup and warm-up;
process peak memory remains cumulative. Preserve raw reports with runtime,
hardware, source and backend details when comparing runs. These measurements
do not establish a final SQL baseline.

For a small repeated-save/state-growth check without PostgreSQL:

```sh
dotnet run -c Release --project benchmark/CellBridge.Storage.Benchmark -- --content memory --sizes 0 --clients 1 --iterations 64 --workload delta-chain --retry --output artifacts/delta-chain.json
```

Use current supported workload limits and do not interpret quota rejection as a
successful save. Larger retained graphs and multiple clients need separate
qualification. The [storage guide](storage-providers.md#limits-and-qualification)
describes supported limits.

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

The development AppHost uses an application-scoped durable volume. Integration
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
