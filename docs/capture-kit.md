# Capture Word and SharePoint traffic

The capture kit runs a local HTTP/HTTPS proxy using mitmproxy 12.2.3. Normal Aspire startup includes a `sharepoint` HTTPS reverse proxy using Aspire's existing development certificate. Standalone forward capture is also supported. The kit records selected hosts to local files and provides offline validation and SOAP/MTOM extraction.

The proxy preserves application body bytes while streaming them onward. It does not decode and reserialize SOAP or binary attachments during forwarding. HTTP transfer framing, header casing, TLS, and connection timing may differ; these files are application-level evidence, not packet captures. Capture limits truncate the recording, never the forwarded body, and make the run fail validation.

## Setup

Use Python 3.12 or newer on Windows. From the repository root:

```powershell
./tools/capture/setup.ps1
```

The setup creates a private virtual environment. It does not configure a Windows proxy, change certificate trust, or require a SharePoint farm. For standalone use on the work laptop, copy the `tools/capture` source directory and run its setup there. Do not copy `.venv`, `captures`, or a generated certificate authority between machines.

## Run standalone

Supply the exact SharePoint hostname you normally open in Word:

```powershell
./tools/capture/.venv/Scripts/python.exe tools/capture/run.py --hosts sharepoint-server --name open-save
```

`sharepoint-server` is a placeholder; replace it with your test farm hostname. Multiple exact hosts can be comma-separated. Wildcards and URLs are rejected. Redirects to additional hosts are blocked until those hosts are explicitly included. A blocked destination marks the run incomplete so a partially observed scenario cannot pass validation.

The listener is `127.0.0.1:8877`. Each invocation prints a unique run directory below `tools/capture/captures`. `--port` and `--output-root` override those defaults. Requests to hosts outside the allowlist are blocked. The kit does not authenticate upstream on behalf of Word or substitute a shared proxy identity.

Configure the test client's HTTP and HTTPS proxy to that listener, keeping the original SharePoint document URL. Record the original proxy configuration first and restore it after the run. Word may bypass proxies for intranet/local addresses or use a different Windows networking path. Verify that the initial discovery and document request appear in the capture before editing. No recorded flows means routing has not been established, not that Word sent no requests.

For HTTPS, mitmproxy creates a private CA below `captures/.mitmproxy`. The client must explicitly trust `mitmproxy-ca-cert.cer` or `.pem` for interception to work. Only trust it in the authorized test environment and remove that trust after testing. Never export `mitmproxy-ca.pem`, which includes the private key. The kit never installs trust automatically. For an upstream private CA, pass `--upstream-ca path/to/lab-ca.pem`; upstream certificate verification remains enabled.

Windows authentication, TLS channel binding, and enterprise proxy policy can prevent interception. First compare a successful direct Word open/save with an open/save through this proxy. A synthetic authentication-challenge test is not proof that NTLM/Kerberos or Word works. If interception changes authentication behavior, retain that failure as diagnostic evidence and use another approved capture mechanism; do not disable authentication protections to force a pass.

## Reverse capture with the existing Aspire certificate

Use this mode to open a local document URL without changing Windows proxy settings or installing a capture CA. Aspire supplies its existing development certificate and key to mitmproxy. The certificate must already be trusted and cover the local hostname. The current laptop's certificate covers `localhost` and `*.dev.localhost`.

Normal startup from the repository root creates `web`, `sharepoint`, and four capture parameter resources:

```powershell
aspire start --apphost aspire/apphost.cs --non-interactive
aspire wait sharepoint --apphost aspire/apphost.cs --non-interactive
```

| Aspire parameter | Default | Purpose |
| --- | --- | --- |
| `capture-upstream` | `http://sharepoint.example.test:42292` | Fixed HTTP or HTTPS upstream origin |
| `capture-port` | `8443` | Local HTTPS listener port |
| `capture-upstream-ca` | Local preflight PEM if present, otherwise empty | Upstream trust inside the proxy process |
| `capture-output` | `tools/capture/captures` as an absolute path | Raw evidence directory |

The upstream default is a placeholder and will not reach a server. Configure your own test farm before capturing traffic. The addresses and portal names in the examples below are anonymized.

Set values in the AppHost's `Parameters` configuration section, for example `Parameters:capture-upstream`. They appear as Aspire parameter resources and are passed to the executable. Restart the AppHost after changing configuration. The AppHost always creates the reverse HTTPS proxy; it has no separate enable flag or forward-mode switch.

The default upstream certificate file is local to the reviewed laptop. On another machine, set `capture-upstream-ca` to the verified upstream certificate or CA PEM file. This configures trust inside the proxy process, not the Windows certificate store.

Open `https://sharepoint.dev.localhost:8443/Shared%20Documents/Document.docx` in Word. The listener binds IPv4 loopback. The AppHost explicitly sets this endpoint hostname, so Aspire does not append its dashboard name. Some desktop clients do not resolve `*.dev.localhost` automatically. If Word cannot resolve it, diagnose name resolution before testing; substituting `localhost` also changes the Host header and will not match the working SharePoint mappings below.

The current connection is Word → mitmproxy HTTPS listener → SharePoint HTTP on port 42292. HTTPS upstreams remain supported. `capture-upstream-ca` is ignored for HTTP upstreams. The Aspire endpoint is unproxied; there is no additional Aspire HTTP proxy hop. No Windows proxy or certificate installation commands are run by the kit. Mitmproxy uses the supplied server certificate rather than its generated interception CA for this listener. Its combined server PEM is temporary and removed on normal shutdown; a forced process termination may leave a private-key file in the user's temporary directory. Do not share certificate keys or capture directories.

Reverse mode fixes the upstream connection and, for HTTPS, the TLS server name, but preserves the incoming hostname and replaces the Host header port with the upstream port for SharePoint alternate access mappings. The current forwarded header is `Host: sharepoint.dev.localhost:42292`; `original_host_header` in each flow records the client value on port 8443. SOAP bodies and binary payloads are unchanged. Absolute document URLs in SOAP, cookies, and redirects are not rewritten. Word may follow an upstream URL directly and bypass capture, or SharePoint may reject the local document URL. Therefore a valid reverse capture proves recorded-file integrity, not complete observation of a Word session. Check discovery, Cell requests, authentication and save behavior before treating it as a protocol baseline. Authentication protections remain enabled.

### Working SharePoint mapping, confirmed by the user on 2026-09-25

The `Example portal` web application is extended into the **Extranet** zone with an HTTP IIS listener on port **42292**. The proxy connects to `http://192.0.2.24:42292`, while the client opens `https://sharepoint.dev.localhost:8443`. The ports do not need to match.

The working Alternate Access Mappings include both rows:

| Internal URL | Zone | Public URL for zone |
| --- | --- | --- |
| `https://sharepoint.dev.localhost:8443` | Extranet | `https://sharepoint.dev.localhost:8443` |
| `http://sharepoint.dev.localhost:42292` | Extranet | `https://sharepoint.dev.localhost:8443` |

Initially only the HTTPS row existed. After authentication, SharePoint redirected GET `/` to `http://sharepoint.dev.localhost:42292/SitePages/Home.aspx`, taking the browser away from the local HTTPS endpoint. Adding the HTTP internal URL to **Example portal → Extranet** through **Add Internal URLs** resolved the reported redirect problem. The user confirmed that it worked. No matching-port change or IIS Host Header change was needed for this fix; the existing Default zone URL was retained.

This confirms the reported browser routing fix, not Word desktop open/save or coauthoring. Those still need acceptance testing. No farm settings were changed by the agent. Host-named site collections require their own site URL configuration instead of this web-application AAM setup.

References: [SharePoint alternate access mappings](https://learn.microsoft.com/en-us/sharepoint/administration/plan-alternate-access-mappings), [IIS bindings and web application URLs](https://learn.microsoft.com/en-us/sharepoint/administration/update-a-web-application-url-and-iis-bindings).

Stop and validate using the printed run directory as described below, then stop Aspire.

Standalone reverse mode accepts `--reverse-upstream https://host --tls-cert server.crt --tls-key server.key --upstream-ca upstream.pem --port 8443`. Supply an existing server certificate; the launcher does not install or trust one. Manifests identify `proxy_mode` and `reverse_upstream` so reverse captures can be distinguished from forward captures.

References: [Aspire certificate configuration](https://aspire.dev/app-host/certificate-configuration/) and [mitmproxy reverse mode](https://docs.mitmproxy.org/stable/concepts/modes/#reverse-proxy).

## Finish and validate

Wait until Word has finished saving and closing the document, then use Ctrl+C in the standalone console. For a process started through Aspire, request a graceful stop with its printed run directory:

```powershell
python tools/capture/stop.py path/to/run
python tools/capture/inspect_capture.py validate path/to/run
python tools/capture/inspect_capture.py extract path/to/run --output path/to/new-local-extraction
```

Finish the capture before stopping the AppHost. A forced process exit leaves the manifest running or incomplete, and validation rejects it. `stop.py` does not repair or relabel interrupted traffic as complete. A run with zero exchanges also fails validation.

TLS handshake, upstream connection, and CONNECT failures mark the run incomplete even when they happen before an HTTP exchange can be recorded. Check the manifest's `errors` field and the proxy console when Word produces no HTTP flows.

Validation checks completion, files, lengths, and hashes. Extraction then checks MIME structure and XOP attachment references. It writes decoded HTTP bodies and individual SOAP/attachment files to a new directory; it never overwrites a prior extraction. Unsupported content encodings fail explicitly. Feed extracted binary responses into the repository's existing FSSHTTPB dump tools for deeper inspection.

Default limits are 32 MiB per body and 512 MiB of captured body data per run. Use `--max-body-bytes` and `--max-total-bytes` to change them deliberately. Keep the first test documents small. Inspect the manifest for recorder failures and truncation before promoting any fixtures.

The recorder also limits each run to 10,000 exchanges. Exceeding that limit stops
recording new exchanges and marks the run unhealthy; allowed traffic continues.

## Files and privacy

Each run contains `manifest.json`, `flows/*.json`, and `files/*.body.bin`. Exchange metadata contains request/response details, connection IDs, timestamps, body hashes, byte counts, and completeness. Each raw body preserves the content encoding, such as gzip, but excludes HTTP chunk framing. The offline extractor decodes content encodings into separate files.

On Windows, final file replacement retries brief access/sharing locks for up to 140 ms. Persistent filesystem errors still mark the run unhealthy. `recording_error_details` in the manifest and console diagnostics identify the operation, relative file, errno and Windows error code. HTTP authentication challenges are separate from these local recording errors.

Authentication and cookie header values are redacted from metadata without modifying traffic. URLs, SOAP, document bytes, application usernames, redirects, and other headers can still contain sensitive data. Captures and extracted payloads are **not sanitized exports**. Keep them local and use only synthetic test documents and accounts when preparing shareable fixtures. Review all metadata and decoded content before transfer. This version does not claim automatic binary sanitization or replay of a stateful authenticated Word session.

The default capture directory and virtual environment are ignored by Git. If selecting another directory, keep it outside version control. Deleting old runs is manual; the recorder does not silently discard evidence. Do not upload captures as CI artifacts.

## First work-laptop experiment

1. Record SharePoint build, Word build, authentication scheme, document URL, and whether the document was previously opened by this Windows user.
2. Create a new synthetic document in a test library and retain the original file.
3. Verify a direct open/save works. Use a separate fresh document for the captured first-open scenario so the direct test does not warm its cache.
4. Start capture, configure routing, and open the fresh document from its original SharePoint URL. Confirm requests are recorded.
5. Insert a distinctive test sentence, save, close, and reopen. Retain the resulting file and note whether Word was editable and whether the sentence persisted.
6. Stop and validate the capture. Extract locally, review authentication and document content, then prepare sanitized fixtures separately.

Only after that succeeds should a second user be introduced. Use separate Windows sessions or machines with independent Office caches, and verify both users appear separately upstream. On 2026-09-25 the user confirmed Word could open through the reverse proxy after correcting the Extranet HTTP incoming AAM port from `4229` to `42292`. The public URL remains `https://sharepoint.dev.localhost:8443`. The user subsequently confirmed saving, closing, reopening and further editing; captured save and reopen SOAP responses report success. Coauthoring and clean whole-run capture validation remain outstanding. See the readiness review for the recorder error affecting this run.

## Tests

See [the 2026-09-25 readiness review](capture-kit-review-2026-09-25.md) for verified local behavior and the remaining desktop acceptance steps.

```powershell
./tools/capture/.venv/Scripts/python.exe -m pip install -r tools/capture/requirements-dev.txt
./tools/capture/.venv/Scripts/python.exe -m pytest tools/capture -q
```

Tests start a local upstream and a real proxy subprocess. TLS tests use temporary certificate files, never the operating-system trust store. The dedicated Windows CI workflow runs the capture tests without requiring Word, SharePoint, or the external Office Inspectors checkout.

Transport references: [mitmproxy streaming](https://docs.mitmproxy.org/stable/overview/features/#streaming), [options](https://docs.mitmproxy.org/stable/concepts/options/), and [certificates](https://docs.mitmproxy.org/stable/concepts/certificates/).
