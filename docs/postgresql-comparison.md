# Warm PostgreSQL provider comparisons

`benchmark/CellBridge.PostgreSql.Comparison` preserves the corrected warm
BenchmarkDotNet method used for the EF provider comparison. It is non-packable.
The existing storage, Lists and allocation runners remain separate workloads.

Use the .NET SDK selected by `global.json`, the repository's Aspire prerequisites
and a working container runtime. The executable calls the existing
`PostgreSqlEnvironment` fixture. Each case owns a disposable Aspire session,
PostgreSQL container, generated database and restricted runtime role. The fixture
removes persistent mounts, WebDemo and capture. Do not start or stop the development
AppHost for these commands.

## Run current EF

Run from the checkout root. Commit source changes before collecting a comparison;
the analysis requires a clean checkout. Choose a fresh run directory each time.

```sh
export CB_MEASUREMENT_ROOT="$PWD/benchmark/CellBridge.PostgreSql.Comparison"
export CB_MEASUREMENT_OUTPUT="$PWD/artifacts/postgresql-comparison/ef"
export CB_BENCHMARK_VARIANTS=ef
dotnet build "$CB_MEASUREMENT_ROOT/CellBridge.PostgreSql.Comparison.csproj" -c Release
dotnet run -c Release --no-build --project "$CB_MEASUREMENT_ROOT" -- --list flat
dotnet run -c Release --no-build --project "$CB_MEASUREMENT_ROOT" -- --smoke
```

`--smoke` runs create and patch preparation, one validated untimed warm batch,
and two validated batches with intervening schema resets. It collects no latency
or allocation measurements. Use a different directory for the measured run:

```sh
export CB_MEASUREMENT_OUTPUT="$PWD/artifacts/postgresql-comparison/ef-measured"
mkdir -p "$CB_MEASUREMENT_OUTPUT"
dotnet run -c Release --no-build --project "$CB_MEASUREMENT_ROOT" -- --filter '*' --artifacts "$CB_MEASUREMENT_OUTPUT/bdn" --exporters json > "$CB_MEASUREMENT_OUTPUT/run.log" 2>&1
```

The six benchmark methods expand to ten EF cases. `--filter` can select a subset.
No historical SQL binary is needed for EF. `--diagnostics` runs the selected
providers with Npgsql command/batch tracing and EF compilation counts. Its output
is diagnostic evidence, never a timing result.

## Prepare and pair historical SQL

The SQL source is commit `9827c29211ded20543546fe7696c8c28521189cb`.
Build its two provider assemblies in a temporary checkout, then retain only those
DLLs and their source/checksum manifests under ignored `artifacts/`. Run these
commands in the same shell as the EF setup above, from the checkout root:

```sh
sql_source=9827c29211ded20543546fe7696c8c28521189cb
sql_build=$(mktemp -d)
git worktree add --detach "$sql_build/source" "$sql_source"
dotnet build "$sql_build/source/src/CellBridge.Lists.PostgreSql/CellBridge.Lists.PostgreSql.csproj" -c Release -o "$sql_build/lists"
dotnet build "$sql_build/source/src/CellBridge.Storage.PostgreSql/CellBridge.Storage.PostgreSql.csproj" -c Release -o "$sql_build/storage"
export CB_SQL_PROVIDER_DIRECTORY="$PWD/artifacts/postgresql-comparison/sql-provider"
mkdir -p "$CB_SQL_PROVIDER_DIRECTORY"
cp "$sql_build/lists/CellBridge.Lists.PostgreSql.dll" "$CB_SQL_PROVIDER_DIRECTORY/"
cp "$sql_build/storage/CellBridge.Storage.PostgreSql.dll" "$CB_SQL_PROVIDER_DIRECTORY/"
git -C "$sql_build/source" rev-parse HEAD > "$CB_SQL_PROVIDER_DIRECTORY/SOURCE_HEAD"
(cd "$CB_SQL_PROVIDER_DIRECTORY" && sha256sum CellBridge.Lists.PostgreSql.dll CellBridge.Storage.PostgreSql.dll > SHA256SUMS)
git worktree remove "$sql_build/source"
rm -r "$sql_build"
export CB_BENCHMARK_VARIANTS=sql,ef
export CB_MEASUREMENT_OUTPUT="$PWD/artifacts/postgresql-comparison/paired-smoke"
dotnet run -c Release --no-build --project "$CB_MEASUREMENT_ROOT" -- --smoke
```

The loader checks `SOURCE_HEAD`, both DLL checksums and the source commit in each
assembly's informational version. Only the two SQL provider DLLs load in a separate
assembly context. Contracts, shared core, Npgsql and TestInfrastructure come from
the current checkout for both variants. Keep the generated manifests with the run.

The original measured SQL DLL SHA-256 identities were
`f88da906eacd0cb55cfc7fd51192052e712beb82b7155c55f4d4c9dbc2e43369`
for Lists and
`f45679ed866dcd01a0c034d13438d741b1fd8b5411a5f009ca8572bcd12d288f`
for storage. A source rebuild can produce different DLL hashes because SDK and
build paths affect identity. The maintained loader records and verifies the new
build's identities instead of requiring the old archived binaries.

Run the twenty paired cases serially on an otherwise idle machine, then analyze:

```sh
export CB_MEASUREMENT_OUTPUT="$PWD/artifacts/postgresql-comparison/paired-measured"
mkdir -p "$CB_MEASUREMENT_OUTPUT"
dotnet run -c Release --no-build --project "$CB_MEASUREMENT_ROOT" -- --filter '*' --artifacts "$CB_MEASUREMENT_OUTPUT/bdn" --exporters json > "$CB_MEASUREMENT_OUTPUT/run.log" 2>&1
node "$CB_MEASUREMENT_ROOT/compare.mjs" "$CB_MEASUREMENT_OUTPUT" "$PWD/artifacts/postgresql-comparison/analysis" "$(git rev-parse HEAD)"
```

The Node analysis requires all twenty cases, full compressed BDN JSON reports and
`run.log`. It verifies successful exits, source and assembly consistency, input and
normalized result parity, PostgreSQL settings and pool settings. It writes
`comparison.json` and `comparison.md` to an empty output directory. The JSON keeps
confidence intervals, medians, variation, actual and retained iteration counts,
removed measurements and detected outliers. Source hashes absent from the supplied
git commit are listed as unavailable. The analysis does not collect or qualify host
resource pressure; record that separately before drawing performance conclusions.

## Inputs and timing

Inputs require no download or archived files. Setup generates seed-42 Lists
payloads, 1 MiB content objects with seeds 42 through 49, and deterministic DOCX
packages with seeds 42 through 54. `expected-inputs.json` contains the relevant
input lengths and hashes from the corrected method. Setup verifies these and the
unchanged linked `SyntheticWorkload.cs` and `MinimalDocx.cs` generators. Generated
packages stay in memory. JSONL validation and provenance go to
`CB_MEASUREMENT_OUTPUT`; BDN results go to its `bdn/` directory.

Lists mutation cases retain one engine and runtime pool for the case. Global setup
executes and validates one successful 16-operation batch on that engine. Iteration
setup resets only the generated Lists schema and prepares the same public fixture,
requests and starting counts outside timing. The engine, EF model and context pool
remain alive. Iteration cleanup verifies results, readback, revisions, audit events,
receipts, counts and hashes. Both providers use this lifecycle.

Paging measures first and second 100-record pages from 1,000 prepared records,
with fresh or explicitly analyzed statistics. Content reads consume a complete
1 MiB object. Content writes measure eight distinct objects. Service saves measure
one save on each of eight prepared documents with four prior saves, using
changed-full or delta requests. Document write/save fixtures still recreate their
database per iteration, as in the corrected source. These are direct provider and
service measurements with one client, without HTTP or desktop Office.

Runtime defaults retain normal tiered JIT and PGO. Setup rejects the recorded JIT,
ReadyToRun and GC environment overrides. The original BDN job, operation counts,
adaptive warmup and validation remain intact. Lists mutations, content writes and
service saves use one batch per iteration. Do not compare the discarded method
that recreated the Lists engine each iteration with this warm method.

The original measured EF source was
`538101e3135981ecf7d27e6f57607fed770e8d34`. Running this project measures the current
checkout, including later production changes. It does not recreate those historical
EF binaries. Maintained changes are the project name and references, configurable
artifact paths, optional SQL loading with explicit source-build manifests,
EF-only default selection, the untimed smoke entry point and trimmed analysis.
The analysis omits the investigation's archive inventories, frozen-run comparison,
cgroup snapshots and study-report assembly. Timed bodies and the corrected warm
mutation lifecycle are unchanged. Retain new results separately from old evidence.

## Profile Lists mutations

Use a fresh output directory with the same environment variables as above:

```sh
dotnet run -c Release --no-build --project "$CB_MEASUREMENT_ROOT" -- --profile-mutations
dotnet run -c Release --no-build --project "$CB_MEASUREMENT_ROOT" -- --analyze-profile "$CB_MEASUREMENT_OUTPUT/ef-create.nettrace"
dotnet run -c Release --no-build --project "$CB_MEASUREMENT_ROOT" -- --analyze-profile "$CB_MEASUREMENT_OUTPUT/ef-patch.nettrace"
```

This mode uses the existing fixture and mutation workload. After eight validated
warm batches, it records 48 batches per operation with Npgsql command durations,
EF compilation counts and EventPipe allocation/thread samples. Schema resets,
preparation and validation remain outside the marked batch regions. Analysis
selects only those regions. Allocation ticks estimate allocated bytes, not retained
heap. Sampled stacks include blocked threads and profiler work; their counts are
not CPU utilization. Command durations include driver and server work. Keep these
instrumented results separate from BDN timings and allocations.
