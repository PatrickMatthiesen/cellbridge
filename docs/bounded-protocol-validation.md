# Bounded protocol validation

The deterministic tests added for [#34](https://github.com/PatrickMatthiesen/cellbridge/issues/34)
exercise current codecs, graph reconstruction and complete-save publication.
They add reproducible cases to the [requirements ledger](protocol-requirements.md).
They do not qualify desktop OneNote, two-desktop coauthoring or Office Online
Server, and do not establish equivalence to SharePoint server processing.

| Suite | Fixed bound and variations | Assertion |
| --- | --- | --- |
| [Framing mutations](../tests/CellBridge.FssHttpB.Tests/BoundedProtocolMutationTests.cs) | Seed `0x1834`; 16 requests with 1–4 allocation operations, varied identities/priorities/counts, at most 1 KiB each; 16 allocation responses. Every shorter prefix and each of eight signature bytes. | Complete valid consumption; malformed cases fail with `EndOfStreamException` or `InvalidDataException`, rather than any exception. Response reserved bits remain accepted. |
| Optional unknown leaves | Payload sizes 0, 1, 7, 31, 255; a declared 4096-byte payload without those bytes. | Valid unknown optional objects are skipped with complete consumption; malformed declared length fails. |
| [Graph properties](../tests/CellBridge.FssHttpB.Tests/BoundedGraphPropertyTests.cs) | Seed `0x3418`; 32 two-cell inherited graphs, content 1–4096 bytes, inline/BLOB, nearest revision override and allowed cell cycles. | Cell/partition identity and effective bytes survive reconstruction. Duplicate element identity, revision-base cycles and missing BLOBs fail explicitly. |
| Graph budgets | Fixed model, five budgets: element count, payload sum, revision depth 2, object visits 10, reference visits 20. | Each exact boundary succeeds; one less fails. These visit budgets count parsing and resolution work, not just distinct nodes. |
| [Microsoft differential decoding](../tests/CellBridge.Interop.Tests/BoundedDifferentialGraphTests.cs) | Seed `0x1834`; 32 generated responses at most 16 KiB; shuffled element order, inline/BLOB, overrides and cell cycles. | Independent decoded IDs, counts, serial mappings, revision bases, manifest roots, partition declarations, BLOB references and bytes match the model. Microsoft parser also verifies full response consumption. |
| [Persisted transaction properties](../tests/CellBridge.Storage.Tests/BoundedTransactionPropertyTests.cs) | Seed `0x1834`; eight documents per provider; reviewed captured complete saves with varied request IDs, MultiRequestPutHint and captured reserved flag behavior. In-memory and disposable PostgreSQL. | Success increments file version and creates one receipt. Retry after service/provider recreation returns identical acknowledgement and full persisted state. Partial/PartialLast rejection and exact Cell/CoherencyFailure leave state, content, index, knowledge and receipts unchanged. |

Microsoft Interop-TestSuites parser sources already vendored under
[MicrosoftProtocol](../tests/CellBridge.Interop.Tests/MicrosoftProtocol) remain
unchanged, with their original license notices. This parser is independent of
CellBridge's decoder, but parsing success is not independent validation of
server graph traversal, locking or publication semantics. The generated model
supplies expected semantic values; local serialization is still part of the
input construction. No new SharePoint reference captures or defect fixtures
were produced by these bounded runs.

With this workspace's Aspire app stopped, run the fast checks:

```sh
dotnet test tests/CellBridge.FssHttpB.Tests -c Release --filter FullyQualifiedName~Bounded
dotnet test tests/CellBridge.Interop.Tests -c Release --filter FullyQualifiedName~BoundedDifferentialGraphTests
dotnet test tests/CellBridge.Storage.Tests -c Release --filter FullyQualifiedName~BoundedTransactionPropertyTests
```

The standalone storage command skips PostgreSQL unless its test connection is
provided. Use the inspected isolated runner for complete live/provider validation:

```sh
python3 tools/testing/run.py --output artifacts/testing/audit-fuzz
```

See [test prerequisites and isolation](automated-testing.md). The runner creates
and stops its own disposable Aspire stack; use only your bound worktree.
Diagnostics record seed, sample, mutation, byte count or budget. Keep TRX files,
raw mutation output and run summaries under ignored `artifacts/`. To investigate
a failure, reproduce the reported seed/sample, reduce the input while preserving
the same failure, identify the normative expectation and consult the production
owner. A production fix requires a concrete design review. Only a reviewed
minimal regression fixture belongs under `testdata/`, with its provenance and
expected behavior; no future staging/history/deletion/lock behavior is introduced
to make the current tests pass.

Remaining #34 gates include additional compound/nested/header combinations,
identity collision and reference permutations, independent server processing
vectors, and bounded tests for future implemented modes. The ledger retains
these separately from optional optimizations and unqualified clients. This
suite does not close #18 or #34 by itself.
