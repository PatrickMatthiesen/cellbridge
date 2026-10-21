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

The results below describe earlier allocation changes. They are not the SQL
baseline for a later storage-provider comparison.

## Measured nested copy reduction

The baseline was merged commit `58958fcb5b4a777f99b72bbd3e25620753d0ec81`,
PR #62. Measurements used Debian GNU/Linux 13, Linux x64, .NET SDK 10.0.401,
.NET runtime 10.0.12, four reported processors, workstation GC and tiered
compilation disabled. The providers were the source versions at that baseline
plus the candidate binary-writer change. No Office client ran during measurement.

Nested builders previously copied each child writer into a temporary array,
then copied that array into the parent. The internal `BinaryWriterEx.CopyTo`
method copies directly into the parent's owned buffer. Header lengths, framing
and final defensive arrays are unchanged. Only the object-group builder and the
file builder's nested transfers changed.

These are allocated bytes per operation, rounded to the nearest byte:

| Input | Object-group before | Object-group after | Reduction | File builder before | File builder after | Reduction |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Synthetic 1 KiB | 9,840 | 7,632 | 22.4% | 30,064 | 26,480 | 11.9% |
| Synthetic 1 MiB | 6,295,641 | 4,198,332 | 33.3% | 10,506,850 | 7,360,780 | 29.9% |
| Synthetic 8 MiB | 50,336,656 | 33,558,256 | 33.3% | 83,906,661 | 58,740,006 | 30.0% |
| First save's 13,506 file bytes | 84,744 | 57,568 | 32.1% | 154,896 | 113,864 | 26.5% |
| Second save's 14,005 file bytes | 87,736 | 59,560 | 32.1% | 159,896 | 117,360 | 26.6% |

At large sizes, object-group allocation scales from about six to four times
payload size. File-builder allocation scales from about ten to seven times
payload size. The reductions remove two payload-sized temporary arrays in the
opaque builder and one more in the file builder. Small metadata arrays account
for the remaining savings. Latency varied between runs; these results establish
an allocation reduction, not a latency or server-capacity guarantee.

Parsing and restoration were controls. At 8 MiB, preloaded inbound parsing
allocated 8,432,503 bytes, binary parsing 8,395,510 bytes and in-memory eager
restore 50,348,883 bytes before and after. Restore read six unique handles totaling
16,777,786 logical bytes. The reviewed first/second restored graphs contained
23/38 elements and required 23/36 unique reads. Eager restoration remains costly,
but this measurement does not justify changing defensive ownership or adopting
a lazy graph architecture. Filesystem restore allocations stayed within 0.01%.

## Measured restore comparison reduction

Eager restore previously compared the document bytes with
`FilePartition.Content`. That public getter returns a defensive copy, so the
comparison allocated another complete file buffer. Restore now compares spans
under the partition lock. Public getters still return independent arrays, and
different verified document and partition bytes still reject as corruption.

The October 17, 2026 run used source `465ffd749461c92a048f61ef347eb8d71664baae`
plus this comparison change, Windows build 26200, x64, .NET SDK 10.0.401 and
runtime 10.0.13, 16 reported processors, workstation GC and disabled tiered
compilation. Both batches used the runner above with three repetitions and 30
measured operations after five warmups. A disposable Aspire test host was idle
during both batches. This was an offline stage benchmark, not an HTTP load test.

These are median batch means in allocated bytes per eager restore, rounded to
the nearest byte:

| Input | Content provider | Before | After | Reduction |
| --- | --- | ---: | ---: | ---: |
| Synthetic 1 KiB | In-memory | 22,896 | 21,848 | 4.58% |
| Synthetic 1 MiB | In-memory | 6,309,757 | 5,261,121 | 16.62% |
| Synthetic 8 MiB | In-memory | 50,348,883 | 41,960,522 | 16.66% |
| Synthetic 8 MiB | Filesystem | 50,758,069 | 42,366,653 | 16.53% |
| First save's 13,506 file bytes | In-memory | 131,344 | 117,808 | 10.31% |
| Second save's 14,005 file bytes | In-memory | 171,448 | 157,416 | 8.18% |

At 8 MiB the change removes about one payload-sized allocation per restore.
In-memory allocation falls from about six to five times payload size. Logical
read counts and bytes, graph sizes, workload hashes and generated wire hashes
matched across all 90 before/after stage pairs. Parser and nested-builder
allocation controls remained effectively unchanged. Wall times varied, so this
run establishes the allocation reduction without a latency claim.

`ContentStoreReaderTests` covers same-length corruption using distinct verified
handles and mutation isolation of restored document/partition content views and
graph materialization. Raw reports, executable snapshots and source manifests
are retained locally under ignored `artifacts/allocations/issue33-before` and
`issue33-after`. Inbound streaming, eager graph construction and loading retained
graphs for explicit multi-cell queries remain separate work under #33.

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
The full main-solution run passed 987 tests, with 76 existing opt-in live/durable
tests skipped. Quota, save publication, unsupported upload modes and parser
bounds retain their existing regressions and behavior.

The [reviewed fixture provenance](../testdata/sharepoint/fixtures/save_reopen/README.md)
identifies the selected SharePoint exchanges and sanitization. Exact historical
Office and SharePoint builds are absent from those fixtures. They are offline
selected exchanges, not complete reference sessions or new desktop evidence.
Target deployment load tests with explicit server, backend and client versions,
and correlated desktop/reference checks remain external prerequisites for
broader compatibility or production-scale claims.
