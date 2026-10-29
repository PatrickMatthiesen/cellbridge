# Measuring active Office user capacity

The 10,000 successful commits/minute target remains unmet. The latest measured
replacement acknowledged 4,854 saves inside a minute while offering 11,250,
with HTTP 503 overload failures. A separate run offering 3,000 saves/minute
passed without HTTP failures, with 2,992 acknowledged inside its minute.
Both used 1,500 authenticated users and 15,000 documents.

The archived implementation reached 8,151 inside a measured minute with HTTP
failures. The rewrite has not demonstrated a throughput gain over that result.
These synthetic workloads do not validate desktop Office capacity.

## Document staging admission

The security fix charges unique content handles and ownership metadata against
the affected document before physical staging. A failed save keeps its pending
charge until successful publication or explicit quiescent collection. This adds
one transaction under that document's row lock per content batch. Physical
staging and final publication retain their separate transactions. It adds no
provider-wide counter or bucket. Known revision, receipt and delivery limits,
and the combined response-size limit, reject before physical staging.

Fresh runs retained the pool cap of 64, automatic preparation cap of 64, durable
PostgreSQL settings, request admission of 128 globally/two per subject, and the
same 256 KiB package/16 KiB changed-XML workload, warm-up and measurement periods.

| Profile | Offered saves/minute | In-window acknowledgements | Excluded late acknowledgements | HTTP result |
| --- | ---: | ---: | ---: | --- |
| Primary, save every 30 seconds | 3,000 | 2,992 | 7 | No failures |
| AutoSave, save every 8 seconds | 11,250 | 4,854 | 114 | Overload failures, HTTP 503 |

Neither run missed a user cadence slot. All 4,483 primary and 7,024 AutoSave
acknowledgements, including warm-up/drain, passed package, revision, receipt and
distinct-payload verification. The AutoSave step totals, including warm-up/drain
and queries/opens, include 12,956 HTTP 503 responses; they are not a count confined
to saves in the measured minute.

The AutoSave result is about 8% below the earlier 5,286 run. These single local
runs do not isolate the exact cost of the added transaction. The primary workload
still passed at 3,000 offered saves/minute; it does not establish the target
capacity. The earlier measurements below precede this staging-admission fix.

During measurement WebDemo used 135% of one logical CPU and at most 596,426,752
sampled RSS bytes in the primary run. AutoSave used 156% and 755,703,808 bytes.
These are process observations, not allocation or managed-heap measurements.
The PostgreSQL sampler observed no blocked backends in either run; its 250 ms
sampling can miss short waits. Physical object growth remained about 17.47 KiB
per acknowledgement. Document charged bytes grew about 33.55 KiB per
acknowledgement, including the new ownership metadata; this is not physical
database or WAL growth.

The retained reports are
`artifacts/load-tests/office-users-1500-20261029T0658326926623Z-bce1fe02890940d2afdaf6ce8eb9fdb9.pgstats.json`
and
`artifacts/load-tests/office-users-1500-20261029T0703328924140Z-b302b928a68e47ab9e5401d527833992.pgstats.json`.

## Initial replacement measurements

The first completed replacement run used the frequent-AutoSave profile and the
command below: 1,500 authenticated users, 15,000 documents, 256 KiB packages,
16 KiB changed XML, 30 seconds of warm-up and 60 seconds of measurement. It
acknowledged 1,922 saves inside the minute, with 117 late acknowledgements excluded.
It had HTTP 503 admission failures, so this is an overload observation, not passing
capacity. All 2,990 acknowledged saves including warm-up passed final content,
version and receipt validation.

During that minute WebDemo used 121% of one logical CPU and at most 685,334,528
sampled RSS bytes. Runtime counters over 50 seconds within the measurement
reported about 269 MB/s of managed allocations and 1.23 seconds of GC pauses.
The runner and database shared this four-CPU host; WebDemo metrics exclude them.
The database sampler observed no blocked backends at its 250 ms sampling interval;
that does not prove every short wait was absent. Most observed backend time was
idle in transaction, awaiting the application. Individual SQL execution times
were small, while many lookups each opened a separate transaction.

Across warm-up, measurement and completion drain, physical object bytes grew by
53,485,091 for 2,990 saves, about 17.5 KiB/save. Document charges grew by
99,773,938 bytes, about 32.6 KiB/save. These are different measures: charges
include metadata and per-document references; physical objects deduplicate
identical bytes. Allocated table/index space and WAL are separate again. No new
complete 256 KiB package object is required for each edit. Reconstruction,
package hashing and ZIP validation still process the complete candidate file.

A diagnostic rerun with canonical payload batching, reuse of prepared mappings,
three transaction-free immutable lookup APIs and the simpler strict JSON checker
acknowledged 1,933 in-window saves. It included a 40-second managed-stack,
allocation and database trace and is not a clean comparison with the untraced
run. All 3,147 acknowledged saves including warm-up passed final validation.
Further changes require an untraced measurement before claiming a gain. The trace
contains substantial instrumentation overhead, including DiagnosticSource event
serialization and runtime suspension. Managed samples also include waits; they
are not a scheduler CPU-time profile. Its allocation samples identify repeated EF
expression processing, content-buffer copies and object-group parsing. Those
inclusive paths overlap. The captured per-record publication paths were replaced
by the subsequent bounded prefetch implementation; the trace does not measure
that later code.

Import setup is outside request timing. Bounded EF flushes and a verified private
payload spool reduced one 15,000-document import from 336 seconds to about
80 seconds. The earlier attempt stalled during account setup and was stopped;
it produced no capacity result. The change keeps all import flushes and its
recovery receipt inside one atomic transaction. Batching bounds extra EF tracking,
not the caller-supplied complete snapshot's memory.

Raw local results are under `artifacts/office-rewrite/` and
`artifacts/load-tests/office-users-1500-20261029T0019338889908Z-38cca6a2250940648cd74c72bebd06da.pgstats.json`.
The traced report is
`office-users-1500-20261029T0036130440825Z-3de6b77dc7a54257a31e2f148023f069.pgstats.json`
in the same load-test directory. Query statistics cover warm-up, measurement and
drain before final validation. They are not per-request wall-clock traces.
The [implementation cost table](office-operation-costs.md#costs-of-the-replacement-implementation)
records remaining catalog scans, package-sized validation and checkpoint costs.

## Later replacement runs

The same 1,500-user frequent-AutoSave profile produced these untraced overload
results. These are individual local runs, not statistical estimates of a
capacity gain. Every row had HTTP 503 failures and missed the target.

| Working implementation / experiment | Document pool cap | In-window acknowledgements/minute | Excluded late acknowledgements |
| --- | ---: | ---: | ---: |
| First completed replacement | 24 | 1,922 | 117 |
| Bounded publication prefetch and resource-head reads | 24 | 3,075 | 117 |
| Verified payload batches, combined ACK staging and selected-index claims | 24 | 3,299 | 109 |
| Same implementation, larger pool | 64 | 3,881 | 114 |
| External-reader lease fix and request-local immutable metadata reuse | 64 | 3,928 | 110 |
| Same source, automatic preparation cap 64 | 64 | 4,595 | 110 |
| Reused partition reads and batched affected-key current mappings, preparation cap 64 | 64 | 5,286 | 114 |

All 5,807 acknowledged saves including warm-up/drain in the lease-fix row passed final
content, revision, receipt and distinct-payload checks. WebDemo's measured phase
used 135% of one logical CPU and at most 709,591,040 sampled RSS bytes. A separate
one-second `/proc/stat` sampler observed 93.8% host CPU busy over 59.15 seconds
inside that minute, with 0.08% I/O wait and 0.06% steal. The runner used about
45% of one CPU; the database and other processes also shared the four-CPU host.
This is evidence of a local resource limit, not an application-only CPU profile.
Automatic preparation is a benchmark-only connection-string override. It uses
`Parameters__LoadPostgreSqlMaxAutoPrepare=64`; the default minimum usage count
remains five. All 6,696 acknowledged saves including warm-up/drain passed final
verification. Npgsql [documents preparation](https://www.npgsql.org/doc/prepare.html)
as a way to reuse PostgreSQL statement plans; it is disabled by default. The
library and demo defaults are unchanged. The statement limit applies separately
to each physical WebDemo connection; runner/diagnostic connections retain their
existing configuration.

The final read cleanup reused partition headers inside each preparation and
replaced repeated root/history lookups with bounded current-key reads. A
260-key regression on both databases changed preparation from 524 partition,
root and interval queries to four partition, root and current-mapping queries;
the publication fence changed from 520 root/interval queries to three root/current
queries. These counts exclude other preparation/publication work. It adds no
schema, cache across requests or shared lock.

The last row acknowledged 5,286 saves inside the measured minute, about 15%
more than the preceding run with the same pool and preparation settings.
All 7,652 acknowledged saves including warm-up/drain passed final validation.
WebDemo used 159% of one logical CPU and at most 756,051,968 sampled RSS bytes
during measurement. The PostgreSQL sampler observed no blocked backends; short
waits can escape its sampling. The report is
`artifacts/load-tests/office-users-1500-20261029T0321148414127Z-e77cbfcd7d784055a990a9768b7d91cd.pgstats.json`.
This is still an overload result with HTTP 503 failures.

A fresh run of the primary profile used the same final source, pool cap 64 and
automatic preparation cap 64, with each user saving every 30 seconds. It passed
without HTTP failures or missed cadence slots. Of 2,998 saves started during
measurement, 2,994 were acknowledged inside the minute and four completed later.
All 4,482 acknowledged saves including warm-up passed final content, revision,
receipt and distinct-payload checks. Queries and opens ran alongside saves.
WebDemo used 143% of one logical CPU and at most 579,747,840 sampled RSS bytes
during measurement. This single passing run supports approximately 3,000 offered
saves/minute on this host, not the 10,000 target. Its report is
`artifacts/load-tests/office-users-1500-20261029T0326070813059Z-61dbd9ee51f842ee9a6cf421564e1211.pgstats.json`.

That primary run made 80,814 document-head SELECTs. The service paths account
for them exactly: seven per save, eight per query and four per download. Each
open performs both a download and a query. These are current metadata reads,
not history scans. An experiment reused the entry head for the first operation
to remove one read per Cell request. It failed the existing file/metadata tests
where a lease is reacquired after the entry snapshot but before operation
admission: a successful save became a conflict. The experiment was reverted;
all 70 focused tests then passed. This rejects that reuse strategy, rather than
establishing that the current number of reads is minimal.

The archived 8,151/minute overload result remains higher than these replacement
results. The rewrite has not demonstrated an overall throughput gain over it.

The new batch reader initially held its database header connection while
invoking the configured external reader. When both used the same pool, a
one-connection pool stalled. The unchanged actual PostgreSQL and SQL Server
regression now passes after header queries release library-opened connections
before invoking that reader. Already-open caller connections/transactions retain
their ownership. These tests cover the provider pairing used by WebDemo.

A temporary opt-in phase run preceded that fix. Median one-second p50 values
were 1.43 s for save total, 740 ms for graph preparation, 324 ms for guarded
publication, 186 ms for payload verification and 108 ms for package materializing
and validation. Several connection acquisition phases were around 40 ms. Nested
scopes overlap; these percentiles cannot be summed or treated as lock-hold time.
The content writer admission wait was near zero. Those observations motivated
the lease and repeated-lookup fixes; they do not profile the final source.

The retained meter is `CellBridge.Operations`, with histogram
`cellbridge.operation.duration` in seconds and fixed `phase` labels. Without a
listener, its scopes skip timestamp/tag work and allocate no bytes after
initialization. For a diagnostic collection, use the verified WebDemo process ID
from the benchmark log, rather than the benchmark runner:

```sh
TMPDIR="$PWD/artifacts/tmp" dnx --allow-roll-forward dotnet-counters -- collect \
  --process-id "$office_webdemo_pid" \
  --counters 'CellBridge.Operations[cellbridge.operation.duration]' \
  --maxHistograms 64 --maxTimeSeries 256 --refresh-interval 1 \
  --format json --duration 00:00:30 --output artifacts/office-phases.json
```

Set `office_webdemo_pid` to that logged ID first. The collector's `TMPDIR` must
match the target process; the command uses the fixture's default temporary path.
The default histogram cap of ten is insufficient for the 22 fixed phases.
Timings include failed/cancelled attempts and instrumentation overhead, so keep
these collections separate from untraced qualification runs.

## History scaling of the replacement

A separate component diagnostic used one document, in-memory storage and tiny
fixed-timestamp DOCX packages of about 1.25 KB. It verified package bytes and
revision counts at 1, 10, 50, 100 and 250 revisions. Each stage had ten warm-ups
and fifteen measured samples. Construction, aging, isolated-store import and
correctness checks were outside timing. Content was shared and warm; this does
not measure novel-content persistence, HTTP concurrency or Office capacity.

| Component | Allocated bytes at 1 revision | Allocated bytes at 250 revisions |
| --- | ---: | ---: |
| Current head read | 40 | 40 |
| Current partition metadata | 624 | 624 |
| Current bindings | 712 | 712 |
| Current package stream | 80,112 | 80,120 |
| Whole-query client graph | 48,472 | 5,575,616 |
| Next full-package save | 809,256 | 1,063,208 |

These are process allocation-counter deltas including the measurement callback.
Whole-query output processing includes the visible catalog, which grew from four
to 1,000 elements. The save input includes the complete current expected-index
envelope; retained revision mappings make that input grow with history. The
31% save-allocation increase is therefore not an isolated provider bound at fixed
input size. The source-based bounds are in the operation cost guide.

This experiment exposed an implementation bug: expected-value and unreferenced
index envelopes were processed as new serial claims. They now remain input
preconditions; only the selected update index supplies new claims. The earlier
five-sample run allocated 1,827,888 bytes for the aged save; the final run allocated
1,063,208. Different sample counts and runtime tiering prevent a clean CPU speedup
claim. Process CPU can include background GC triggered by setup; short wall times
also depend on the shared host. Allocation counts are the useful evidence here.
Raw results are `artifacts/office-rewrite/history-scaling-final.json` and
`history-scaling-before-selected-claims.json` in that directory.

## Workload profiles

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

## Durable quota allowances and chunk inserts

The next experiment reduced contention on the global storage quota row. PostgreSQL
and SQL Server now keep 64 durable allowance buckets, selected by hashing all bytes
of the document ID. A normal positive atomic save consumes its bucket's allowance
in the same transaction as publication. The slow path locks the global ledger and
then the buckets in a fixed order, admits the save and tops its bucket up toward
4 MiB. Failed claims release their bucket locks before entering that path.

If `C` is globally charged capacity and `R` is each bucket's unused allowance,
exact retained usage is `C - SUM(R)`. Reservations consume quota headroom rather
than permitting an overshoot. Near the limit, the slow path reclaims unused
allowances before checking admission. Deletion, pruning and other ordinary quota
writers also reclaim allowances under the same lock order. Durable rows and
transaction rollback preserve accounting across restart and failed publication.
Both SQL providers use this accounting; the throughput measurements below used
PostgreSQL only.

This changes the frequency of global quota writes, not the history complexity
above. With available headroom and positive saves, global refills depend mainly
on newly charged bytes divided by the 4 MiB allowance, rather than every save.
Saves in the same bucket still serialize. Refills, low-headroom reclamation and
ordinary quota mutations still share the global lock. Increasing user count does
not increase the fixed number of buckets, and larger snapshots consume allowances
sooner. Full state serialization, retained metadata traversal, content insertion
and WAL durability remain costs that this change does not remove.

PostgreSQL also inserts each bounded batch of up to sixteen 64 KiB chunks with
`UNNEST` and three typed array parameters. This reduces command and parameter work
without changing chunk size or stored bytes. Consumer EF customizations and
`SaveChanges` hooks retain the tracked insert path.

The exploratory comparison on 2026-10-28 used the same shared four-CPU,
approximately 13 GiB host, durable database settings and admission configuration
as above. Each run had 1,500 users, 15,000 documents, 30 seconds of warm-up and
60 seconds of measurement. The eight-second cadence offered 11,250 saves/minute.
Packages were synthetic 256 KiB DOCX files with shared padding and a fixed-size
16 KiB XML part at the tail of an uncompressed ZIP. These are single local runs,
not representative average Office files or statistically established gains.

| Eight-second experiment | Strict commits/minute | Failed HTTP steps including warm-up | Quota lock blocked backend observations |
| --- | ---: | ---: | ---: |
| Fresh baseline | 7,960 | 6,697 | Not sampled |
| Baseline with activity sampling | 7,977 | 6,628 | 786 |
| 64 buckets, 4 MiB target | 8,146 | 6,293 | 143 |
| Plus array inserts | 8,690 | 5,489 | 134 |
| Plus complete-document-ID hash | 8,472 | 6,033 | 132 |

The last two runs used the retained array-insert design. Every run overloaded;
the 10,000 strict commits/minute target remains unmet. Activity was sampled every
250 ms, with 378 samples per sampled run. The last column sums blocked backend
observations only when the query class is `quota` and `waitEventType` is `Lock`.
The classifier recognizes queries mentioning `cellbridge_usage`; it does not
identify every quota transaction or prove which relation caused a wait. These
point-in-time observations are neither unique waits nor lock-hold durations.
Other observed waits included WAL synchronization and relation extension.

A 16 MiB target reduced quota lock observations to 55 but completed only 8,243
strict commits/minute. Binary COPY completed 8,425. Both experiments were reverted
in favor of the 4 MiB target and array inserts. A twelve-second cadence with that
design completed 7,492 strict commits and seven late acknowledgements, but had
68 warm-up HTTP failures. It did not pass the no-failures gate despite zero
measurement-period HTTP failures. Startup allowance funding then completed 7,453
strict commits with 188 HTTP failures, including 19 during measurement, and was
also reverted. Raw comparisons and report filenames are in the local generated
`artifacts/office-performance/quota-experiment-comparison.json`.

A final control rebuilt commit `07db700` with the same activity sampler and
pre/post inventory checks as the final implementation. It completed 7,974 strict
commits/minute; the final implementation completed 8,151, a 2.2% difference in
these single runs. Failed HTTP steps including warm-up were 6,349 and 6,334,
respectively. Save p95 was 1,084.41 ms and 1,122.30 ms. The earlier exploratory
high of 8,690 is not the final build's qualified result. Both final runs failed
the overload gate. Quota-class lock blocked backend observations fell from 780
to 121 across 378 samples per run. Raw results are listed in
`artifacts/office-performance/quota-final-comparison.json`.

At a thirteen-second cadence, all 6,923 measurement-period saves succeeded;
6,913 acknowledgements arrived inside the minute and ten arrived late. Queries
and downloads also had no measurement-period failures. The run still failed its
gate because 58 HTTP steps failed during warm-up. It qualifies warmed behavior
for this short run, not a fully passing capacity result.

The final fifteen-second cadence passed with no HTTP failures, including warm-up.
It started 5,997 measured saves, acknowledged 5,995 inside the minute and received
two acknowledgements afterward. All 8,962 saves including warm-up had distinct,
verified committed payloads. Queries and downloads also passed. This run offered
6,000 saves/minute; it does not establish the boundary between this cadence and
the failing thirteen-second run. To reproduce either paced profile, copy the
frequent-AutoSave JSON and change only `SaveIntervalMs` to `15000` or `13000`,
retaining its warm-up, measurement, query and open settings.

The final build issued 12,048 chunk-array inserts averaging 5.02 ms, 12,048
bucket claims averaging 1.49 ms and 958 global ledger locks averaging 13.21 ms.
These database execution times include waiting and are not CPU profiles.
The control acquired the global ledger 12,013 times, averaging 17.59 ms.
The final run wrote 3.63 GB of WAL over warm-up, measurement and drain. Payload
insertion and durability remain substantial work after reducing quota contention.

One-second WebDemo process samples from the 64-bucket run observed a peak RSS of
798.50 MiB and peak PSS of 727.54 MiB. Sampling can miss peaks. These are process
memory observations, not managed heap or GC measurements. The shared host and
remaining waits limit what this comparison says about another deployment; it
does not establish a production SLA or show that other optimizations are exhausted.
Separate one-second samples during the final workload observed peak RSS of
796.55 MiB, peak PSS of 729.93 MiB and mean WebDemo CPU use of 1.35 cores.
Thirty-two one-second host samples near the end of that load averaged 95% CPU
busy, about 17 runnable tasks and 0.03% I/O wait. This includes the generator,
database and Aspire processes on the same host. It supports qualifying with an
independent generator before attributing the plateau to the server alone.

An offline fixed-chunk reuse check used sixteen synthetic documents with twelve
edits each and SHA-256 plus exact chunk length. Distinct padding per document
still allowed 75% within-document byte reuse when fixed-size XML stayed at the
ZIP tail. Moving XML before padding and growing it by seventeen bytes per edit
reduced that reuse to zero as later chunk boundaries shifted. This check split
whole ZIP bytes into 64 KiB chunks; it did not measure HTTP, database storage or
throughput, and excluded protocol graph metadata. The local raw result is
`artifacts/office-performance/quota-chunk-reuse.json`. It supports further
qualification of package layouts, not a large storage-format rewrite or claims
about ordinary Office files.

Final validation passed 2,236 solution tests, with three opt-in tests skipped.
PostgreSQL and SQL Server user HTTP smoke runs each acknowledged all fifty
measured saves and verified fifty-nine distinct committed payloads including
warm-up. All six combined legacy smoke scenarios passed, including ten HTTP
commits and ten replays. These checks establish the exercised behavior, not
SQL Server capacity or live desktop Office compatibility.
