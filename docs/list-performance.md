# Measuring list performance

Run all maintained BenchmarkDotNet cases, including Lists, with:

```sh
dotnet run -c Release --project benchmark/CellBridge.Benchmarks
```

Add `-- --list flat` to list methods without starting the database.
BenchmarkDotNet owns warm-up, sampling and allocation measurements. See
[measurement setup](allocation-measurements.md) for prerequisites and limits.

Paging cases prepare 1,000 records and measure first and second pages with fresh
or explicitly analyzed PostgreSQL statistics. Mutation cases measure batches of
16 creates or patches. The unified parent process owns one Aspire PostgreSQL
environment for the run. Measured child processes own connections and reset the
schema before preparing each case. Aspire health checks and telemetry stay in the
parent process. Schema reset, fixture preparation and result checks run outside
the timed method. Select cases with standard BenchmarkDotNet arguments:

```sh
dotnet run -c Release --project benchmark/CellBridge.Benchmarks -- --filter '*PostgreSqlPaging*' --artifacts artifacts/benchmarks/paging --exporters json
dotnet run -c Release --project benchmark/CellBridge.Benchmarks -- --filter '*PostgreSqlListMutation*' --artifacts artifacts/benchmarks/mutations --exporters json
```

## Load tests

NBomber replaces the custom Lists and document traffic runners. Both use the
same entry point. Run the bounded check of every load workflow with:

```sh
dotnet run -c Release --project benchmark/CellBridge.Benchmarks -- --load-test --config benchmark/CellBridge.Benchmarks/load-smoke.json
```

`lists-provider` calls the public provider APIs. `lists-http` calls WebDemo's
actual Lists endpoints using cookie authentication and CSRF protection. Setup logs
in and seeds data before measurement. HTTP scenarios use only HTTP during load;
provider reads validate saved data after the run.

Lists supports these `Workload` settings:

- `independent` distributes client records across `Workspaces`.
- `same-workspace` gives each client a different record in one workspace.
- `contention` sends clients to the same record. Each patch reads its revision
  through HTTP first. Rejected revisions have status `revision_conflict` and do
  not count as successful patches.
- `smoke` creates and patches a record, then checks two query pages.

`GetWeight`, `QueryWeight`, `PatchWeight` and `CreateWeight` select the operation
mix. `Clients` bounds the prepared client records. `Records`, `PageSize` and
`PayloadBytes` control the query fixture and record size. HTTP request steps are
`get`, `query`, `create` and `patch`.

Document provider scenarios include `delta` and `delta-chain`. These change
revision references while keeping file bytes unchanged. The `captured` mode uses
fixed recorded save examples.

The document HTTP scenario supports `Workload` values `independent` and
`contention`. Clients query the current graph through SOAP, then upload an entire
changed Office package as a full replacement through real SOAP/MTOM. `Clients`,
`DocumentsPerClient`, `SizeMiB`, `Seed` and `WritePercent` control the workload. Payloads are prepared before load.
`Retry` adds a separate identical-request replay. Steps report queries, downloads,
new saves and replays separately. A protocol coherency failure has status
`document_conflict`; it is not a successful save. This is an API client, not a
desktop Office emulator.

`PayloadVariants` defaults to two different files per client. Saves alternate
between them, so later writes can reuse stored content. This measures a warm
working set. To measure new-content writes, prepare more variants than each
client can save during warm-up and measurement. The final validation output
reports committed saves and distinct payload hashes. Those counts must match
for a run to qualify as a new-content test. Prepared payload memory grows with
`Clients`, `PayloadVariants` and `SizeMiB`.

Random small-edit HTTP load is not implemented. Maximum Office editing capacity
remains unmeasured.

Aspire starts the API and disposable databases, runs schema setup and seeds the
account. Runtime requests use restricted database users. Add `--backend sqlserver`
to test the SQL Server document and Lists providers. Authentication still uses
PostgreSQL. Development volumes are excluded from test runs. Disposing the Aspire
application owns resource cleanup. No running development host or second machine
is required.

The checked-in `load.json` selects Lists HTTP with eight clients, 30 seconds of
warm-up and two minutes of measurement:

```sh
dotnet run -c Release --project benchmark/CellBridge.Benchmarks -- --load-test --config benchmark/CellBridge.Benchmarks/load.json
```

Use `load-rate.json` for fixed arrival traffic. It offers 200 Lists workflows per
second or four document workflows per second, with HTTP requests measured as
separate steps. These are workload examples, not capacity claims.

Select document HTTP with `--target document-http`. Run one target at a time for
comparisons. Change NBomber's `LoadSimulationsSettings` to compare client counts or
fixed arrival rates. Keep the source, runtime, backend, workload and duration fixed
between implementations. Prepare enough clients for the scenario's instance
numbers. Do not queue requests in the scenario to hide a generator capacity limit.

All clients use one seeded account. The load environment sets document admission
limits to 16 global requests and 16 requests per subject, so it can exercise
concurrent storage work. Override these through Aspire parameters
`Parameters__LoadMaxConcurrentRequests` and
`Parameters__LoadMaxConcurrentRequestsPerSubject`. Shipping defaults remain eight
global requests and two per subject. Record the tested limits with the results.
These runs do not establish distinct-user capacity or desktop Office coauthoring.

NBomber's [license](https://nbomber.com/docs/getting-started/license/) permits free
personal and academic use. Organizations need the appropriate paid license.
The dependency belongs only to the non-shipping benchmark project.

## Interpret results

Reports go to ignored `artifacts/load-tests/`. Compare matching configurations
on an otherwise idle machine. Keep runtime, hardware, source and backend details
with results you need for a comparison. Remove results when that work ends.
The bounded smoke configuration checks execution and produces no useful capacity
estimate.

NBomber reports counts, throughput and latency percentiles for each request step.
The checked-in profiles set `MaxFailCount` explicitly because NBomber counts
expected revision conflicts toward that limit too. Reject a run that stops before
its configured duration, even if its exit code is zero.

Compare successful `patch` or `save` throughput and response times at the same
load. Report conflicts, replays, timeouts and server failures separately. The
whole iteration includes client preparation and several requests, so its latency
is not one API response time. Login and seeding happen before measurement.

HTTP client and WebDemo run in separate processes on the same machine. Check
client CPU, server CPU, memory and database waits before interpreting a limit as
server capacity. Provider scenarios measure a different path. Use BenchmarkDotNet
for allocation costs and database diagnostics for blocking and deadlocks.

Requests check protocol/API success. Final probes check persisted record revisions,
values and document state. Expected revision conflicts continue the run. Other request failures remain in
the results and cause a nonzero exit code. Incorrect saved data stops the run.
Detailed correctness assertions remain in `tests/CellBridge.Benchmarks.Tests` and
the protocol, storage and Lists test projects. A passing load test qualifies its
declared workload, not general production capacity.
