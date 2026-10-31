using System.Text;
using CellBridge.FssHttp;
using CellBridge.FssHttpB;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace CellBridge.AspNetCore;

public static class CellBridgeEndpoints
{
    /// <summary>Registers ordinary operation-time external authority. Cross-operation caching is opt-in.</summary>
    /// <remarks>The singleton provider can use an IDbContextFactory or another async host service.
    /// Existing native/versioned bindings require explicit migration before this mode can use them.</remarks>
    public static IServiceCollection AddCellBridgeWithExternalAuthorization<TProvider>(this IServiceCollection services,
        StorageProvider provider, bool requireDurability = true, bool multipleInstances = false,
        Action<CellBridgeOptions>? configure = null) where TProvider : class, IDocumentAuthorizationProvider
    {
        if (services.Any(x => !x.IsKeyedService && (x.ServiceType == typeof(CellBridgeOptions) ||
            x.ServiceType == typeof(ICellBridgeAuthorizationPolicy) || x.ServiceType == typeof(IDocumentAuthorizationProvider))))
            throw new InvalidOperationException("Select one document authority in the first CellBridge registration.");
        CellBridgeHostingRegistration.RequireSingleton<TProvider>(services);
        services.TryAddSingleton<TProvider>();
        services.AddSingleton<IDocumentAuthorizationProvider>(sp => sp.GetRequiredService<TProvider>());
        return services.AddCellBridge(provider, requireDurability, multipleInstances, configure);
    }

    /// <summary>Registers CellBridge with a shared singleton host document-permission policy.</summary>
    /// <remarks>Call this overload instead of AddCellBridge when selecting a custom policy.
    /// An existing singleton registration for the concrete policy is reused.</remarks>
    public static IServiceCollection AddCellBridge<TAuthorizationPolicy>(this IServiceCollection services,
        StorageProvider provider, bool requireDurability = true, bool multipleInstances = false,
        Action<CellBridgeOptions>? configure = null) where TAuthorizationPolicy : class, ICellBridgeAuthorizationPolicy
    {
        if (services.Any(x => !x.IsKeyedService && (x.ServiceType == typeof(CellBridgeOptions) ||
            x.ServiceType == typeof(ICellBridgeAuthorizationPolicy))))
            throw new InvalidOperationException("Select the custom policy with the first AddCellBridge registration; do not register a competing policy.");
        CellBridgeHostingRegistration.RequireSingleton<TAuthorizationPolicy>(services);
        services.TryAddSingleton<TAuthorizationPolicy>();
        services.AddSingleton<ICellBridgeAuthorizationPolicy>(sp => sp.GetRequiredService<TAuthorizationPolicy>());
        return services.AddCellBridge(provider, requireDurability, multipleInstances, configure);
    }

    public static IServiceCollection AddCellBridge(this IServiceCollection services, StorageProvider provider,
        bool requireDurability = true, bool multipleInstances = false, Action<CellBridgeOptions>? configure = null)
    {
        provider.Require(requireDurability, multipleInstances);
        var options = new CellBridgeOptions();
        configure?.Invoke(options);
        if (options.EnableOfficeRealTime && multipleInstances)
            throw new NotSupportedException("Office RTC requires a single host. Set EnableOfficeRealTime to false when using multiple instances.");
        ArgumentNullException.ThrowIfNull(options.Hashing);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxRequestBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxConcurrentRequests);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxConcurrentRequestsPerSubject);
        ArgumentOutOfRangeException.ThrowIfNegative(options.MaxQueuedRequests);
        ArgumentOutOfRangeException.ThrowIfNegative(options.MaxQueuedRequestsPerSubject);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxCaptureBytes);
        if (options.RequestTimeout <= TimeSpan.Zero || options.RequestTimeout > TimeSpan.FromHours(1))
            throw new ArgumentOutOfRangeException(nameof(options.RequestTimeout));
        CellBridgePublicOrigin.Validate(options.PublicOrigin);
        if (options.DocumentLibraryId == Guid.Empty)
            throw new ArgumentException("DocumentLibraryId must be a stable, nonempty catalog GUID.", nameof(configure));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxMtomParts);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxMtomHeaderBytes);
        if (options.MaxRequestBytes > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(options.MaxRequestBytes));
        services.AddSingleton<ProtocolRequestAdmission>();
        services.AddSingleton<ProtocolCaptureStore>();
        services.AddSingleton(options);
        services.TryAddSingleton(options.Hashing);
        services.AddSingleton(provider);
        services.AddAuthorization();
        services.TryAddSingleton<ICellBridgeAuthorizationPolicy, StoredGrantAuthorizationPolicy>();
        services.AddSingleton<CellBridgeDocumentService>();
        services.AddSingleton<CellBridgeRequestProcessor>();
        if (options.EnableOfficeRealTime) services.AddOfficeRealTime();
        services.AddHealthChecks().AddCheck<CellBridgeStorageHealthCheck>("cellbridge-storage");
        return services;
    }

    public static IEndpointRouteBuilder MapCellBridge(this IEndpointRouteBuilder app)
    {
        CellBridgeLogin.MapLogin(app);
        var office = CellBridgeOfficeFormsAuthentication.MapCompletion(app);
        app.MapOfficeRealTime();
        var routes = app.MapGroup("").WithMetadata(new CellBridgeOfficeFormsAuthentication.OfficeEndpointMetadata());
        routes.AddEndpointFilter(async (context, next) => CellBridgePublicOrigin.Resolve(context.HttpContext) is null
            ? Results.BadRequest() : await next(context));
        if (office is null) routes.RequireAuthorization();
        else routes.RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = office.ProtocolAuthenticationScheme });
        routes.MapMethods("/", ["OPTIONS"], (HttpContext context) => Discovery(context));
        routes.MapSharedAccess();
        routes.MapGet("/_cellbridge/history/settings", (HttpContext context, CellBridgeDocumentService service, CellBridgeOptions options) =>
        {
            if (options.DocumentLibraryId is null) return Results.NotFound();
            context.Response.Headers.CacheControl = "private, no-store";
            context.Response.Headers.ContentSecurityPolicy = "default-src 'none'; frame-ancestors 'self'";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            return Results.Content($"""
                <!doctype html><html lang="en"><meta charset="utf-8"><title>Version history settings</title>
                <h1>Version history settings</h1>
                <p>A document can have up to {service.Provider.Limits.MaxHistoryRevisions} saved revisions.</p>
                <p>The server rejects a publication that would exceed this limit. It does not automatically delete older revisions.</p>
                <p>These settings are managed by the server administrator.</p></html>
                """, "text/html; charset=utf-8");
        });
        routes.MapMethods("/_cellbridge/history/{resourceId:guid}/{generation:long}/{revisionNumber}", ["GET", "HEAD"],
            async (Guid resourceId, long generation, string revisionNumber, HttpContext context, CellBridgeDocumentService service) =>
            {
                if (!ulong.TryParse(revisionNumber, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var number)) return Results.NotFound();
                var actor = CellBridgeActor.FromPrincipal(context.User);
                if (actor is null) return Results.Unauthorized();
                return HttpMethods.IsHead(context.Request.Method)
                    ? await DownloadAsync()
                    : await HandleAdmittedRequest(context, actor, DownloadAsync);

                async Task<IResult> DownloadAsync()
                {
                    DocumentPackage package;
                    DateTime modifiedUtc;
                    Stream? content = null;
                    try
                    {
                        if (HttpMethods.IsHead(context.Request.Method))
                        {
                            var revision = await service.GetRevisionSummaryAsync(resourceId, number, actor, context.RequestAborted);
                            package = revision.Package;
                            modifiedUtc = revision.CreatedUtc;
                        }
                        else
                        {
                            var download = await service.OpenDocumentAsync(new DocumentIdentity.Resource(resourceId), number, actor, context.RequestAborted);
                            package = download.Package;
                            modifiedUtc = download.ModifiedUtc;
                            content = download.Content;
                        }
                    }
                    catch (UnauthorizedAccessException) { return Results.NotFound(); }
                    catch (KeyNotFoundException) { return Results.NotFound(); }
                    try
                    {
                        if (package.LifecycleGeneration != generation) return Results.NotFound();
                        context.Response.Headers.CacheControl = "private, no-store";
                        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
                        context.Response.ContentType = "application/octet-stream";
                        context.Response.Headers.ContentDisposition = "attachment";
                        context.Response.Headers.ContentSecurityPolicy = "sandbox; default-src 'none'";
                        context.Response.ContentLength = package.Length;
                        context.Response.Headers.ETag = $"\"{resourceId:D}:{package.LifecycleGeneration}:{number}\"";
                        context.Response.Headers.LastModified = modifiedUtc.ToString("R");
                        if (content is not null) await content.CopyToAsync(context.Response.Body, context.RequestAborted);
                    }
                    finally { if (content is not null) await content.DisposeAsync(); }
                    return Results.Empty;
                }
            });
        routes.MapMethods("/_vti_bin/cellstorage.svc/{**suffix}", ["OPTIONS"], (HttpContext context) =>
            HttpDocumentPath.TryRequestPath(context, out _) ? Discovery(context, serviceEndpoint: true) : Results.BadRequest());
        routes.MapPost("/_vti_bin/cellstorage.svc/{**suffix}", (HttpContext context, CellBridgeDocumentService service) => HandleCellStoragePost(context, service));
        routes.MapMethods("/shared/{**documentPath}",
            app.ServiceProvider.GetRequiredService<CellBridgeOptions>().EnableOfficeRealTime
                ? ["GET", "HEAD", "OPTIONS", "POST", "PROPFIND"]
                : ["GET", "HEAD", "OPTIONS", "POST"], HandleSharedRequest);
        return app;
    }


private static async Task<IResult> HandleSharedRequest(HttpContext context, CellBridgeDocumentService service)
{
    if (!HttpDocumentPath.TryRequestPath(context, out var path)) return Results.BadRequest();
    const string marker = "/_vti_bin/cellstorage.svc";
    var protocol = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
    if (protocol >= 0 && (protocol + marker.Length == path.Length || path[protocol + marker.Length] == '/'))
    {
        if (HttpMethods.IsOptions(context.Request.Method)) return Discovery(context, serviceEndpoint: true);
        return HttpMethods.IsPost(context.Request.Method)
            ? await HandleCellStoragePost(context, service) : Results.NotFound();
    }
    if (path.EndsWith("/_vti_bin/sharedaccess.asmx", StringComparison.OrdinalIgnoreCase))
    {
        if (!HttpMethods.IsPost(context.Request.Method)) return Results.NotFound();
        await SharedAccessEndpoint.HandleAsync(context, service);
        return Results.Empty;
    }
    // These names belong to protocol discovery and cannot identify a document.
    if (path.Split('/').Any(s => s.Equals("_vti_bin", StringComparison.OrdinalIgnoreCase) ||
        s.Equals("_vti_inf.html", StringComparison.OrdinalIgnoreCase) || s.Equals("_cellbridge", StringComparison.OrdinalIgnoreCase)))
        return Results.NotFound();
    if (HttpMethods.IsOptions(context.Request.Method)) return Discovery(context);
    if (HttpMethods.IsPost(context.Request.Method)) return Results.NotFound();
    var actor = CellBridgeActor.FromPrincipal(context.User);
    if (actor is null) return Results.Unauthorized();
    if (context.Request.Method.Equals("PROPFIND", StringComparison.OrdinalIgnoreCase))
        return await HandleAdmittedRequest(context, actor,
            () => OfficeDocumentDiscovery.PropFindAsync(context, service, path, actor));
    return HttpMethods.IsHead(context.Request.Method)
        ? await HandleSharedDownload(context, service, path, actor)
        : await HandleAdmittedRequest(context, actor, () => HandleSharedDownload(context, service, path, actor));
}

private static async Task<IResult> HandleSharedDownload(HttpContext context, CellBridgeDocumentService service,
    string path, CellBridgeActor actor)
{
    DocumentHead state;
    DocumentPackage package;
    Stream? content = null;
    try
    {
        if (HttpMethods.IsHead(context.Request.Method))
        {
            var metadata = await service.FindDocumentMetadataByPathKeyAsync(StorageIds.PathKey(path), actor, context.RequestAborted);
            if (metadata is null) return Results.NotFound();
            state = metadata;
            package = metadata.Package;
        }
        else
        {
            var download = await service.OpenDocumentAsync(new DocumentIdentity.Path(StorageIds.PathKey(path)), null, actor, context.RequestAborted);
            state = download.Current;
            package = download.Package;
            content = download.Content;
        }
    }
    catch (UnauthorizedAccessException) { return Results.NotFound(); }
    catch (KeyNotFoundException) { return Results.NotFound(); }
    try
    {
        context.Response.Headers.CacheControl = "private, no-store";
        context.Response.ContentLength = package.Length;
        context.Response.Headers.ETag = state.Etag;
        context.Response.Headers.LastModified = state.ModifiedUtc.ToString("R");
        DocumentDelivery.Apply(context, state.Path);
        if (content is not null) await content.CopyToAsync(context.Response.Body, context.RequestAborted);
    }
    finally { if (content is not null) await content.DisposeAsync(); }
    return Results.Empty;

}

private static string PublicOrigin(HttpContext context) => CellBridgePublicOrigin.Resolve(context)
    ?? throw new InvalidOperationException("Configure PublicOrigin or restrict AllowedHosts before advertising protocol URLs.");

private static IResult Discovery(HttpContext context, bool serviceEndpoint = false)
{
    // MS-FSSHTTP 3.1.4.10 requires at least 1.1 for GetVersions.
    // Microsoft 365's observed native history workflow uses discovery 1.5.
    context.Response.Headers["X-MSFSSHTTP"] = "1.5";
    // Opt-in RTC storage discovery marker observed in native OneDrive traffic.
    if (context.RequestServices.GetRequiredService<CellBridgeOptions>().EnableOfficeRealTime)
    {
        context.Response.Headers["MS-Storage"] = "1";
        // Native Office uses DAV metadata discovery alongside FSSHTTP authoring.
        context.Response.Headers["MS-Author-Via"] = "DAV";
        // Current Office uses this observed server-classification marker before
        // requesting RTC properties. Identify this host rather than a cloud node.
        context.Response.Headers["X-MSNSERVER"] = "CellBridge";
        var rtcMethods = serviceEndpoint ? "OPTIONS,POST" : "OPTIONS,GET,HEAD,POST,PROPFIND";
        context.Response.Headers["Allow"] = rtcMethods;
        context.Response.Headers["Public"] = rtcMethods;
        return Results.Ok();
    }
    context.Response.Headers["X-MS-AuthDomainSupport"] = "False";
    context.Response.Headers["X-FORMS_BASED_AUTH_ACCEPTED"] = "t";
    context.Response.Headers["X-MSGETWEBURL"] = PublicOrigin(context) + context.Request.PathBase.ToUriComponent() + "/shared/";
    context.Response.Headers["Public-Extension"] = "http://schemas.microsoft.com/repl-2";
    var methods = serviceEndpoint ? "OPTIONS,POST"
        : context.RequestServices.GetRequiredService<CellBridgeOptions>().EnableOfficeRealTime
            ? "OPTIONS,GET,HEAD,POST,PROPFIND" : "OPTIONS,GET,HEAD,POST";
    context.Response.Headers["Allow"] = methods;
    context.Response.Headers["Public"] = methods;
    return Results.Ok();
}

static bool ProtocolRequestAllowed(HttpContext context)
{
    var contentType = context.Request.ContentType?.Split(';')[0].Trim();
    if (contentType is null || !(contentType.Equals("text/xml", StringComparison.OrdinalIgnoreCase) ||
        contentType.Equals("application/soap+xml", StringComparison.OrdinalIgnoreCase) ||
        contentType.Equals("multipart/related", StringComparison.OrdinalIgnoreCase))) return false;
    var origin = context.Request.Headers.Origin.ToString();
    return origin.Length == 0 || string.Equals(origin, PublicOrigin(context), StringComparison.OrdinalIgnoreCase);
}

static async Task<IResult> HandleCellStoragePost(HttpContext ctx, CellBridgeDocumentService service)
{
    if (!HttpDocumentPath.TryRequestPath(ctx, out _)) return Results.BadRequest();
    var actor = CellBridgeActor.FromPrincipal(ctx.User);
    if (actor is null) return Results.Unauthorized();
    return await HandleAdmittedRequest(ctx, actor, () => HandleAdmittedCellStoragePost(ctx, service));
}

static async Task<IResult> HandleAdmittedRequest(HttpContext ctx, CellBridgeActor actor, Func<Task<IResult>> handle)
{
    var admission = ctx.RequestServices.GetRequiredService<ProtocolRequestAdmission>();
    var originalToken = ctx.RequestAborted;
    var deadline = CancellationTokenSource.CreateLinkedTokenSource(originalToken);
    deadline.CancelAfter(ctx.RequestServices.GetRequiredService<CellBridgeOptions>().RequestTimeout);
    ctx.RequestAborted = deadline.Token;
    ProtocolRequestAdmission.Lease? lease = null;
    bool transferred = false;
    try
    {
        var admitted = await admission.AcquireAsync(actor.Identity.Subject, deadline.Token);
        lease = admitted.Lease;
        if (lease is null)
        {
            ctx.Response.Headers.RetryAfter = "1";
            return Results.StatusCode(admitted.RejectionStatus);
        }
        var result = await handle();
        transferred = true;
        return new AdmittedResult(result, lease, deadline, originalToken);
    }
    catch (OperationCanceledException) when (deadline.IsCancellationRequested && !originalToken.IsCancellationRequested)
    {
        if (ctx.Response.HasStarted) { ctx.Abort(); return Results.Empty; }
        ctx.Response.Clear();
        return Results.StatusCode(408);
    }
    finally
    {
        if (!transferred)
        {
            ctx.RequestAborted = originalToken;
            deadline.Dispose();
            lease?.Dispose();
        }
    }
}

private sealed class AdmittedResult(IResult result, ProtocolRequestAdmission.Lease lease,
    CancellationTokenSource deadline, CancellationToken originalToken) : IResult
{
    public async Task ExecuteAsync(HttpContext context)
    {
        try { await result.ExecuteAsync(context); }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !originalToken.IsCancellationRequested)
        {
            if (context.Response.HasStarted) context.Abort();
            else { context.Response.Clear(); context.Response.StatusCode = 408; }
        }
        finally
        {
            context.RequestAborted = originalToken;
            deadline.Dispose();
            lease.Dispose();
        }
    }
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
    ReadOnlyMemory<byte> rawBody;
    try { rawBody = await ProtocolRequestBody.ReadAsync(ctx.Request.Body, options.MaxRequestBytes, ctx.RequestAborted); }
    catch (ProtocolRequestBody.RequestBodyLimitException) { return Results.StatusCode(413); }
    var captureId = Guid.NewGuid().ToString("N");
    var captures = ctx.RequestServices.GetRequiredService<ProtocolCaptureStore>();
    if (options.CaptureDirectory is not null)
    {
        await captures.WriteAsync(captureId, ".request.bin", rawBody, ctx.RequestAborted);
        await captures.WriteTextAsync(captureId, ".request.content-type.txt", ctx.Request.ContentType ?? "", ctx.RequestAborted);
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
        rawBody.Length, ProtocolLogValues.Path(ctx.Request.Headers["SOAPAction"].ToString().Trim('"')));

    CellStorageRequest request;
    try
    {
        request = CellStorageRequestParser.Parse(soapXml);
    }
    catch (Exception ex)
    {
        log.LogWarning("Failed to parse FSSHTTP request ({ErrorType})", ex.GetType().Name);
        return Results.BadRequest();
    }

    if (parts is not null)
    {
        log.LogInformation("MTOM request contains {PartCount} parts", parts.Count);
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
                log.LogInformation("MTOM reference resolved={Resolved} bytes={Bytes}",
                    subRequest.SubRequestDataBinaryMemory is not null,
                    subRequest.SubRequestDataBinaryMemory?.Length ?? 0);
            }
        }
    }

    // Log every decoded request (success criterion 5).
    foreach (var fileRequest in request.Requests)
    {
        log.LogInformation("Request url={Url} token={Token} subrequests={Subrequests}",
            ProtocolLogValues.Path(fileRequest.Url),
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

    var execution = await ctx.RequestServices.GetRequiredService<CellBridgeRequestProcessor>()
        .ExecuteAsync(request, PublicOrigin(ctx), actor, ctx.RequestAborted, ctx.Request.PathBase.Value ?? "");
    var response = execution.Response;

    if (options.CaptureDirectory is not null)
        await captures.WriteTextAsync(captureId, ".summary.json",
            WireCaptureSummary.Create(request, response), ctx.RequestAborted);

    if (ctx.Request.ContentType?.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase) == true)
    {
        var mtom = response.PrepareMtomMessage();
        if (options.CaptureDirectory is not null)
        {
            await captures.WriteStreamingAsync(captureId, ".response.bin", mtom.ContentLength,
                stream => mtom.WriteToAsync(stream, ctx.RequestAborted).AsTask(), ctx.RequestAborted);
            await captures.WriteTextAsync(captureId, ".response.content-type.txt", mtom.ContentType, ctx.RequestAborted);
        }
        ctx.Response.ContentType = mtom.ContentType;
        ctx.Response.ContentLength = mtom.ContentLength;
        log.LogInformation("FSSHTTP response id={CaptureId} bytes={Length}", captureId, mtom.ContentLength);
        await mtom.WriteToAsync(ctx.Response.Body, ctx.RequestAborted);
        return Results.Empty;
    }

    string envelope = response.ToSoapEnvelope();
    log.LogInformation("FSSHTTP response id={CaptureId} chars={Length}", captureId, envelope.Length);
    if (options.CaptureDirectory is not null)
    {
        await captures.WriteTextAsync(captureId, ".response.bin", envelope, ctx.RequestAborted);
        await captures.WriteTextAsync(captureId, ".response.content-type.txt", response.SoapContentType, ctx.RequestAborted);
    }
    return new SoapResponseResult(envelope, response.SoapContentType);
}

private sealed class SoapResponseResult(string envelope, string contentType) : IResult
{
    public async Task ExecuteAsync(HttpContext context)
    {
        context.Response.ContentType = contentType;
        context.Response.ContentLength = Encoding.UTF8.GetByteCount(envelope);
        await context.Response.WriteAsync(envelope, Encoding.UTF8, context.RequestAborted);
    }
}

}
