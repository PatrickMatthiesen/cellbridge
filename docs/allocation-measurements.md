# Measuring request allocations

Run every maintained BenchmarkDotNet case with one command:

```sh
dotnet run -c Release --project benchmark/CellBridge.Benchmarks
```

List the benchmark methods without starting a database or collecting measurements:

```sh
dotnet run -c Release --project benchmark/CellBridge.Benchmarks -- --list flat
```

This entry point uses BenchmarkDotNet directly across the allocation, Lists and
storage benchmark assemblies. It defaults to all cases and accepts normal
BenchmarkDotNet arguments, including `--filter`, `--job`, `--artifacts` and
`--exporters`. The parent process starts one disposable PostgreSQL environment
through Aspire and disposes it after all cases finish. Generated benchmark child
processes receive its connection strings and own only their data preparation and
connections. Aspire health checks and telemetry run in the parent process, outside
the process whose allocations BenchmarkDotNet measures. The unified entry point
requires Docker and the repository's Aspire prerequisites for timed runs.
Listing methods and displaying help start no environment. The standalone
allocation project below remains database-free.
No second machine, running development host or measurement wrapper is required.
Setup and result checks remain outside measured invocations. A failed benchmark
returns a nonzero exit code. Use an otherwise idle machine for timing comparisons.
`--job Dry` checks execution with minimal sampling; its numbers are not useful
performance measurements.

Benchmarks use the runtime's normal GC configuration. .NET 11 includes the
[allocation-counter fix](https://github.com/dotnet/runtime/pull/131069), so the
runners no longer override heap adaptation. Record runtime and GC settings when
comparing results.

`benchmark/CellBridge.Allocations.Benchmark` measures preloaded inbound parsing,
eager document and file-graph restoration, and nested binary builders.
BenchmarkDotNet owns process isolation, warm-up, measurement and allocation
reporting for synthetic inputs. It does not measure HTTP ingestion, network
latency, Office rendering or end-to-end save throughput.

Build in Release and select the methods to measure:

```sh
dotnet build benchmark/CellBridge.Allocations.Benchmark -c Release
dotnet benchmark/CellBridge.Allocations.Benchmark/bin/Release/net11.0/CellBridge.Allocations.Benchmark.dll --filter '*' --artifacts artifacts/allocations/bdn --exporters json
```

The default cases use deterministic seed-42 payloads of 1 KiB, 1 MiB and 8 MiB
with memory and fresh filesystem content providers. `GlobalSetup` constructs the
request, graph and persisted state outside measured invocations. Filesystem paths
belong to the run and are removed by `GlobalCleanup`. No development database,
Aspire application or shared volume is used. BenchmarkDotNet records its job,
runtime and timing settings in its output. Additional JSON files under
`artifacts/allocations/metadata` record workload hashes, source commit, tracked
diff hash, working-tree status and loaded assembly hashes. Set
`CELLBRIDGE_BENCHMARK_METADATA` to keep these beside a particular run's report.
Use a fresh output directory for each comparison and record the source and
assembly identities. Other work on the machine can affect timing.

## Current PostgreSQL provider cases

The storage benchmark also measures complete 1 MiB content reads, batches of
eight new content objects, and batches of eight document saves. Save cases use
`changed-full` and `delta` requests with four prior saves per document.
`changed-full` replaces the package bytes. `delta` changes revision references
while keeping file bytes unchanged. The provider load mode `delta-chain` also
keeps file bytes unchanged; it adds references to preceding revisions. Database
creation, content preparation and save-history construction happen outside the
timed method. Iteration cleanup checks stored content, restored documents, save
receipts and revision counts.

Run these cases with standard BenchmarkDotNet jobs:

```sh
dotnet run -c Release --project benchmark/CellBridge.Benchmarks -- --filter '*PostgreSqlContent*' --artifacts artifacts/provider-benchmarks/content --exporters json
dotnet run -c Release --project benchmark/CellBridge.Benchmarks -- --filter '*PostgreSqlServiceSave*' --artifacts artifacts/provider-benchmarks/saves --exporters json
```

The unified parent process owns the disposable PostgreSQL environment through
the shared Aspire test infrastructure. Cases reset their dataset outside measured
invocations and do not start Aspire in a measured child process. They do not use a
running development AppHost or its persistent volume. The Lists and storage
benchmark projects are libraries loaded by the unified entry point.
BenchmarkDotNet records runtime, job, timing and allocation results. Keep reports
under ignored `artifacts/`.

## Reviewed captures and provider diagnostics

The `--diagnostics` mode retains reviewed SharePoint requests, logical provider
read counts and stage reports for comparisons that need those details:

```sh
dotnet benchmark/CellBridge.Allocations.Benchmark/bin/Release/net11.0/CellBridge.Allocations.Benchmark.dll --diagnostics --workload save-second --stage restore --content filesystem --iterations 30 --output artifacts/allocations/capture/restore.json
```

Use `--workload synthetic --bytes 1048576` for synthetic diagnostic input, or
`save-first` and `save-second` for the reviewed fixtures. The second save merges
the first save's graph before restoration. These are fixed recorded examples,
not generated random edits. Setup verifies the fixture hashes.
For repetition, invoke the executable again with a new output path. The native
benchmark process owns its content directory and cleanup. There is no Python
lifecycle runner.

| Diagnostic stage | BenchmarkDotNet method | Measured operation |
| --- | --- | --- |
| `inbound` | `InboundMtomSoapBinary` | MTOM views, SOAP parsing and complete binary request decoding from a preloaded buffer |
| `binary` | `BinaryParse` | Complete binary request decoding |
| `restore` | `Restore` | Verified content reads, document/partition copies and eager file-graph restoration |
| `nested` | `NestedObjectGroupBuild` | Object-group construction, including its final owning payload array |
| `file-build` | `FileResponseBuild` | File response construction, including nested builders |

In diagnostic mode, input preparation, persistence, five warm-ups, output
serialization and probes are outside the timed operation loop. The operation
count is `--iterations`. Retained managed bytes include setup and the final
result. Process peak includes setup, JIT and warm-up. Neither isolates transient
operation peak memory. Logical read bytes include integrity verification and do
not measure physical disk I/O. The nested and file-build stages use materialized
file bytes rather than reserializing the original captured graph.

`compare.py` compares diagnostic reports with exactly one stage each. It checks
workload, wire hashes and logical reads before comparing repeat medians:

```sh
python3 benchmark/CellBridge.Allocations.Benchmark/compare.py artifacts/allocations/before artifacts/allocations/after
```

## Interpret reports

Compare runs only when their workload hashes, logical reads, generated wire hashes,
runtime settings and provider configuration match. Allocation changes do not
establish latency or server-capacity changes. Parser, builder and restore stages
measure different ownership costs and must be compared separately.

Keep raw BenchmarkDotNet and diagnostic reports under ignored `artifacts/`.
Current correctness belongs in the protocol and storage test projects; benchmark
verification only checks that each measured workload produced the requested
content, graph and receipt state.

## Semantic checks and evidence limits

`NestedSerializationTests` compares baseline SHA-256 wire goldens for both
Current and SharePoint13_11 framing, including payload lengths 32761, 32762 and
32763 around the ObjectGroupObjectData large-length transition. It checks exact
materialization and independent ownership after input/result mutation and writer
growth. `NestedSerializationInteropTests` uses the vendored Microsoft
OfficeDev Interop-TestSuites decoder to check declarations, sizes, references and
payload bytes for both profiles through 1 MiB.

The framing authorities remain [MS-FSSHTTPB Data Element Package](https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttpb/99a25464-99b5-4262-a964-baabed2170eb)
and [Object Group Data Elements](https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttpb/21404be6-0334-490e-80b5-82fccb9c04af).
Quota, save publication, unsupported upload modes and parser bounds remain in
the correctness suites rather than this benchmark guide.

The [reviewed fixture provenance](../testdata/sharepoint/fixtures/save_reopen/README.md)
identifies the selected SharePoint exchanges and sanitization. Exact historical
Office and SharePoint builds are absent from those fixtures. They are offline
selected exchanges, not complete reference sessions or new desktop evidence.
The [NBomber load scenarios](list-performance.md#load-tests) measure provider and
HTTP workflows. The document HTTP workload sends full replacements through real
SOAP/MTOM. Random small-edit HTTP load is not implemented, and maximum Office
editing capacity remains unmeasured. Target deployment tests with explicit server,
backend and client versions, and correlated desktop/reference checks remain
external prerequisites for broader compatibility or production-scale claims.
