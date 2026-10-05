# Beta release checklist

The [beta release tracker](https://github.com/PatrickMatthiesen/cellbridge/issues/43) covers publication.
The first candidate is `0.1.0-beta.1`, targeting .NET 10. Its purpose is to let
external hosts consume CellBridge without a sibling source checkout and reuse
the supported SOAP execution, authorization and persistence APIs. Pre-release
versions carry no stable API or storage-upgrade guarantee.

## Release contents

Publish these nine packages at the same version, together with their symbols:

| Package | Purpose |
| --- | --- |
| CellBridge.AspNetCore | HTTP hosting and parsed-SOAP execution, actors, permissions and save results. |
| CellBridge.FssHttp | SOAP/MTOM codecs and outer protocol state. |
| CellBridge.FssHttpB | Binary codecs and graph resolution. |
| CellBridge.Storage.Abstractions | State/content/provider contracts and limits. |
| CellBridge.Storage | Persistence codecs and immutable graph snapshots. |
| CellBridge.Storage.InMemory | Volatile development storage. |
| CellBridge.Storage.PostgreSql | Durable database state and content. |
| CellBridge.Storage.FileSystem | Immutable filesystem content with database coordination. |
| CellBridge.Storage.Conformance | Checks for custom provider implementations. |

The sample web host, authentication library, demo and test utilities are not
published libraries. Packages include the MIT license declaration, repository
metadata, readme and portable symbol packages.

## Acceptance before publication

1. Merge the reviewed release changes and require the protocol, PostgreSQL,
   capture, demo and package-consumer CI checks to pass on that source.
2. Run `python3 tools/verify_packages.py` from the exact release commit with this
   workspace's Aspire stopped. It clears only `artifacts/packages`, packs the
   expected library set and tests consumers against an isolated local feed.
3. Check `manifest.json`: nine matching versions and SHA-256 hashes, with each
   `repositoryCommit` matching the selected release commit and `sourceDirty` false. Keep the tested
   artifacts for publication; do not repack different source under the same version.
4. Test an external consumer against package references. Record its source commit,
   package hashes and test scope. Source-reference success alone is insufficient.
5. Confirm NuGet.org ownership of every package ID and configure a publisher with
   access to all nine IDs. Do not commit credentials. Publishing reserves an
   immutable ID/version, so resolve every release blocker first.
6. Publish dependency packages before the hosting package, then verify a fresh
   consumer can restore `CellBridge.AspNetCore` and the chosen provider from
   NuGet.org without the local feed. Upload matching symbol packages and create
   the release tag and notes for the source commit.

CI's protocol job uploads `cellbridge-beta-packages`, containing the exact
`.nupkg`/`.snupkg` files and manifest after successful verification. It does not
publish automatically. Use that artifact or an equivalent verified release-commit
build. NuGet publishing and GitHub release creation remain explicit release steps.

## Integration qualification

[WopiHost PR #735](https://github.com/petrsvihlik/WopiHost/pull/735#issuecomment-5993115599)
identified the host APIs this batch addresses. The existing adapter at
`1b25dd232afb12f3962fe3d37c295f4f52e1aa2f` passed its 15 tests against CellBridge
source and the local beta packages after replacing only its two CellBridge source
references with package references and adding matching central package versions.
No upstream WopiHost implementation change is included in this repository.
Its tests do not qualify Office Online Server editing or exercise adoption of
the new parsed-SOAP processor. CellBridge's package tests exercise that processor
and compare its response with the mapped HTTP endpoint.

## Remaining limits

Desktop Word and Excel evidence remains scoped to the sessions recorded in
[interoperability](interoperability.md). OneNote desktop synchronization,
two-desktop coauthoring and Office Online Server editing remain unqualified.
True partial and non-file uploads fail explicitly. There is no stable storage
migration/export workflow or automatic live-graph pruning.

External hosting also needs [recoverable content write-back with destination CAS](https://github.com/PatrickMatthiesen/cellbridge/issues/44),
[shared WOPI/FSSHTTP leases](https://github.com/PatrickMatthiesen/cellbridge/issues/45)
and [conditional document deletion/eviction](https://github.com/PatrickMatthiesen/cellbridge/issues/46)
that cannot resurrect stale state. Accepted-save
receipts report known commits and do not replace those mechanisms. These are
integration limits, not prerequisites for an honestly scoped experimental beta.
