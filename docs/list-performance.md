# Measuring list performance

`tools/CellBridge.Lists.Benchmark` measures durable list creates, independent-record
patches and database-filtered pages. It uses the public provider APIs and an actual
loopback HTTP host with a local benchmark bearer scheme. The HTTP credential exists
only for this disposable runner; use a production authentication handler in a host.
The repository's document-storage and allocation benchmarks measure different work.

Each invocation creates its own PostgreSQL database and restricted runtime login,
runs the normal setup and health checks, and drops only those generated resources.
The administrative connection must permit database and role creation. Runtime
operations retain authorization, forced workspace RLS, validation, conditional
revisions, receipts, auditing, foreign keys and synchronous commits. Connection
strings and credentials are excluded from the report.

## Run a local measurement

Build the executable before starting its Aspire AppHost:

```sh
dotnet build tools/CellBridge.Lists.Benchmark -c Release
aspire start --apphost tools/CellBridge.Lists.Benchmark/aspire/apphost.cs --non-interactive
aspire wait lists-benchmark-postgres --apphost tools/CellBridge.Lists.Benchmark/aspire/apphost.cs --non-interactive
aspire resource lists-benchmark start --apphost tools/CellBridge.Lists.Benchmark/aspire/apphost.cs --non-interactive
aspire logs lists-benchmark --apphost tools/CellBridge.Lists.Benchmark/aspire/apphost.cs --follow --non-interactive
aspire stop --apphost tools/CellBridge.Lists.Benchmark/aspire/apphost.cs --non-interactive
```

The log ends with the report's path. The executable exits nonzero if an operation,
persisted-record verification or measurement check fails. A finite executable's
terminal resource state is not a replacement for its exit code and JSON report.
Stop another AppHost from this checkout before starting this one if their dashboard
ports conflict. Leave unrelated checkouts' AppHosts alone.

Set `Benchmark__Clients`, `Benchmark__Workspaces`, `Benchmark__Iterations`,
`Benchmark__Modes`, `Benchmark__Records` and `Benchmark__Output` before `aspire start`.
Defaults are 1/8/32 writers, 1/16 workspaces, 200 iterations per writer, both provider
and HTTP modes, and 1,000 records for the separate query workload. Each writer
cycles through the requested customer workspaces. Cases measure creates and patches
separately. Warm-up, setup and final persisted-record checks are outside the timed
write interval.

For 100,000-record query qualification, set `Benchmark__Records=100000`. Preparation
uses `CreateRecordAsync` for every row and records its elapsed time separately.
Query cases filter and sort the integer field, request 100-row pages and verify
the first and second pages. They do not establish performance for every text sort
or arbitrary combination of filters. Index selected fields based on real queries.
Use `Benchmark__SkipRecords=true` when measuring writes alone.

## Measure the 4,000-writes/minute baseline

This Linux example assigns the application and PostgreSQL to the same four logical
CPUs and limits the PostgreSQL container to 4 GiB. Pick CPU IDs available on the
machine. The application/client share those CPUs; the host's other work can still
affect the result.

```sh
Benchmark__Clients=8 Benchmark__Workspaces=16 Benchmark__Iterations=500 \
Benchmark__Modes=http Benchmark__SkipRecords=true Benchmark__TargetPerMinute=4000 \
Benchmark__CpuSet=0,1,2,3 Benchmark__PostgresMemory=4g \
Benchmark__Output=artifacts/lists-performance/baseline.json \
aspire start --apphost tools/CellBridge.Lists.Benchmark/aspire/apphost.cs --non-interactive
```

Then wait for PostgreSQL and start `lists-benchmark` with the commands above.
The create case offers 4,000 writes over one minute; the patch case does the same
in its own interval. Positive `Benchmark__TargetPerMinute` uses staggered writer
deadlines and retains the full offered interval in the throughput denominator.
Late operations count toward latency and extend elapsed time. This is a paced,
bounded-writer workload, not an unrestricted open-loop request flood.

Set the target to zero, its default, to measure saturation. Compare one busy
workspace with many customer workspaces. Shared per-list numbering and workspace
accounting still require short counter-row coordination near commit. Extra writers
do not guarantee a higher rate within one workspace.

## Interpret the report

Reports include successes, failures and their categories, attempted/offered and
successful rates, p50/p95/p99 latency, SQL execution and commit distributions,
application CPU/allocation/GC/memory data, PostgreSQL durability settings, runtime,
CPU model/affinity, source commit/dirty status and loaded assembly hashes. HTTP
measurements include the loopback client and server in one process. Memory and CPU
are process measurements, not PostgreSQL-container measurements; peak memory is
cumulative across cases. JSON-body byte counts are not network or WAL byte counts.

SQL executions count Npgsql commands or batches, not individual statements inside
a batch. Durable commits use Npgsql's completed-transaction events and are reported
separately. Successful measured creates and patches must observe one commit.
Typical record mutations use two SQL executions, or three when assigning references,
plus commit. A concurrent create that missed another caller's receipt can require
a further recovery lookup. Provider queries use two executions plus commit; the
native HTTP query also loads cursor/schema metadata, making three executions and
two commits. Cold health checks are excluded through warm-up.

Keep raw JSON under ignored `artifacts/`. Record hardware, storage and deployment
network conditions when sharing results. A passing local run qualifies that
declared workload and environment; it is not a general production capacity claim.
