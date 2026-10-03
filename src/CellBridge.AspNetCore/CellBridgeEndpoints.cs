using System.Text;
using CellBridge.FssHttp;
using CellBridge.FssHttpB;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace CellBridge.AspNetCore;

public static class CellBridgeEndpoints
{
    public static IServiceCollection AddCellBridge(this IServiceCollection services, StorageProvider provider,
        bool requireDurability = true, bool multipleInstances = false, Action<CellBridgeOptions>? configure = null)
    {
        provider.Require(requireDurability, multipleInstances);
        var options = new CellBridgeOptions();
        configure?.Invoke(options);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxRequestBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxConcurrentRequests);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxMtomParts);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxMtomHeaderBytes);
        if (options.MaxRequestBytes > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(options.MaxRequestBytes));
        services.AddSingleton(new RequestAdmission(options.MaxConcurrentRequests));
        services.AddSingleton(options);
        services.AddSingleton(provider);
        services.AddAuthorization();
        services.TryAddSingleton<ICellBridgeAccessEvaluator, StoredDocumentAccessEvaluator>();
        services.AddSingleton<CellBridgeDocumentService>();
        services.AddHealthChecks().AddCheck<CellBridgeStorageHealthCheck>("cellbridge-storage");
        return services;
    }

    public static IEndpointRouteBuilder MapCellBridge(this IEndpointRouteBuilder app)
    {
        var routes = app.MapGroup("").RequireAuthorization();
        routes.MapMethods("/_vti_bin/cellstorage.svc/{**suffix}", ["OPTIONS"], () => Results.Ok());
        routes.MapMethods("/shared/{fileName}/_vti_bin/cellstorage.svc/{**suffix}", ["OPTIONS"], () => Results.Ok());
        routes.MapMethods("/shared", ["OPTIONS"], (HttpContext context) =>
        {
            var origin = $"{context.Request.Scheme}://{context.Request.Host}";
            context.Response.Headers["X-MSFSSHTTP"] = "1.0";
            context.Response.Headers["X-MS-AuthDomainSupport"] = "False";
            context.Response.Headers["X-FORMS_BASED_AUTH_ACCEPTED"] = "t";
            context.Response.Headers["X-MSGETWEBURL"] = origin + "/shared/";
            context.Response.Headers["Public-Extension"] = "http://schemas.microsoft.com/repl-2";
            context.Response.Headers["Allow"] = "OPTIONS,GET,HEAD,POST";
            return Results.Ok();
        });
        routes.MapPost("/_vti_bin/cellstorage.svc/{**suffix}", (HttpContext context, CellBridgeDocumentService service) => HandleCellStoragePost(context, service));
        routes.MapPost("/shared/{fileName}/_vti_bin/cellstorage.svc/{**suffix}", (HttpContext context, CellBridgeDocumentService service) => HandleCellStoragePost(context, service));
        routes.MapMethods("/shared/{fileName}", ["GET", "HEAD"], async (HttpContext context, CellBridgeDocumentService service) =>
        {
            var escaped = string.Join('/', context.Request.Path.Value!.Split('/').Select(Uri.EscapeDataString));
            var state = await service.Provider.State.FindByPathKeyAsync(StorageIds.PathKey(DocumentStore.NormalizeUrl(escaped)), context.RequestAborted);
            var actor = CellBridgeActor.FromPrincipal(context.User);
            if (actor is null) return Results.Unauthorized();
            if (state is null) return Results.NotFound();
            if (!service.Access(actor, state).HasFlag(DocumentAccess.Read)) return Results.StatusCode(403);
            context.Response.Headers.CacheControl = "private, no-store";
            context.Response.ContentLength = state.Content.Length;
            context.Response.Headers.ETag = state.Etag;
            context.Response.Headers.LastModified = state.ModifiedUtc.ToString("R");
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            var contentTypes = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider();
            context.Response.ContentType = contentTypes.TryGetContentType(state.Path, out var type) ? type : "application/octet-stream";
            if (context.Request.Query["download"] == "true")
                context.Response.Headers.ContentDisposition = new System.Net.Http.Headers.ContentDispositionHeaderValue("attachment")
                { FileNameStar = Path.GetFileName(state.Path) }.ToString();
            if (!HttpMethods.IsHead(context.Request.Method))
            {
                await using var source = await service.Provider.Content.OpenReadAsync(state.Content, context.RequestAborted);
                await source.CopyToAsync(context.Response.Body, context.RequestAborted);
            }
            return Results.Empty;
        });
        return app;
    }

static bool ProtocolRequestAllowed(HttpContext context)
{
    var contentType = context.Request.ContentType?.Split(';')[0].Trim();
    if (contentType is null || !(contentType.Equals("text/xml", StringComparison.OrdinalIgnoreCase) ||
        contentType.Equals("application/soap+xml", StringComparison.OrdinalIgnoreCase) ||
        contentType.Equals("multipart/related", StringComparison.OrdinalIgnoreCase))) return false;
    var origin = context.Request.Headers.Origin.ToString();
    return origin.Length == 0 || string.Equals(origin, $"{context.Request.Scheme}://{context.Request.Host}", StringComparison.OrdinalIgnoreCase);
}

static bool OwnsSession(DocumentState state, FssHttpSubRequest request, CellBridgeActor actor, DateTime now)
{
    if (request.Type is not (SubRequestType.EditorsTable or SubRequestType.Coauth)) return true;
    if (!Guid.TryParse(request.SubRequestDataAttributes.GetValueOrDefault("ClientID"), out var client)) return true;
    var session = state.Editors.FirstOrDefault(e => e.ClientId == client && e.ExpiresUtc > now);
    return session is null || session.Owner?.Subject == actor.Identity.Subject;
}

static async Task<IResult> HandleCellStoragePost(HttpContext ctx, CellBridgeDocumentService service)
{
    var admission = ctx.RequestServices.GetRequiredService<RequestAdmission>();
    if (!await admission.Gate.WaitAsync(0, ctx.RequestAborted)) return Results.StatusCode(503);
    try { return new AdmittedResult(await HandleAdmittedCellStoragePost(ctx, service), admission.Gate); }
    catch { admission.Gate.Release(); throw; }
}

private sealed class AdmittedResult(IResult result, SemaphoreSlim gate) : IResult
{
    public async Task ExecuteAsync(HttpContext context)
    {
        try { await result.ExecuteAsync(context); }
        finally { gate.Release(); }
    }
}

private sealed class RequestAdmission(int maximum)
{
    public SemaphoreSlim Gate { get; } = new(maximum);
}

static async Task<IResult> HandleAdmittedCellStoragePost(HttpContext ctx, CellBridgeDocumentService service)
{
    var actor = CellBridgeActor.FromPrincipal(ctx.User);
    if (actor is null) return Results.Unauthorized();
    if (!ProtocolRequestAllowed(ctx)) return Results.StatusCode(415);
    ctx.Response.Headers.CacheControl = "private, no-store";
    var log = ctx.RequestServices.GetRequiredService<ILogger<CellBridgeDocumentService>>();
    var options = ctx.RequestServices.GetRequiredService<CellBridgeOptions>();
    if (ctx.Request.ContentLength > options.MaxRequestBytes) return Results.StatusCode(413);
    await using var body = new MemoryStream();
    var buffer = new byte[65536];
    int read;
    while ((read = await ctx.Request.Body.ReadAsync(buffer, ctx.RequestAborted)) != 0)
    {
        if (body.Length + read > options.MaxRequestBytes) return Results.StatusCode(413);
        await body.WriteAsync(buffer.AsMemory(0, read), ctx.RequestAborted);
    }
    var rawBody = body.GetBuffer().AsMemory(0, checked((int)body.Length));
    var captureId = Guid.NewGuid().ToString("N");
    if (options.CaptureDirectory is { } captureDirectory)
    {
        Directory.CreateDirectory(captureDirectory);
        await File.WriteAllBytesAsync(Path.Combine(captureDirectory, captureId + ".request.bin"), rawBody, ctx.RequestAborted);
        await File.WriteAllTextAsync(Path.Combine(captureDirectory, captureId + ".request.content-type.txt"), ctx.Request.ContentType ?? "", ctx.RequestAborted);
    }
    string soapXml;
    IReadOnlyList<MtomPart>? parts = null;

    try
    {
        if (ctx.Request.ContentType?.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase) == true)
        {
            parts = MtomMessageParser.ParseViews(rawBody, ctx.Request.ContentType, options.MaxMtomParts, options.MaxMtomHeaderBytes);
            var start = ctx.Request.ContentType.Split(';', StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(p => p.TrimStart().StartsWith("start=", StringComparison.OrdinalIgnoreCase))?
                .Split('=', 2)[1].Trim().Trim('"', '<', '>');
            var soapPart = start is not null
                ? parts.FirstOrDefault(p => string.Equals(p.ContentId, start, StringComparison.OrdinalIgnoreCase))
                : null;
            soapPart ??= parts.FirstOrDefault(p => p.ContentType.Contains("xop+xml", StringComparison.OrdinalIgnoreCase))
                ?? parts[0];
            soapXml = Encoding.UTF8.GetString(soapPart.ContentMemory.Span);
        }
        else
        {
            soapXml = Encoding.UTF8.GetString(rawBody.Span);
        }
    }
    catch (Exception ex) when (ex is InvalidDataException or FormatException)
    {
        log.LogWarning(ex, "Failed to parse MTOM request ({Length} bytes)", rawBody.Length);
        return Results.BadRequest();
    }

    log.LogInformation("FSSHTTP request received: {Length} bytes, action={Action}",
        rawBody.Length, ctx.Request.Headers["SOAPAction"].ToString());

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
            $"id={p.ContentId ?? "<none>"}, type={p.ContentType}, bytes={p.ContentMemory.Length}")));
        foreach (var subRequest in request.Requests.SelectMany(r => r.SubRequests))
        {
            if (subRequest.SubRequestDataAttributes.TryGetValue("IncludeHref", out var href))
            {
                var contentId = href.Trim().Trim('<', '>');
                if (contentId.StartsWith("cid:", StringComparison.OrdinalIgnoreCase))
                {
                    contentId = Uri.UnescapeDataString(contentId[4..]).Trim('<', '>');
                }

                subRequest.SubRequestDataBinaryMemory = parts
                    .FirstOrDefault(p => string.Equals(p.ContentId, contentId, StringComparison.OrdinalIgnoreCase))?.ContentMemory;
                log.LogInformation("MTOM reference {Href} resolved={Resolved} bytes={Bytes}",
                    href, subRequest.SubRequestDataBinaryMemory is not null,
                    subRequest.SubRequestDataBinaryMemory?.Length ?? 0);
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
                log.LogDebug("{Type} SubRequestData {Key}: {Length} characters",
                    subRequest.Type, attr.Key, attr.Value.Length);
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
        var initial = await service.LookupAsync(fileRequest, ctx.RequestAborted);
        var doc = initial is null ? null : StoredDocument.RestoreMetadata(initial, DateTime.UtcNow);
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
        var canRead = service.Access(actor, initial!).HasFlag(DocumentAccess.Read);
        if (canRead) fileRequest.Url = DocumentRequestResolver.CanonicalUrl(doc, $"{ctx.Request.Scheme}://{ctx.Request.Host}");
        else response.VersionErrorCode = "FileUnauthorizedAccess";
        var fileResponse = new FssHttpResponse
        {
            Url = fileRequest.Url,
            RequestToken = fileRequest.RequestToken,
            IntervalOverride = 0,
            ResourceId = canRead ? doc.TransitionId : null,
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

            try
            {
                if (FssHttpDependencies.Error(subRequest, fileResponse.SubResponses) is { } dependencyError)
                {
                    subResponse.ErrorCode = dependencyError;
                    subResponse.HResult = "2147500037";
                    subResponse.EmitEmptySubResponseData = true;
                }
                else if (subRequest.Type == SubRequestType.Cell)
                    await HandleCellSubRequest(service, doc.TransitionId, subRequest, subResponse, log, actor, ctx.RequestAborted);
                else
                    await service.Provider.State.TransitionAsync(doc.TransitionId, (current, now) =>
                    {
                        var access = service.Access(actor, current);
                        if (!access.HasFlag(CellBridgeAuthorization.RequiredAccess(subRequest)) || !OwnsSession(current, subRequest, actor, now))
                        {
                            subResponse.ErrorCode = "FileUnauthorizedAccess";
                            subResponse.HResult = CellBridgeAuthorization.AccessDeniedHResult.ToString();
                            return new StateTransition<bool>(null, false);
                        }
                        var document = StoredDocument.RestoreMetadata(current, now);
                        var coordinator = FssHttpLockCoordinator.Restore(document, current.Coordination, now, actor.Identity);
                        var store = new DocumentStore();
                        store.Attach(document);
                        ApplyMetadata(document, store, fileRequest, subRequest, subResponse, log, $"{ctx.Request.Scheme}://{ctx.Request.Host}", actor);
                        if (subResponse.ErrorCode == "FileUnauthorizedAccess") return new StateTransition<bool>(null, false);
                        var next = document.CaptureCoordination(current, coordinator.Capture());
                        if (System.Text.Json.JsonSerializer.Serialize(next) == System.Text.Json.JsonSerializer.Serialize(current))
                            return new StateTransition<bool>(null, true);
                        next = next with { Coordination = next.Coordination with { Generation = checked(current.Coordination.Generation + 1) } };
                        return new StateTransition<bool>(next, true);
                    }, ctx.RequestAborted);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or KeyNotFoundException)
            {
                log.LogError(ex, "Storage operation failed for resource {ResourceId}", doc.TransitionId);
                subResponse = new FssHttpSubResponse
                {
                    Type = subRequest.Type, SubRequestToken = subRequest.SubRequestToken,
                    ErrorCode = "CellRequestFail", HResult = "2147500037",
                };
            }

            fileResponse.SubResponses.Add(subResponse);
        }

        response.Responses.Add(fileResponse);
    }

    string envelope = response.ToSoapEnvelope();
    if (options.CaptureDirectory is { } summaryDirectory)
        await File.WriteAllTextAsync(Path.Combine(summaryDirectory, captureId + ".summary.json"),
            WireCaptureSummary.Create(request, response));
    log.LogInformation("FSSHTTP response id={CaptureId} chars={Length}", captureId, envelope.Length);

    if (ctx.Request.ContentType?.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase) == true)
    {
        var mtom = response.ToMtomMessage();
        if (options.CaptureDirectory is { } responseCaptureDirectory)
        {
            await File.WriteAllBytesAsync(Path.Combine(responseCaptureDirectory, captureId + ".response.bin"), mtom.Body);
            await File.WriteAllTextAsync(Path.Combine(responseCaptureDirectory, captureId + ".response.content-type.txt"), mtom.ContentType);
        }
        ctx.Response.ContentType = mtom.ContentType;
        ctx.Response.ContentLength = mtom.Body.Length;
        await ctx.Response.Body.WriteAsync(mtom.Body);
        return Results.Empty;
    }

    if (options.CaptureDirectory is { } soapCaptureDirectory)
    {
        await File.WriteAllTextAsync(Path.Combine(soapCaptureDirectory, captureId + ".response.bin"), envelope);
        await File.WriteAllTextAsync(Path.Combine(soapCaptureDirectory, captureId + ".response.content-type.txt"), "text/xml; charset=utf-8");
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
static async Task HandleCellSubRequest(
    CellBridgeDocumentService service,
    Guid resourceId,
    FssHttpSubRequest subRequest,
    FssHttpSubResponse subResponse,
    ILogger log, CellBridgeActor actor, CancellationToken cancellationToken)
{

    if (!TryResolvePartition(subRequest, out var partitionKind))
    {
        subResponse.ErrorCode = "InvalidArgument";
        subResponse.HResult = "2147942487";
        log.LogWarning("Unsupported Cell partition: token={Token} partitionId={PartitionId}",
            subRequest.SubRequestToken,
            subRequest.SubRequestDataAttributes.GetValueOrDefault("PartitionID", "<default>"));
        return;
    }


    // Try to decode the FSSHTTPB request payload if one was provided.
    FsshttpbCellRequest? fsshttpbRequest = null;
    if (subRequest.SubRequestDataAttributes.TryGetValue("IncludeHref", out var href) &&
        href.StartsWith("cid:", StringComparison.OrdinalIgnoreCase))
    {
        // MTOM part reference; the raw binary would arrive as a MIME part.
        // For non-MTOM requests the payload is inline base64 in SubRequestData.
    }

    if (subRequest.SubRequestDataBinaryMemory is not null)
    {
        fsshttpbRequest = TryDecodeFsshttpbBinary(subRequest.SubRequestDataBinaryMemory.Value, log);
    }

    if (fsshttpbRequest is null && subRequest.SubRequestDataXml is not null)
    {
        fsshttpbRequest = TryDecodeFsshttpbPayload(subRequest.SubRequestDataXml, log);
    }

    if (fsshttpbRequest is null)
    {
        subResponse.ErrorCode = "InvalidArgument";
        return;
    }
    var execution = await service.ExecuteAsync(resourceId, partitionKind, fsshttpbRequest,
        subRequest.SubRequestDataAttributes, actor, cancellationToken);
    if (execution.LockError is not null)
    {
        subResponse.ErrorCode = execution.LockError;
        return;
    }
    var fsshttpbResponse = execution.Response;
    if (service.Access(actor, execution.State).HasFlag(DocumentAccess.Read) &&
        string.Equals(subRequest.SubRequestDataAttributes.GetValueOrDefault("GetFileProps"), "true", StringComparison.OrdinalIgnoreCase))
    {
        subResponse.SubResponseDataAttributes["Etag"] = execution.State.Etag;
        subResponse.SubResponseDataAttributes["CreateTime"] = execution.State.CreatedUtc.ToFileTimeUtc().ToString();
        subResponse.SubResponseDataAttributes["LastModifiedTime"] = execution.State.ModifiedUtc.ToFileTimeUtc().ToString();
        subResponse.SubResponseDataAttributes["ModifiedBy"] = execution.State.Security.ModifiedBy?.Login ?? "unknown";
    }
    subResponse.SubResponseDataBase64 = fsshttpbResponse.ToByteArray(
        FsshttpbSerializationProfile.SharePoint13_11);
    if (log.IsEnabled(LogLevel.Debug))
    {
    var inspection = FsshttpbResponseInspector.Inspect(subResponse.SubResponseDataBase64);
    log.LogDebug(
        "Cell response dump token={Token} partition={Partition}:{NewLine}{Dump}",
        subRequest.SubRequestToken,
        partitionKind,
        Environment.NewLine,
        inspection.ToCanonicalText());
    }
    log.LogInformation("Cell response token={Token} partition={Partition} bytes={Bytes}",
        subRequest.SubRequestToken, partitionKind, subResponse.SubResponseDataBase64.Length);
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
            session.Owner?.DisplayName ?? session.UserName,
            session.Owner?.Login ?? session.UserName,
            HasEditorPermission: session.AsEditor && session.Owner is not null && doc.Security.AccessFor(session.Owner.Subject).HasFlag(DocumentAccess.Write),
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
    CellBridgeActor actor,
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

            doc.JoinEditingSession(clientId, joinTimeout, joinAsEditor, actor.Identity.Login, actor.Identity);
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

static FsshttpbCellRequest? TryDecodeFsshttpbBinary(ReadOnlyMemory<byte> payload, ILogger log)
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

static void ApplyMetadata(StoredDocument doc, DocumentStore store, FssHttpRequest fileRequest,
    FssHttpSubRequest subRequest, FssHttpSubResponse subResponse, ILogger log, string publicOrigin, CellBridgeActor actor)
{
            switch (subRequest.Type)
            {
                case SubRequestType.Cell:
                    throw new InvalidOperationException("Cell execution is handled asynchronously.");

                case SubRequestType.Coauth:
                    HandleCoauthSubRequest(store, fileRequest, subRequest, subResponse, log);
                    break;

                case SubRequestType.SchemaLock:
                        HandleSchemaLockSubRequest(store, fileRequest, subRequest, subResponse, log);
                    break;

                case SubRequestType.WhoAmI:
                    subResponse.SubResponseDataAttributes["UserName"] = actor.Identity.DisplayName;
                    subResponse.SubResponseDataAttributes["UserLogin"] = actor.Identity.Login;
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
                        .BuildGetVersions(doc, publicOrigin).SubResponseXml;
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
                    HandleEditorsTableSubRequest(actor, store, fileRequest, subRequest, subResponse, log);
                    break;

                default:
                    // Other subrequest types are not yet implemented; report
                    // a protocol error rather than a malformed success.
                    subResponse.ErrorCode = "NotSupported";
                    break;
            }


}

}
