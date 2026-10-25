# Measuring request allocations

`benchmark/CellBridge.Allocations.Benchmark` measures preloaded inbound parsing,
eager document and file-graph restoration, and nested binary builders.
BenchmarkDotNet owns process isolation, warm-up, measurement and allocation
reporting for synthetic inputs. It does not measure HTTP ingestion, network
latency, Office rendering or end-to-end save throughput.

Build in Release and select the methods to measure:

```sh
dotnet build benchmark/CellBridge.Allocations.Benchmark -c Release
dotnet benchmark/CellBridge.Allocations.Benchmark/bin/Release/net10.0/CellBridge.Allocations.Benchmark.dll --filter '*' --artifacts artifacts/allocations/bdn --exporters json
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
Use a fresh output directory for each comparison and retain the exact source and
build outputs. Other work on the machine can affect timing.

## Current PostgreSQL provider cases

The storage benchmark also measures complete 1 MiB content reads, batches of
eight new content objects, and batches of eight document saves. Save cases use
`changed-full` and `delta` requests with four prior saves per document. Database
creation, content preparation and save-history construction happen outside the
timed method. Iteration cleanup checks stored content, restored documents, save
receipts and revision counts.

Run these cases with standard BenchmarkDotNet jobs:

```sh
dotnet run -c Release --project benchmark/CellBridge.Storage.Benchmark -- --filter '*PostgreSqlContent*' --artifacts artifacts/provider-benchmarks/content --exporters json
dotnet run -c Release --project benchmark/CellBridge.Storage.Benchmark -- --filter '*PostgreSqlServiceSave*' --artifacts artifacts/provider-benchmarks/saves --exporters json
```

Each case starts a disposable PostgreSQL environment through the shared test
infrastructure and removes its generated database afterward. It does not use the
local AppHost or persistent volume. BenchmarkDotNet records runtime, job, timing
and allocation results. Keep reports under ignored `artifacts/`.

## Reviewed captures and provider diagnostics

The `--diagnostics` mode retains reviewed SharePoint requests, logical provider
read counts and stage reports for comparisons that need those details:

```sh
dotnet benchmark/CellBridge.Allocations.Benchmark/bin/Release/net10.0/CellBridge.Allocations.Benchmark.dll --diagnostics --workload save-second --stage restore --content filesystem --iterations 30 --output artifacts/allocations/capture/restore.json
```

Use `--workload synthetic --bytes 1048576` for synthetic diagnostic input, or
`save-first` and `save-second` for the reviewed fixtures. The second save merges
the first save's graph before restoration. Setup verifies the fixture hashes.
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
Target deployment load tests with explicit server, backend and client versions,
and correlated desktop/reference checks remain external prerequisites for
broader compatibility or production-scale claims.
