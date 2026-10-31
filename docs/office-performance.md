# Measuring active Office user capacity

The retained candidate reached 11,234 successful saves in a measured
Windows-host minute with direct PostgreSQL and proxied HTTPS, with no measured HTTP
failures. All 16,760 acknowledgments passed final historical verification.
Warm-up still had 49 HTTP 503s. This is a bounded synthetic result, not production
or near-limit history qualification. The [database audit](#database-transaction-and-connection-audit)
records the separate code improvement and endpoint-control results.

Earlier exact-current Windows-host measurements at `fd6b8e0` and local Linux runs are
retained below. The Linux control-read/native-JSON candidate acknowledged 5,863
saves/minute under overload; batching alone reached 5,813 against 5,519. Those
runs used 1,500 authenticated users and 15,000 documents. They are not matched
comparisons with the later endpoint control.

The archived implementation reached 8,151 inside a measured minute with HTTP
failures. The replacement implementation has not demonstrated a throughput gain over that result.
These synthetic workloads do not validate desktop Office capacity.

The earlier retained runs below predate shared download admission. Their document GETs
did not consume the SOAP concurrency budget or use its deadline. Current GETs
share that budget through reconstruction and transfer. Qualification of this
combined request policy is included in the exact-current Windows-host runs.

## Exact-current Windows-host qualification

On 2026-10-30, the clean isolated Windows checkout matched
`fd6b8e012dedd0e354be2383efc35d2149d6f561` before building or measuring.
The SDK was `11.0.100-rc.3.26522.148`. PostgreSQL 18.3 ran in Docker
Desktop. The existing NBomber HTTP workload used 1,500 authenticated users,
15,000 documents, 256 KiB packages, 16 KiB changed XML, no retries, seed 42,
30 seconds warm-up and 60 seconds measurement. Queries remained at 30 seconds
and opens at 120 seconds. Admission 128/two per subject, pool/preparation caps
64/64, 4 GiB WAL and query statistics were benchmark overrides throughout.
HTTPS remained proxied. PostgreSQL confirmed fsync, synchronous_commit and
full_page_writes enabled.

| Exact-current run | Offered saves/minute | Strict ACK/minute | Late ACK | Measured save failures | Save p50/p95/p99, ms |
| --- | ---: | ---: | ---: | ---: | --- |
| Primary, 30-second saves | 3,000 | 2,997 | 3 | 0 | 50.66 / 477.95 / 658.94 |
| Target, eight-second saves | 11,250 | 8,483 | 106 | 2,653 | 428.54 / 1,615.87 / 2,244.61 |

Primary had no HTTP failures, including warm-up. Target also failed 647 measured
periodic queries, 164 open downloads and 41 open queries. Successful target
query/download/open-query p95 was 1,039.87/912.38/470.78 ms. Neither run missed a
cadence slot. After timing, all 4,483/13,223 acknowledged revisions, including
warm-up and drain, passed historical package SHA-256 and one-to-one accepted-index
verification. Primary exited zero; target exited one because of overload.
Strict ACKs must start and finish within the minute. Successful save-step counts
of 3,000/8,589 also include the excluded late ACKs.

The current Windows process probe previously rejected every sample. It now
accepts only the direct Aspire resource PID running `CellBridge.WebDemo.exe`
with its adjacent assembly, and checks PID/start-time identity on each sample.
The target run verified this exact resource/application PID and collected 240
measurement samples. A launcher PID still fails closed. This instrumentation
change does not change the application or its persistence contract.

| Measurement CPU equivalents | Primary | Target |
| --- | ---: | ---: |
| WebDemo | 2.15 | 4.16 |
| Generator | 0.31 | 0.75 |
| Owned DCP processes | 1.82 | 3.01 |
| PostgreSQL container | 0.99 | 4.56 |

Primary process counters were external, with approximately aligned boundary
samples. Target WebDemo counters came from the fixture's verified process;
generator/DCP counters were external. DCP includes controller and proxy work,
not isolated HTTPS proxy CPU. Container CPU is already part of WSL/host totals.
Do not add it again to host CPU. These are CPU-time deltas, not wall-stack
percentages. Target WebDemo sampled peak working set was 651 MiB; container
memory is a different measurement and includes its filesystem cache.

A single persistent `docker exec` collector read the owned container's cumulative
cgroup CPU and memory counters. Received timestamps advanced with guest sample
timestamps. Primary/target retained 59 measurement samples each, with maximum
gaps of 1.034/1.038 seconds. This avoids the delayed Docker statistics reader in
the earlier HTTPS experiment. PostgreSQL activity sampling retained 378 samples
per run, with maximum gaps of 317/366 ms. Both runs recorded zero deadlocks and
rollbacks. These samples can miss short waits.

### Allocation diagnostic and rejected serialization candidate

A separate retained-code primary run enabled runtime counters and phase metrics,
with a 30-second GC allocation trace during measurement. It completed 2,997
strict ACKs, three late ACKs and no HTTP failures; all 4,482 ACKs passed final
historical verification. Save p50/p95/p99 was 49.85/69.63/86.66 ms. This substantial
variation from the first primary run prevents attributing latency changes from
single runs to code alone.

Across 59 interior one-second counter intervals, managed allocations averaged
393.8 MB/s. Normalized to this mixture's approximately 125 completed HTTP requests
per second, that is about 3.15 MB/request. WebDemo used 1.97 CPU equivalents,
about 15.8 CPU ms/completed mixed request. These ratios include save, query and
download work; they are not isolated per-save costs. Allocation counters measure
allocated bytes, not RSS. A 30-second host counter window was 45.9% busy and the
generator used 0.33 CPU equivalents. Moving the generator is not justified by
this lower-load observation alone.

The trace estimated 11.90 GB of weighted allocations, including 7.03 GB of byte
arrays. The first CellBridge frame was data-element package serialization for
2.30 GB, chiefly query response serialization. It lost no events and had no
missing allocation stacks or decoder depth-cap drops. Allocation samples do not
assign CPU percentages to those methods. Capture-level stack truncation was not
separately assessed.

A bounded experiment measured package length before serialization and reserved
the writer buffer once. All 335 binary tests passed. Its primary HTTP diagnostic
completed 2,992 strict ACKs and eight late ACKs with no measured HTTP failures.
Allocation counters averaged 389.4 MB/s, only 1.1% below the preceding diagnostic.
WebDemo CPU rose to 2.21 equivalents; save p50/p95/p99 was
51.68/579.58/738.30 ms. These single runs demonstrate no useful endpoint/resource
gain. The reservation code was removed. An initial component control measured
response construction rather than serialization, so its numbers do not measure
the proposed optimization and are excluded from the decision. All 4,483 candidate
ACKs passed final historical verification, and its process exited zero.

### Retained-code aged component check

The existing history-scaling diagnostic checked 1, 250 and 999 retained revisions,
with five measured operations per stage after ten warm-ups and tiering disabled.
It used a tiny valid DOCX, in-memory state and shared warm content. Each measured
save started from the same aged state and passed content/revision verification
outside timing. These are library allocations, not aged PostgreSQL/HTTP capacity.
The run overlapped untimed candidate verification, so short wall/CPU samples
cannot establish isolated performance changes. CPU counters were often below
the Windows counter resolution; zero samples do not mean zero CPU work.

| Median allocated bytes/operation | 1 revision | 250 revisions | 999 revisions |
| --- | ---: | ---: | ---: |
| Current head read | 40 | 40 | 40 |
| Current partition metadata read | 624 | 624 | 624 |
| Whole query and client graph | 47,672 | 5,636,168 | 20,890,592 |
| Full-package save | 817,720 | 1,086,080 | 1,926,784 |

The current-record bound holds in this fixture. Complete queries and saves still
grow with retained protocol data. Aged HTTP qualification remains outstanding.
The offline snapshot grew from 27,707 to 17,909,891 bytes; offline export is not
the representation loaded by a current head read.

Final benchmark tests also exposed a preserved-input bug: source checkout CRLF
characters appeared in `MinimalDocx` XML copied into `StorageBenchmarkInputs`.
The supposedly fixed 1 MiB benchmark package was 17 bytes larger on Windows.
Normalizing those XML parts to LF restores the existing size. ZIP creator OS
and file-permission attributes still differ by platform, so the test preserves
an explicit hash for each. Changing only those central-directory fields in the
Windows diagnostic copy reproduced the original Linux SHA-256 exactly.
This changes the separate provider benchmark fixture, not the Office-user
HTTP corpus used for these measurements.

The candidate is not part of the implementation. No archived implementation
comparison was rerun.

### Architecture and remaining capacity work

The current-code audit agrees with the replacement cost table. Current heads,
controls, bindings and affected mapping keys avoid unrelated application history
on ordinary publication. Immutable elements, visibility intervals, revisions,
receipts and checkpoints retain their separate roles. Final publication rechecks
authority under the affected document guard; matching SQL state/content writes
remain atomic. No provider-wide quota or publication lock was introduced.

Complete saves still load selected graph payloads, reconstruct/validate the ZIP
and hash the complete package. QueryChanges still enumerates selected catalog
and mapping metadata. Historical downloads resolve a checkpoint and bounded tail
plus the selected payload closure. These are stable contracts with explicit
remaining costs, not claims that saves are `O(delta)` or all operations are
history-independent.

The 3,000-save offer passed this bounded synthetic run. The 11,250-save offer
failed at 8,483 strict ACKs with failures across writes and reads. This brackets
two fixture outcomes; it does not establish the maximum clean rate, an intrinsic
server limit or a production SLA. The target mixed workload recorded 608,963
database transactions across warm-up, measurement and drain for 13,223 ACKs.
That ratio is not a per-save transaction count. App, PostgreSQL and DCP costs
all need consideration before another access change. Small database execution
means do not include connection acquisition, transport or application delays.
Further capacity qualification must preserve these distinctions and the final
historical verifier. Feature development can use the current contracts while
the 10,000 successful saves/minute requirement remains unqualified.

## Database transaction and connection audit

The mixed target's 608,963 committed database transactions divided by 13,223
acknowledged saves is 46.05. This is not 46 write transactions per save. The
counter includes implicit statement transactions, read traffic, Identity,
connection reset SQL, health checks, diagnostics, warm-up and drain. Dividing the
lower-load total by its fewer saves produces an even larger ratio. The activity
collector issued 378 queries, so it does not explain most of this work.

The replacement split large state reads into bounded repository APIs, but callers
still combined many separately leased reads. Earlier reports recorded this
chatter without establishing an operation-level connection or transaction budget.
That was a gap in the performance audit. Correctness and history-scaling checks
did not establish efficient composition of the read APIs.

Attribution reused the existing authenticated HTTP workload with one user and
one document. Saves, queries and opens ran separately every five seconds, with
the other action intervals disabled. The existing 30-second warm-up and
60-second measurement remained. SQL statement logging began after setup, only on
the owned disposable PostgreSQL container, with bind parameter logging disabled.
The twelve measured save starts all acknowledged successfully; all 18
acknowledgments including warm-up passed final historical verification.

An ordinary baseline save burst contained 168 SQL log records, seven explicit
transaction blocks and 17 pooled connection resets. These are separate units.
Most publication statements share one write transaction and batches; neither
168 records nor the database-wide ratio is a network round-trip count. A periodic
query contained three explicit blocks and ten resets. An open's download plus
query contained six blocks and twenty resets. Larger periodic bursts also
contain background work and are not used as isolated save counts.

A first candidate folded immutable metadata into the existing preparation
snapshot and batched selected file-group payload reads. Its ordinary save trace
had 156 log records, six explicit blocks and twelve resets. A further fix retains
the exact checked mapping-key set, including known absence, so graph resolution
does not re-query a newly introduced revision key. QueryChanges also obtains its
mappings in the partition snapshot. Unknown keys retain their provider lookup.
Fresh authorization, visibility checks, checkpoint validation and the final
publication fence remain.

The final isolated trace confirmed 148 records, five explicit blocks and eleven
connection resets on an ordinary save. All 18 acknowledgments passed final
historical verification. The remaining blocks resolve the path, prepare a
coherent snapshot, check visibility, validate a checkpoint and publish atomically.
The trace covers captured document-backend bursts and excludes other backends;
it is not a universal statement count for every HTTP save. Reset messages and
standalone statements are not reported as committed write transactions.

### Fresh lower-load control

Both runs below used the same 1,500-user mixed workload, 30-second saves and fixed
overrides from the exact-current qualification. The control restored the retained
clean `f548a89` Release benchmark and WebDemo outputs and verified every benchmark
DLL hash before launch. The working-tree candidate did not execute in that
control. The candidate then used its separately built and hashed Release outputs.
Both enabled the same one-second runtime, process and container collectors, with
no phase tracing or SQL statement logging during timing.

| Measurement | Baseline | Candidate |
| --- | ---: | ---: |
| Strict ACK/minute | 2,973 | 2,999 |
| Late ACK excluded | 26 | 2 |
| Measured HTTP failures | 0 | 0 |
| Save p50 / p95 / p99, ms | 46.62 / 390.14 / 631.81 | 44.29 / 376.06 / 498.94 |
| WebDemo CPU equivalents | 1.982 | 2.156 |
| PostgreSQL CPU equivalents | 0.886 | 0.872 |
| Owned DCP CPU equivalents | 1.570 | 1.919 |
| Generator CPU equivalents | 0.319 | 0.320 |
| Managed allocation, MB/s | 395.11 | 388.67 |
| Mixed successful HTTP steps | 7,499 | 7,501 |
| WebDemo CPU ms/successful HTTP step | 15.86 | 17.25 |
| PostgreSQL CPU ms/successful HTTP step | 7.09 | 6.98 |
| Managed MB/successful HTTP step | 3.161 | 3.109 |

All 4,482/4,483 acknowledged revisions passed historical SHA-256 and one-to-one
accepted-index verification after timing. Both processes exited zero. Strict
ACK boundaries and NBomber step boundaries differ slightly, so the mixed-step
resource ratios are approximate. External resource samples exclude boundary
samples and approximately align with the measurement minute. They include the
whole workload, not isolated save costs. Allocation rates use 59 interior
one-second intervals; allocation is not working set. Container samples had
maximum gaps of 1.019/1.027 seconds. PostgreSQL CPU is already part of WSL/host
CPU and must not be added to host totals again.

This single pair does not demonstrate a clear efficiency win. Save p95 fell 3.6%
and mixed-request allocations fell 1.7%, while application CPU per completed step
rose 8.8%. PostgreSQL CPU changed little. The connection-count reduction alone
does not qualify the target or establish that PostgreSQL is the sole bottleneck.

### Six-thousand-save offer

Two fresh baseline controls at 6,000 offered saves/minute returned 5,598/5,797
strict ACKs, 397/198 measured save failures and five late ACKs each. The second
control and candidate used the same runtime and external collectors. The first
control had only the fixture's verified application process measurements and is
not an allocation comparison. Both controls used the clean retained baseline
binaries; source edits present during the second control did not execute.

| Measurement | Instrumented baseline | Candidate |
| --- | ---: | ---: |
| Strict ACK/minute | 5,797 | 5,994 |
| Late ACK excluded | 5 | 6 |
| Measured save failures | 198 | 0 |
| Measured query / download / open-query failures | 96 / 17 / 9 | 0 / 0 / 0 |
| Save p50 / p95 / p99, ms | 68.99 / 1,049.60 / 1,408.00 | 53.92 / 440.57 / 623.10 |
| WebDemo CPU equivalents | 3.474 | 3.187 |
| PostgreSQL CPU equivalents | 2.145 | 1.805 |
| Owned DCP CPU equivalents | 2.609 | 2.227 |
| Generator CPU equivalents | 0.455 | 0.393 |
| Managed allocation, MB/s | 571.73 | 594.70 |
| Mixed successful HTTP steps | 10,164 | 10,500 |
| WebDemo CPU ms/successful HTTP step | 20.55 | 18.21 |
| PostgreSQL CPU ms/successful HTTP step | 12.66 | 10.31 |
| Managed MB/successful HTTP step | 3.375 | 3.398 |

In this pair strict ACKs rose 3.4%, save p95 fell 58.0%, and the candidate
completed all measured HTTP steps successfully. Application/PostgreSQL CPU per
successful mixed step fell 11.4%/18.5%. Managed allocation per successful mixed
step rose 0.7%; there is no demonstrated allocation saving at this load. This is
one instrumented pair, not a maximum-capacity result or a production SLA. The
lower-load CPU regression remains part of the evidence.
All 8,371/8,747 baseline and 8,963 candidate acknowledgments passed final
historical SHA-256 and one-to-one accepted-index verification. The controls
exited one because of overload; the candidate exited zero.

The database-wide counter fell from 462,797 to 364,592 committed transactions
across warm-up, measurement and drain. This 21.2% decrease includes reads and
background work, and must not be presented as a per-save write-transaction count.
Both runs recorded zero database rollbacks and deadlocks. Container sampling had
maximum gaps of 1.049/1.022 seconds; process sampling had gaps of 1.146/1.161
seconds. These counters measure CPU time, not SQL execution time or wall stacks.

### Candidate at the target offer

The same retained candidate offered 11,250 saves/minute with the original
proxied endpoints. It completed 7,776 strict ACKs and 62 late ACKs, with 3,404
measured save failures and 853/210/45 query/download/open-query failures. All
HTTP failures were 503. Successful save p50/p95/p99 was
314.62/1,897.47/2,185.22 ms. All 12,106 acknowledgments, including warm-up and
drain, passed final historical SHA-256 and accepted-index verification. The run
exited one because of overload. No database deadlock or rollback was recorded.

WebDemo/PostgreSQL/DCP/generator CPU was 3.804/3.718/3.525/0.768 equivalents.
Managed allocation was 635.79 MB/s, about 3.462 MB per successful mixed HTTP step.
WebDemo and PostgreSQL CPU per successful mixed step were 20.71/20.24 ms. These
ratios include failed-request work. Container/process sample gaps were at most
1.044/1.182 seconds. Mean sampled GC pause was 4.5% of wall time; this is not GC's
share of application CPU. The 10,000-save requirement remains unmet in this run.
It is not a matched target-rate comparison with the earlier 8,483-ACK result.

The fixture runs Release binaries through Hosting.Testing, with WebDemo's
development environment and DCP proxies. It is not an `aspire deploy` deployment.
Runtime endpoint logging confirmed that PostgreSQL, as well as HTTPS, was
proxied. DCP's measured CPU therefore cannot be assigned solely to HTTPS.

### PostgreSQL endpoint control

`Parameters__LoadDirectPostgreSql=true` selects Aspire's supported
[proxyless endpoint](https://aspire.dev/fundamentals/networking-overview/#proxyless-endpoints)
for the disposable load database. The default remains proxied. The fixture
explicitly requests `127.0.0.1`; an independent Docker inspection confirmed the
published host IP and port matched the allocated endpoint before load. HTTPS
remained proxied. No application code, workload, admission, pool, WAL or durability
setting changed between the following two target offers.

| Target offer measurement | Proxied PostgreSQL | Direct PostgreSQL |
| --- | ---: | ---: |
| Strict ACK/minute | 7,776 | 11,234 |
| Late ACK excluded | 62 | 9 |
| Measured save failures | 3,404 | 0 |
| Measured query / download / open-query failures | 853 / 210 / 45 | 0 / 0 / 0 |
| Save p50 / p95 / p99, ms | 314.62 / 1,897.47 / 2,185.22 | 94.91 / 323.33 / 387.33 |
| WebDemo CPU equivalents | 3.804 | 5.146 |
| PostgreSQL CPU equivalents | 3.718 | 3.677 |
| Owned DCP CPU equivalents | 3.525 | 0.390 |
| Generator CPU equivalents | 0.768 | 0.701 |
| Managed allocation, MB/s | 635.79 | 927.12 |
| WebDemo CPU ms/successful mixed HTTP step | 20.71 | 19.61 |
| PostgreSQL CPU ms/successful mixed HTTP step | 20.24 | 14.02 |
| Managed MB/successful mixed HTTP step | 3.462 | 3.534 |

All 16,760 direct-run acknowledgments passed final historical SHA-256 and
one-to-one accepted-index verification. The measured minute exceeded 10,000
successful saves with no measured HTTP failures. Warm-up still failed
40 saves, three queries and six downloads, all 503, so the whole run exited one.
There were no database rollbacks or deadlocks. Container/process gaps were at
most 1.022/1.167 seconds; allocation uses 59 interior counter intervals.

This single endpoint-control pair increased strict ACKs by 44.5% and reduced DCP
CPU by 88.9% while PostgreSQL CPU stayed near 3.7 equivalents. It identifies the
development PostgreSQL proxy as a major fixture bottleneck. It is a transport
configuration result, not a code speedup or an `aspire deploy` result. Application
CPU and allocation rates rose as more operations completed; allocation per
successful mixed step rose 2.1%. The separate 6,000-save pair tests the retained
code improvement. Neither pair establishes production or near-limit history
capacity.

Three preliminary direct-endpoint attempts stopped during setup because a
native Windows listener probe could not observe Docker's published NAT endpoint.
They contain no load result. The corrected control checks the explicit loopback
endpoint model and retains Docker's actual port-binding inspection. Its preceding
single-user smoke verified all 18 acknowledgments. No probe was repaired by
restarting or truncating a measured workload.

### Early-history HTTP qualification

One further existing-workload profile kept 1,500 authenticated users and the
same save/query/open intervals, package, changed XML, transport and overrides.
It used one document per user and 120 seconds of internal warm-up before the
60-second measurement. A read-only PostgreSQL sample just before the window
found 1,500 documents at revision numbers 12 through 16, median 15. The sample
ran during warm-up; runtime counters covered the full extended run and their
analysis selected the later measurement window. This is an early-history check,
not a matched comparison with the ten-document-per-user profile or qualification
near 250/1,000 revisions.

The run completed 10,613 strict ACKs, with 37 late ACKs excluded. It failed 604
measured saves and 110/34/14 queries/downloads/open queries, all 503. Successful
save p50/p95/p99 was 181.25/741.38/806.40 ms. All 32,244 acknowledgments passed
final historical SHA-256 and one-to-one accepted-index verification. Warm-up
also failed 835 saves, 161 queries, 48 downloads and 32 open queries. The process
exited one; there were no database rollbacks or deadlocks.

WebDemo/PostgreSQL/DCP/generator CPU was 5.457/4.333/0.737/0.747 equivalents.
Managed allocation was 1,028.95 MB/s. Per successful mixed HTTP step, WebDemo and
PostgreSQL CPU were 21.89/17.38 ms and managed allocation was 4.128 MB. Allocation
used 59 interior intervals. Container/process sample gaps were at most
1.019/1.183 seconds. These ratios include failed-request work and do not isolate
the effect of history from the different document count and longer warm-up.

The fresh direct-endpoint minute clears the numerical target without measured
failures. The early-history run clears the ACK target but still has failures.
Failure-free aged capacity, longer-duration stability and production deployment
remain unqualified. The indexed/immutable architecture and durability contracts
can support other feature work while those capacity gates remain explicit. The
retained access changes add no migration or global coordination mechanism.

The profiles use the existing NBomber generator and verifier; they are not a new
benchmark framework.

## Atomic SQL publication experiment

Commit `0b9a2d0` buffers up to 8 MiB and 64 provisional objects when SQL state
and content share the same configured database. It validates the owned bytes
before opening the write transaction, inserts content, then guards and publishes
the affected document in that transaction. Rejection or replay rolls back
speculative content. Larger saves and external content keep charged staging.
The SQL Server implementation also uses its existing exact-key physical lookup;
a generic scan caused a reproduced deadlock with uncommitted content inserts.

The first local matched pair used the same 1,500-user AutoSave profile, 256 KiB
packages, 16 KiB changed XML, pool/preparation caps of 64, admission 128/two per
subject, PostgreSQL, 30 seconds warm-up and 60 seconds measurement.

| Measurement | Preceding implementation | Atomic SQL candidate |
| --- | ---: | ---: |
| In-window acknowledgements/minute | 4,576 | 5,295 |
| Excluded late acknowledgements | 116 | 112 |
| Failed HTTP steps including warm-up/drain | 13,306 | 12,056 |
| All acknowledgements including warm-up/drain | 6,786 | 7,767 |
| Successful measured save-step p95 | 1,586 ms | 1,378 ms |
| Successful measured query-step p95 | 656 ms | 691 ms |
| WebDemo CPU cores during measurement | 1.49 | 1.57 |
| Peak sampled measurement working set | 710 MiB | 725 MiB |
| Workload-wide committed database transactions/all ACK | 57.69 | 47.72 |
| Cluster WAL/all ACK | 49.62 KiB | 47.63 KiB |

Both runs overloaded and neither missed a cadence slot. The first pair recorded
15.7% more strict acknowledgements; it is not a repeated estimate or a
failure-free capacity result. Query latency and sampled memory did not improve.
Higher accepted work, provisional copies and ordinary run variation are not
isolated by these two runs. Database/WAL ratios cover mixed queries, opens,
authentication and warm-up/drain traffic; they are not transactions or bytes
inside one save.

After timing, each retained acknowledged package was materialized and hashed.
The candidate additionally checks one-to-one acknowledgement/index coverage;
the baseline used the first historical-byte verifier. Those untimed checks do
not change the offered workload.

## Coherent control-read batching experiment

The next local pair compared `e147d204` with control-read batching, keeping the
same atomic SQL save path, workload, database settings and verifier.
One tagged command returns coordination, editors, partition heads and optional
database time. It returns separate rows rather than multiplying editors by
partitions. Preparation reuses the partition rows it already read. Mutations
reuse their existing guarded database time. Transaction isolation, head checks,
authorization and document row locks are unchanged.

A normal control read changes from five reader commands to two, including its
head lookup. Save preparation plus publication removes five reader commands.
The actual workload recorded one preparation batch and one publication batch per
acknowledgement, with two and four rows respectively for these documents.

| Measurement | Atomic SQL before batching | Batched control reads |
| --- | ---: | ---: |
| In-window acknowledgements/minute | 5,519 | 5,813 |
| Excluded late acknowledgements | 110 | 107 |
| Failed HTTP steps including warm-up/drain | 11,789 | 11,255 |
| All byte-verified acknowledgements | 7,933 | 8,396 |
| Successful measured save-step p95 | 1,293 ms | 1,236 ms |
| Successful measured query-step p95 | 646 ms | 636 ms |
| WebDemo CPU cores during measurement | 1.56 | 1.61 |
| Peak sampled measurement working set | 719 MiB | 717 MiB |
| Workload-wide committed database transactions/all ACK | 47.91 | 47.46 |
| Cluster WAL/all ACK | 47.59 KiB | 47.51 KiB |

This single pair recorded 5.3% more strict acknowledgements. Both runs overloaded
with HTTP 503 failures and neither missed a cadence slot. All acknowledgements
passed historical-package hashes and one-to-one revision coverage after timing.
The database sampler observed zero blocked backends; database counters recorded
zero deadlocks and rollbacks. Small changes in latency and memory are not
repeated estimates. Batching removes commands; it does not remove transactions
or change the graph algorithm's scaling.

## Native JSON decoding experiment

Repository reads previously encoded each JSON string into a new UTF-8 array,
then copied that array for strict document validation before deserialization.
The native text overload validates a JSON document and deserializes its root
while the document is alive. It preserves duplicate-name checks throughout ignored
and converter-skipped subtrees, depth and schema checks, Unicode validation and
exception distinctions. The raw-byte recovery decoder is unchanged.

An isolated Release allocation experiment used 1,000 warm-ups per path/type and
five alternating rounds of 10,000 decodes. Canonical decoded values matched.

| Record | Previous allocations/decode | Native text allocations/decode |
| --- | ---: | ---: |
| Document head, 1,598 UTF-8 bytes | 8,544 B | 5,296 B |
| Graph element, 393 UTF-8 bytes | 2,992 B | 2,144 B |
| Object binding, 441 UTF-8 bytes | 3,272 B | 2,328 B |

The reductions were stable across these rounds, about 28–38% for the tested
record types. They are not endpoint allocation rates or CPU improvements.
The subsequent unchanged AutoSave workload recorded 5,863 in-window saves,
compared with 5,813 before this decoder change, with HTTP 503 failures in both.
That difference is below 1% and does not demonstrate an endpoint throughput gain.
Save/query p95 was 1,226/633 ms, compared with 1,236/636 ms. WebDemo used 1.61
CPU equivalents and peak sampled measurement RSS was 714 MiB, compared with
1.61 and 717 MiB. All 8,447 acknowledgements passed historical-byte and
one-to-one verification after timing, with no missed cadence slots.

## Windows-host measurements

The same application commit, `b41e42b`, was also measured on a Windows host before
atomic SQL publication. The host had a Ryzen 7 9800X3D (8 cores,
16 threads), 31.2 GiB RAM and PostgreSQL 18.3 through Docker Desktop. The
benchmark retained the pool/preparation caps of 64, admission 128/two per subject,
1,500 users, 15,000 documents, PostgreSQL settings and the same cadence.

Primary acknowledged 2,995 saves inside its minute, with six late acknowledgements
and no HTTP failures. AutoSave acknowledged 9,386 inside its minute, with 112 late
acknowledgements and 1,745 measured save failures. It still failed the capacity
gate. The different host, operating system and container environment prevent
attributing this result to a specific hardware component.

These baseline runs used the earlier verifier. It checked final packages,
revision/receipt counts and distinct expected hashes, without reading every
acknowledged historical package. The current verifier additionally materializes
and hashes each acknowledged revision after measurement. That check is outside
the measured window and database-statistics capture.

The diagnostic thread-stack samples include waits and are not scheduler CPU
samples. Allocation ticks estimated 781 MB/s
in the separate instrumented run; that rate is approximate and does not describe
the untraced qualification runs.

The first atomic SQL candidate runs used commit `0b9a2d0`. They did not reproduce
the local Linux improvement.

| Measurement | Preceding AutoSave | Atomic AutoSave | Atomic, 15-second saves |
| --- | ---: | ---: | ---: |
| Offered saves/minute | 11,250 | 11,250 | 6,000 |
| In-window acknowledgements/minute | 9,386 | 7,984 | 5,607 |
| Excluded late acknowledgements | 112 | 105 | 39 |
| Measured save failures | 1,745 | 3,156 | 354 |
| Failed HTTP steps including warm-up/drain | 4,071 | 6,551 | 635 |
| Successful measured save-step p95 | 1,180 ms | 1,602 ms | 1,458 ms |
| WebDemo CPU cores during measurement | 4.39 | 3.91 | 3.32 |
| Peak sampled working set | 740 MB | 730 MB | 655 MB |
| Cluster WAL including warm-up/drain | 679 MB | 567 MB | 419 MB |

All failures were HTTP 503. Candidate primary throughput remained 2,996/minute
without failures, but its successful-save p95 increased to 557 ms, compared with
128 ms in the baseline. The candidate verified historical bytes and one-to-one
revision coverage for all 11,657 AutoSave, 8,553 bounded and 4,484 primary
acknowledgements after timing. The earlier baseline used the final-package checks
described above.

These single runs do not isolate the implementation's effect. Unrelated
containers were running during the candidate measurements and were absent from
the saved baseline inventory. A separate traced candidate run reached 9,403
in-window acknowledgements with failures. It is a diagnostic result, not a clean
comparison or evidence of failure-free capacity. The controlled comparison below tests that direction at a lower cadence.

The separate candidate trace estimated 883 MB/s of allocations, with byte arrays,
strings and character arrays leading the sampled types. Working stacks include
PostgreSQL byte-array reads, EF expression processing and object-group parsing.
The baseline diagnostic estimate was 781 MB/s. Neither sample establishes the
allocation rate of an untraced run. Sampled blocked-backend observations were
zero in the three untraced candidate runs; deadlocks and rollbacks were also
zero. This does not exclude short waits.

## Controlled Windows-host comparison

Four subsequent untraced runs compared A=`b41e42b` and B=`0b9a2d0` in A/B/B/A
order at 6,000 offered saves/minute. All four retained the same account/document
counts, package sizes, read cadences and database limits. No runs were discarded.
Container inventories and limits matched before and after every run.

| Run | In-window ACK/minute | Late ACK | Measured save failures | All HTTP failures | Successful save p95/p99 |
| --- | ---: | ---: | ---: | ---: | ---: |
| A1, preceding implementation | 5,033 | 20 | 951 | 3,186 | 2,024/2,789 ms |
| B1, atomic SQL | 5,637 | 14 | 352 | 972 | 1,383/1,848 ms |
| B2, atomic SQL | 5,978 | 23 | 0 | 0 | 472/601 ms |
| A2, preceding implementation | 5,120 | 85 | 796 | 1,721 | 1,964/2,605 ms |

Both atomic runs exceeded both preceding runs in strict acknowledgements and
had lower successful save/query latency. The earlier atomic regression did not
reproduce at this cadence. B2 completed without HTTP failures, but this single
6,000-offered cadence establishes neither maximum capacity nor the 10,000 target.
All 8,342 B1 and 8,963 B2 acknowledgements passed historical-byte and one-to-one
revision verification; A retained the older final-byte/count verifier.

Host pressure varied. Sampled host CPU averaged 97.7%, 86.2%, 94.1% and 98.7%
in run order; minimum available memory was 208, 786, 1,843 and 1,263 MiB.
Mean CPU queues were 20.9, 12.3, 10.5 and 18.5. WebDemo used 3.45–3.59 CPU cores
and peak measurement RSS of 594–637 MiB. These observations limit a precise
code-only effect estimate. They do not establish that unrelated containers caused
the differences. Sampled blocked backends and recorded rollbacks were zero.

Host CPU counts the load generator as well as the server and Docker Desktop.
The generator's CPU and memory were not captured separately in these Windows
runs, so the remaining host CPU cannot be attributed to a process. Container CPU
is already included in host totals. Low available RAM and aggregate disk activity
alone do not establish paging. A later idle inventory found about 17.4 GiB
available, which cannot explain consumption during the earlier runs. Future
qualification needs generator and host-process measurements alongside the server.

## Windows-host runs with process attribution

Commit `2025796` was measured with separate generator, server and DCP process
samples. These runs precede shared download admission.

| Save interval | In-window ACK/minute | Late ACK | Measured save failures | All HTTP failures | Historical verification |
| --- | ---: | ---: | ---: | ---: | --- |
| 8,000 ms | 10,231 | 54 | 958 | 2,153 | Unconfirmed for 15,076 ACKs; controller interruption lost terminal evidence |
| 8,800 ms | 9,447 | 73 | 703 | 1,714 | All 13,945 ACKs passed byte hashes and one-to-one coverage |

Both runs exited on overload. The higher-load count exceeds 10,000, but its
failures and incomplete verification prevent capacity qualification. The lower
point completed verification and still overloaded. Successful save p95/p99 was
968/1,713 ms and 909/1,548 ms respectively.

WebDemo used 4.41/4.28 mean logical CPU equivalents, the generator 0.65/0.63, and
DCP 2.58/2.75. Peak sampled working sets were 668/650 MiB, 2,628/2,590 MiB and
352/353 MiB respectively. PostgreSQL's first container sample averaged 4.53 CPU
equivalents; the second container sampler failed because of command quoting.
Host CPU averaged 98.1%/97.6%. Process and container accounting boundaries differ;
container CPU is already included in host CPU and is not an extra host total.
DCP's observed cost warrants checking the benchmark's proxy paths before assigning
the whole host limit to the library.

Available RAM remained above 9.4 GiB. Windows page-output and sampled Linux swap
counters stayed zero; paging was not demonstrated. SQL samples recorded no blocked
backends or deadlocks. WAL was 733,373,576/676,126,427 bytes across warm-up,
measurement and drain.

## HTTPS proxy comparison

Commit `91bcf77` compared the default Aspire HTTPS proxy (A) with the owned
loopback HTTPS listener (B). Four fixed runs used A/B/B/A order and an 8,800 ms
save interval, with the same users, document sizes, admission limits,
PostgreSQL configuration and application code. PostgreSQL routing stayed unchanged.

| Run | In-window ACK/minute | Late ACK | Measured save failures | All HTTP failures | Historical ACKs verified |
| --- | ---: | ---: | ---: | ---: | ---: |
| A1 | 9,027 | 86 | 1,112 | 2,912 | 13,090 |
| B1 | 9,014 | 28 | 1,184 | 1,733 | 13,902 |
| B2 | 9,525 | 23 | 676 | 1,422 | 14,146 |
| A2 | 9,284 | 106 | 832 | 1,900 | 13,857 |

Every run completed historical SHA-256 and one-to-one verification, with zero
cadence misses. All exited on HTTP 503 overload. The direct pair averaged 1.25%
more strict acknowledgements, but the counts did not show a repeatable gain.
No clean 10,000/minute point was demonstrated. The HTTPS option is an experimental
fixture control, not a retained application performance improvement.

Both Windows functional preflights passed. Docker reader delays invalidated
PostgreSQL CPU, memory and I/O measurements for the timed windows; host/process
sampling also missed its one-second target. Those measurements cannot establish
a database CPU improvement or a precise explanation for the run differences.
No runs were discarded or repeated.

## Immutable group-read experiment

A local A/B/B/A comparison tested removing explicit transactions from nonempty
PostgreSQL object-group reads against `91bcf77`. Empty or missing groups retained
a coherent fallback; SQL Server kept its snapshot transaction. All four runs used
the same 1,500-user 8-second save workload and existing limits.

| Run | In-window ACK/minute | Late ACK | Measured save failures | All HTTP failures |
| --- | ---: | ---: | ---: | ---: |
| A1, current implementation | 5,974 | 110 | 5,159 | 11,200 |
| B1, candidate | 5,735 | 110 | 5,397 | 11,366 |
| B2, candidate | 6,020 | 112 | 5,112 | 11,022 |
| A2, current implementation | 5,835 | 110 | 5,296 | 11,274 |

The candidate averaged 5,877.5 acknowledgements/minute versus 5,904.5 for the
current implementation, a 0.46% decrease. It showed no repeatable throughput or
latency benefit and was discarded. Candidate peak sampled WebDemo RSS averaged
723.4 MiB versus 713.7 MiB; CPU averaged 1.604 versus 1.599 logical CPUs.
These small differences do not establish a regression mechanism.

Every run completed historical-byte and one-to-one revision verification.
All failures were HTTP 503 overload; cadence misses, deadlocks, rollbacks and
sampled blocked observations were zero. Short waits remain possible. SQL reports
retained the same leading costs: binding metadata reads, ordered chunk reads and
visibility lookups. No PostgreSQL process CPU or allocation measurement was
collected. This experiment establishes no clean capacity gain.

## Save package stream experiment

A subsequently discarded candidate replaced only the incremental-save temporary package file
with a read-only, seekable view over the existing validated leaf arrays. It kept
the same ordered bytes, graph budgets, full ZIP-entry validation and whole-package
SHA-256. Downloads retained their existing verified scratch stream. This removed
save-time file I/O; graph loading, parsing, decompression and hashing still processed
the current package.

A component comparison used the actual two-leaf graph generated by
`IncrementalOfficeWorkload(256,42,16)`, with setup outside timing and no competing
tests. Eight rounds in A/B/B/A/A/B/B/A order, each with 200 operations, followed ten
warm-up operations per mode. Every operation checked the exact 262,144-byte length and expected digest.

| Component measurement per operation | Existing scratch file | Candidate leaf stream |
| --- | ---: | ---: |
| Mean elapsed time | 0.451 ms | 0.138 ms |
| Whole-process CPU estimate | 0.745 ms | 0.138 ms |
| Whole-process allocated bytes estimate | 138,472 | 70,258 |

Linux `/proc/self/io` recorded 209,715,200 written bytes and 1,600 write calls
across the 800 scratch operations, versus zero for the candidate operations.
These counters do not establish physical disk traffic. CPU and allocation
counters include runtime activity and small measurement overhead; these short
component runs establish no HTTP throughput or capacity gain. Full validation and
hashing remained in both paths.

### Full HTTP result and decision

The same local 1,500-user workload then compared `91bcf77` (A) with the candidate
(B) in four untraced A/B/B/A runs, keeping all limits and cadences fixed.

| Run | In-window ACK/minute | Late ACK | Measured save failures | All HTTP failures | Historical ACKs verified |
| --- | ---: | ---: | ---: | ---: | ---: |
| A1 | 5,966 | 107 | 5,164 | 11,183 | 8,555 |
| B1 | 5,718 | 111 | 5,417 | 11,514 | 8,394 |
| B2 | 6,013 | 106 | 5,120 | 11,081 | 8,640 |
| A2 | 5,981 | 109 | 5,157 | 11,187 | 8,569 |

Candidate mean throughput was 5,865.5 versus 5,973.5 acknowledgements/minute,
1.81% lower. Successful-save p95/p99 pair means increased 1.96%/1.90%.
Peak sampled WebDemo RSS averaged 715.0 versus 717.6 MiB and CPU averaged
1.561 versus 1.629 logical CPUs. These runs showed no endpoint benefit, so the
candidate and its added API were discarded. The isolated component saving did
not justify retaining the stream implementation. All failures were HTTP 503;
cadence misses, deadlocks, rollbacks and sampled blocked observations were zero.
Every acknowledged historical package passed byte and one-to-one verification.

A separate diagnostic run used the discarded candidate, not the retained code,
and verified all 8,062 acknowledgements. Its 40.38-second warm-up/measurement
trace estimated 325 MB/s of weighted allocations, led by byte arrays,
verified-content reads and graph parsing/loading. Repository prefetch/publication
led application wall-stack samples; those samples include waits and do not
identify scheduler CPU percentages. Event loss, missing stacks and stack-cap
drops were zero; 9,115 stacks reached the 96-frame depth limit. This is no
allocation comparison against the retained implementation or earlier traces.

Binding metadata reads, ordered chunk reads, visibility reads and chunk inserts
remained leading SQL costs. The evidence does not identify one remaining lock
as the limiting factor or establish that another storage representation would
reach the target. Saves still load and validate the current package, including
whole-package hashing; incremental physical storage does not make that work
proportional only to changed bytes.

Iteration stops at this measured plateau. No clean 10,000/minute point has been
established. Further changes need evidence that reducing verified-content reads,
graph parsing or publication work improves the full mixed workload; removing
more guards or changing representation on these samples alone is unjustified.

## Retained document staging admission measurements

The security fix charges unique content handles and ownership metadata against
the affected document before physical staging. A failed save keeps its pending
charge until successful publication or explicit quiescent collection. This adds
one transaction under that document's row lock per content batch. Physical
staging and final publication retain their separate transactions. It adds no
provider-wide counter or bucket. Known revision, receipt and delivery limits,
and the combined response-size limit, reject before physical staging.

Fresh runs retained the pool cap of 64, automatic preparation cap of 64,
PostgreSQL settings, request admission of 128 globally/two per subject, and the
same 256 KiB package/16 KiB changed-XML workload, warm-up and measurement periods.

| Profile | Offered saves/minute | In-window acknowledgements | Excluded late acknowledgements | HTTP result |
| --- | ---: | ---: | ---: | --- |
| Primary, save every 30 seconds | 3,000 | 2,992 | 7 | No failures |
| AutoSave, save every 8 seconds | 11,250 | 4,854 | 114 | Overload failures, HTTP 503 |

Neither run missed a user cadence slot. All 4,483 primary and 7,024 AutoSave
acknowledgements, including warm-up/drain, passed final-package, revision-count,
receipt-count and distinct expected-payload-hash checks. These runs did not
re-read every acknowledged historical package against its expected hash.
The AutoSave step totals, including warm-up/drain
and queries/opens, include 12,956 HTTP 503 responses; they are not a count confined
to saves in the measured minute.

The AutoSave result is about 8% below the earlier 5,286 run. These single
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

Import setup is outside request timing. Bounded EF flushes and a verified temporary
payload spool reduced one 15,000-document import from 336 seconds to about
80 seconds. The change keeps all import flushes and its recovery receipt inside
one atomic transaction. Batching bounds extra EF tracking, not the caller-supplied
complete snapshot's memory.

Query statistics cover warm-up, measurement and drain before final validation.
They are not per-request wall-clock traces.
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
waits can escape its sampling. This is still an overload result with HTTP 503
failures.

A fresh run of the primary profile used the same final source, pool cap 64 and
automatic preparation cap 64, with each user saving every 30 seconds. It passed
without HTTP failures or missed cadence slots. Of 2,998 saves started during
measurement, 2,994 were acknowledged inside the minute and four completed later.
All 4,482 acknowledged saves including warm-up passed final content, revision,
receipt and distinct-payload checks. Queries and opens ran alongside saves.
WebDemo used 143% of one logical CPU and at most 579,747,840 sampled RSS bytes
during measurement. This single passing run supports approximately 3,000 offered
saves/minute on the measured host, not the 10,000 target.

That primary run made 80,814 document-head SELECTs. The service paths account
for them exactly: seven per save, eight per query and four per download. Each
open performs both a download and a query. These are current metadata reads,
not history scans. An experiment reused the entry head for the first operation
to remove one read per Cell request. It failed the existing file/metadata tests
where a lease is reacquired after the entry snapshot but before operation
admission: a successful save became a conflict. The experiment was reverted.
This rejects that reuse strategy, rather than
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
server's WAL allowance while retaining settings for commit persistence. PostgreSQL explains
[how frequent checkpoints increase I/O](https://www.postgresql.org/docs/18/wal-configuration.html).
`Parameters__LoadPostgreSqlPoolSize` bounds each WebDemo document and Identity
pool separately. Account for both pools and other clients in the server connection
budget. `Parameters__LoadQueryStatistics=true` captures normalized statement totals,
I/O and WAL deltas, durability settings and connection samples in
`artifacts/load-tests/*.pgstats.json`, covering warm-up and measurement before validation.

`Parameters__LoadDirectHttps=true` selects the owned WebDemo HTTPS listener over
loopback for a proxy-path experiment. The default `false` keeps the Aspire HTTPS
proxy. HTTP health checks and PostgreSQL routing remain unchanged. Setup reports
the allocated mode and ports and verifies the direct listener's loopback binding.
Compare both modes on the same commit, profile and resource limits, and retain
their results as separate configurations.

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

| Profile | Commits inside measured minute | Save starts | Late acknowledgements | Failed HTTP steps, including warm-up |
| --- | ---: | ---: | ---: | ---: |
| Primary | 2,998 | 3,000 | 2 | 0 |
| Larger documents | 2,966 | 2,998 | 23 | 33 |
| Frequent AutoSave | 6,569 | 11,244 | 117 | 9,529 |
| Stretch | 5,690 | 9,997 | 79 | 14,677 |

Only primary passed the check requiring no HTTP failures. All reported failures
in these runs were HTTP 503 admission rejections. Late acknowledgements are
successful commits outside the strict time window, counted separately from
failures. All four runs reported zero missed cadence slots, and every acknowledged
commit had a distinct expected payload hash. Validation checked final packages,
revision metadata and receipt counts; it did not re-read every historical package. Primary's maximum action-start
lateness was 22 ms. Mean serialized save sizes were 17,732 bytes for primary and
stretch, 17,733 for frequent AutoSave and 17,770 for larger documents. All changed
ZIP byte totals were 16,700 per save. The common unchanged package parts remained warm.

Primary's successful save p50/p95/p99 latencies were 23.44/46.17/69.06 ms; query
latencies were 7.11/17.41/24.54 ms and open-download latencies were
4.27/12.09/18.41 ms. These CSV step measurements exclude scheduled waits and can
include late completions. Use the strict counter above for commits/minute.

A separate eight-client, 1 MiB full-replacement diagnostic improved from 297 to
377 saves in 30 seconds, or 9.90 to 12.57 saves/second, without HTTP failures in
the final run.
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
| Persisted graph and revision trees | Approximately `O(delta log g + log n)` | Design only; requires root publication and collection |
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

| Implementation | Strict commits/minute | Failed HTTP steps including warm-up |
| --- | ---: | ---: |
| Baseline | 6,481 | 9,591 |
| Shared memberships only | 6,297 | 9,938 |
| Optimistic preparation only | 6,914 | 8,478 |
| Shared memberships and optimistic preparation | 6,928 | 8,617 |
| Plus selective delta loading | 7,579 | 7,737 |
| Plus one fresh save-entry view | 7,919 | 6,998 |
| Combined publication read, reverted | 7,886 | 7,123 |
| Deferred initial binary read, reverted | 7,905 | 7,069 |

All runs overloaded. Every acknowledged expected payload hash was distinct,
and all runs reported zero missed cadence slots. Verification covered final
packages and revision/receipt counts, not each historical package. The small differences in these
single runs do not establish statistical significance. The last two read experiments
did not improve throughput and were reverted. Shared memberships did not improve
the young-document stress result on their own; their benefit appeared in the aged
library workload. The retained combination increased strict acknowledgements by
about 22% over this fresh baseline, but did not meet the 10,000/minute target.

The retained implementation was also checked below the overloaded stress rate:

| Save interval, 1,500 users | Strict commits/minute | Save starts | Late acknowledgements | Failed HTTP steps including warm-up | Successful-save p95 |
| --- | ---: | ---: | ---: | ---: | ---: |
| 30 seconds | 2,998 | 3,000 | 2 | 0 | 35.49 ms |
| 15 seconds | 5,994 | 5,998 | 4 | 0 | 133.63 ms |
| 12 seconds | 7,337 | 7,497 | 104 | 565 | 926.72 ms |

All retained-profile acknowledgements had distinct expected payload hashes and
zero missed cadence slots. These runs checked final packages and revision/receipt
counts, not each historical package. The 15-second profile offers 6,000 saves/minute; it
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
The repeated-save diagnostic uses warm shared
immutable content; the HTTP workload above uses globally distinct edits.

The final diagnostic also checked 999 revisions before a measured save appended
the 1,000th. Decode allocated 14.04 MB versus 3.50 MB at 250 revisions, and full-save
time was 39.86 ms versus 9.32 ms. Tests independently check shared backing slots,
logical quota boundaries and allocation growth through 1,000 revisions. These
results support the normal append-history improvement; they do not remove the
linear metadata work or the arbitrary-membership fallback described above.

## Quota allowances and chunk inserts

The next experiment reduced contention on the global storage quota row. PostgreSQL
and SQL Server now keep 64 allowance buckets stored in the database, selected by hashing all bytes
of the document ID. A normal positive atomic save consumes its bucket's allowance
in the same transaction as publication. The slow path locks the global ledger and
then the buckets in a fixed order, admits the save and tops its bucket up toward
4 MiB. Failed claims release their bucket locks before entering that path.

If `C` is globally charged capacity and `R` is each bucket's unused allowance,
exact retained usage is `C - SUM(R)`. Reservations consume quota headroom rather
than permitting an overshoot. Near the limit, the slow path reclaims unused
allowances before checking admission. Deletion, pruning and other ordinary quota
writers also reclaim allowances under the same lock order. Database rows and
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
approximately 13 GiB host, database settings and admission configuration
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
also reverted.

A final control rebuilt commit `07db700` with the same activity sampler and
pre/post inventory checks as the final implementation. It completed 7,974 strict
commits/minute; the final implementation completed 8,151, a 2.2% difference in
these single runs. Failed HTTP steps including warm-up were 6,349 and 6,334,
respectively. Save p95 was 1,084.41 ms and 1,122.30 ms. The earlier exploratory
high of 8,690 is not the final build's qualified result. Both final runs failed
the overload gate. Quota-class lock blocked backend observations fell from 780
to 121 across 378 samples per run.

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
throughput, and excluded protocol graph metadata. It supports further
qualification of package layouts, not a large storage-format rewrite or claims
about ordinary Office files.

Final validation passed 2,236 solution tests, with three opt-in tests skipped.
PostgreSQL and SQL Server user HTTP smoke runs each acknowledged all fifty
measured saves and verified fifty-nine distinct committed payloads including
warm-up. All six combined legacy smoke scenarios passed, including ten HTTP
commits and ten replays. These checks establish the exercised behavior, not
SQL Server capacity or live desktop Office compatibility.
