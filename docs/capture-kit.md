# Capture Word and SharePoint traffic

The kit uses a pinned mitmproxy source revision to record Office traffic for
comparison with SharePoint. It streams HTTP application bodies unchanged, records WebSocket
application messages and provides offline validation and SOAP/MTOM extraction.

Choose reverse mode through Aspire to open a local HTTPS URL, or standalone
forward mode to keep the original SharePoint URL and configure a client proxy.

## Setup

Use Python 3.12 or newer. Aspire prepares the virtual environment and installs
`requirements.txt` automatically. For standalone Windows capture:

```powershell
./tools/capture/setup.ps1
```

Copy the `tools/capture` source directory to another client and run setup there.
Virtual environments, captures and generated certificate authorities stay local.
Setup does not alter proxy settings or certificate trust.

The current pin is upstream commit
`5253dcbd1d8f0522de097bfe56918fe12a0f267a`, version `13.0.0.dev0`.
The published `12.2.3` release excludes patched transitive dependencies through
its version bounds. The source revision accepts the patched graph without
package metadata overrides. `requirements.txt` and `requirements-dev.txt` lock
source/dependency versions and hashes for Python 3.12 or newer on Linux and
Windows. Install with `--require-hashes`. The launcher verifies the engine's
source URL and archive hash. Input requirements live in the matching `.in`
files; regenerate and test both platforms when changing the pins.

This is an unreleased transport engine. Package advisory tools do not match its
source version against PyPI advisories. The isolated source-build backend is
selected by upstream's build configuration. Local process tests cover forwarding
and evidence recording, but a dependency update still needs live Office capture
validation before replacing an established reference setup.

## Capture the CellBridge server

Server capture records the SOAP/MTOM bodies handled by
`/_vti_bin/cellstorage.svc`. It is separate from the SharePoint proxy below.
Capture is off by default. The sample host reads `Protocol:CaptureDirectory`;
an absent, empty or whitespace-only value disables recording. For a host
configuration file, use an absolute directory:

```json
{
  "Protocol": {
    "CaptureDirectory": "/absolute/path/to/CellBridge/artifacts/office-wire"
  }
}
```

The equivalent host environment variable is `Protocol__CaptureDirectory`.
Aspire wires its `WireCaptureDirectory` parameter into that variable for `webdemo`:

```sh
export Parameters__WireCaptureDirectory="$PWD/artifacts/office-wire"
aspire start --isolated --apphost src/CellBridge.AppHost/CellBridge.AppHost.csproj --non-interactive
```

The Aspire parameter defaults to empty. For an existing app, finish outstanding
requests and restart only that app after changing configuration. To disable
capture, remove the setting or set it to empty, then restart. Configuration is
read at startup; changing the file does not switch capture on or off in a
running host. Existing recordings remain on disk. Reusable hosts set
`CellBridgeOptions.CaptureDirectory` through the `AddCellBridge` configure
callback; they must bind configuration themselves.

Application logs retain document paths, request tokens, operation types, sizes, response IDs
and error diagnostics without full SOAP envelopes or base64 document chunks.
Debug response inspection records graph identifiers, lengths and hashes.
Application logs omit URL authority, credentials, query strings and fragments and
cap path length. These diagnostics remain available with capture disabled. Paths and identifiers
can still be sensitive. The response log's `id` matches the capture filename
prefix; individual request diagnostic lines do not all carry that ID.

A completed SOAP/MTOM exchange normally writes five files under one generated
ID:

| Suffix | Contents |
| --- | --- |
| `.request.bin` | Raw request body, including inline or attached document bytes |
| `.request.content-type.txt` | Request Content-Type for parsing SOAP or MIME |
| `.response.bin` | SOAP or MTOM response body |
| `.response.content-type.txt` | Response Content-Type, including the MIME boundary |
| `.summary.json` | Decoded operation outcomes used by the Office evidence verifier |

Malformed, cancelled or failed requests can leave incomplete sets. File creation
does not prove that the response reached Office or that a save succeeded.
Server output has no mitmproxy manifest and is not a run directory for
`inspect_capture.py validate` or `extract`. Use the
[server save verifier](automated-testing.md#verify-the-server-save) for desktop
save evidence. For binary inspection, parse SOAP/MTOM using the body files and
their Content-Type companions, then pass the extracted Cell response payloads to
the [binary dump tools](office-inspectors.md#run-the-checks). Follow the
[replay fixture workflow](automated-testing.md#turn-a-desktop-run-into-replay-fixtures)
when preparing automated cases.

The operator owns access and retention. Before enabling capture, create the
directory with permissions or an ACL limited to the host identity and authorized
investigators. Keep it outside the web root and in ignored `artifacts/` or other
local output. On Unix the host creates missing directories with mode `0700` and new files
with mode `0600`. Existing files/directories keep their permissions. Windows
creation inherits ACLs; restrict the parent directory before recording. Raw bodies can contain document contents and embedded
identity metadata. Restrict backups and copies as well as the original files.

`Protocol:MaxCaptureBytes` defaults to 1 GiB. A process includes existing files
when initializing its capture budget and skips further writes at the limit.
Failed or cancelled writes remove their partial file and release its reservation.
If cleanup fails, the retained bytes remain counted toward the budget.
Skipped or failed diagnostic writes do not fail a save. Concurrent processes
must use separate capture directories; this limit is not a shared-volume quota.
There is no automatic rotation or expiration. Choose a retention period and
monitor free space. After disabling capture and
finishing outstanding requests, manually remove expired evidence sets by their
common ID or remove a completed trial's directory. Sanitize decoded content
before retaining reviewed fixtures in `testdata/`; keep raw files out of source
control and CI uploads. See [files and privacy](#files-and-privacy) for the
separate proxy output and its manual-retention rules.

## Reverse capture with the existing Aspire certificate

Set `Parameters__CaptureUpstream` to your test farm's HTTP or HTTPS origin.
The default upstream is a placeholder. Aspire declares the proxy but starts it
only on request:

```sh
aspire start --isolated --apphost src/CellBridge.AppHost/CellBridge.AppHost.csproj --non-interactive
aspire resource sharepoint start --apphost src/CellBridge.AppHost/CellBridge.AppHost.csproj --non-interactive
aspire wait sharepoint --apphost src/CellBridge.AppHost/CellBridge.AppHost.csproj --non-interactive
```

| Aspire parameter | Default | Purpose |
| --- | --- | --- |
| `CaptureUpstream` | `http://sharepoint.example.test:42292` | Fixed upstream origin |
| `CaptureUpstreamCa` | Empty | Upstream certificate trust inside the proxy |
| `CaptureOutput` | Absolute `tools/capture/captures` path | Raw evidence directory |

Configure these through the AppHost `Parameters` section, user secrets or
environment variables. Aspire allocates the direct TLS listener port; inspect
`aspire describe` for the current `sharepoint` endpoint. Test fixtures omit the proxy.
For HTTPS upstreams with a private CA, set `CaptureUpstreamCa` to a verified
certificate/CA PEM on the Aspire machine. HTTP upstreams ignore that setting.

Open the allocated `sharepoint` HTTPS endpoint followed by
`/Shared%20Documents/Document.docx` in Word. The listener binds IPv4 loopback and uses Aspire's existing development
certificate without an extra Aspire proxy hop. The client must resolve the
hostname and trust a certificate covering it. Using `localhost` instead changes
the Host header and requires matching SharePoint mappings.

Reverse mode fixes the upstream connection/TLS server name, preserves the
client hostname and replaces the Host header port with the upstream port.
For the example, the forwarded header is
`Host: sharepoint.dev.localhost:42292`; flow metadata retains the original
client header.

### Configure SharePoint alternate access mappings

SharePoint must recognize the proxy origin and upstream origin in the same zone.
For a web application extended into an HTTP zone on port 42292, the following
example uses capture port 8443. Substitute the allocated `sharepoint` port in
both proxy URLs for your run.

| Internal URL | Zone | Public URL for zone |
| --- | --- | --- |
| `https://sharepoint.dev.localhost:8443` | Extranet | `https://sharepoint.dev.localhost:8443` |
| `http://sharepoint.dev.localhost:42292` | Extranet | `https://sharepoint.dev.localhost:8443` |

An absent/wrong-zone internal URL can redirect Word directly upstream, bypassing
capture. Inspect the redirect and incoming port before changing farm settings.
Host-named site collections use their own site URL configuration.
See [alternate access mappings](https://learn.microsoft.com/en-us/sharepoint/administration/plan-alternate-access-mappings)
and [IIS bindings](https://learn.microsoft.com/en-us/sharepoint/administration/update-a-web-application-url-and-iis-bindings).

Standalone reverse mode is also available:

```sh
python tools/capture/run.py --reverse-upstream https://host --tls-cert server.crt --tls-key server.key --upstream-ca upstream.pem --port 8443
```

Use the capture virtual environment's Python and an existing server certificate.
Manifests record the proxy mode and upstream.

## Run standalone

Supply the exact SharePoint hostname used by Word:

```powershell
./tools/capture/.venv/Scripts/python.exe tools/capture/run.py --hosts sharepoint-server --name open-save
```

Replace the hostname with your test farm. Multiple exact hosts are comma-separated;
URLs and wildcards are rejected. The listener defaults to `127.0.0.1:8877`.
`--port` and `--output-root` override listener/output settings.
Requests and redirects outside the allowlist are blocked and mark the run
incomplete.

Configure the client's HTTP/HTTPS proxy to that listener and keep the original
document URL. Record the old proxy configuration and restore it afterwards.
Word may bypass proxies for local/intranet addresses. Confirm discovery and
document requests appear before editing.

For HTTPS interception, trust the public `mitmproxy-ca-cert.cer` or `.pem`
from `captures/.mitmproxy` in the test environment. Remove that trust after the
trial. Keep `mitmproxy-ca.pem` private because it contains the CA key.
The kit does not install trust. For private upstream certificates, pass
`--upstream-ca path/to/lab-ca.pem`; verification stays enabled.

## Capture constraints

The proxy preserves application body bytes while streaming. HTTP framing,
header casing, TLS and timing may change; this is application-level capture.
Recording limits truncate recorded bytes, leave forwarded bodies intact and
cause validation to fail.

Reverse mode leaves absolute SOAP URLs, cookies and redirects unchanged.
Word can follow an upstream URL outside the listener. Check discovery,
authentication and saves to establish which part of a session was observed.

The proxy does not substitute an upstream user identity. Windows authentication,
TLS channel binding or enterprise proxy policy can prevent interception.
Compare direct and proxied editing; diagnose an authentication difference before
using the capture as a baseline.

The reverse listener uses a temporary combined server PEM. Normal shutdown
removes it; forced termination can leave a private-key file in the user's
temporary directory. Keep certificate keys and raw captures private.

## Capture a desktop scenario

1. Record SharePoint/Office builds, authentication scheme, document URL and
   whether the client previously opened it.
2. Verify direct open/save, then create a separate synthetic file for the capture
   so the first-open scenario starts without a warmed document cache.
3. Start capture, configure routing and confirm discovery/document requests.
4. Add distinctive text, save, close and reopen. Retain the saved file and note
   whether edits persisted.
5. Stop, validate and extract the capture. Review content before preparing fixtures.

Use separate Windows sessions or machines for different users and check that
both identities appear separately upstream.

## Finish and validate

Wait for Word to finish saving and closing. Use Ctrl+C for standalone capture,
or gracefully stop an Aspire-started capture using its printed run directory:

```sh
python tools/capture/stop.py path/to/run
python tools/capture/inspect_capture.py validate path/to/run
python tools/capture/inspect_capture.py extract path/to/run --output path/to/new-local-extraction
```

Finish the capture before stopping Aspire. Forced exits, zero exchanges,
recording failures and truncated runs fail validation. TLS, CONNECT and upstream
connection failures can mark a run incomplete before any HTTP flow exists.
Inspect the manifest's `errors` field and proxy console.

Validation checks completion, files, lengths and hashes. Extraction decodes
content encodings, validates MIME/XOP references and writes HTTP bodies and
SOAP/attachment files to a new directory. WebSocket payloads and their message
metadata are copied into each flow's `websocket` extraction directory, with
`.txt` for text and `.bin` for binary messages. Existing extraction output is not
overwritten; unsupported encodings fail. Use the
[Office Inspectors dump tool](office-inspectors.md#run-the-checks) for binary responses.

| Recording limit | Default | Override |
| --- | --- | --- |
| Body size | 32 MiB | `--max-body-bytes` |
| WebSocket message size | 32 MiB | `--max-websocket-message-bytes` |
| Total HTTP body and WebSocket payload data | 512 MiB | `--max-total-bytes` |
| WebSocket messages across the run | 10,000 | `--max-websocket-messages` |
| Exchanges | 10,000 | Fixed |

Allowed traffic continues after recording limits. The recorder marks the run
unhealthy rather than silently discarding evidence.

## WebSocket capture

No extra switch is required. The kit uses the pinned engine's `websocket_start`,
`websocket_message` and `websocket_end` hooks. The HTTP upgrade remains an
ordinary recorded flow, including its request/response headers and bodies.
Negotiation, rejected upgrades and HTTP fallback requests are also recorded
through the HTTP hooks. Capturing these exchanges does not classify a session
as SignalR or establish its application protocol.

Each upgraded flow has a `websocket` lifecycle summary. Its start/end events
have UTC timestamps and a run-wide WebSocket event sequence. Each message has a
one-based sequence within the connection, a run-wide event sequence, the
engine's receipt timestamp, direction, text/binary type and dropped/injected
flags. Payload descriptors include the full observed length and SHA-256 hash,
the captured length and an integrity/completeness flag. The run's `session_id`,
recorded `flow_id`, original `mitmproxy_flow_id` and HTTP client/server connection
IDs correlate messages with the upgrade. Separate reconnects produce separate
flows. These identifiers describe proxy observations, not Office user identities
or application collaboration sessions.

End events retain the engine's close time, initiating side, close code/reason and
any flow error. Mitmproxy has no separate WebSocket error hook. Abnormal closes,
including local code 1006 for a connection lost without a close frame, fail
validation. The kit accepts end codes 1000 and 1001 as normal capture completion;
other codes remain recorded but require investigation. Stopping with an open
WebSocket leaves its lifecycle pending and the run incomplete. Close the client
connections before requesting capture shutdown.

Mitmproxy reassembles fragmented text/binary messages and handles negotiated
compression before the message hook. The recordings are application messages,
not original frames, masking keys or compression bytes. Ping/pong frames are
relayed by the engine but do not reach the message hook. An unfinished fragmented
message never reaches that hook. A peer can abandon it with a normal close, so
close codes alone cannot detect this loss. The manifest explicitly sets
`websocket_capture_scope` to `completed_application_messages`. A valid run
establishes integrity for completed messages exposed by the engine; it cannot
prove that every fragment sent by a peer was recorded. Investigations needing
partial frames or control frames require separate packet-level evidence.

The payload byte cap truncates only the recording. The message count cap stops
creating message files, increments observed/dropped counters and marks the run
unhealthy. Forwarding continues unchanged. Both HTTP and WebSocket payloads use
the total byte budget. The addon keeps counters and at most the latest engine
message per connection rather than accumulating payload history. The pinned
engine still buffers a whole message before invoking the hook, so these limits
bound evidence files and retained history, not the memory required to receive
one arbitrarily large or unfinished message. Use the kit as the sole custom addon;
adding message-mutating addons requires separate qualification.

Validation checks lifecycle, correlation, ordering, message counts, payload
files, lengths/hashes and limits. Missing records, truncated payloads, recording
loss and dropped/injected messages fail validation. It checks the messages
observed by the proxy, not traffic that bypassed it. Schema 1 HTTP-only captures
remain readable, but a legacy WebSocket upgrade cannot pass as a complete
WebSocket capture.

Local tests use synthetic upstream/client peers with independent RFC 6455 frame
parsing, including fragmented UTF-8, binary/empty messages, simultaneous
connections, TLS interception, reverse listeners, HTTP fallback, abnormal ends
and exhausted limits. They do not establish Office/SharePoint compatibility.
A cloud two-PC capture still requires controlled Windows clients, exact client
and backend versions, distinct identities, trusted interception and reachable
allowlisted destinations. No RTC/OCS service or native Excel coauthoring is
implemented by this tooling.

## Files and privacy

Each run has `manifest.json`, `flows/*.json` and `files/*.body.bin`.
Flow metadata includes connections, timestamps, sizes, hashes and completeness.
Raw bodies retain content encoding but exclude HTTP chunk framing; extraction
decodes them separately.

New runs use schema 2. WebSocket message metadata lives under
`websockets/<flow_id>/<sequence>.json`, with payload bytes in
`files/<flow_id>.websocket.<sequence>.body.bin`. Run counters include WebSocket
connections, recorded/observed/dropped messages and combined captured bytes.

On Windows, replacement retries brief sharing locks for up to 140 ms.
Persistent errors mark the run unhealthy. `recording_error_details` and console
diagnostics identify the failing operation, path and OS error.

Authentication/cookie header values are redacted from metadata. Request query
strings/fragments and credentials in URL-valued metadata headers are also
redacted. Forwarded traffic remains unchanged. Raw SOAP, document content,
WebSocket payloads, path names, embedded usernames and other headers can still
be sensitive. Keep raw captures local and sanitize
metadata and decoded content before publishing a fixture. Capture validation
checks recorded-file integrity; desktop outcomes establish client behavior.

The default capture directory and virtual environment are ignored by Git.
Keep custom output directories outside version control. Old-run deletion is
manual; raw captures should not be uploaded as CI artifacts.

On Unix, the launcher and capture/extraction helpers create missing directories
with mode `0700` and new files with mode `0600`. The dedicated transport process
also uses `umask 077` while the engine runs, including certificate-cache writes.
Existing directories and older files keep their permissions. Restrict them
before resuming an old capture setup, and protect backups and copies separately.
Windows creation inherits ACLs; limit the output and certificate-cache roots to
the service identity and authorized investigators.

## Tests

```powershell
./tools/capture/.venv/Scripts/python.exe -m pip install --require-hashes -r tools/capture/requirements-dev.txt
./tools/capture/.venv/Scripts/python.exe -m pytest tools/capture -q
```

Tests start a local upstream and proxy subprocess. TLS tests use temporary
certificates rather than changing OS trust. CI runs without Office or SharePoint.

For standalone Debian testing, create a local environment before running the
same checks:

```sh
python3 -m venv tools/capture/.venv
tools/capture/.venv/bin/python -m pip install --require-hashes -r tools/capture/requirements-dev.txt
tools/capture/.venv/bin/python -m pytest tools/capture -q
```

References: [Aspire certificates](https://aspire.dev/app-host/certificate-configuration/),
[mitmproxy modes](https://docs.mitmproxy.org/stable/concepts/modes/#reverse-proxy),
[streaming](https://docs.mitmproxy.org/stable/overview/features/#streaming),
[options](https://docs.mitmproxy.org/stable/concepts/options/) and
[certificates](https://docs.mitmproxy.org/stable/concepts/certificates/).
WebSocket behavior follows the pinned engine's
[relay hooks](https://github.com/mitmproxy/mitmproxy/blob/5253dcbd1d8f0522de097bfe56918fe12a0f267a/mitmproxy/proxy/layers/websocket.py)
and [message model](https://github.com/mitmproxy/mitmproxy/blob/5253dcbd1d8f0522de097bfe56918fe12a0f267a/mitmproxy/websocket.py),
with [RFC 6455](https://www.rfc-editor.org/rfc/rfc6455) as the transport reference.
