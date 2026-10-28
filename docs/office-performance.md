# Measuring active Office user capacity

The 10,000 successful commits/minute target was not met on the tested shared host.
The 1,500-user primary workload completed 2,998 commits inside its measured minute
without HTTP failures. The larger and faster workloads had HTTP admission failures.
These measurements describe synthetic workloads and do not validate desktop Office capacity.

Maintained profiles use distinct authenticated users and ten documents per user:

| Profile | Users | Package | Save interval per user |
| --- | ---: | ---: | ---: |
| [Primary](../benchmark/CellBridge.Benchmarks/load-office-users.json) | 1,500 | 256 KiB | 30 seconds |
| [Larger documents](../benchmark/CellBridge.Benchmarks/load-office-users-large.json) | 1,500 | 1.5 MiB | 30 seconds |
| [Frequent AutoSave](../benchmark/CellBridge.Benchmarks/load-office-users-autosave.json) | 1,500 | 256 KiB | 8 seconds |
| [Stretch](../benchmark/CellBridge.Benchmarks/load-office-users-stretch.json) | 5,000 | 256 KiB | 30 seconds |

Each capacity profile has 30 seconds of warm-up and 60 seconds of measurement.
User clocks run continuously through one 90-second simulation. Keep NBomber's
separate `WarmUpDuration` at zero; warm-up requests have `warmup-` step names.
The bounded [smoke profile](../benchmark/CellBridge.Benchmarks/load-office-users-smoke.json)
checks the same workflow with ten users. It checks correctness, not capacity,
and the performance workflow runs it for both SQL backends.

Run from the repository root with the prerequisites and NBomber license described
in [load test setup](list-performance.md#load-tests):

```sh
Parameters__LoadMaxConcurrentRequests=128 \
Parameters__LoadMaxConcurrentRequestsPerSubject=2 \
Parameters__LoadPostgreSqlPoolSize=24 \
Parameters__LoadPostgreSqlMaxWalSizeMiB=4096 \
Parameters__LoadQueryStatistics=true \
dotnet run -c Release --project benchmark/CellBridge.Benchmarks -- --load-test --config benchmark/CellBridge.Benchmarks/load-office-users.json
```

Select another profile by changing the config path. The command reproduces the
measured admission, pool and WAL settings; it overrides the shipping defaults.

The runner owns disposable Aspire PostgreSQL and WebDemo resources. Without
overrides, admission defaults to eight requests globally and two per subject.
`Parameters__LoadPostgreSqlMaxWalSizeMiB` optionally changes the disposable
server's WAL allowance while retaining durable commit settings. PostgreSQL explains
[how frequent checkpoints increase I/O](https://www.postgresql.org/docs/18/wal-configuration.html).
`Parameters__LoadPostgreSqlPoolSize` bounds each WebDemo document and Identity
pool separately. Account for both pools and other clients in the server connection
budget. `Parameters__LoadQueryStatistics=true` captures normalized statement totals,
I/O and WAL deltas, durability settings and connection samples in
`artifacts/load-tests/*.pgstats.json`, covering warm-up and measurement before validation.

Independent clocks schedule saves at the profile's interval, queries every
30 seconds and opens every 120 seconds, with random initial phases. Each user
edits one active document; an open selects the next, downloads it and queries its
graph. User actions never overlap. Response time does not extend intervals:
blocked users skip elapsed slots.
Metrics report missed slots and action-start lateness.

The primary workload offers 50 saves/second, or 3,000/minute, over 15,000 documents.
The stretch offers about 166.67 saves/second, or 10,000/minute, over 50,000 documents.
The frequent-AutoSave profile offers 187.5 saves/second, or 11,250/minute.
Intervals apply per user across their documents; ten documents does not multiply
saves tenfold. Queries and opens add requests. Thirty seconds is our assumption:
Microsoft describes cloud Office
AutoSave as saving [every few seconds while working](https://support.microsoft.com/en-us/office/collab-files/what-is-autosave).

These sizes are planning choices, not universal averages. Microsoft's
[SharePoint storage planning example](https://learn.microsoft.com/en-us/SharePoint/administration/storage-and-sql-server-capacity-planning-and-configuration)
uses 250 KB documents and advises estimating the intended collection. Its
[migration guide](https://learn.microsoft.com/en-us/sharepointmigration/sharepoint-online-and-onedrive-migration-speed)
uses Office files around 1.5 MB. Neither is an empirical average.
An [older empirical study](https://www.cs.toronto.edu/~delara/papers/usenix_win2000/html/node9.html)
found long-tailed distributions in 2000-era formats, not current Office packages.
`PackageKiB` and `ChangedPartKiB` vary package and edited-part sizes independently;
the fixture also covers 4 KiB and 64 KiB edits. None is a measured average change size.

Saves make a synthetic small edit to a 16 KiB `word/document.xml` part stored at
the ZIP tail, reuse unchanged chunks and produce globally unique content.
The [protocol's ZIP chunking](https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttpd/1cb2efd5-db6f-40fe-aae6-6957666c4e42)
uses package-entry boundaries; this is different from sending only typed characters.
Reported FSSHTTPB save bytes exclude SOAP/MTOM, HTTP and TLS overhead. This warm
workload does not qualify full-package replacement, cold reads, arbitrary edits,
Excel or PowerPoint traffic.

Setup creates accounts and preissued Identity sessions before timing; this does
not measure login throughput. Runtime requests retain authentication, CSRF and
ownership checks. Documents have separate identities but share initial template
bytes and graph identifiers. Each edit produces different bytes. Shared data avoids
retaining whole packages per user; it does not qualify a cold, distinct 50,000-file
collection. Recovery bulk-seeds an empty owned namespace with document quotas
explicitly raised to 15,000 or 50,000 in migration and runtime. Shipping defaults
stay unchanged. Report setup separately and monitor generator, server and database resources.

Use the strict counter for saves started and acknowledged inside the measured
minute. Late acknowledgements are separate. NBomber step RPS divides counts by
90 seconds and can include retiring invocations; it does not establish the target.
Report successful `document_commit` saves, step p50/p95/p99 latency, cadence and
HTTP failures separately. Replays and conflicts are not new commits. Reject shortened
runs. Final graph, receipt, version and hash checks run outside timing; console
totals include warm-up. Keep state within [storage limits](storage-providers.md#limits-and-qualification)
and retain local reports with source, hardware, runtime, backend and admission settings.

## Measured results

These runs used the final core changes represented by `d239141` on 2026-10-27 UTC.
NBomber, WebDemo and PostgreSQL 18.3 shared a host with four logical CPUs and
approximately 13 GiB memory. The SDK was `11.0.100-rc.3.26522.148`. Admission was
128 requests globally and two per subject; WebDemo's document and Identity pools
each allowed 24 connections. PostgreSQL used a 4,096 MiB WAL allowance with
`fsync`, synchronous commit and full-page writes enabled. The document quotas
were explicitly 15,000 for the 1,500-user profiles and 50,000 for stretch.

CSV files below are local generated reports under `artifacts/load-tests/`.

| Profile | Commits inside measured minute | Save starts | Late acknowledgements | Failed HTTP steps, including warm-up | CSV filename |
| --- | ---: | ---: | ---: | ---: | --- |
| Primary | 2,998 | 3,000 | 2 | 0 | `nbomber_report_2026-10-27--23-40-21.csv` |
| Larger documents | 2,966 | 2,998 | 23 | 33 | `nbomber_report_2026-10-27--23-43-50.csv` |
| Frequent AutoSave | 6,569 | 11,244 | 117 | 9,529 | `nbomber_report_2026-10-27--23-23-19.csv` |
| Stretch | 5,690 | 9,997 | 79 | 14,677 | `nbomber_report_2026-10-27--23-18-09.csv` |

Only primary passed the check requiring no HTTP failures. All reported failures
in these runs were HTTP 503 admission rejections. Late acknowledgements are
successful commits outside the strict time window, counted separately from
failures. All four runs reported zero missed cadence slots, and every acknowledged
commit had a distinct verified payload hash. Primary's maximum action-start
lateness was 22 ms. Mean serialized save sizes were 17,732 bytes for primary and
stretch, 17,733 for frequent AutoSave and 17,770 for larger documents. All changed
ZIP byte totals were 16,700 per save. The common unchanged package parts remained warm.

Primary's successful save p50/p95/p99 latencies were 23.44/46.17/69.06 ms; query
latencies were 7.11/17.41/24.54 ms and open-download latencies were
4.27/12.09/18.41 ms. These CSV step measurements exclude scheduled waits and can
include late completions. Use the strict counter above for commits/minute.

A separate eight-client, 1 MiB full-replacement diagnostic improved from 297 to
377 saves in 30 seconds, or 9.90 to 12.57 saves/second, without HTTP failures in
the final run (`nbomber_report_2026-10-27--23-46-41.csv`).
It reused a fixed 16-file palette: 488 commits including warm-up produced only
16 distinct payload hashes. It measures a different workload from unique paced
incremental saves and does not establish active-user capacity. Longer runs and
separate generator hardware still need qualification; these measurements do not
predict that another host configuration will meet the target.

## History growth and algorithm experiments

The many-user runs above do not establish performance for documents with long edit
histories. Current state contains immutable revision records whose graphs retain
earlier elements. Copying and traversing each cumulative membership makes work grow
quadratically when every edit adds a fixed number of elements.

Four storage approaches were considered. The complexity estimates below separate
retained revision count `n`, retained graph size `g`, and changed data `delta` from
current package validation and response serialization.

| Approach | Intended metadata work per save | Status |
| --- | --- | --- |
| Shared graph memberships | `O(g + n)` for normal append histories | Implemented and measured |
| Durable persistent graph and revision trees | Approximately `O(delta log g + log n)` | Design only; requires durable root publication and collection |
| Relational immutable revisions with a small current-state record | Approximately `O(delta log g + log n)` with the necessary indexes | Preferred longer-term design; requires new bounded mutation and response contracts |
| Append log with bounded checkpoints | `O(delta)` foreground append, plus checkpoint and index costs | Design only; checkpoint work and cold replay still need measurement |

The shared-membership experiment preserves every revision and descriptor. Compact
decoding keeps table views, capture shares unchanged prefixes, and codec validation
and reference accounting visit shared backing slots once. Reordered multi-range
memberships retain a potentially nonlinear validation fallback. Full state still
contains revision metadata and retained graph descriptors; this is not a constant-time
or logarithmic save implementation.

A separate optimistic-preparation experiment removes a read-only save-preparation
coordination transaction. It retains the fresh initial read, provider clock and all
final locked publication checks. It targets request overhead rather than history
growth. Both changes are compared separately in the same authenticated HTTP workload.

Selective delta loading then separates the complete retained descriptor catalog
from the graph used to materialize a self-contained proposed file. It reads all
retained storage indexes, reused incoming identities and specifically selected old
object groups, stages only new graph objects and preserves every old descriptor.
General inherited, BLOB-backed and multi-cell cases retain the full-graph path.
Metadata and knowledge processing still depend on the catalog size. Unrelated old
payload corruption is checked when those payloads are read, rather than on every
new save; selected and reused payloads still require verification before staging.

The retained preparation implementation also reuses the coherent view taken
freshly at file-save entry across digest calculation. It does not reuse an earlier
SOAP lookup or authorization observation. Final publication checks current authority
and the prepared authority, so concurrent changes and lease expiry can reject the
prepared save. Exact receipt replay still revalidates current access and version.

Run the library diagnostic without starting an HTTP host:

```sh
DOTNET_TieredCompilation=0 dotnet run -c Release --project benchmark/CellBridge.Allocations.Benchmark -- --history-scaling --source local --output artifacts/office-performance/history-scaling.json
```

It uses actual synthetic full-package protocol saves of one small DOCX in memory.
Setup, request generation and verification are outside measured intervals. Each
measured save checks committed content and exact revision counts. Its isolated
state store shares a warm content store and deterministic next payload with other
samples; this does not measure novel-content persistence. Measurements include CPU,
wall time and allocated bytes, with fresh state objects for admission to avoid its
serialized-size cache. `--checkpoints 1,10,50,100,250` and `--iterations 15` are the
defaults. `--snapshots DIRECTORY` preserves aged state JSON and verified content.
This diagnostic does not establish HTTP capacity or desktop Office compatibility.

The first controlled comparison on 2026-10-28 used the same shared host and
admission, pool, WAL and durability settings as above. Each HTTP run offered
11,250 saves/minute from 1,500 users at an eight-second cadence, plus queries and
downloads, with 30 seconds of continuous warm-up and 60 seconds of measurement.

| Implementation | Strict commits/minute | Failed HTTP steps including warm-up | Local CSV |
| --- | ---: | ---: | --- |
| Baseline | 6,481 | 9,591 | `nbomber_report_2026-10-28--04-59-44.csv` |
| Shared memberships only | 6,297 | 9,938 | `nbomber_report_2026-10-28--05-14-15.csv` |
| Optimistic preparation only | 6,914 | 8,478 | `nbomber_report_2026-10-28--05-09-58.csv` |
| Shared memberships and optimistic preparation | 6,928 | 8,617 | `nbomber_report_2026-10-28--05-22-29.csv` |
| Plus selective delta loading | 7,579 | 7,737 | `nbomber_report_2026-10-28--05-43-31.csv` |
| Plus one fresh save-entry view | 7,919 | 6,998 | `nbomber_report_2026-10-28--05-53-33.csv` |
| Combined publication read, reverted | 7,886 | 7,123 | `nbomber_report_2026-10-28--06-01-52.csv` |
| Deferred initial binary read, reverted | 7,905 | 7,069 | `nbomber_report_2026-10-28--06-19-23.csv` |

All runs overloaded. Every acknowledged payload hash was distinct and verified,
and all runs reported zero missed cadence slots. The small differences in these
single runs do not establish statistical significance. The last two read experiments
did not improve throughput and were reverted. Shared memberships did not improve
the young-document stress result on their own; their benefit appeared in the aged
library workload. The retained combination increased strict acknowledgements by
about 22% over this fresh baseline, but did not meet the 10,000/minute target.

The retained implementation was also checked below the overloaded stress rate:

| Save interval, 1,500 users | Strict commits/minute | Save starts | Late acknowledgements | Failed HTTP steps including warm-up | Successful-save p95 | Local CSV |
| --- | ---: | ---: | ---: | ---: | ---: | --- |
| 30 seconds | 2,998 | 3,000 | 2 | 0 | 35.49 ms | `nbomber_report_2026-10-28--06-35-57.csv` |
| 15 seconds | 5,994 | 5,998 | 4 | 0 | 133.63 ms | `nbomber_report_2026-10-28--06-44-45.csv` |
| 12 seconds | 7,337 | 7,497 | 104 | 565 | 926.72 ms | `nbomber_report_2026-10-28--06-40-59.csv` |

All retained-profile acknowledgements had distinct verified payload hashes and
zero missed cadence slots. The 15-second profile offers 6,000 saves/minute; it
passed this bounded run, not a production SLA. The baseline was not measured at
that cadence, so it is not evidence of a before/after capacity doubling. The
12-second workload offers 7,500 saves/minute and did not pass the no-failures gate.
Queries and opens retained their 30- and 120-second clocks in every run.

During the final 30 seconds of the reverted publication-read stress experiment,
host samples averaged fewer than 0.2 idle cores out of four and about 17 runnable
tasks, with negligible I/O wait. The HTTP generator, server, PostgreSQL and Aspire
infrastructure shared that host. These observations constrain this local comparison;
they do not establish an intrinsic server limit or predict another host's capacity.

At 250 revisions, the baseline and retained library implementations produced these
median wall times with 15 samples after warm-up and tiered compilation disabled:

| Operation | Baseline | Shared memberships and optimistic preparation | Retained implementation |
| --- | ---: | ---: | ---: |
| Encode current state | 37.22 ms | 3.92 ms | 3.78 ms |
| Decode current state | 22.92 ms | 12.89 ms | 12.68 ms |
| Fresh state admission | 40.89 ms | 3.10 ms | 3.02 ms |
| Full-package save | 100.78 ms | 12.39 ms | 9.32 ms |

Decode allocation fell from 17.73 MB to 3.50 MB per operation. Other stages can
allocate more despite lower CPU time: the intermediate full-save diagnostic allocated
9.35 MB versus 8.01 MB in the baseline. Selective delta capture lowered the retained
implementation's full-save allocation to 6.01 MB. Both states
still represented 1,250 current elements and 158,125 logical graph memberships.
Local raw results are `artifacts/office-performance/history-scaling-baseline.json`
and `history-scaling-final.json`; the intermediate result is
`history-scaling-combined.json`. The repeated-save diagnostic uses warm shared
immutable content; the HTTP workload above uses globally distinct edits.

The final diagnostic also checked 999 revisions before a measured save appended
the 1,000th. Decode allocated 14.04 MB versus 3.50 MB at 250 revisions, and full-save
time was 39.86 ms versus 9.32 ms. Tests independently check shared backing slots,
logical quota boundaries and allocation growth through 1,000 revisions. These
results support the normal append-history improvement; they do not remove the
linear metadata work or the arbitrary-membership fallback described above.
