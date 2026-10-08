# Bounded protocol validation

The deterministic tests added for [#34](https://github.com/PatrickMatthiesen/cellbridge/issues/34)
exercise current codecs, graph reconstruction and complete-save publication.
They add reproducible cases to the [requirements ledger](protocol-requirements.md).
They do not qualify desktop OneNote, two-desktop coauthoring or Office Online
Server, and do not establish equivalence to SharePoint server processing.

Header cases follow the Microsoft rules for
[16-bit starts](https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttpb/a1017f48-a888-49ff-b71d-cc3c707f753a),
[32-bit starts](https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttpb/ac629d63-60a1-49b2-9db2-fa3c19971cc9)
and compound terminators. Graph expectations follow the
[MS-FSSHTTPB data model](https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttpb/6c7e4447-6ccd-4764-8dbc-17a382fb631d).

| Suite | Fixed bound and variations | Assertion |
| --- | --- | --- |
| [Framing mutations](../tests/CellBridge.FssHttpB.Tests/BoundedProtocolMutationTests.cs) | Seed `0x1834`; 16 requests with 1–4 allocation operations, varied identities/priorities/counts, at most 1 KiB each; 16 allocation responses. Every shorter prefix and each of eight signature bytes. | Complete valid consumption; malformed cases fail with `EndOfStreamException` or `InvalidDataException`, rather than any exception. Response reserved bits remain accepted. |
| Optional unknown leaves | Payload sizes 0, 1, 7, 31, 255; a declared 4096-byte payload without those bytes. | Valid unknown optional objects are skipped with complete consumption; malformed declared length fails. |
| [Nested framing mutations](../tests/CellBridge.FssHttpB.Tests/BoundedNestedFramingTests.cs) | Seed `0x1834`; 16 trees with 1–5 compounds at each of four sites: request, UserAgent, subrequest and raw data-element payload; at most 1 KiB per small case. Mixed 16/32-bit starts and representable 8/16-bit ends, header-like preambles and siblings. Every shorter prefix, missing/mismatched/premature ends, compound-bit flips and oversized preambles. | Valid siblings and raw element bytes survive. Complete-payload decoding rejects malformed framing with `EndOfStreamException` or `InvalidDataException`. An early outer end can leave trailing bytes in the stream decoder; the host rejects those bytes. |
| Nested and length boundaries | Terminal leaf at depth 32 versus 33 in each skip path; leaves of 127, 128, 32766 and 32767 bytes, with selected header/payload truncations. | Exact implementation nesting boundary succeeds; one deeper fails. Payloads resembling end headers preserve alignment. LargeLength truncation fails. These are implementation bounds, not protocol depth limits. |
| [Graph properties](../tests/CellBridge.FssHttpB.Tests/BoundedGraphPropertyTests.cs) | Seed `0x3418`; 32 two-cell inherited graphs, content 1–4096 bytes, inline/BLOB, nearest revision override and allowed cell cycles. | Cell/partition identity and effective bytes survive reconstruction. Duplicate element identity, revision-base cycles and missing BLOBs fail explicitly. |
| [Graph identity and reference mutations](../tests/CellBridge.FssHttpB.Tests/BoundedGraphIdentityTests.cs) | Seed `0x3418`; 32 renamed graphs, 12 reference-order cases, nine invalid mutations over six identity/order combinations each. Fixed limits: 11 elements, 16 KiB payload, revision depth 2, 24 object visits and 64 reference visits. Identity maps share GUIDs or integer components, vary encoded identity widths and swap CellId halves. | Paired declaration/data permutations, mapping order and package order preserve cell-local bytes and partition identity. Allowed opaque object/cell cycles preserve reference order. Near-matching GUIDs/integers, swapped halves, cross-cell object references, an existing wrong-kind BLOB target and conflicting mapping keys fail before exhausting budgets. |
| Graph budgets | Fixed model, five budgets: element count, payload sum, revision depth 2, object visits 10, reference visits 20. | Each exact boundary succeeds; one less fails. These visit budgets count parsing and resolution work, not just distinct nodes. |
| [Microsoft differential decoding](../tests/CellBridge.Interop.Tests/BoundedDifferentialGraphTests.cs) | Seed `0x1834`; 32 generated responses at most 16 KiB; shuffled element order, inline/BLOB, overrides and cell cycles. | Independent decoded IDs, counts, serial mappings, revision bases, manifest roots, partition declarations, BLOB references and bytes match the model. Microsoft parser also verifies full response consumption. |
| Microsoft identity and header decoding | Seed `0x3418`; 12 responses with near-colliding identities and paired record permutations, at most 16 KiB. [Header matrix](../tests/CellBridge.Interop.Tests/BoundedDifferentialHeaderTests.cs) uses known leaf/compound types, representable start/end widths and lengths 0, 3, 127, 128, 32766 and 32767. | Microsoft decoding preserves complete identity pairs, serials, mapping targets and declaration/data pairing. Header bytes and decoded lengths agree in both directions. Arbitrary unknown extension acceptance is checked only against CellBridge; Microsoft's decoder rejects unknown type enums. |
| [Local server-processing properties](../tests/CellBridge.Interop.Tests/BoundedServerProcessingTests.cs) | Seed `0x1834`; eight in-memory documents with 1–1024-byte inputs, six mixed operations per request and all four manifest/cell inclusion combinations; five separate unsupported-control cases. Microsoft writes each request, at most 4 KiB; CellBridge decodes and executes it through `CellBridgeDocumentService`; Microsoft parses actual 13/11 responses. | Request IDs/types, access results, allocation cardinality and distinct allocation namespaces match inputs. Returned graph IDs come from pre-execution persisted state and object bytes match imported content. Unknown targets, Partial/PartialLast, continuation requirements and failing hierarchy filters return explicit Cell/RequestNotSupported. Optional hierarchy filters fall back to full data. Valid queries following rejection still execute. Full persisted state and materialized file bytes remain unchanged. |
| [Persisted transaction properties](../tests/CellBridge.Storage.Tests/BoundedTransactionPropertyTests.cs) | Seed `0x1834`; eight documents per provider; reviewed captured complete saves with varied request IDs, MultiRequestPutHint and captured reserved flag behavior. In-memory and disposable PostgreSQL. | Success increments file version and creates one receipt. Retry after service/provider recreation returns identical acknowledgement and full persisted state. Partial/PartialLast rejection and exact Cell/CoherencyFailure leave state, content, index, knowledge and receipts unchanged. |

Microsoft Interop-TestSuites parser sources already vendored under
[MicrosoftProtocol](../tests/CellBridge.Interop.Tests/MicrosoftProtocol) remain
unchanged, with their original license notices. This parser is independent of
CellBridge's decoder. The local server-processing tests also use Microsoft's
request serializer and fixed input/state expectations. They exercise CellBridge
processing, but do not supply an independent SharePoint server oracle for graph
traversal, locking or publication semantics. Generated graph responses still use
local serialization for input construction. The hierarchy-failure test appends
filter flags with Microsoft's header writer because the vendored query writer
omits that optional object. No new SharePoint reference captures or defect fixtures
were produced by these bounded runs; no production defect was reproduced.

Partial-mode tests include the existing stored graph, independently parsed by
Microsoft and carried with its original payload bytes through Microsoft's
envelope writer. A positive control accepts the same graph with only the partial
flags cleared. The optional hierarchy fallback is also executed alone so other
queries cannot supply its missing data through the shared response package.

The shared synthetic graph fixture uses 16-bit starts for fixed storage-index
mapping and manifest records. Validate each graph mutation against a valid
baseline that both independent parsers consume completely under the 12/11 and
13/11 profiles. Both parsers must also decode the mutated wire before its
intended graph error counts as semantic rejection. A framing failure or an
exhausted graph budget does not establish that a reference mutation was rejected
for the correct reason.

OfficeInspectors reads Object Data BLOB payloads as opaque bytes using their
stream-object length. Complete inline and BLOB graphs can therefore be checked
independently, including the existing BLOB-target wrong-kind mutation. This is
separate from decoding save replies that contain only an applied storage index.

The local server-processing tests use disposable in-memory providers and need
no Office client, SharePoint farm or database server. Record the checked-out
commit, tool versions and parser source identity with each run's ignored
artifacts. The PostgreSQL transaction tests require a separate test connection.

With this workspace's Aspire app stopped, run the fast checks:

```sh
dotnet test tests/CellBridge.FssHttpB.Tests -c Release --filter FullyQualifiedName~Bounded
dotnet test tests/CellBridge.Interop.Tests -c Release --filter FullyQualifiedName~Bounded
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

Remaining #34 gates include independent SharePoint server-processing vectors
for these combinations, sanitized reference traffic for identity/reference edge
cases, and bounded tests for future implemented modes. The ledger retains
these separately from optional optimizations and unqualified clients. This
suite does not close #18 or #34 by itself.
