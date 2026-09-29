using System.Text;
using OfficeCollabServer.FssHttp;
using OfficeCollabServer.FssHttpB;
using OfficeCollabServer.Storage;
using OfficeCollabServer.Web;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
builder.Services.AddSingleton<DocumentStore>();
builder.Services.AddLogging();

var app = builder.Build();
var sensitiveHeaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    "Authorization", "Proxy-Authorization", "Cookie", "Set-Cookie",
    "WWW-Authenticate", "Proxy-Authenticate",
};

// Log EVERY request Word sends — this is our primary interop diagnostic.
app.Use(async (ctx, next) =>
{
    var log = app.Logger;
    log.LogInformation(">>> {Method} {Path}{Query} from {Remote}",
        ctx.Request.Method,
        ctx.Request.Path,
        ctx.Request.QueryString,
        ctx.Connection.RemoteIpAddress);

    foreach (var header in ctx.Request.Headers)
    {
        log.LogInformation("    {Name}: {Value}", header.Key,
            sensitiveHeaders.Contains(header.Key) ? "[REDACTED]" : header.Value.ToString());
    }

    await next();

    log.LogInformation("<<< {Method} {Path} => {Status}",
        ctx.Request.Method, ctx.Request.Path, ctx.Response.StatusCode);
});

// Seed examples and optionally load a directory of documents.
var store0 = app.Services.GetRequiredService<DocumentStore>();
DocumentLibrary.Seed(store0, app.Configuration, app.Environment.ContentRootPath);
app.Logger.LogInformation("Loaded {Count} documents", store0.List().Length);

app.MapGet("/api/documents", (HttpContext ctx, DocumentStore store) =>
{
    ctx.Response.Headers.CacheControl = "no-store";
    return Results.Ok(DocumentLibrary.List(store));
});

app.MapPost("/api/documents", (CreateDocumentRequest request, DocumentStore store) =>
{
    var result = DocumentCreation.TryCreate(store, request.Name, request.Type);
    if (!result.Created)
        return result.Conflict
            ? Results.Conflict(new { error = result.Error })
            : Results.BadRequest(new { error = result.Error });

    var path = string.Join('/', result.Document!.Path.Split('/').Select(Uri.EscapeDataString));
    return Results.Created(path, result.Document);
});

// Serve every document at its own stable URL.
// HEAD is required: Word's "Microsoft Office Existence Discovery" probe uses
// HEAD and treats a 405 as "server not responding".
app.MapMethods("/shared/{fileName}", new[] { "GET", "HEAD" },
    async (HttpContext ctx, DocumentStore store) =>
{
    // Request.Path is already decoded. Escape literal percent signs before
    // passing it to the store, which decodes URL paths once.
    var encodedPath = string.Join('/', ctx.Request.Path.Value!.Split('/').Select(Uri.EscapeDataString));
    var doc = store.Get(encodedPath);
    if (doc is null)
    {
        return Results.NotFound();
    }

    byte[] content;
    lock (doc)
    {
        // Capture bytes and headers from the same revision while another client saves.
        content = doc.Content;
        ctx.Response.ContentType = DocumentLibrary.ContentType(doc.Url);
        ctx.Response.ContentLength = content.Length;
        ctx.Response.Headers.ETag = doc.Etag;
        ctx.Response.Headers.LastModified = doc.LastModifiedUtc.ToString("R");
    }
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    if (string.Equals(ctx.Request.Query["download"], "true", StringComparison.OrdinalIgnoreCase))
    {
        ctx.Response.Headers.ContentDisposition = new System.Net.Http.Headers.ContentDispositionHeaderValue("attachment")
        {
            FileNameStar = Path.GetFileName(doc.Url),
        }.ToString();
    }

    if (HttpMethods.IsHead(ctx.Request.Method))
    {
        return Results.Empty;
    }

    await ctx.Response.Body.WriteAsync(content);
    return Results.Empty;
});

// Office capability discovery (success criterion 2).
app.MapMethods("/_vti_bin/cellstorage.svc", new[] { "OPTIONS" }, () => Results.Ok());

// Aspire health endpoints for WaitFor and the dashboard.
app.MapDefaultEndpoints();

// Word probes OPTIONS on the document's folder with X-MSGETWEBURL / 
// X-MS-AuthDomainSupport to discover the web URL and auth scheme before it
// will use FSSHTTP. A 404 here breaks the handshake.
//
// The critical header is X-MSFSSHTTP: per [MS-OCPROTO] section 2.1.2.1.2,
// Word selects MS-FSSHTTP only when the folder OPTIONS response advertises
// X-MSFSSHTTP >= 1.0. Without it Word falls back to plain GET + read-only.
// We advertise 1.0 (not 1.5) to keep the required subrequest surface minimal:
// 1.1+ gates GetDocMetaInfo and GetVersions.
app.MapMethods("/shared", new[] { "OPTIONS" }, (HttpContext ctx) =>
{
    // CRITICAL: return same-origin URLs. Word connected via
    // web-aspire.dev.localhost; advertising localhost makes Word treat the
    // discovered web as a different security context and abandon authoring.
    var origin = $"{ctx.Request.Scheme}://{ctx.Request.Host}";

    ctx.Response.Headers["X-MSFSSHTTP"] = "1.0";
    ctx.Response.Headers["X-MS-AuthDomainSupport"] = "False";
    ctx.Response.Headers["X-MS-CookieUri"] = $"{origin}/";
    ctx.Response.Headers["X-MSGETWEBURL"] = $"{origin}/shared/";
    ctx.Response.Headers["X-IDCRL_ACCEPTED"] = "t";
    ctx.Response.Headers["Public-Extension"] = "http://schemas.microsoft.com/repl-2";
    ctx.Response.Headers["Allow"] = "OPTIONS,GET,HEAD,POST,PUT";
    return Results.Ok();
});

// The cell storage service endpoint (success criterion 4).
// Word/SharePoint traffic uses several URL forms for the service; route them
// all to the same handler ([MS-FSSHTTP] section 1.5):
//   /_vti_bin/cellstorage.svc
//   /_vti_bin/cellstorage.svc/CellStorageService
//   /shared/test.docx/_vti_bin/cellstorage.svc
// ASP.NET Core forbids a catch-all in the middle of a route, so each URL
// form gets an explicit route:
//   /_vti_bin/cellstorage.svc                          (root form)
//   /_vti_bin/cellstorage.svc/CellStorageService       (trailing segment)
//   /shared/test.docx/_vti_bin/cellstorage.svc         (document-prefixed)
app.MapMethods("/_vti_bin/cellstorage.svc/{**suffix}", new[] { "OPTIONS" }, () => Results.Ok());
app.MapMethods("/shared/{fileName}/_vti_bin/cellstorage.svc/{**suffix}", new[] { "OPTIONS" }, () => Results.Ok());

// Word POSTs to the document-prefixed form (observed: 
// /shared/test.docx/_vti_bin/cellstorage.svc/CellStorageService with
// Scenario: OpenFile_Open). Route it to the same handler as the root form.
app.MapPost("/shared/{fileName}/_vti_bin/cellstorage.svc/{**suffix}", (HttpContext ctx, DocumentStore store) => HandleCellStoragePost(ctx, store));

app.MapPost("/_vti_bin/cellstorage.svc/{**suffix}", (HttpContext ctx, DocumentStore store) => HandleCellStoragePost(ctx, store));

app.Run();

/// <summary>
/// Shared handler for ExecuteCellStorageRequest POSTs to any of the
/// cellstorage.svc URL forms.
/// </summary>
static async Task<IResult> HandleCellStoragePost(HttpContext ctx, DocumentStore store)
{
    var log = ctx.RequestServices.GetRequiredService<ILogger<Program>>();
    await using var body = new MemoryStream();
    await ctx.Request.Body.CopyToAsync(body);
    var rawBody = body.ToArray();
    string soapXml;
    IReadOnlyList<MtomPart>? parts = null;

    try
    {
        if (ctx.Request.ContentType?.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase) == true)
        {
            parts = MtomMessageParser.Parse(rawBody, ctx.Request.ContentType);
            var start = ctx.Request.ContentType.Split(';', StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(p => p.TrimStart().StartsWith("start=", StringComparison.OrdinalIgnoreCase))?
                .Split('=', 2)[1].Trim().Trim('"', '<', '>');
            var soapPart = start is not null
                ? parts.FirstOrDefault(p => string.Equals(p.ContentId, start, StringComparison.OrdinalIgnoreCase))
                : null;
            soapPart ??= parts.FirstOrDefault(p => p.ContentType.Contains("xop+xml", StringComparison.OrdinalIgnoreCase))
                ?? parts[0];
            soapXml = Encoding.UTF8.GetString(soapPart.Content);
        }
        else
        {
            soapXml = Encoding.UTF8.GetString(rawBody);
        }
    }
    catch (Exception ex) when (ex is InvalidDataException or FormatException)
    {
        log.LogWarning(ex, "Failed to parse MTOM request ({Length} bytes)", rawBody.Length);
        return Results.BadRequest();
    }

    log.LogInformation("FSSHTTP request received: {Length} bytes, action={Action}",
        rawBody.Length, ctx.Request.Headers["SOAPAction"].ToString());
    log.LogInformation("FSSHTTP SOAP part ({Length} chars): {SoapXml}",
        soapXml.Length, soapXml.Length > 12000 ? soapXml[..12000] : soapXml);

    CellStorageRequest request;
    try
    {
        request = CellStorageRequestParser.Parse(soapXml);
    }
    catch (Exception ex)
    {
        log.LogWarning(ex, "Failed to parse FSSHTTP request");
        return Results.BadRequest();
    }

    if (parts is not null)
    {
        log.LogInformation("MTOM parts: {Parts}", string.Join("; ", parts.Select(p =>
            $"id={p.ContentId ?? "<none>"}, type={p.ContentType}, bytes={p.Content.Length}")));
        foreach (var subRequest in request.Requests.SelectMany(r => r.SubRequests))
        {
            if (subRequest.SubRequestDataAttributes.TryGetValue("IncludeHref", out var href))
            {
                var contentId = href.Trim().Trim('<', '>');
                if (contentId.StartsWith("cid:", StringComparison.OrdinalIgnoreCase))
                {
                    contentId = Uri.UnescapeDataString(contentId[4..]).Trim('<', '>');
                }

                subRequest.SubRequestDataBinary = parts
                    .FirstOrDefault(p => string.Equals(p.ContentId, contentId, StringComparison.OrdinalIgnoreCase))?.Content;
                log.LogInformation("MTOM reference {Href} resolved={Resolved} bytes={Bytes}",
                    href, subRequest.SubRequestDataBinary is not null,
                    subRequest.SubRequestDataBinary?.Length ?? 0);
            }
        }
    }

    // Log every decoded request (success criterion 5).
    foreach (var fileRequest in request.Requests)
    {
        log.LogInformation("Request url={Url} token={Token} subrequests={Subrequests}",
            fileRequest.Url,
            fileRequest.RequestToken,
            string.Join(",", fileRequest.SubRequests.Select(s => s.Type)));

        foreach (var subRequest in fileRequest.SubRequests)
        {
            foreach (var attr in subRequest.SubRequestDataAttributes)
            {
                log.LogInformation("  {Type} SubRequestData {Key}={Value}",
                    subRequest.Type, attr.Key, attr.Value);
            }
        }
    }

    // Build the response (success criterion 6).
    // WebUrl must be same-origin with the request ([MS-FSSHTTP] 2.2.3.6).
    var response = new CellStorageResponse
    {
        // SharePoint answers Word's 2.2 request with the highest response
        // revision it supports. The captured editable session uses 2.3.
        Version = request.Version,
        MinorVersion = request.Version == 2 ? 3u : request.MinorVersion,
        UsesDirectBody = request.UsesDirectBody,
        WebUrl = $"{ctx.Request.Scheme}://{ctx.Request.Host}",
    };

    foreach (var fileRequest in request.Requests)
    {
        var doc = DocumentRequestResolver.Resolve(store, fileRequest);
        if (doc is null)
        {
            response.Responses.Add(new FssHttpResponse
            {
                Url = fileRequest.Url,
                RequestToken = fileRequest.RequestToken,
                ErrorCode = "FileNotExistsOrCannotBeCreated",
            });
            continue;
        }

        // Every subrequest, including lock release, targets the resolved file.
        fileRequest.Url = DocumentRequestResolver.CanonicalUrl(doc, $"{ctx.Request.Scheme}://{ctx.Request.Host}");
        var fileResponse = new FssHttpResponse
        {
            Url = fileRequest.Url,
            RequestToken = fileRequest.RequestToken,
            IntervalOverride = 0,
            ResourceId = doc.TransitionId,
        };

        foreach (var subRequest in fileRequest.SubRequests)
        {
            var subResponse = new FssHttpSubResponse
            {
                Type = subRequest.Type,
                SubRequestToken = subRequest.SubRequestToken,
                ErrorCode = "Success",
                HResult = "0",
            };

            switch (subRequest.Type)
            {
                case SubRequestType.Cell:
                    HandleCellSubRequest(store, fileRequest, subRequest, subResponse, log);
                    break;

                case SubRequestType.Coauth:
                    HandleCoauthSubRequest(store, fileRequest, subRequest, subResponse, log);
                    break;

                case SubRequestType.SchemaLock:
                    if (string.Equals(subRequest.DependencyType, "OnNotSupported", StringComparison.OrdinalIgnoreCase))
                    {
                        subResponse.ErrorCode = "DependentOnlyOnNotSupportedRequestGetSupported";
                        subResponse.HResult = "2147500037";
                        subResponse.EmitEmptySubResponseData = true;
                        log.LogInformation(
                            "SchemaLock fallback skipped because Coauth is supported: token={Token} dependsOn={DependsOn}",
                            subRequest.SubRequestToken, subRequest.DependsOn);
                    }
                    else
                    {
                        HandleSchemaLockSubRequest(store, fileRequest, subRequest, subResponse, log);
                    }
                    break;

                case SubRequestType.WhoAmI:
                    subResponse.SubResponseDataAttributes["UserName"] = "officelab";
                    subResponse.SubResponseDataAttributes["UserLogin"] = "officelab";
                    break;

                case SubRequestType.ServerTime:
                    subResponse.SubResponseDataAttributes["ServerTime"] =
                        DateTime.UtcNow.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    break;

                case SubRequestType.GetDocMetaInfo:
                    subResponse.SubResponseDataXml = MetadataVersioningResponseBuilder
                        .BuildGetDocMetaInfo(doc).SubResponseDataXml;
                    break;

                case SubRequestType.GetVersions:
                    subResponse.SubResponseXml = MetadataVersioningResponseBuilder
                        .BuildGetVersions(doc, $"{ctx.Request.Scheme}://{ctx.Request.Host}").SubResponseXml;
                    break;

                case SubRequestType.ExclusiveLock:
                    FssHttpLockCoordinator.For(doc).ApplyExclusiveLock(subRequest, subResponse);
                    break;

                case SubRequestType.LockStatus:
                    FssHttpLockCoordinator.For(doc).ApplyLockStatus(subRequest, subResponse);
                    break;

                case SubRequestType.AmIAlone:
                    FssHttpLockCoordinator.For(doc).ApplyAmIAlone(doc, subRequest, subResponse);
                    break;

                case SubRequestType.EditorsTable:
                    HandleEditorsTableSubRequest(store, fileRequest, subRequest, subResponse, log);
                    break;

                default:
                    // Other subrequest types are not yet implemented; report
                    // a protocol error rather than a malformed success.
                    subResponse.ErrorCode = "NotSupported";
                    break;
            }

            fileResponse.SubResponses.Add(subResponse);
        }

        response.Responses.Add(fileResponse);
    }

    string envelope = response.ToSoapEnvelope();
    log.LogInformation("FSSHTTP response ({Length} chars): {Envelope}", envelope.Length, envelope);

    if (ctx.Request.ContentType?.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase) == true)
    {
        var mtom = response.ToMtomMessage();
        ctx.Response.ContentType = mtom.ContentType;
        ctx.Response.ContentLength = mtom.Body.Length;
        await ctx.Response.Body.WriteAsync(mtom.Body);
        return Results.Empty;
    }

    return Results.Content(envelope, "text/xml; charset=utf-8");
}

/// <summary>
/// Handles a Cell subrequest: decodes the FSSHTTPB binary payload from the
/// SubRequestData and produces the matching binary response.
///
/// Word's Cell subrequest flow is: QueryAccess → QueryChanges (download) →
/// PutChanges (upload). We answer QueryAccess with access granted and
/// QueryChanges with a full data element graph carrying the file content.
/// </summary>
static void HandleCellSubRequest(
    DocumentStore store,
    FssHttpRequest fileRequest,
    FssHttpSubRequest subRequest,
    FssHttpSubResponse subResponse,
    ILogger log)
{
    // The document URL from the SOAP request maps to the store.
    var doc = store.Get(fileRequest.Url)!;

    if (!TryResolvePartition(subRequest, out var partitionKind))
    {
        subResponse.ErrorCode = "InvalidArgument";
        subResponse.HResult = "2147942487";
        log.LogWarning("Unsupported Cell partition: token={Token} partitionId={PartitionId}",
            subRequest.SubRequestToken,
            subRequest.SubRequestDataAttributes.GetValueOrDefault("PartitionID", "<default>"));
        return;
    }

    var partition = doc.GetPartition(partitionKind);

    // Try to decode the FSSHTTPB request payload if one was provided.
    FsshttpbCellRequest? fsshttpbRequest = null;
    if (subRequest.SubRequestDataAttributes.TryGetValue("IncludeHref", out var href) &&
        href.StartsWith("cid:", StringComparison.OrdinalIgnoreCase))
    {
        // MTOM part reference; the raw binary would arrive as a MIME part.
        // For non-MTOM requests the payload is inline base64 in SubRequestData.
    }

    if (subRequest.SubRequestDataBinary is not null)
    {
        fsshttpbRequest = TryDecodeFsshttpbBinary(subRequest.SubRequestDataBinary, log);
    }

    if (fsshttpbRequest is null && subRequest.SubRequestDataXml is not null)
    {
        fsshttpbRequest = TryDecodeFsshttpbPayload(subRequest.SubRequestDataXml, log);
    }

    var requestType = fsshttpbRequest?.SubRequests.FirstOrDefault()?.RequestType
        ?? RequestTypes.QueryAccess;

    // Keep the request type visible during interop testing. A missing or
    // unresolved MTOM attachment otherwise silently becomes QueryAccess.
    if (fsshttpbRequest is null)
    {
        log.LogWarning("Cell FSSHTTPB payload could not be decoded: binaryBytes={BinaryBytes}, xmlChars={XmlChars}",
            subRequest.SubRequestDataBinary?.Length ?? 0,
            subRequest.SubRequestDataXml?.Length ?? 0);
        subResponse.ErrorCode = "InvalidArgument";
        return;
    }

    bool getFileProps = subRequest.SubRequestDataAttributes.TryGetValue("GetFileProps", out var getFilePropsValue) &&
        string.Equals(getFilePropsValue, "true", StringComparison.OrdinalIgnoreCase);
    // The request ID is required to correlate each FSSHTTPB response.
    log.LogInformation(
        "Cell request token={Token} partition={Partition} partitionId={PartitionId} getFileProps={GetFileProps} type={RequestType} subrequests={Count} knowledge={Knowledge}",
        subRequest.SubRequestToken,
        partition.Kind,
        subRequest.SubRequestDataAttributes.GetValueOrDefault("PartitionID", "<default>"),
        getFileProps,
        requestType,
        fsshttpbRequest.SubRequests.Count,
        partition.KnowledgeSequence);

    FsshttpbResponse Execute() => CellBinaryRequestExecutor.Execute(doc, partition, fsshttpbRequest,
        requestId => BuildEditorsTableSharePoint13QueryChangesResponse(doc, partition, requestId));
    FsshttpbResponse fsshttpbResponse;
    if (fsshttpbRequest.SubRequests.Any(x => x.RequestType == RequestTypes.PutChanges))
    {
        if (subRequest.SubRequestDataBinary is { } saveBytes)
        {
            var encoded = Convert.ToBase64String(saveBytes);
            var captureId = Guid.NewGuid().ToString("N");
            for (int offset = 0; offset < encoded.Length; offset += 4096)
                log.LogInformation("Cell save evidence id={CaptureId} offset={Offset} total={Total} base64={Data}",
                    captureId, offset, encoded.Length, encoded.Substring(offset, Math.Min(4096, encoded.Length - offset)));
        }
        if (!FssHttpLockCoordinator.For(doc).ExecuteCellWrite(subRequest.SubRequestDataAttributes,
            Execute, out var savedResponse, out var lockError))
        {
            subResponse.ErrorCode = lockError!;
            return;
        }
        fsshttpbResponse = savedResponse!;
        log.LogInformation("Cell save completed: version={Version} bytes={Bytes} errors={Errors}",
            doc.ContentVersion, doc.Content.Length, fsshttpbResponse.SubResponses.Count(x => x.Status));
    }
    else fsshttpbResponse = Execute();

    if (getFileProps)
    {
        subResponse.SubResponseDataAttributes["Etag"] = doc.Etag;
        subResponse.SubResponseDataAttributes["CreateTime"] = doc.CreatedUtc.ToFileTimeUtc().ToString();
        subResponse.SubResponseDataAttributes["LastModifiedTime"] = doc.LastModifiedUtc.ToFileTimeUtc().ToString();
        subResponse.SubResponseDataAttributes["ModifiedBy"] = "officelab";
    }

    subResponse.SubResponseDataBase64 = fsshttpbResponse.ToByteArray(
        FsshttpbSerializationProfile.SharePoint13_11);
    var inspection = FsshttpbResponseInspector.Inspect(subResponse.SubResponseDataBase64);
    log.LogInformation(
        "Cell response dump token={Token} partition={Partition}:{NewLine}{Dump}",
        subRequest.SubRequestToken,
        partition.Kind,
        Environment.NewLine,
        inspection.ToCanonicalText());
    log.LogInformation("Cell response token={Token} partition={Partition} bytes={Bytes}",
        subRequest.SubRequestToken, partition.Kind, subResponse.SubResponseDataBase64.Length);
}

/// <summary>Maps a SOAP Cell selector to the corresponding FSSHTTPB partition.</summary>
static bool TryResolvePartition(FssHttpSubRequest subRequest, out DocumentPartitionKind partitionKind)
{
    return CellPartitionSelector.TryResolve(subRequest.SubRequestDataAttributes, out partitionKind);
}

static FsshttpbResponse BuildEditorsTableSharePoint13QueryChangesResponse(
    StoredDocument doc,
    DocumentPartition partition,
    ulong requestId)
{
    lock (doc)
    {
        // The editor payload and its knowledge must describe the same state.
        var editors = doc.Sessions.Select(session => new EditorsTableEditor(
            session.ClientId.ToString("D"),
            session.ExpiresUtc.Ticks,
            session.UserName,
            session.UserName,
            HasEditorPermission: session.AsEditor,
            Metadata: session.Metadata));

        return EditorsTablePartitionBuilder.BuildSharePointV13QueryChangesResponse(
            requestId,
            editors,
            partition.FssHttpBIdentity.CellId,
            partition.FssHttpBIdentity.SerialGuid,
            partition.KnowledgeSequence);
    }
}

/// <summary>
/// Handles the SOAP EditorsTable operations that maintain the state returned
/// by the binary editors-table partition. [MS-FSSHTTP] section 3.1.4.8.
/// Successful responses contain an empty SubResponseData element; all input
/// validation failures are returned as protocol errors without that element.
/// </summary>
static void HandleEditorsTableSubRequest(
    DocumentStore store,
    FssHttpRequest fileRequest,
    FssHttpSubRequest subRequest,
    FssHttpSubResponse subResponse,
    ILogger log)
{
    var attrs = subRequest.SubRequestDataAttributes;
    string requestType = attrs.GetValueOrDefault("EditorsTableRequestType", string.Empty);
    if (requestType.Length == 0 ||
        !attrs.TryGetValue("ClientID", out var clientIdText) ||
        !Guid.TryParse(clientIdText, out var clientId))
    {
        subResponse.ErrorCode = "InvalidArgument";
        subResponse.EmitEmptySubResponseData = false;
        return;
    }

    if (string.IsNullOrWhiteSpace(fileRequest.Url))
    {
        subResponse.ErrorCode = "FileNotExistsOrCannotBeCreated";
        return;
    }

    var doc = store.Get(fileRequest.Url)!;

    switch (requestType)
    {
        case "JoinEditingSession":
            if (!TryParseEditorsTimeout(attrs, out var joinTimeout) ||
                !TryParseXmlBoolean(attrs, "AsEditor", out var joinAsEditor))
            {
                subResponse.ErrorCode = "InvalidArgument";
                return;
            }

            doc.JoinEditingSession(clientId, joinTimeout, joinAsEditor);
            log.LogInformation("EditorsTable join: client={Client} asEditor={AsEditor} timeout={Timeout} editors={Count}",
                clientId, joinAsEditor, joinTimeout, doc.Sessions.Count);
            break;

        case "LeaveEditingSession":
            // Leave is intentionally idempotent. SharePoint treats a stale
            // leave as successful, which also makes retries safe for Word.
            doc.LeaveSession(clientId);
            log.LogInformation("EditorsTable leave: client={Client} editors={Count}",
                clientId, doc.Sessions.Count);
            break;

        case "RefreshEditingSession":
            if (!TryParseEditorsTimeout(attrs, out var refreshTimeout) ||
                !TryParseXmlBoolean(attrs, "AsEditor", out var refreshAsEditor))
            {
                subResponse.ErrorCode = "InvalidArgument";
                return;
            }

            if (!doc.RefreshEditingSession(clientId, refreshTimeout, refreshAsEditor))
            {
                subResponse.ErrorCode = "EditorClientIdNotFound";
                return;
            }

            log.LogInformation("EditorsTable refresh: client={Client} timeout={Timeout} editors={Count}",
                clientId, refreshTimeout, doc.Sessions.Count);
            break;

        case "UpdateEditorMetadata":
            if (!TryGetEditorMetadataKey(attrs, out var updateKey) ||
                !TryGetEditorMetadataValue(subRequest, out var updateValue))
            {
                subResponse.ErrorCode = "InvalidArgument";
                return;
            }

            lock (doc)
            {
                var updateSession = doc.GetSession(clientId);
                if (updateSession is null)
                {
                    subResponse.ErrorCode = "EditorClientIdNotFound";
                    return;
                }

                // Check and update atomically when clients send parallel requests.
                if (!updateSession.Metadata.ContainsKey(updateKey) && updateSession.Metadata.Count >= 4)
                {
                    subResponse.ErrorCode = "EditorMetadataQuotaReached";
                    return;
                }

                doc.UpdateEditorMetadata(clientId, updateKey, updateValue);
            }
            log.LogInformation("EditorsTable metadata update: client={Client} key={Key} bytes={Bytes}",
                clientId, updateKey, updateValue.Length);
            break;

        case "RemoveEditorMetadata":
            if (!TryGetEditorMetadataKey(attrs, out var removeKey))
            {
                subResponse.ErrorCode = "InvalidArgument";
                return;
            }

            if (!doc.RemoveEditorMetadata(clientId, removeKey))
            {
                subResponse.ErrorCode = "EditorClientIdNotFound";
                return;
            }

            log.LogInformation("EditorsTable metadata remove: client={Client} key={Key}",
                clientId, removeKey);
            break;

        default:
            subResponse.ErrorCode = "InvalidSubRequest";
            return;
    }

    subResponse.ErrorCode = "Success";
    subResponse.EmitEmptySubResponseData = true;
}

static bool TryParseEditorsTimeout(
    IReadOnlyDictionary<string, string> attrs,
    out int timeoutSeconds)
{
    timeoutSeconds = 0;
    if (!attrs.TryGetValue("Timeout", out var timeoutText) ||
        !int.TryParse(timeoutText, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var requested) ||
        requested < 60 || requested > 120000)
    {
        return false;
    }

    // The specification permits 60..3600 as an input range but requires the
    // server to replace it with its implementation default. Use 3600.
    timeoutSeconds = requested < 3600 ? 3600 : requested;
    return true;
}

static bool TryParseXmlBoolean(
    IReadOnlyDictionary<string, string> attrs,
    string name,
    out bool value)
{
    value = false;
    if (!attrs.TryGetValue(name, out var text))
    {
        return false;
    }

    if (bool.TryParse(text, out value))
    {
        return true;
    }

    if (text == "1")
    {
        value = true;
        return true;
    }

    if (text == "0")
    {
        return true;
    }

    return false;
}

static bool TryGetEditorMetadataKey(
    IReadOnlyDictionary<string, string> attrs,
    out string key)
{
    key = string.Empty;
    if (!attrs.TryGetValue("Key", out var supplied) || string.IsNullOrEmpty(supplied))
    {
        return false;
    }

    if (Encoding.UTF8.GetByteCount(supplied) > 64)
    {
        return false;
    }

    try
    {
        if (System.Xml.XmlConvert.VerifyName(supplied) != supplied)
        {
            return false;
        }
    }
    catch (System.Xml.XmlException)
    {
        return false;
    }

    key = supplied;
    return true;
}

static bool TryGetEditorMetadataValue(FssHttpSubRequest subRequest, out byte[] value)
{
    value = Array.Empty<byte>();
    string? text = null;
    if (subRequest.SubRequestDataXml is not null)
    {
        try
        {
            var element = System.Xml.Linq.XElement.Parse(subRequest.SubRequestDataXml);
            text = element.Value;
        }
        catch (System.Xml.XmlException)
        {
            return false;
        }
    }

    // Value is represented as base64 text by the SOAP schema. Accepting the
    // Value attribute as well keeps the handler compatible with clients that
    // materialize the optional attribute instead of element text.
    if (subRequest.SubRequestDataAttributes.TryGetValue("Value", out var valueAttribute))
    {
        text = valueAttribute;
    }

    if (text is null)
    {
        return false;
    }

    try
    {
        value = Convert.FromBase64String(text.Trim());
    }
    catch (FormatException)
    {
        return false;
    }

    return value.Length <= 1024;
}

/// <summary>
/// Attempts to decode a base64 FSSHTTPB payload from the SubRequestData XML.
/// Returns null when no payload is present or it cannot be decoded.
/// </summary>
static FsshttpbCellRequest? TryDecodeFsshttpbPayload(string subRequestDataXml, ILogger log)
{
    try
    {
        var doc = System.Xml.Linq.XDocument.Parse(subRequestDataXml);
        string? text = doc.Root?.Value;
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        byte[] bytes = Convert.FromBase64String(text.Trim());
        return TryDecodeFsshttpbBinary(bytes, log);
    }
    catch (Exception ex) when (ex is FormatException or InvalidDataException or EndOfStreamException)
    {
        log.LogWarning(ex, "Inline FSSHTTPB payload decode failed");
        return null;
    }
}

static FsshttpbCellRequest? TryDecodeFsshttpbBinary(byte[] payload, ILogger log)
{
    try
    {
        var reader = new BinaryReaderEx(payload);
        var request = FsshttpbCellRequest.Deserialize(reader);
        if (reader.Remaining != 0)
            throw new InvalidDataException($"FSSHTTPB request has {reader.Remaining} trailing bytes.");
        return request;
    }
    catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or ArgumentException)
    {
        log.LogWarning(ex, "Binary FSSHTTPB payload decode failed ({Length} bytes)", payload.Length);
        return null;
    }
}

/// <summary>
/// Handles a Coauth subrequest (join/exit/refresh coauthoring session, etc.).
/// [MS-FSSHTTP] sections 2.3.1.5, 2.3.1.6, 2.3.3.3.
/// </summary>
static void HandleCoauthSubRequest(
    DocumentStore store,
    FssHttpRequest fileRequest,
    FssHttpSubRequest subRequest,
    FssHttpSubResponse subResponse,
    ILogger log)
{
    var doc = store.Get(fileRequest.Url)!;
    var result = FssHttpLockCoordinator.For(doc)
        .ApplyCoauthSession(doc, subRequest, subResponse);
    log.LogInformation("Coauth operation {Operation} for {Url}: result={Result} editors={Count}",
        subRequest.SubRequestDataAttributes.GetValueOrDefault("CoauthRequestType", "<missing>"),
        fileRequest.Url, result, doc.Sessions.Count);
}

/// <summary>
/// Handles a SchemaLock subrequest: grants a shared schema lock.
/// [MS-FSSHTTP] section 2.3.1.13.
/// </summary>
static void HandleSchemaLockSubRequest(
    DocumentStore store,
    FssHttpRequest fileRequest,
    FssHttpSubRequest subRequest,
    FssHttpSubResponse subResponse,
    ILogger log)
{
    var doc = store.Get(fileRequest.Url)!;
    var result = FssHttpLockCoordinator.For(doc)
        .ApplySchemaLock(subRequest, subResponse);
    log.LogInformation("SchemaLock operation {Operation} for {Url}: result={Result}",
        subRequest.SubRequestDataAttributes.GetValueOrDefault("SchemaLockRequestType", "<missing>"),
        fileRequest.Url, result);
}
