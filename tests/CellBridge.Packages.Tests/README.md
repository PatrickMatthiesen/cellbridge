# Package consumption tests

The document and Lists tests use NuGet package references only. Document checks
cover custom-provider conformance and HTTP hosting for discovery, download, HEAD
and SOAP Cell QueryAccess.

Run from the repository root with Aspire stopped:

```sh
python3 tools/verify_packages.py
python3 tools/verify_packages.py --sqlserver
```

The script packs the libraries, checks package metadata and restores the
[consumer example](../../examples/NuGetConsumer/README.md) and package tests from
an isolated local feed and cache. By default it builds document and Lists
consumers, runs non-database checks and reports database checks as skipped.

`--sqlserver` starts a disposable SQL Server environment and runs the document
and Lists package consumers against separate databases. Each consumer calls its
package migrator with setup credentials, then uses restricted runtime credentials.
Registration and requests never migrate schemas. See
[SQL Server prerequisites](../../docs/automated-testing.md#sql-server-engine-checks).
The consumers test the public package APIs.

Outputs go under ignored `artifacts/packages`. Skipped database checks are not
passes. These package checks do not test live Office editing or SQL Server
performance.

The package-only test projects stay outside `CellBridge.slnx` until the packed
feed exists. The package verification script runs in CI.
