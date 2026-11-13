# OfficeDev Interop-TestSuites adaptations

Copyright (c) 2015 Office. The full upstream MIT copyright and permission
notice is in [LICENSE-Interop-TestSuites.txt](LICENSE-Interop-TestSuites.txt).

Source repository: https://github.com/OfficeDev/Interop-TestSuites

Scenario sources were inspected at commit
`fe87ed3253de01804a2ae6e1d0015943da6023f8`:

- `FileSyncandWOPI/Source/SharedTestSuite/SharedTestCase/S02_Coauth.cs`
- `FileSyncandWOPI/Source/SharedTestSuite/SharedTestCase/S13_PutChanges.cs`

Both inspected files begin with the namespace declaration and have no separate
license header. The repository root LICENSE supplies the MIT notice above.

`StatefulCoauthoringTests.cs` selectively adapts S02 scenario sequences to the
current xUnit HTTP client. The adapted cases are TC01, TC03, TC04, TC06,
TC14, TC15, TC16, TC18, TC22, TC23, TC27, TC28, TC29, TC30 and TC31.
The tests combine related cases into bounded lifecycle sequences. Explicit
`GetCoauthoringStatus` requests check Alone before the second client joins,
Coauthoring for both clients, and Alone after the first client departs (TC27/28).

The conflicting Exit assertions retain current CellBridge behavior and are not
SharePoint Subscription Edition compatibility expectations:

- TC16 establishes an exclusive lock, then sends `ExitCoauthoring`; upstream
  expects `Success`, while this regression expects `FileAlreadyLockedOnServer`.
- TC18 establishes two same-schema participants, then sends Exit with a different
  schema identifier; upstream expects `Success`. The two-participant phase here
  uses the same authenticated account rather than upstream's third account and
  expects the lock error. Its mapping is TC18, not TC17.
- TC17 establishes one participant, then sends Exit with a different schema
  identifier from a second account using the original client identifier. Its
  requirement-selected SE/2019 branch expects `Success`; older-server branches
  expect the lock error. The fresh single-participant and delayed Exit checks here are TC17-like,
  with the same authenticated account throughout. They do not port the
  upstream distinct-account precondition.

[MS-FSSHTTP section 3.1.4.3.2](https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttp/2697878c-f39b-48fb-b9e7-e621cf977641)
recommends the lock error with SHOULD. However,
[Appendix B footnote 45](https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttp/9ad3bd41-0f08-4f8b-ad2d-a894196a7e2e)
documents SE returning `Success` for an exclusive lock, for a different-schema
shared lock with a valid multi-client coauthoring session, and for a
different-schema shared lock with one coauthor (also documented for 2019).
Neither statement establishes what current Office clients send or require.
A return-code difference does not authorize deleting another client's lease or
session. Native Office/SE traces and subsequent ownership/save checks are needed
before changing CellBridge's behavior.

TC30's upstream body sends Join despite its Refresh title; this adaptation sends
Refresh. Delayed old-schema exits are an additional local regression scenario.
The private session helper creates unique documents and validates SOAP token
correlation. Clients share one authenticated account; the existing distinct-user
test retains account-ownership coverage.
`EditorKnowledgeInteropTests.cs` extends the stateful join/departure sequence
with editor-partition knowledge checks using the existing independent Microsoft
FSSHTTPB codec. The test transfers returned knowledge between its HTTP clients
and scans decoded object payloads for a small editor stream. It does not traverse
the object-reference graph or prove that separate clients can reconstruct their
state from only their own knowledge and payloads. These are synthetic protocol
checks, not Office emulation or full independent-client evidence.

S13 was audited against existing `GraphUploadInteropTests` and
`CapturedSaveTests`. Expected-index conflicts, applied-index acknowledgements,
added-element receipts and identical cross-host retries already have coverage;
this change does not import another copy of those scenarios.

The adaptations omit PTF, SharePoint SUT setup, legacy .NET Framework adapters,
Office DLLs, binaries and upstream fixtures. No MS-ONESTORE adapter code is
included. The MIT permission notice is not comprehensive patent clearance.

The previously vendored codec retains its own
[notice](MicrosoftProtocol/FssHttpB/NOTICE.md) and
[license](MicrosoftProtocol/FssHttpB/LICENSE-MIT.txt). This notice covers the
scenario adaptations outside that codec directory.
