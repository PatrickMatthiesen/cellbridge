The main problem in your trace is almost certainly this: **your `OPTIONS /shared/` response does not contain `X-MSFSSHTTP`**.

Microsoft's documented Office protocol-selection algorithm in **[MS-OCPROTO] §2.1.2.1.2, “Web Servers”** is:

1. Office sends `OPTIONS` to the **folder containing the document**.
2. It checks `X-MSFSSHTTP`.
3. If `X-MSFSSHTTP >= 1.0`, it selects MS-FSSHTTP.
4. Otherwise it examines `MS-Author-Via` / `DAV` for WebDAV/FPSE.
5. If no authoring protocol is advertised, it does a normal `GET` and opens the file **read-only in browse mode**.

That last case is almost an exact description of your trace. ([Microsoft Learn][1])

## What I would change first

Return this on **all successful Office protocol-discovery `OPTIONS /shared/` responses**:

```http
HTTP/1.1 200 OK
X-MSFSSHTTP: 1.0
Allow: OPTIONS, GET, HEAD, POST
Content-Length: 0
```

For the first experiment, I would deliberately advertise **`1.0`, not `1.5`**. `1.1+` enables additional client behavior such as `GetDocMetaInfo` and `GetVersions`; both are explicitly gated on `X-MSFSSHTTP >= 1.1`. Starting at `1.0` reduces how much of FSSHTTP you need to implement initially. ([Microsoft Learn][2])

Also launch the test explicitly in edit mode:

```text
ms-word:ofe|u|https://web-aspire.dev.localhost:7292/shared/test.docx
```

`ofe` means “open for edit”; `ofv` means read-only/view. That removes the browser/launch mechanism as another variable. ([Microsoft Learn][3])

After that change, I would expect your trace to change substantially.

---

## 1. Which `OPTIONS` header selects FSSHTTP?

The authoritative one is:

```http
X-MSFSSHTTP: 1.0
```

`[MS-OCPROTO] §2.1.2.1.2` explicitly says Word evaluates `X-MSFSSHTTP` **before** `MS-Author-Via`, and a value of at least `1.0` means “use the File Synchronization via SOAP over HTTP Protocol.” ([Microsoft Learn][1])

The other headers have different roles:

| Header                            | Meaning for Word protocol selection                                        |
| --------------------------------- | -------------------------------------------------------------------------- |
| `X-MSFSSHTTP: >=1.0`              | **Select MS-FSSHTTP**                                                      |
| `MS-Author-Via: DAV`              | Select WebDAV **if FSSHTTP wasn't selected**                               |
| `DAV: 1` / `DAV: 1,2`             | WebDAV fallback/capabilities                                               |
| `MS-Author-Via: MS-FP/4.0,DAV`    | Advertises FPSE + DAV; not required to select FSSHTTP                      |
| `Public-Extension`                | WebDAV Microsoft extensions; **not FSSHTTP discovery**                     |
| `DocumentManagementServer`        | FPSE/document-management capabilities                                      |
| `MicrosoftSharePointTeamServices` | Identifies/version-brands SharePoint; not the FSSHTTP selector             |
| `X-MSDAVEXT`                      | Microsoft WebDAV extension capability                                      |
| `SharePointError`                 | Error handling, not protocol discovery                                     |
| `X-MSGSWVERSION`                  | I found no Open Specification making it part of FSSHTTP selection          |
| `X-FSSHTTP-*`                     | I found no documented family of such headers involved in initial selection |

Microsoft even provides an OCPROTO scenario where, after authentication, SharePoint responds:

```http
X-MSFSSHTTP: 1.0
MS-Author-Via: MS-FP/4.0,DAV
```

and Office consequently uses FSSHTTP. ([Microsoft Learn][4])

Your `Public-Extension` is also slightly wrong if you are trying to emulate Microsoft's WebDAV response. `[MS-WDVME] §3.2.5.1` specifies:

```http
Public-Extension: http://schemas.microsoft.com/repl-2
```

not `repl-2.0`. That header still isn't what selects FSSHTTP, though. ([Microsoft Learn][5])

### What real SharePoint returns

There isn't a Microsoft-published, fixed byte-for-byte `OPTIONS` transcript for every SharePoint 2016/2019/SE patch level. Authentication type, web-app settings, IIS configuration and patch level affect the response.

A captured SharePoint response from the classic implementation contains the familiar set:

```http
Allow: GET, POST, OPTIONS, HEAD, MKCOL, PUT, PROPFIND, PROPPATCH,
       DELETE, MOVE, COPY, GETLIB, LOCK, UNLOCK
MS-Author-Via: MS-FP/4.0,DAV
X-MSDAVEXT: 1
DocumentManagementServer: Properties Schema;Source Control;Version History;
X-MSFSSHTTP: 1.0
DAV: 1,2
Public-Extension: http://schemas.microsoft.com/repl-2
MicrosoftSharePointTeamServices: ...
```

That capture was SharePoint 2010, so don't copy the version header, but the relevant protocol pattern is clear. ([SharePoint Stack Exchange][6])

A newer Microsoft-hosted storage `OPTIONS` capture shows:

```http
MS-Author-Via: DAV
DAV: 1, 2
MS-Storage: 1
X-MSFSSHTTP: 1.5
Public: OPTIONS, GET, HEAD, DELETE, PUT, POST, MKCOL,
        PROPFIND, PROPPATCH, LOCK, UNLOCK
```

demonstrating the newer `1.5` advertisement. ([Microsoft Learn][7])

For **your implementation**, don't try to reproduce every SharePoint header yet. `X-MSFSSHTTP` is the meaningful missing item.

Also, OCPROTO specifies the discovery request against the **containing folder**. So the fact Word asks:

```text
OPTIONS /shared/
```

rather than:

```text
OPTIONS /shared/test.docx
```

is correct and expected. ([Microsoft Learn][1])

---

## 2. Does `X-IDCRL_OPTIONS: force-auth-challenge` need a 401?

I would **not make that your next change**.

I could not find a published Microsoft Open Specification defining the semantics of:

```http
X-IDCRL_OPTIONS: force-auth-challenge
```

It appears to be an Office authentication implementation detail rather than part of MS-FSSHTTP discovery.

FSSHTTP itself says authentication is assumed to have been performed by the underlying protocol; authentication isn't defined by FSSHTTP. ([Microsoft Learn][8])

If your server genuinely requires authentication, then returning a truthful `401` with the authentication mechanism you actually implement is correct. For example, an on-premises Windows-auth implementation could challenge with `Negotiate`. Don't invent an IDCRL/BPOSIDCRL/OAuth challenge merely to look like SharePoint.

Real SharePoint 2016 modern-auth traffic demonstrates this distinction. An initial Office `OPTIONS` containing empty:

```http
Authorization: Bearer
```

can produce:

```http
HTTP/1.1 401 Unauthorized
WWW-Authenticate: Bearer realm="...",
    client_id="00000003-0000-0ff1-ce00-000000000000",
    trusted_issuers="...",
    cookie_uri="https://.../_api/SP.OAuth.NativeClient/Authenticate"
```

when SharePoint is actually configured for that authentication path. ([SharePoint Stack Exchange][9])

Microsoft also documents that the empty `Authorization: Bearer` request is normal Office behavior for SharePoint Online/OneDrive compatibility. ([Microsoft Learn][10])

So:

**If your experimental server is anonymous**, keep returning `200`; absence of a 401 isn't documented as a reason not to select FSSHTTP.

**If it is authenticated**, issue a genuine challenge and, after successful authentication, make sure the subsequent successful `OPTIONS` still returns:

```http
X-MSFSSHTTP: 1.0
```

The OCPROTO forms-auth example follows exactly that model: authenticate first, then repeat `OPTIONS`, then advertise `X-MSFSSHTTP`. ([Microsoft Learn][4])

---

## 3. Does `MS-Author-Via: DAV` enable or disable FSSHTTP?

Neither. It's the **fallback** after the FSSHTTP decision.

The ordering documented by Microsoft is important:

```text
X-MSFSSHTTP
      ↓
if not selected:
MS-Author-Via / DAV
      ↓
if still nothing:
GET + read-only browse mode
```

Therefore:

```http
X-MSFSSHTTP: 1.0
MS-Author-Via: DAV
DAV: 1,2
```

still selects FSSHTTP first. `DAV` doesn't override it. ([Microsoft Learn][1])

Conversely, your current response has neither `X-MSFSSHTTP` nor a recognized `MS-Author-Via`/`DAV`, which drives Word straight into the documented read-only HTTP GET path.

I would **not advertise DAV unless you intend to implement WebDAV fallback**. It isn't necessary for the FSSHTTP experiment.

---

## 4. Registry, Group Policy and trusted-host issues

There are two particularly relevant Office settings.

The classic FSSHTTP kill switch is:

```text
HKCU\Software\Microsoft\Office\16.0\Common\Internet
    FSSHTTPOff    REG_DWORD
```

Historically:

```text
FSSHTTPOff = 1
```

completely prevents Office from using FSSHTTP and forces it toward WebDAV. Microsoft's support article specifically warns that coauthoring and related functionality depends on FSSHTTP. ([Microsoft Support][11])

Microsoft announced in 2022 that the corresponding “Turn on file synchronization via SOAP over HTTP” policy and eventually the registry override were being deprecated for Microsoft 365 Apps, with SOAP/FSSHTTP becoming the preferred fixed behavior. Therefore, on a current Microsoft 365 build the setting might no longer have the same effect, but it is still worth checking for it, especially on machines that have accumulated older Office policies. ([TECHCOMMUNITY.MICROSOFT.COM][12])

Another relevant Office 16 policy is:

```text
HKCU\Software\Policies\Microsoft\Office\16.0\Common\Internet
    OpenDocumentsReadWriteWhileBrowsing
```

The Office 2016 ADMX policy describes value `1` as allowing web documents opened while browsing to open read/write; disabled/default behavior can be read-only. ([gpedit.tplant.com.au][13])

That is why I recommend using this test URL rather than just clicking an HTTPS hyperlink:

```text
ms-word:ofe|u|https://web-aspire.dev.localhost:7292/shared/test.docx
```

It explicitly tells Word that this is an **edit** operation. ([Microsoft Learn][3])

I found no credible current Microsoft documentation making `UseCoauth`, `SpecifyUploadMethod`, `DisableSSLCertificateValidation`, or a special “non-SharePoint FSSHTTP host allowlist” part of the FSSHTTP-selection algorithm.

Internet Security Zones / Protected View can independently make a document read-only, but that is distinct from the FSSHTTP capability decision. In your particular trace, the much stronger signal is that it follows OCPROTO's exact “no authoring protocol advertised → GET → read-only” path.

A properly trusted TLS certificate is still advisable. Since Word is already successfully doing `HEAD` and `GET` over your HTTPS connection, certificate validation isn't currently preventing network access.

---

## 5. Does `/shared/test.docx` need to look like SharePoint?

No documented path-pattern requirement exists.

`[MS-OCPROTO]` describes sending `OPTIONS` to the folder containing the document; it doesn't say the path must contain:

```text
/Shared Documents/
/shared%20documents/
```

Those paths appear throughout the specifications because `Shared Documents` is the traditional default SharePoint library name. ([Microsoft Learn][1])

Likewise, `[MS-FSSHTTP] §1.5` uses URLs such as:

```text
http://www.contoso.com/shared%20documents/test1.docx
```

as examples, not as grammar constraints. ([Microsoft Learn][8])

Your:

```text
/shared/test.docx
```

is therefore fine.

A nonstandard HTTPS port is also not prohibited; the protocol operates on URLs.

One endpoint detail is worth accommodating, however. `[MS-FSSHTTP] §1.5` defines the server endpoint as:

```text
/_vti_bin/cellstorage.svc
```

and real Office/SharePoint traces very commonly POST to:

```text
/_vti_bin/cellstorage.svc/CellStorageService
```

For compatibility while reverse-engineering the client, I would route both forms to the same ASP.NET Core handler. Microsoft captures show the `/CellStorageService` form in actual Office traffic. ([Microsoft Learn][8])

The spec also describes a “whole document URL” form such as:

```text
/shared/test.docx/_vti_bin/cellstorage.svc
```

for an initial request. I would cheaply route that form as well rather than assume your Word build will always choose the root service URL. ([Microsoft Learn][8])

---

## 6. Does Word call `WhoAmI` or `ServerTime` before FSSHTTP?

No separate HTTP/SOAP capability call is documented.

`WhoAmI` and `ServerTime` are **FSSHTTP subrequests**, contained inside an `ExecuteCellStorageRequest` request. They aren't pre-flight methods outside CellStorage. `[MS-FSSHTTP] §2.2.5.11` defines both as `SubRequest` types. ([Microsoft Learn][14])

More importantly, Microsoft's actual “opening a file” example sends a single FSSHTTP request containing multiple subrequests, including:

```xml
<SubRequest Type="Coauth" ... />
<SubRequest Type="SchemaLock" ... />
<SubRequest Type="Cell" ... />
...
<SubRequest Type="ServerTime" ... />
<SubRequest Type="WhoAmI" ... />
```

all inside one `ExecuteCellStorageRequest`. ([Microsoft Learn][15])

So you do need to implement `WhoAmI` and `ServerTime` fairly early, because modern Word may include them in the **first POST**. But Word doesn't need a successful earlier `WhoAmI` round trip in order to decide to issue that POST.

The first POST may also contain `Coauth`, `SchemaLock` and Cell/FSSHTTPB requests. This is likely the next implementation challenge once `X-MSFSSHTTP` gets you past discovery.

---

## 7. Is there another FSSHTTP capability-discovery operation?

Not in MS-FSSHTTP itself.

The split between the specifications is essentially:

```text
[MS-OCPROTO]
    Office web-server protocol selection
           ↓
       X-MSFSSHTTP
           ↓
[MS-FSSHTTP]
    ExecuteCellStorageRequest
           ↓
[MS-FSSHTTPB]
    binary file synchronization structures
```

The documented selection step is OCPROTO §2.1.2.1.2. Once FSSHTTP has been selected, `[MS-FSSHTTP] §2.1` specifies SOAP/HTTP and the SOAP action:

```http
SOAPAction: http://schemas.microsoft.com/sharepoint/soap/ICellStorages/ExecuteCellStorageRequest
```

([Microsoft Learn][1])

The Microsoft Interoperability Test Suites are synthetic protocol clients designed primarily to verify server-side protocol requirements. They aren't intended to reproduce all of Word's cross-protocol UI/discovery heuristics. Accordingly, the important discovery contract is in OCPROTO rather than an extra FSSHTTP test-suite handshake. ([Microsoft Learn][16])

WOPI is also a separate architecture. `/hosting/discovery` is a mechanism whereby a **WOPI server discovers a WOPI client**, typically Office Online Server/Microsoft 365 for the web. Microsoft describes the interaction as the WOPI server fetching:

```text
http://WOPIClient/hosting/discovery
```

and learning how to invoke that browser-based client. ([Microsoft Learn][17])

Desktop Word using direct SharePoint-style FSSHTTP therefore does **not** need to probe your server's `/hosting/discovery`. The absence of such a request in your trace is normal.

---

## Minimal configuration I would test now

For your next run I would make only these changes:

```http
OPTIONS /shared/

HTTP/1.1 200 OK
X-MSFSSHTTP: 1.0
Allow: OPTIONS,GET,HEAD,POST
Content-Length: 0
```

Keep the `HEAD` and `GET` behavior you already have.

Then launch:

```text
ms-word:ofe|u|https://web-aspire.dev.localhost:7292/shared/test.docx
```

And configure ASP.NET Core to log/accept POSTs for at least:

```text
/_vti_bin/cellstorage.svc
/_vti_bin/cellstorage.svc/CellStorageService
/shared/test.docx/_vti_bin/cellstorage.svc
```

The first thing I would look for is a request carrying:

```http
SOAPAction: "http://schemas.microsoft.com/sharepoint/soap/ICellStorages/ExecuteCellStorageRequest"
```

Microsoft's own Office Fiddler inspector identifies an FSSHTTP request essentially by that SOAP action. ([GitHub][18])

If adding just `X-MSFSSHTTP: 1.0` does **not** produce a POST, then I would investigate launch/read-only policy and authentication next. I would not spend time emulating `MicrosoftSharePointTeamServices`, IDCRL, WebDAV, `X-MSGSWVERSION`, etc. before running this experiment.

## Word-side logging

There are usable Office logging switches, although I found no Microsoft-published ETW provider specifically named **`OfficeProtocolDiscovery`**.

Microsoft support guidance for Office 2016 uses:

```cmd
reg add HKCU\SOFTWARE\Microsoft\Office\16.0\Common\Logging /v EnableLogging /t REG_DWORD /d 1 /f
reg add HKCU\SOFTWARE\Microsoft\Office\16.0\Common\Logging /v DefaultMinimumSeverity /t REG_DWORD /d 100 /f
```

After reproducing, logs appear under `%TEMP%`, commonly named:

```text
<computername>-<datetime>.log
```

Then disable them again:

```cmd
reg delete HKCU\SOFTWARE\Microsoft\Office\16.0\Common\Logging /v EnableLogging /f
reg delete HKCU\SOFTWARE\Microsoft\Office\16.0\Common\Logging /v DefaultMinimumSeverity /f
```

([Microsoft Learn][19])

There is additionally:

```text
HKCU\Software\Microsoft\Office\16.0\Common\Logging
    MsoEtwTracingEnabled = 1
```

which Microsoft has used for Office ETW tracing/support diagnostics. ([Microsoft Support][20])

And:

```text
HKCU\Software\Microsoft\Office\16.0\Common\Debug
    TCOTrace = 1
```

is a real Office common tracing switch. Microsoft's current public documentation explicitly demonstrates it for Outlook and warns that it can generate very large `%TEMP%` logs, so I would treat Word support for it as an implementation detail rather than promise that it will expose the FSSHTTP decision. ([Microsoft Learn][21])

For discovering what ETW providers your exact M365 build actually registered, rather than relying on undocumented provider names, I would also run:

```cmd
logman query providers | findstr /i "office mso word winhttp"
```

That gives you the provider names present on **that build**. You can then capture them with WPR/WPA or `logman`.

The Office generic logging plus your HTTP trace should give you a useful A/B experiment:

```text
current:
OPTIONS -> no X-MSFSSHTTP -> GET -> read-only

test:
OPTIONS -> X-MSFSSHTTP: 1.0 -> ?
```

That experiment should answer the fundamental discovery question much more cleanly than trying to clone an entire SharePoint `OPTIONS` response.

[1]: https://learn.microsoft.com/en-us/openspecs/office_protocols/ms-ocproto/2d268a23-420b-4339-8c56-9e4f8a924171?utm_source=chatgpt.com "[MS-OCPROTO]: Web Servers | Microsoft Learn"
[2]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttp/6e6db998-6014-4eef-b43d-4ff1ce3b4ba5?utm_source=chatgpt.com "[MS-FSSHTTP]: GetVersions Subrequest | Microsoft Learn"
[3]: https://learn.microsoft.com/en-us/office/client-developer/office-uri-schemes?utm_source=chatgpt.com "Office URI Schemes | Microsoft Learn"
[4]: https://learn.microsoft.com/bg-bg/openspecs/office_protocols/ms-ocproto/5ca6956b-748d-41d3-b717-90eeb7324925?utm_source=chatgpt.com "[MS-OCPROTO]: Example 1: Open a Document from a Web Server Gated by Forms Authentication | Microsoft Learn"
[5]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-wdvme/e91f5e49-99b1-43bf-90f6-dbfc3be9149d?utm_source=chatgpt.com "[MS-WDVME]: Extensions to OPTIONS | Microsoft Learn"
[6]: https://sharepoint.stackexchange.com/questions/40776/access-error-publishing-infopath-form-to-forms-services?utm_source=chatgpt.com "Access error publishing InfoPath form to Forms Services"
[7]: https://learn.microsoft.com/en-au/answers/questions/696289/use-method-options-to-request-a-sharepoint-url-get?utm_source=chatgpt.com "Use method OPTIONS to request a sharepoint url gets \"405 Method Not Allowed\" - Microsoft Q&A"
[8]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttp/750596b8-c711-4da5-89ae-993b01bf6eca?utm_source=chatgpt.com "[MS-FSSHTTP]: Prerequisites/Preconditions | Microsoft Learn"
[9]: https://sharepoint.stackexchange.com/questions/206623/sharepoint-2016-adfs-persistent-cookie-office-client-integration-authent?noredirect=1&utm_source=chatgpt.com "SharePoint 2016 - ADFS - persistent cookie - office client integration - authentication prompt - SharePoint Stack Exchange"
[10]: https://learn.microsoft.com/en-us/troubleshoot/sharepoint/sharing-and-permissions/receive-credentials-prompting?utm_source=chatgpt.com "You're prompted for credentials when you open documents anonymously - SharePoint | Microsoft Learn"
[11]: https://support.microsoft.com/en-us/sharepoint/admin/using-the-fsshttpoff-registry-key?utm_source=chatgpt.com "Using the FSSHTTPOff registry key | Microsoft Support"
[12]: https://techcommunity.microsoft.com/discussions/microsoft-365/deprecating-turn-on-file-synchronization-via-soap-over-http-group-policy-setting/3638176?utm_source=chatgpt.com "Deprecating \"Turn On File Synchronization Via SOAP Over HTTP\" Group Policy setting"
[13]: https://gpedit.tplant.com.au/en-us/policy/office16/L_OpenOfficedocumentsasreadwritewhilebrowsing/?utm_source=chatgpt.com "Open Office documents as read/write while browsing - ADMX Viewer"
[14]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttp/a3bb03aa-bbc6-4fab-96f4-4909fb2b813b?utm_source=chatgpt.com "[MS-FSSHTTP]: SubRequestAttributeType | Microsoft Learn"
[15]: https://learn.microsoft.com/en-us/openspecs/sharepoint_protocols/ms-fsshttp/77c5af49-9c67-42bd-a047-59256bfb5e30?utm_source=chatgpt.com "[MS-FSSHTTP]: Request | Microsoft Learn"
[16]: https://learn.microsoft.com/en-us/openspecs/dev_center/ms-devcentlp/a859bab5-e1b5-481c-9b73-4ce93efc0a06?utm_source=chatgpt.com "[MS-DEVCENTLP]: Interoperability Test Tools | Microsoft Learn"
[17]: https://learn.microsoft.com/en-us/openspecs/office_protocols/ms-wopi/426e8b44-2833-416b-a135-b5d68df3ba2d?utm_source=chatgpt.com "[MS-WOPI]: Accessing Discovery XML | Microsoft Learn"
[18]: https://github.com/OfficeDev/Office-Inspectors-for-Fiddler/blob/main/FSSHTTPWOPIInspector/Source/FSSHTTPandWOPIInspector.cs?utm_source=chatgpt.com "Office-Inspectors-for-Fiddler/FSSHTTPWOPIInspector/Source/FSSHTTPandWOPIInspector.cs at main · OfficeDev/Office-Inspectors-for-Fiddler · GitHub"
[19]: https://learn.microsoft.com/en-us/questions/245450/error-to-save-to-server-correct-the-invalid-missin?utm_source=chatgpt.com "Error \"To save to server, correct the invalid missing required properties\" - Microsoft Q&A"
[20]: https://support.microsoft.com/en-us/topic/description-of-the-office-2013-hotfix-package-mso-x-none-msp-msores-x-none-msp-june-20-2013-69c517f5-d7f5-fd04-c680-5748ae605afe?utm_source=chatgpt.com "Description of the Office 2013 hotfix package (Mso-x-none.msp; Msores-x-none.msp): June 20, 2013 - Microsoft Support"
[21]: https://learn.microsoft.com/en-us/troubleshoot/outlook/performance/outlook-exe-log-file-in-temp-folder-consumes-too-much-space?utm_source=chatgpt.com "Outlook.exe.log in %Temp% consumes too much disk space - Outlook | Microsoft Learn"
