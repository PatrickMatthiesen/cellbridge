# Package-only consumer

This executable verifies that the reusable libraries work through NuGet package
references, without referencing `CellBridge.Web` or repository fixtures. It
composes a custom state-provider wrapper with in-memory content and exercises
provider conformance, discovery, download, HEAD and a SOAP Cell QueryAccess.
The embedded test server does not require an Office installation or PostgreSQL.

With Aspire stopped, run from the repository root:

```sh
python3 tools/verify_packages.py
```

The script packs the libraries, restores this consumer from an isolated local
package feed and cache, builds it, and runs its checks. Generated packages and
cache files stay under ignored `artifacts/packages`. See
[storage and hosting setup](../../docs/storage-providers.md) for a durable
application and custom-provider contracts.
