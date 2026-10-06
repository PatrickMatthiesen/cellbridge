# Beta release checklist

The [beta release tracker](https://github.com/PatrickMatthiesen/cellbridge/issues/43) covers publication.
The published first beta is `0.1.0-beta.1`, targeting .NET 10. Its purpose is to let
external hosts consume CellBridge without a sibling source checkout and reuse
the supported SOAP execution, authorization and persistence APIs. Pre-release
versions carry no stable API or storage-upgrade guarantee.

## Published release

Issue #43 is completed. All nine packages below and their matching `.snupkg`
symbols are public on NuGet.org, owned by `CellBridge`. The release source is
[`b22bc2d29ec0cff61cd212c7d41fc68a297b3003`](https://github.com/PatrickMatthiesen/cellbridge/commit/b22bc2d29ec0cff61cd212c7d41fc68a297b3003),
and [tag `v0.1.0-beta.1`](https://github.com/PatrickMatthiesen/cellbridge/releases/tag/v0.1.0-beta.1)
identifies that commit. Fresh consumption from NuGet.org passed all four
CellBridge consumer tests and all 15 WopiHost PR #735 adapter tests pinned at
`1b25dd232afb12f3962fe3d37c295f4f52e1aa2f`.

Remote restoration, package execution and symbols are completed release gates.
They do not qualify Office Online Server editing, OneNote synchronization or
two-desktop coauthoring. The workflow instructions below apply to a future
reviewed release. Do not republish the immutable `0.1.0-beta.1` version.

## Release contents

These nine packages were published at the same version, together with their symbols:

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

## Trusted Publishing setup

The NuGet organization is `CellBridge`; its administrator and policy creator is
`Subjective`. Keep `NUGET_USER=Subjective` as a GitHub repository variable.
The login action uses the individual policy creator's profile name. The policy's
package owner is the organization. No permanent NuGet API key or GitHub secret
is required.

In [NuGet Trusted Publishing](https://www.nuget.org/account/trustedpublishing),
create this policy while signed in as `Subjective`:

| Field | Value |
| --- | --- |
| Policy name | `CellBridge GitHub releases` |
| Package owner | `CellBridge` |
| Repository owner | `PatrickMatthiesen` |
| Repository | `cellbridge` |
| Workflow file | `publish-nuget.yml`, filename only. |
| Environment | `nuget` |
| Package pattern | `CellBridge.*` |
| Scopes | Publish new packages and publish new versions. |

The GitHub `nuget` environment allows the `main` branch. The publishing job alone
has `id-token: write`. [NuGet's setup guide](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing)
describes the policy fields and temporary credential exchange.

## Run the release workflow

After merging reviewed changes and configuring the policy, open
[Publish NuGet beta](https://github.com/PatrickMatthiesen/cellbridge/actions/workflows/publish-nuget.yml).
Select `main`, enter the version in `Directory.Build.props` and leave `publish`
unchecked to validate packages and test the NuGet login without uploading.
The workflow tests protocol/storage and demo
code against PostgreSQL, runs Python checks, packs the nine libraries and tests
package consumption. It uploads `nuget-release-<commit>` containing the exact
packages, symbols and manifest. It validates that run's downloaded artifacts
and requests temporary credentials through the configured Trusted Publishing
policy. A successful login verifies authentication; it does not prove package
upload permissions or first-publication success.

For publication, run the same workflow on the intended unchanged `main` commit
with `publish` checked. It builds and validates that run's artifacts, then
validates the downloaded hashes, archive metadata, clean-source flag and commit
again before obtaining temporary credentials. It publishes dependency packages
before the host package, followed by symbols. Runs are serialized, and publication
cannot run from a tag or feature branch.

The existing protocol CI also uploads `cellbridge-beta-packages`. That is useful
for evaluating a PR but is not automatically published. NuGet publication happens
only through the explicit release workflow. Verify fresh NuGet.org restoration
and create release notes/tag afterward; this workflow does not claim those steps
are complete just because uploads succeeded.

### Partial publication and retry

NuGet publication is not atomic across nine packages. The workflow stops on the
first upload failure or version conflict; it does not skip duplicates. Preserve
the run artifact and determine exactly which package and symbol versions were
accepted. Do not blindly rerun the full publish job after partial publication.

A maintainer can recover by verifying accepted versions against the original
release artifact/provenance and publishing only the remaining artifacts using
appropriately scoped credentials. NuGet repository signing means an archive's
remote byte hash need not equal the original `.nupkg` hash. If matching provenance
cannot be established, choose a new beta version and run the full reviewed
release process. A symbol failure after all primary uploads does not require
republishing the primary packages.

## Integration qualification

[WopiHost PR #735](https://github.com/petrsvihlik/WopiHost/pull/735#issuecomment-5993115599)
identified the host APIs this batch addresses. The existing adapter at
`1b25dd232afb12f3962fe3d37c295f4f52e1aa2f` passed its 15 tests against CellBridge
source, local beta packages and freshly restored public NuGet.org packages after replacing only its two CellBridge source
references with package references and adding matching central package versions.
No upstream WopiHost implementation change is included in this repository.
Its tests do not qualify Office Online Server editing or exercise adoption of
the new parsed-SOAP processor. CellBridge's package tests exercise that processor
and compare its response with the mapped HTTP endpoint.

## Remaining limits

Desktop Word and Excel evidence remains scoped to the sessions recorded in
[interoperability](interoperability.md). OneNote desktop synchronization,
two-desktop coauthoring and Office Online Server editing remain unqualified.
True partial and unsupported non-file uploads fail explicitly. Current source
has an explicit [provider recovery workflow](provider-portability.md), with
authorization and destination-context restrictions. Automatic live-graph pruning
remains unavailable. These later source changes are not part of beta.1.

Current source implements the opt-in publication, shared-lock and conditional
lifecycle contracts described in [external host reliability](external-host-reliability.md).
External applications still need to adopt and qualify those contracts.
Accepted-save receipts report known commits and do not replace destination CAS,
durable delivery receipts or coordinated permissions.

## Next candidate

The next candidate is `0.1.0-beta.2`. Follow the [beta.1 upgrade guide](beta-2-upgrade.md).
Publication remains blocked on candidate validation and documented outstanding
client gates. This section does not record a published release or successful
desktop retest.
