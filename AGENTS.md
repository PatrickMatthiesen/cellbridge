# Working on CellBridge

CellBridge is an experimental ASP.NET Core implementation of MS-FSSHTTP and
MS-FSSHTTPB for desktop Office editing, saving and coauthoring. Read
[README.md](README.md) for current capabilities and limitations.

## Beta compatibility policy

- During beta, make needed breaking changes directly and update repository
  callers, tests, examples and current docs. Do not add compatibility shims,
  deprecation periods or consumer migration guides for superseded designs.
  Preserve real database migrations and existing developer data.
- Release all shipping CellBridge NuGet packages together with the same version
  throughout beta. Keep document and Lists dependencies independent.

## Protocol scope

- Use MS-OCPROTO, MS-FSSHTTP and MS-FSSHTTPB as protocol authorities.
- Implement both layers. MS-FSSHTTP is the outer SOAP/MTOM protocol at
  `/_vti_bin/cellstorage.svc`; MS-FSSHTTPB carries the binary Cell payloads.
  See [the protocol decision](docs/protocol-version-decision.md).
- Do not implement unrelated SharePoint APIs such as lists or search.
- The optional `CellBridge.Lists*` packages provide native customer-workspace
  lists requested separately from the Office protocol implementation. Maintain
  them independently; they do not implement SharePoint list API compatibility.
- Keep binary serialization in its standalone library with protocol unit tests.
- Compare decoded requests and responses with SharePoint reference traffic and
  the OfficeDev Interop-TestSuites. Log enough detail to diagnose wire failures.

## Run and validate

- Use the Aspire CLI to run the app. Follow the commands in
  [automated testing](docs/automated-testing.md) and the
  [README](README.md).
- Stop Aspire before building outputs that its processes have locked.
- Run checks relevant to the change in `CellBridge.slnx`. Live remote Office
  tests are opt-in; see [interop instructions](tests/CellBridge.Interop.Tests/README.md).
- For desktop Office failures, inspect request logs first. Use a hostname Windows
  can resolve and the origin reported by Aspire.
- During an active capture, stop it with `tools/capture/stop.py` before stopping
  Aspire, then validate the capture. See [the capture guide](docs/capture-kit.md).

## Implementation constraints

- Resolve supplied resource IDs correctly. Do not create documents on SOAP lookup
  misses or hide unknown IDs with a URL fallback. See
  [document identity and save evidence](docs/demo-library.md).
- Keep file-partition saves graph-aware through `IndexedPartitionSavePreparer` and
  `IndexedDocumentGraph`; never recover a file by selecting the largest BLOB.
- Preserve explicit errors for unsupported partial, multi-request and non-file
  partition uploads. Do not claim support from package creation or download alone.
- Distinguish offline replay, local desktop package checks and live remote editing.
  Consult the README before describing current client or authentication support.

## Documentation

Keep maintained user and contributor guides in `docs/`. Report routine task and
test results in the conversation. Do not create task journals, cleanup reports or
other standalone reports unless requested or needed for ongoing work. Keep
AGENTS.md limited to current instructions and links. Retain only reviewed fixtures
required by automated tests under `testdata/`.

Use ignored `artifacts/tmp/<task>/` for disposable tooling and remove it when
finished. Preserve files needed to reproduce open issues or unfinished work.
Do not force-add artifacts to Git. Reuse suitable checkouts and avoid worktrees
for read-only reviews. Follow the
[worktree cleanup instructions](docs/automated-testing.md#generated-files-and-worktree-cleanup).

## Writing

Use plain, specific English. Describe what persists, what commits, or what survives
a restart instead of calling a host, execution path, save or fix "durable".
Preserve exact API names and established technical terms when required. Do not
replace vague wording with a different vague adjective or claim an untested guarantee.

## Guides

- [Architecture](docs/architecture.md)
- [Demo library and desktop validation](docs/demo-library.md)
- [Capture setup and SharePoint mapping](docs/capture-kit.md)
- [Storage and reusable hosting](docs/storage-providers.md)
- [Automated testing](docs/automated-testing.md)
- [Interoperability coverage](docs/interoperability.md)
