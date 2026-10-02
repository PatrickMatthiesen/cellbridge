# Package consumption tests

These tests use NuGet package references only, without source project references
or repository fixtures. They check custom-provider conformance and reusable HTTP
hosting through discovery, download, HEAD and SOAP Cell QueryAccess.

Run from the repository root with Aspire stopped:

```sh
python3 tools/verify_packages.py
```

The script packs the libraries, restores both the
[consumer example](../../examples/NuGetConsumer/README.md) and this project from
an isolated local feed/cache, builds the example and runs the tests. Outputs stay
under ignored `artifacts/packages`.

This project is intentionally outside `CellBridge.slnx`: its packages must be
built before it can restore. The package verification script runs in CI.
