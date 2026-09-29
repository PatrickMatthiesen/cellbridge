# Capture kit readiness review, 2026-09-25

Hostnames, network addresses, and portal names in this review are anonymized examples.

Ready for a supervised Word experiment. Desktop Word and SharePoint authentication remain untested by this review.

## Finding and fix

The recorder could report a complete capture after a failed client TLS handshake. I reproduced this with a client that rejected the proxy CA, followed by a successful HTTPS request. The original validator accepted that run because the failed handshake had no HTTP flow.

The addon now marks client/server TLS failures, upstream connection errors, and CONNECT errors as capture errors. A process-level regression test rejects the proxy CA, completes a later request, and checks that validation rejects the run.

## Verification

- Installed the private capture environment on Windows with Python 3.13.15 and mitmproxy 12.2.3.
- All 33 original tests passed. All 34 tests passed after the fix, including the new regression.
- Started the AppHost with capture enabled for `localhost`. Aspire reported both `web` and `capture` healthy.
- Sent an explicit proxied GET to `http://localhost:5181/shared/test.docx`. Received HTTP 200 and 1,716 bytes.
- Stopped the capture with `stop.py`. Validation accepted one complete exchange; extraction produced two body files.
- Stopped Aspire after the smoke test. No Windows proxy or certificate trust settings were changed.

Local smoke evidence is in the ignored directory `tools/capture/captures/20260925T065705Z-session-57570531`. Extracted bodies are in `tools/capture/captures/review-smoke-extracted`. These paths are local evidence, not committed fixtures.

## First desktop test

1. Choose the machine and exact document URL. Use SharePoint for the known-good open/save reference, then repeat against OfficeCollabServer. The server currently rejects binary saves, so successful saving there is not a capture-kit acceptance requirement.
2. Record Word build, server build, authentication scheme, original proxy settings, and direct-open behavior. Use a separate fresh synthetic document for the captured SharePoint run to avoid a warmed Office cache.
3. Start the kit with the exact destination hosts. For HTTPS, arrange client trust of the capture CA and upstream trust of the farm CA separately, following the main guide.
4. Route Word through `127.0.0.1:8877`. Verify that the document URL and discovery requests appear before editing. A curl smoke test alone does not establish Word routing.
5. On SharePoint, insert a distinctive sentence, save, close, and reopen to verify persistence. Record whether Word was editable and the times of these actions.
6. Stop the capture before Aspire, validate, and extract. Restore proxy settings and remove temporary certificate trust after testing.
7. Compare the SOAP Cell subrequests and their binary attachments with the server capture. Preserve raw evidence locally. Introduce a second user only after the single-user baseline works.

Validation establishes recorded-file integrity, not protocol correctness or successful Word behavior. The existing authentication test passes synthetic challenge headers; it does not prove a real NTLM/Kerberos session. Background requests to hosts outside the allowlist also make a run incomplete, so keep the test session focused and inspect the error list.

## Reverse-mode follow-up

The user ruled out installing a capture CA or changing Windows proxy settings. The kit now supports a reverse HTTPS listener supplied with Aspire's existing development certificate. The laptop already trusts that certificate and its names include `localhost` and `*.dev.localhost`.

The AppHost builds with the documented `WithHttpsDeveloperCertificate` and `WithHttpsCertificateConfiguration` APIs. All 43 capture tests pass. A real proxy integration test connects using `sharepoint.dev.localhost`, checks the supplied server certificate, forwards to a different upstream hostname, and verifies both binary bodies. Forward mode remains available.

Live Aspire startup with the SharePoint configuration was rejected by automatic approval review with the message "blocked by policy" and no further reason. No live Word acceptance result is claimed. The [main guide](capture-kit.md#reverse-capture-with-the-existing-aspire-certificate) has configuration and startup steps. No Windows proxy or trust-store changes were made.

## Default startup and parameter correction

The first reverse implementation required an environment flag and exposed no Aspire parameters. Normal startup therefore omitted the proxy. The AppHost now includes `sharepoint` by default and exposes `capture-upstream`, `capture-port`, `capture-upstream-ca`, and `capture-output` as parameter resources. The AppHost now uses only standard `Parameters` configuration. The fallback helper, `Capture` settings, enable flag, and forward-mode branch were removed after review.

After the user's follow-up, restarting Aspire succeeded. `aspire describe` showed the proxy and all four parameters running. An HTTPS HEAD through `https://sharepoint-aspire.dev.localhost:8443/Shared%20Documents/Document.docx` passed Windows certificate validation without bypass flags and returned SharePoint's HTTP 401 with an NTLM challenge. The probe used curl's per-request `--resolve` option for loopback routing; it did not change DNS or Windows settings. The earlier startup block is no longer the current state. Aspire was left running for the user's Word test. Real Word authentication and document operations remain unverified.


## Redirect and hostname correction

The first browser capture contains seven complete exchanges. After authentication, GET `/` returned HTTP 302 with Location `https://sharepoint.example.test/SitePages/Home.aspx`. It was gracefully stopped and validated before restarting.

The AppHost now explicitly sets the HTTPS endpoint TargetHost to `sharepoint.dev.localhost`, overriding Aspire's generated resource-plus-dashboard hostname. Reverse mode preserves the incoming HTTP Host header for SharePoint alternate access mappings while retaining the fixed upstream connection and TLS server name. The reverse HTTPS integration test passes with the local Host header and unchanged binary bodies. SharePoint public URL and IIS configuration still require farm-side verification; no farm changes were made.

## HTTP extension

At the user's request, the upstream default is now `http://192.0.2.24:42292`. The launcher accepts HTTP and HTTPS origins while still requiring a supplied certificate for the local HTTPS listener. HTTP upstreams ignore the upstream CA setting and do not set upstream TLS SNI. All 44 tests pass, including a real HTTPS-client-to-HTTP-server test that checks the local Host header, binary bodies, and unused CA configuration.

Aspire was restarted and left running. A verified HTTPS HEAD to `https://sharepoint.dev.localhost:8443/` returned SharePoint's NTLM challenge. The active manifest confirms the HTTP upstream on port 42292. This verifies transport; authenticated redirects, Word open/save, and SharePoint alternate access mappings still need user testing.

## User-confirmed redirect fix

The AAM screenshot showed only the HTTPS internal URL for Extranet. The user added internal URL `http://sharepoint.dev.localhost:42292` to Example portal's Extranet zone, keeping public URL `https://sharepoint.dev.localhost:8443`, and reported that it worked. The previous redirect to HTTP port 42292 is therefore recorded as resolved by the additional internal mapping. Matching the proxy and backend ports or changing the IIS Host Header was not needed. The capture guide records the exact two AAM rows. Word desktop open/save and coauthoring remain unverified.

## Word reaches FSSHTTP but SharePoint rejects the document URL

In local run `20260925T082444Z-session-5e91fd96`, flows 97–101 show Word authenticating and receiving successful OPTIONS responses. Flows 103 and 104 POST to `/_vti_bin/cellstorage.svc/CellStorageService` and receive HTTP 200 MTOM responses, but the SOAP subresponses fail. SharePoint reports `ClrException_System_ArgumentException`, message `URL is not for this web`, from `SPWeb.GetWebRelativeUrlFromUrl` / `SPWeb.GetFile`. Cell subresponses report `CellRequestFail`.

The embedded Request Url is `https://sharepoint.dev.localhost:8443/Shared%20Documents/Document.docx`. The transport reaches SharePoint via HTTP port 42292 with Host `sharepoint.dev.localhost:8443`. This points to a web URL/context mismatch; the exact server-side context has not been verified. The browser redirect fix alone did not establish Word compatibility. A subsequent fallback GET for the document receives HTTP 401 on a different connection.

The run manifest also reports a recorder `PermissionError`, so the whole run must not be treated as a validated fixture. Both request/response body pairs for flows 103 and 104 individually pass their recorded length, completeness and SHA-256 checks. No request bodies were rewritten to hide the server error.


## Host-port translation experiment

At the user's request, reverse capture now keeps the client hostname but translates the HTTP Host port to the upstream port. It records the original client Host separately. SOAP and binary bodies remain unchanged. Both HTTP and HTTPS upstream integration tests pass after updating the expected Host translation.

Stopped the previous run with its existing recorder error preserved as incomplete. Started fresh run `20260925T084814Z-session-f088f59e`. A document HEAD confirms original Host `sharepoint.dev.localhost:8443`, forwarded Host `sharepoint.dev.localhost:42292`, and SharePoint HTTP 401 with NTLM.

The user retried Word and reported the same download error while the browser still worked. Flow 7 receives an authenticated document HEAD HTTP 200. Flows 10 and 12 confirm the translated Host but still return `URL is not for this web` from `SPWeb.GetWebRelativeUrlFromUrl` inside HTTP 200 SOAP responses. The embedded document URL remains HTTPS port 8443. Host-port translation alone therefore does not fix Word. The precise SharePoint web context remains unknown; do not claim that matching IIS ports or switching upstream TLS is a proven fix.

Both request/response body pairs for flows 10 and 12 pass completeness, length and SHA-256 checks. The run separately reports recorder `PermissionError`, so it is not a validated fixture. SharePoint correlation IDs at 08:50:10 UTC are `ed9e3ea2-e078-10e4-b4fd-722ec2f5574a` and `ed9e3ea2-a083-10e4-b4fd-72d8522c764a`. Next diagnostic: collect farm ULS entries for these IDs to investigate URL/context selection before further configuration changes.

## Farm ULS evidence

The user supplied `word-open.log` for correlation `ed9e3ea2-a083-10e4-b4fd-72d8522c764a`. It covers the failed POST on sharepoint-server at 08:50:10 UTC.

- Lines 10 and 89: host-header site lookup cannot resolve `http://sharepoint.dev.localhost:42292`, including during the Cobalt handler.
- Line 11: the AMSI configuration lookup reports `webApp is null` for that incoming URL.
- Line 55: `IsAuthenticated=True`.
- Lines 90 and 91: the handler nevertheless obtains site ID `714c4d02-3258-4b82-b10c-bc9ae52e7ac3`.
- Line 92: the identity context's `appliesTo` is the original `https://sharepoint.example.test/`. This is not proof of the SPWeb URL or selected zone.
- Line 96: `SPWeb.GetFile` rejects the document URL with the same argument exception recorded by the proxy.

The log strengthens the URL-resolution/configuration hypothesis but does not print the selected SPWeb URL or zone. Failed host-header lookups alone do not establish that the collection is host-named. Verify the actual AAM collection and `SPSite.HostHeaderIsSiteName` before choosing a fix. Microsoft documents different URL mapping and SSL-offload handling for [host-named site collections](https://learn.microsoft.com/en-us/sharepoint/administration/host-named-site-collection-architecture-and-deployment). Do not infer from this log alone that changing IIS ports, adding an IIS Host Header, or adding `Front-End-Https` will fix this farm.

Read-only checks in the farm's SharePoint Management Shell:

```powershell
$site = Get-SPSite 'https://sharepoint.example.test'
try {
    $site | Format-List ID, Url, Zone, HostHeaderIsSiteName
    Get-SPAlternateURL -WebApplication $site.WebApplication | Format-List *
    if ($site.HostHeaderIsSiteName) {
        Get-SPSiteUrl -Identity $site | Format-List *
    }
} finally {
    if ($null -ne $site) { $site.Dispose() }
}
```

In particular, confirm that the HTTP 42292 incoming URL and HTTPS 8443 public URL belong to the same Extranet zone of the web application containing the document. No proxy or farm configuration was changed while reviewing this log.

## Actual AAM port mismatch found

The user's PowerShell output confirms site ID `714c4d02-3258-4b82-b10c-bc9ae52e7ac3` is path-based (`HostHeaderIsSiteName=False`). The Extranet public URL is correctly `https://sharepoint.dev.localhost:8443`, but its HTTP incoming URL is actually `http://sharepoint.dev.localhost:4229`, missing the final `2`. The proxy, IIS extension and ULS request use port **42292**. Earlier notes described the intended mapping, not the verified farm configuration.

Correct the Extranet HTTP incoming URL to `http://sharepoint.dev.localhost:42292`, retaining the HTTPS 8443 public URL, then retry Word. No proxy port or IIS binding change is needed for this discrepancy. Word success after correcting the mapping remains unverified.

The user subsequently confirmed that Word now works after correcting the AAM port. Record this as user-confirmed Word open through the reverse proxy. Save persistence, coauthoring and complete capture validation have not yet been confirmed. The earlier run's recorder `PermissionError` still needs investigation before using it as a validated fixture.

## Save and reopen confirmed

The user then confirmed saving, closing, reopening and making further changes. In run `20260925T084814Z-session-f088f59e`, flows 46 and 59 carry `AutoSaveFile_Save` and receive HTTP 200 with SOAP `Success`; flow 71 carries `OpenFile_Open` and also receives SOAP `Success`. These request and response bodies individually pass completeness, byte-length and SHA-256 checks. Save responses occur at 09:07:51 and 09:09:34 UTC; reopen at 09:09:59 UTC. This establishes a user-confirmed single-user save/reopen baseline against the real SharePoint farm, not against OfficeCollabServer's implementation.

The run remains active and marked unhealthy with `PermissionError`. Flow 37 fails the individual body-integrity check, so the entire run must not be promoted to a validated fixture. Coauthoring and a clean completed capture remain outstanding.

## Recorder file-lock handling

Committed the working reverse-proxy setup and Word evidence as `ebc1136` before changing the recorder. Stopped the original run as incomplete and preserved its evidence.

Closer inspection clarifies flow 37: its body length and hash are intact, but the response is marked incomplete because the client disconnected during a 403 response. Its body error is null. That flow is not evidence of the separate filesystem `PermissionError`; the earlier combined integrity check conflated completeness with byte integrity.

The original filesystem error recorded only its exception class, so its precise operation and cause cannot be recovered. Reproduced a plausible cause using real Windows handles that deny delete sharing: replacing a manifest or finalizing a body raises `PermissionError` until the reader releases its handle. File replacement now retries Windows errors 5, 32 and 33 with a total maximum delay of 140 ms. Persistent failures still mark evidence incomplete and now report operation, relative filename, errno and winerror in console output and manifest diagnostics. No authentication behavior was changed.

All 47 capture tests pass, including real Windows file-lock recovery for manifests and bodies, and a persistent-failure test checking bounded retries and diagnostics. Live run `20260925T091631Z-session-a2ff1ba3` stopped complete and validated two exchanges after the patch. This verifies a clean smoke capture, not another complete Word save session. A fresh proxy run was started for further use.
