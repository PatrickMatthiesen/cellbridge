# Measuring active Office user capacity

The maintained [primary profile](../benchmark/CellBridge.Benchmarks/load-office-users.json)
models 1,500 distinct authenticated users and the
[stretch profile](../benchmark/CellBridge.Benchmarks/load-office-users-stretch.json)
models 5,000. These are target workloads, not achieved capacity or desktop Office
validation. Both use ten documents per user, nominal 256 KiB packages, 30 seconds
of warm-up and 60 seconds of measurement. User clocks run continuously through
both periods in one 90-second simulation. Keep NBomber's separate `WarmUpDuration`
at zero for this model; warm-up requests have `warmup-` step names.

Run from the repository root with the prerequisites and NBomber license described
in [load test setup](list-performance.md#load-tests):

```sh
dotnet run -c Release --project benchmark/CellBridge.Benchmarks -- --load-test --config benchmark/CellBridge.Benchmarks/load-office-users.json
dotnet run -c Release --project benchmark/CellBridge.Benchmarks -- --load-test --config benchmark/CellBridge.Benchmarks/load-office-users-stretch.json
```

The bounded `load-office-users-smoke.json` profile checks the same session,
clock and incremental-save workflow with ten users. It checks correctness,
not capacity, and the performance workflow runs it for both SQL backends.

The runner owns disposable Aspire PostgreSQL and WebDemo resources. It retains
the shipping document admission defaults of eight concurrent requests globally
and two per subject. Experiments can override these through
`Parameters__LoadMaxConcurrentRequests` and
`Parameters__LoadMaxConcurrentRequestsPerSubject`; report the exact limits.
`Parameters__LoadPostgreSqlMaxWalSizeMiB` optionally changes the disposable
PostgreSQL server's WAL allowance. It retains durable commit, fsync and full-page
writes. Record the database settings printed during setup. PostgreSQL explains
[how frequent checkpoints increase I/O](https://www.postgresql.org/docs/18/wal-configuration.html).
`Parameters__LoadPostgreSqlPoolSize` bounds each WebDemo document and Identity
connection pool separately. Account for both pools and other clients when setting
PostgreSQL's connection limit. `Parameters__LoadQueryStatistics=true` enables
`pg_stat_statements` and I/O timing in the disposable PostgreSQL server. The runner
captures normalized statement totals, WAL and I/O counter deltas, durability
settings and connection samples in `artifacts/load-tests/*.pgstats.json`.
These diagnostics cover warm-up and measurement together, before final validation.

Each user has independent clocks for saving every 30 seconds, querying changes
every 30 seconds and opening another document every 120 seconds. Initial phases
are random within each interval. One document is active per user; an open switches
to the next document, downloads it and queries its graph. Actions never overlap
for a user. Response time does not extend the scheduled intervals; blocked users
skip elapsed slots rather than issue catch-up bursts. Metrics report missed slots
and action-start lateness.

The primary workload offers 50 saves/second, or 3,000/minute, over 15,000 documents.
The stretch offers about 166.67 saves/second, or 10,000/minute, over 50,000 documents.
The save interval applies per user across their documents. Ten documents does not
multiply saves tenfold. Separate queries and opens add requests to those totals.
Thirty seconds is our workload assumption: Microsoft describes cloud Office
AutoSave as saving [every few seconds while working](https://support.microsoft.com/en-us/office/collab-files/what-is-autosave).

There is no established universal average document or save size. Microsoft's
[SharePoint storage planning example](https://learn.microsoft.com/en-us/SharePoint/administration/storage-and-sql-server-capacity-planning-and-configuration)
uses 250 KB documents and advises estimating the intended collection; its
[migration guide](https://learn.microsoft.com/en-us/sharepointmigration/sharepoint-online-and-onedrive-migration-speed)
uses Office files around 1.5 MB as another example. Neither is an empirical average.
An [older empirical study](https://www.cs.toronto.edu/~delara/papers/usenix_win2000/html/node9.html)
found broad, long-tailed size distributions, but its 2000-era formats do not
represent current Office packages. Our nominal 256 KiB case is a planning choice.
Run 1.5 MiB and larger package sensitivity cases separately. `PackageKiB` and
`ChangedPartKiB` vary the complete package and edited part independently; the
fixture also covers 4 KiB and 64 KiB edited parts. None is an empirical average
change size.

Saves make a synthetic small edit to a 16 KiB `word/document.xml` part stored at
the ZIP tail, reuse unchanged chunks and produce globally unique content.
The [protocol's ZIP chunking](https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttpd/1cb2efd5-db6f-40fe-aae6-6957666c4e42)
uses package-entry boundaries; this is different from sending only typed characters.
The runner reports actual serialized FSSHTTPB save bytes and changed-chunk bytes,
not an assumed average upload size. Binary byte totals exclude SOAP/MTOM, HTTP
and TLS overhead. This warm immutable-content workload does not qualify full-package
replacement, cold reads, arbitrary edits, Excel or PowerPoint traffic.

Setup creates accounts and valid Identity sessions before timing. Preissued cookies
avoid measuring the login endpoint's limiter; runtime requests still use normal
authentication, CSRF protection and document ownership checks. This is not a login
throughput test. Shared immutable corpus data avoids retaining whole-package arrays
for every user. Catalog documents start with identical template bytes and graph
identifiers scoped by their separate document identities. Every edit produces
different file bytes. This does not qualify a cold, fully distinct 50,000-file
collection.
Bulk seeding uses the existing recovery API in an empty owned namespace. The
fixture explicitly raises the document quota to 15,000 or 50,000 in both the
migration ledger and runtime; shipping defaults stay unchanged. Report setup
duration separately and monitor generator memory, server resources and database waits.

Use the bounded measurement counter for commit capacity. It counts saves started
and acknowledged within the exact measurement window and reports late
acknowledgements separately. NBomber can include retiring invocations beyond its
nominal duration, and step rates use the full 90-second simulation rather than
our 60-second measurement. Its step RPS alone does not prove the target. Count only
successful `save` steps with status `document_commit`. Report save,
query and open p50/p95/p99 latency, achieved cadence, missed slots, HTTP 429/503,
timeouts and other failures. Replays and conflicts are not new commits. Reject
shortened runs. Final graph, receipt, version and content-hash validation occurs
outside timing; console totals include warm-up. Keep per-document state within
[storage limits](storage-providers.md#limits-and-qualification). Save reports from
`artifacts/load-tests/` with source, runtime, hardware, backend and admission limits.
Longer runs must establish sustained behavior. Saturation diagnostics can locate
bottlenecks but do not establish active-user capacity or coauthoring support.

For matching PostgreSQL or SQL Server content and state stores, bounded saves
publish new immutable content, the graph, package and receipt in one transaction.
Other providers and larger saves retain the separate content-write path. Batch
reads and writes retain length, hash, quota and corruption checks. Existing
immutable objects are fully verified without taking exclusive creation locks;
new objects still take those locks. This lets unrelated documents reuse common
template parts without serializing their saves on those parts.
