# Package consumption tests

The document and Lists consumer tests use NuGet package references only, without
source project references or repository fixtures. Document checks include
custom-provider conformance and reusable HTTP
hosting through discovery, download, HEAD and SOAP Cell QueryAccess.

Run from the repository root with Aspire stopped:

```sh
python3 tools/verify_packages.py
python3 tools/verify_packages.py --sqlserver
```

The script packs all fourteen libraries at `0.1.0-beta.4`, validates package and
symbol metadata, and restores the
[consumer example](../../examples/NuGetConsumer/README.md) and package-only test
consumers from an isolated local feed/cache. By default it builds both document
and Lists consumers, runs non-database checks and reports database skips.

`--sqlserver` uses source test orchestration to start the existing disposable
Aspire `SqlServerEnvironment`, then launches separate package-only document and
Lists test processes against separate empty databases. Consumers explicitly call
their public migrators with setup credentials and use restricted runtime options
for operations. Registration and requests never migrate schemas. The SQL Server
packages depend on neither each other nor PostgreSQL. See
[SQL Server prerequisites](../../docs/automated-testing.md#sql-server-engine-checks).
The document consumer checks create/rename and content/state continuation through
fresh providers. The Lists consumer checks create/read, an exact text query and
create retry. These checks use the public packaged APIs.

Ignored `artifacts/packages` retains manifests, the isolated feed/cache, consumer
assembly identities and hashes, TRX results and SQL engine version. Database
skips do not establish durable behavior. Environment startup failures are setup
failures; consumer checks do not establish full provider parity, SQL Server
performance or live Office interoperability.

The package-only test projects stay outside `CellBridge.slnx` until the packed
feed exists. The package verification script runs in CI.
