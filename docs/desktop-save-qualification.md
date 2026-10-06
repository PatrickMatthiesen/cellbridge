# Repeated desktop save qualification

Issue #42 requires repeated desktop evidence on the exact candidate. A passing
package or server test does not establish Word's Saved indicator or normal-close
behavior. The tools below have separate automated content and manual UI gates.

Use a dedicated, signed-in Windows desktop with Word and Excel installed. Close
both applications before each run. Run the candidate through Aspire at an HTTPS
origin Windows resolves. Enable the existing raw Cell capture and record the
candidate commit. The host must expose the document creation and download routes
used by `word-smoke.ps1`. Office must authenticate separately. Supply the existing
authorized account's `CELLBRIDGE_INTEROP_COOKIE` and `CELLBRIDGE_INTEROP_CSRF` in
the local environment. Do not put either value in evidence or shell history.

Run Word and Excel separately, using a new evidence directory each time:

```powershell
./tools/testing/office-repeat.ps1 -BaseUrl https://your-origin.example `
  -CandidateCommit <full-40-character-commit> -Application Word -Cycles 20
./tools/testing/office-repeat.ps1 -BaseUrl https://your-origin.example `
  -CandidateCommit <full-40-character-commit> -Application Excel -Cycles 10
```

The worker leaves Word's BackgroundSave preference unchanged. It alternates text
and image edits in Word and changes cells in Excel. Each cycle calls Save once,
waits up to 60 seconds for the COM Saved state and independently downloaded
content, then closes and reopens in a fresh Office process. It checks all previous
edits, ETag changes, package hashes and Word image counts. Per-cycle UTC windows
allow the verifier to require a successful file PutChanges for every save. The
parent enforces a timeout and can stop only its recorded Office process.

The COM close discards pending changes without prompting. This prevents a second
save from masking failure, but cannot establish absence of a normal-close prompt.
COM Saved also does not prove what the user saw. Complete a separate observed UI
run on the same candidate, origin and Office build. Use at least 20 Word cycles
and 10 Excel cycles, split across fresh and existing documents. Alternate text and
image edits in Word. For each cycle, press Save exactly once, observe the Saved
indicator, close normally and reopen. If Office prompts to save, record failure
and discard the pending edit. Do not issue a second save to convert the cycle to
a pass. Preserve screenshots or recordings and the server capture.

Record manual observations in `manual.json` next to the screenshot/recording
files. Include one cycle object per observed cycle. The following is a schema
example, not passing evidence:

```json
{
  "candidateCommit": "full-40-character-commit",
  "application": "Word",
  "origin": "https://your-origin.example",
  "officeBuild": "value from repeat-result.json",
  "observer": "observer name",
  "cycles": [
    {
      "cycle": 1,
      "singleSave": true,
      "savedUi": true,
      "normalCloseWithoutPrompt": true,
      "reopenedContent": true,
      "imageEdit": false,
      "observedUtc": "2026-10-06T12:00:00Z",
      "evidenceFile": "cycle-1.mp4",
      "sha256": "SHA256 of the evidence file"
    }
  ]
}
```

Stop the capture with the capture tool before stopping Aspire. Copy its summary
files into the evidence directory and run:

```powershell
python tools/testing/verify_office_repeat.py --result artifacts/run/repeat-result.json `
  --capture artifacts/run/capture --manual artifacts/run/manual.json `
  --output artifacts/run/qualification.json
```

Without manual observations the verifier reports automated content results but
returns failure for the full gate. Missing cycles, malformed records, stale or
wrong-origin captures, missing images and changed artifacts also fail. An observer
must review the recordings; hashing a file does not interpret its contents.
Keep raw packages, captures and reports under ignored `artifacts/`. Report the
exact Office build and tested counts. These runs do not prove two-client
coauthoring or that an intermittent failure can never recur.
