# Working on CellBridge

CellBridge is an experimental ASP.NET Core implementation of MS-FSSHTTP and
MS-FSSHTTPB for desktop Office editing, saving and coauthoring. Read
[README.md](README.md) for current capabilities and limitations.

## Protocol scope

- Use MS-OCPROTO, MS-FSSHTTP and MS-FSSHTTPB as protocol authorities.
- Implement both layers. MS-FSSHTTP is the outer SOAP/MTOM protocol at
  `/_vti_bin/cellstorage.svc`; MS-FSSHTTPB carries the binary Cell payloads.
  See [the protocol decision](docs/protocol-version-decision.md).
- Do not implement unrelated SharePoint APIs such as lists or search.
- Keep binary serialization in its standalone library with protocol unit tests.
- Compare decoded requests and responses with SharePoint reference traffic and
  the OfficeDev Interop-TestSuites. Log enough detail to diagnose wire failures.

## Repository map

- `src/CellBridge.Web`: sample HTTP host, imports and document catalog.
- `src/CellBridge.AspNetCore`: reusable discovery, download and cellstorage endpoints.
- `src/CellBridge.FssHttp`: SOAP and MTOM handling.
- `src/CellBridge.FssHttpB`: binary serialization and object graphs.
- `src/CellBridge.Storage`: protocol document state and persistence codecs.
- `src/CellBridge.Storage.*`: provider contracts, backends and conformance checks.
- `demo/`: separate Razor Pages solution, Aspire resource `demo`.
- `aspire/apphost.cs`: file-based AppHost for `web`, `demo` and `sharepoint`.
- `tests/`: protocol, storage and interoperability tests.
- `tools/capture/`: SharePoint capture tooling.

## Run and validate

Use the Aspire CLI to run the app, rather than raw `dotnet run`:

```sh
aspire start --non-interactive
aspire wait web --non-interactive
aspire wait demo --non-interactive
aspire ps --non-interactive
aspire describe --non-interactive
aspire stop --non-interactive
```

- Stop Aspire before building outputs that its processes have locked.
- Run checks relevant to the change. Main solution: `CellBridge.slnx`.
  Demo solution: `demo/CellBridge.Demo.slnx`. Live tests are opt-in; see
  [interop instructions](tests/CellBridge.Interop.Tests/README.md).
- For desktop Office failures, inspect request logs first. Use a hostname Windows
  can resolve; the local demo Office origin defaults to `https://localhost:7292`.
- During an active capture, stop it with `tools/capture/stop.py` before stopping
  Aspire, then validate the capture. See [the capture guide](docs/capture-kit.md).

## Implementation constraints

- Aspire uses PostgreSQL storage. Explicit in-memory storage loses content,
  identities, versions and sessions on restart. Imports create only missing
  documents; saves do not write back to the import directory. See
  [storage setup and provider contracts](docs/storage-providers.md).
- Resolve supplied resource IDs correctly. Do not create documents on SOAP lookup
  misses or hide unknown IDs with a URL fallback. See
  [document identity and save evidence](docs/demo-library.md).
- Keep file-partition saves graph-aware through `FilePartitionSaveHandler` and
  `PartitionGraphSnapshot`; never recover a file by selecting the largest BLOB.
- Preserve explicit errors for unsupported partial, multi-request and non-file
  partition uploads. Do not claim support from package creation or download alone.
- Distinguish offline replay, local desktop package checks and live remote editing.
  Two-desktop coauthoring and distinct authenticated identities remain unverified
  or unimplemented; consult the README before describing support.

## Documentation

Keep maintained user and contributor guides in `docs/`. Keep raw captures,
benchmark JSON, run reports and task journals in ignored `artifacts/` or local
capture output. Keep AGENTS.md limited to current instructions and links. Retain
only reviewed fixtures required by automated tests under `testdata/`.

- [Architecture](docs/architecture.md)
- [Demo library and desktop validation](docs/demo-library.md)
- [Capture setup and SharePoint mapping](docs/capture-kit.md)
- [Storage and reusable hosting](docs/storage-providers.md)
- [Automated testing](docs/automated-testing.md)
- [Interoperability coverage](docs/interoperability.md)
