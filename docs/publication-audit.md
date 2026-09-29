# Publication preparation, 2026-09-29

CellBridge starts from a sanitized source snapshot with one new root commit on
`main`. The private development repository retains its own history. No previous
Git objects, branches, tags, pull-request refs, remotes, build output, local
configuration, raw captures, or recovery bundles were copied into this repository.

## Source and fixture review

Before the snapshot was exported, lab hostnames, addresses, account names and
company metadata were replaced with generic examples. The decoded fixture audit
covered SOAP/MTOM, compressed editor records and 68 XML member versions,
including fragmented ZIP entries reconstructed through the protocol graph.
Complete API and sequential-save document archives passed ZIP CRC validation.

Eight fixture blobs were sanitized. Replacement text, recompression and padding
preserve binary object lengths and archive offsets. ZIP checksums and JSON body
hashes describe the sanitized data. Captured protocol content signatures remain
opaque identifiers, so the fixtures test parsing and materialization rather than
authenticating the original capture bytes. See each fixture directory's README.

Gitleaks 8.30.1 found no confirmed live credentials in the source audit. Its
reported coauthoring GUID is a synthetic value in a SOAP parser test. Recursive
decoding and archive scanning supplement the manual fixture review; no scanner
can guarantee that arbitrary encoded data is free of sensitive information.

## Validation and known limitations

The sanitized source passed 86 binary tests, 104 SOAP/storage tests, nine offline
interop tests, 15 demo tests and 45 Python capture tests. Eight live interop tests
and two platform-specific capture tests were skipped. No fixture test was removed
or weakened. Desktop Office acceptance testing was not repeated after sanitization.

Three pre-existing URL/path tests fail on Linux:

- `DocumentRequestResolverTests.CanonicalUrlEscapesSpacesAndLiteralPercentOnce`
- `DocumentLibraryTests.ConfiguredDirectoryLoadsFlatFilesAndSkipsOfficeLockFiles`
- `DocumentCreationTests.EscapesUrlSpecialCharactersWhileKeepingTheDisplayName`

These three tests were excluded from the final passing SOAP/storage run. The
Office Inspectors adapter needs the Windows Desktop runtime and cannot run here.
The copied source retains these tests and records these limitations explicitly.

The capture upstream defaults to `http://sharepoint.example.test:42292`.
Configure a real test farm through local AppHost `Parameters` settings before
capturing traffic. Documentation addresses and portal names are examples.

## Local snapshot checks

The exported CellBridge solution builds with no errors. The demo compiles with
the new branding and all 15 demo tests pass. All fixture files match the audited
source byte for byte and pass the fixture sanitizer check. A fresh Gitleaks scan
reports only the same synthetic parser-test GUID. Original development history
and remotes are absent; the initial commit uses the personal GitHub identity.

## Publish the new repository

This preparation creates a local repository only. When ready, create an empty
GitHub repository named `cellbridge` and push only this repository's `main` branch.
For example, from this directory with the GitHub CLI:

```sh
gh repo create PatrickMatthiesen/cellbridge --public --source . --remote origin --push
```

This command publishes the code and has not been run during local preparation.
Do not mirror or import refs from the private development repository. Its old
history and pull requests belong there. Keep raw captures, private recovery
bundles, keys, and real upstream configuration outside this repository.
