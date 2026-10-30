# Office publication model

[OfficePublication.tla](OfficePublication.tla) checks the publication contract in
[the storage design](../docs/office-storage-design.md). This is finite-state model
checking of an abstraction. It is not a proof of the C# implementation, SQL
isolation behavior, arbitrary populations, protocol parsing, or throughput.

## Checked scope and assumptions

The model has two workers, two documents, two mapping keys, one authenticated
actor, and one immutable request per worker. Each request names lifecycle 0,
an expected empty target mapping, an operation key, and a distinct digest.
Lifecycle replacement advances to 1 and invalidates these old requests. It does
not model new requests for the replacement document or lifecycle reuse.

`Prepare` abstracts successful body/package validation and durable immutable
payload staging. Validation correctness, graph ancestry, checkpoint construction,
external storage failures and exact package-byte reconstruction require separate
checks. A payload token stands for its entire validated durable dependency closure.
No collector runs. Staged objects may remain unreachable until quiescent cleanup.

Each document has its own `guard[d]`. Permission revocation, lifecycle replacement
and lease revocation require that document's free guard and update control state
atomically. They abstract transactions taking the same row lock as publication.
There is no provider-wide mutex, quota counter or cross-document deduplication.
Any exact shared quota would require a separate admission model.

`Tick` can advance provider time while a document guard is held. `Publish` reads
that current time and atomically linearizes admission, binding publication and
receipt creation. The implementation must identify the corresponding guarded
transactional admission point and read authoritative time there. This does not
prove that the physical database COMMIT completes before wall-clock expiry.
Permission and lifecycle changes cannot interleave inside this atomic decision;
external authorization requires an explicit equivalent fencing contract.

`Crash` releases all volatile locks and preparation state while preserving
committed heads, receipts and staged payloads. A commit with a lost response enters
`unknown`; crash recovery looks up the receipt before attempting another effect.
Receipt lookup here resolves the internal accepted outcome. It does not authorize
returning protected response data to a caller who has since lost access.
Cancellation before publication leaves staged objects; cancellation after an
unknown commit does not undo that commit. Fairness, starvation freedom and retry
bounds are outside this safety model. Terminal and blocked states are allowed,
so deadlock checking is disabled deliberately.

## Invariants and implementation obligations

| Invariant | Required implementation behavior |
| --- | --- |
| `TypeOK`, `GuardOwnership` | The guard belongs only to a worker publishing that document. |
| `DurableHeads`, `ReceiptPayloadDurable` | Published references and accepted receipts refer only to durably staged, immutable content. |
| `AtMostOnce`, `ReceiptAtomicity` | Head mutation and the actor/lifecycle/operation-key receipt commit together. Receipt identity excludes the digest; the stored digest distinguishes a replay from conflicting reuse. |
| `SuccessfulReceipt` | Successful receipt resolution matches the original digest. |
| `CurrentAuthority` | Check current permission, lifecycle, lease ownership and provider time at guarded admission. |
| `PreserveOtherMappings` | Publishing a selected candidate cannot silently discard an unrelated committed mapping. |
| `KeywiseCoherency` | A changed target mapping fails its client precondition. An unrelated mapping does not. |
| `IndependentGuards` | A ready worker can acquire its free document guard even if the other document is locked. |

The audit variables `authSafe`, `rootSafe` and `keySafe` record violations across
transitions for TLC. They are specification instrumentation, not proposed runtime
flags or additional checks.

| Transition | Implementation acceptance test |
| --- | --- |
| `Prepare`, `Acquire` | Pause after durable staging; another document can stage and publish without waiting on this document. |
| `Reprepare` | Stage two candidates changing different keys. Publish the first, then rebuild the second from the current root and publish both changes without a wire coherency error. Expensive rebuilding happens after releasing the guard. |
| `Publish`, `Reject` | With the same target precondition, two changes to that target cannot both publish. Unrelated root changes alone do not reject. |
| `Revoke`, `ReplaceLifecycle`, `RevokeLease`, `Tick` | Change each condition after preparation. Also expire the lease after guard acquisition. The later admission must fail. |
| `Resolve` | Retry a lost response and reuse its actor/lifecycle/key with a different digest. Return the recorded outcome for a match and reject conflicting reuse without another effect. |
| `Crash`, `Cancel` | Inject failure before staging, before publication and after commit. Recovery never exposes missing content or repeats the accepted effect. |

The bounded results support removing a provider-wide publication gate, nested
process publication locks, full-history validation during guarded admission, and
wire rejection based only on an unrelated head change, provided implementation
satisfies these assumptions. The model does not establish that arbitrary existing
checks are redundant. Retain the affected-document transaction/guard, durable
staging, current authority and lifecycle checks, target-key coherency, stale-root
repreparation, and atomic durable receipt. Checking a complete candidate against
its expected root is necessary here because it replaces a whole mapping root.
A different implementation that updates only affected bindings needs its own
argument that its selected file/package still includes concurrent commits.

## Executed results

On 2026-10-28, TLC 2.19 from `tla2tools.jar` release 1.7.4 ran with Eclipse Temurin
21.0.13, two workers and a 1 GiB heap. All eleven invariants passed exhaustive
reachable-state exploration in each safe configuration. No state constraint or
search-depth cutoff was used.

| Configuration | Generated states | Distinct states | Search depth |
| --- | ---: | ---: | ---: |
| Same document, different keys, distinct operation keys | 9,275 | 2,419 | 14 |
| Different documents | 82,996 | 15,313 | 17 |
| Same document and target key | 8,031 | 2,035 | 13 |
| Same actor and operation key, different digests | 6,427 | 1,651 | 12 |

Coverage records execution of `Reprepare`. A separate reachability check adds
`NoSimultaneousGuards` as an expected-to-fail invariant with different documents.
Its five-state trace acquires both document guards simultaneously. This confirms
that the independence check is not satisfied only because simultaneous activity
is unreachable.

Both unsafe controls produced counterexamples:

- With `FenceAuthority = FALSE`, a five-state trace prepares, acquires the guard,
  advances provider time past lease expiry, and publishes. `CurrentAuthority`
  fails. A lock alone does not keep an expiring lease valid.
- With `FenceRoot = FALSE`, a seven-state trace prepares both candidates against
  the empty root. Worker 1 publishes `x = w1`. Worker 2 then publishes its stale
  candidate `y = w2`, resetting `x` to empty. Its target-key precondition still
  passes, but `PreserveOtherMappings` fails. Keywise coherency alone cannot make
  whole-candidate replacement safe.

These are counterexamples to omitted checks in this algorithm, not a claim that
every correct algorithm needs the same fields or locks. The raw logs, exact
configuration variants, source snapshot, tool URLs and hashes are retained in the
ignored `artifacts/office-rewrite/model/` directory for this rewrite. Only the
model, default configuration and this guide belong in Git.

## Reproduce

Download the [official TLC release](https://github.com/tlaplus/tlaplus/releases/tag/v1.7.4)
and use a Java 11 or newer runtime. The verification used a private JRE from the
[official Adoptium API](https://adoptium.net/installation/ci-scripts); its SHA-256
matched the release metadata. Set `JAVA_BIN` and `TLC_JAR` to absolute paths.
From the repository root:

```sh
model_tmp=$(mktemp -d)
cp models/OfficePublication.cfg "$model_tmp/safe.cfg"
sed 's/Target <- SameDocument/Target <- DifferentDocuments/' models/OfficePublication.cfg > "$model_tmp/different-documents.cfg"
sed 's/Slot <- DifferentKeys/Slot <- SameKey/' models/OfficePublication.cfg > "$model_tmp/same-key.cfg"
sed 's/RequestKey <- DistinctRequests/RequestKey <- SharedRequest/' models/OfficePublication.cfg > "$model_tmp/receipt-collision.cfg"
sed 's/FenceAuthority = TRUE/FenceAuthority = FALSE/' models/OfficePublication.cfg > "$model_tmp/unsafe-authority.cfg"
sed 's/FenceRoot = TRUE/FenceRoot = FALSE/' models/OfficePublication.cfg > "$model_tmp/unsafe-root.cfg"
sed '/CHECK_DEADLOCK/i INVARIANT NoSimultaneousGuards' "$model_tmp/different-documents.cfg" > "$model_tmp/concurrent-guard-witness.cfg"
for variant in safe different-documents same-key receipt-collision unsafe-authority unsafe-root concurrent-guard-witness; do
    (cd models && "$JAVA_BIN" -XX:+UseParallelGC -Xmx1g -jar "$TLC_JAR" \
        -workers 2 -coverage 1 -metadir "$model_tmp/$variant-states" \
        -config "$model_tmp/$variant.cfg" OfficePublication.tla) \
        > "$model_tmp/$variant.log" 2>&1
    result=$?
    printf '%s: exit %s\n' "$variant" "$result"
done
```

Expect exit 0 for the four safe variants and exit 12 with the named invariant
violation for both unsafe controls and the reachability witness. The number of
states visited before a counterexample can vary with scheduling. Inspect the
logs before deleting the temporary directory with `rm -rf "$model_tmp"`.
