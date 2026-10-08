# Measuring request allocations

`tools/CellBridge.Allocations.Benchmark` measures preloaded inbound parsing,
eager document and file-graph restoration, and nested binary builders. Use it
when choosing allocation changes. It does not measure HTTP ingestion, network
latency, Office rendering or end-to-end save throughput.

Build in Release, then run separate before/after batches on the same machine:

```sh
dotnet build tools/CellBridge.Allocations.Benchmark -c Release
python3 tools/CellBridge.Allocations.Benchmark/run.py --label before --repeats 3 --iterations 30
# Apply the candidate change, then rebuild.
dotnet build tools/CellBridge.Allocations.Benchmark -c Release
python3 tools/CellBridge.Allocations.Benchmark/run.py --label after --repeats 3 --iterations 30
python3 tools/CellBridge.Allocations.Benchmark/compare.py artifacts/allocations/before artifacts/allocations/after
```

Labels must be new. Reports and immutable executable snapshots stay in ignored
`artifacts/allocations/<label>/`. Each batch records HEAD, the production diff
against HEAD, SDK, benchmark source hashes, assembly hashes and runtime settings.
The runner disables tiered compilation for repeatable stage comparisons. It
starts a separate process per stage, workload, backend and repetition, and runs
those processes sequentially. Other work on the machine can still affect latency.

The runner covers seed-42 payloads of 1 KiB, 1 MiB and 8 MiB plus the reviewed
`save-first` and `save-second` fixtures. Each synthetic request contains one
complete file graph. The second reviewed save merges the first save's graph
before restoration. Fixture hashes are checked during setup.

| Stage | Measured operation |
| --- | --- |
| `inbound` | MTOM views, SOAP XML parsing and complete binary request decoding from a preloaded buffer |
| `binary` | Binary request decoding alone |
| `restore` | `StoredDocument.RestoreAsync`, including verified content reads, document/partition content copies and eager file-graph construction |
| `nested` | `StorageManifestBuilder.BuildObjectGroupDataElement`, including its final owning payload array |
| `file-build` | `FileContentPartitionBuilder.BuildQueryChangesResponse`, including its nested builders |

Restore preserves the reviewed retained graph. The nested and file-build stages
build new graphs using its materialized file bytes. They do not reserialize the
original captured graph. The input bytes, parsed graph, persisted state and
provider writes are prepared outside measured intervals. Five warmups precede
30 measured operations. Allocation totals include runtime overhead within that
interval. Comparisons use the median of three batch means and check workload
hashes, generated wire hashes and logical read counts.

Restore runs against the in-memory provider and a fresh filesystem content root
that is removed on exit. Filesystem reads are warmed and include the provider's
integrity verification. Handle lengths count logical content reads, not physical
disk I/O. No development database, Aspire app or shared volume is used.

Reports also include process CPU time, per-operation wall time, p50/p95 latency,
logical reads, retained managed bytes and process peak working set. Retained
managed bytes include the live setup and final result. Process peak includes
setup, JIT and warmup. Neither isolates transient operation peak memory. Use an
allocation profiler or a separate ingestion/load run for that question.

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
