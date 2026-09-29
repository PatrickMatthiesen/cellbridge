# Office Inspectors parser adapter

This test project links the parser sources from the sibling
`Office-Inspectors-for-Fiddler` checkout. It deliberately does not reference
Fiddler or the WinForms inspector host. `InspectorStateStub.cs` supplies only
the three static fields that the parser reads while decoding an FSSHTTPB
response.

The adapter calls the same parser entry point used by the Fiddler inspector:
`new FSSHTTPandWOPIInspector.Parsers.FsshttpbResponse().Parse(Stream)`. The
test feeds generated FileContents, Metadata, and EditorsTable QueryChanges
responses through that parser. The legacy parser currently stops after the
19-byte response header because it expects the older 16-bit
DataElementPackage start while the server emits the current 32-bit form. The
test records that incompatibility explicitly instead of treating a partial
parse as success.

The sibling checkout is expected at `..\\..\\..\\Office-Inspectors-for-Fiddler`
relative to this project. Override it when invoking the test, for example:

```powershell
dotnet test tests/OfficeInspectors.Adapter/OfficeInspectors.Adapter.csproj `
  -p:OfficeInspectorsRoot=C:\path\to\Office-Inspectors-for-Fiddler
```

If the checkout is unavailable, the adapter checks are reported as skipped.
The captured fixture suite is compiled only when the checkout is available.
The in-repo `FsshttpbResponseInspector` remains the portable
decoder and should be used in that environment.
