# Measuring list performance

`benchmark/CellBridge.Lists.Benchmark` measures durable list creates, independent-record
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

The focused project AppHost owns an ephemeral PostgreSQL container. Workloads
require an explicit start and never run during ordinary WebDemo development:

```sh
aspire start --apphost benchmark/CellBridge.Benchmark.AppHost/CellBridge.Benchmark.AppHost.csproj --isolated --non-interactive
aspire wait benchmark-postgres --apphost benchmark/CellBridge.Benchmark.AppHost/CellBridge.Benchmark.AppHost.csproj --non-interactive
aspire resource lists-benchmark start --apphost benchmark/CellBridge.Benchmark.AppHost/CellBridge.Benchmark.AppHost.csproj --non-interactive
aspire logs lists-benchmark --apphost benchmark/CellBridge.Benchmark.AppHost/CellBridge.Benchmark.AppHost.csproj --follow --non-interactive
aspire stop --apphost benchmark/CellBridge.Benchmark.AppHost/CellBridge.Benchmark.AppHost.csproj --non-interactive
```

The log ends with the report path. Check the executable exit code and JSON
`successful` field. A terminal resource state alone does not establish success.
Stop only the session started for this workflow. Leave other worktrees' AppHosts
and developer databases alone.

Workload arguments use a JSON array in `ListsBenchmark__Arguments` before
`aspire start`. These inputs choose measurements, not resources or startup topology:

```sh
ListsBenchmark__Arguments='["--clients","8","--workspaces","16","--iterations","500","--modes","http","--records-skip","--target-per-minute","4000","--output","artifacts/lists-performance/paced.json"]' \
aspire start --apphost benchmark/CellBridge.Benchmark.AppHost/CellBridge.Benchmark.AppHost.csproj --isolated --non-interactive
```

Then wait for PostgreSQL and explicitly start `lists-benchmark` as above. The
create case offers 4,000 writes over one minute; the patch case does the same in
its own interval. Staggered writer deadlines retain the full offered interval
in the rate denominator. Late operations count toward latency and extend elapsed
time. This is a paced workload with a bounded writer count.

The default arguments select 1/8/32 writers, 1/16 workspaces, 200 iterations per
writer, both provider and HTTP modes, and 1,000 records for query measurements.
Each writer cycles through the workspaces. Set `--target-per-minute 0`, the
default, for saturation. Set `--records 100000` for larger query preparation,
or `--records-skip` for writes alone. Record creation uses the public engine;
its preparation time is reported separately. Query cases filter and sort an
integer field and request first and second pages. They do not qualify arbitrary
filters or text sorts.

Startup, schema and role setup, list/record creation, connection/HTTP warm-up
and final persistence probes are outside the measured write intervals. Each
operation still checks successful creation or revision advancement, result page
size and SQL instrumentation. Detailed revision, payload, ordering and persistence
assertions run in `tests/CellBridge.Benchmarks.Tests`. Workload tests use disposable
PostgreSQL through `CellBridge.TestInfrastructure` and cover provider and HTTP paths.

Records have a title, integer counter and deterministic ASCII payload generated
once before measurement. `--payload-bytes` defaults to 1,024 and accepts 0 through
60,000. The report retains payload and serialized values sizes. Use external
process/container limits when comparing constrained machines and record those
settings with the results. The AppHost does not apply machine-specific CPU IDs.
These commands establish the measurement workflow; they are not a SQL baseline.

## Interpret the report

Reports include successes, failures and their categories, attempted/offered and
successful rates, p50/p95/p99 latency, SQL execution and commit distributions,
application CPU/allocation/GC/memory data, PostgreSQL durability settings, runtime,
CPU model, source commit/dirty status and loaded assembly hashes. HTTP
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
