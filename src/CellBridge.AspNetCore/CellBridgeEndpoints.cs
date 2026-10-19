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
        ArgumentNullException.ThrowIfNull(options.Hashing);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxRequestBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxConcurrentRequests);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxConcurrentRequestsPerSubject);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxCaptureBytes);
        if (options.RequestTimeout <= TimeSpan.Zero || options.RequestTimeout > TimeSpan.FromHours(1))
            throw new ArgumentOutOfRangeException(nameof(options.RequestTimeout));
        CellBridgePublicOrigin.Validate(options.PublicOrigin);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxMtomParts);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxMtomHeaderBytes);
        if (options.MaxRequestBytes > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(options.MaxRequestBytes));
        services.AddSingleton(new ProtocolRequestAdmission(options));
        services.AddSingleton<ProtocolCaptureStore>();
        services.AddSingleton(options);
        services.TryAddSingleton(options.Hashing);
        services.AddSingleton(provider);
        services.AddAuthorization();
        services.TryAddSingleton<ICellBridgeAuthorizationPolicy, StoredGrantAuthorizationPolicy>();
        services.AddSingleton<CellBridgeDocumentService>();
        services.AddSingleton<CellBridgeRequestProcessor>();
        services.AddHealthChecks().AddCheck<CellBridgeStorageHealthCheck>("cellbridge-storage");
        return services;
    }

    public static IEndpointRouteBuilder MapCellBridge(this IEndpointRouteBuilder app)
    {
        var office = CellBridgeOfficeFormsAuthentication.MapCompletion(app);
        var routes = app.MapGroup("").WithMetadata(new CellBridgeOfficeFormsAuthentication.OfficeEndpointMetadata());
        routes.AddEndpointFilter(async (context, next) => CellBridgePublicOrigin.Resolve(context.HttpContext) is null
            ? Results.BadRequest() : await next(context));
        if (office is null) routes.RequireAuthorization();
        else routes.RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = office.CookieScheme });
        routes.MapMethods("/_cellbridge/history/{resourceId:guid}/{generation:long}/{revisionNumber}", ["GET", "HEAD"],
            async (Guid resourceId, long generation, string revisionNumber, HttpContext context, CellBridgeDocumentService service) =>
            {
                if (!ulong.TryParse(revisionNumber, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var number)) return Results.NotFound();
                var actor = CellBridgeActor.FromPrincipal(context.User);
                if (actor is null) return Results.Unauthorized();
                DocumentRevisionSummary revision;
                try { revision = await service.GetRevisionSummaryAsync(resourceId, number, actor, context.RequestAborted); }
                catch (UnauthorizedAccessException) { return Results.NotFound(); }
                catch (KeyNotFoundException) { return Results.NotFound(); }
                if (revision.LifecycleGeneration != generation) return Results.NotFound();
                context.Response.Headers.CacheControl = "private, no-store";
                context.Response.Headers["X-Content-Type-Options"] = "nosniff";
                context.Response.ContentType = "application/octet-stream";
                context.Response.Headers.ContentDisposition = "attachment";
                context.Response.Headers.ContentSecurityPolicy = "sandbox; default-src 'none'";
                context.Response.ContentLength = revision.Content.Length;
                context.Response.Headers.ETag = $"\"{resourceId:D}:{revision.LifecycleGeneration}:{number}\"";
                context.Response.Headers.LastModified = revision.CreatedUtc.ToString("R");
                if (!HttpMethods.IsHead(context.Request.Method))
                {
                    await using var source = await service.Provider.Content.OpenReadAsync(revision.Content, context.RequestAborted);
                    await source.CopyToAsync(context.Response.Body, context.RequestAborted);
                }
                return Results.Empty;
            });
        routes.MapMethods("/_vti_bin/cellstorage.svc/{**suffix}", ["OPTIONS"], (HttpContext context) =>
            DocumentUrlPaths.TryRequestPath(context, out _) ? Results.Ok() : Results.BadRequest());
        routes.MapPost("/_vti_bin/cellstorage.svc/{**suffix}", (HttpContext context, CellBridgeDocumentService service) => HandleCellStoragePost(context, service));
        routes.MapMethods("/shared/{**documentPath}", ["GET", "HEAD", "OPTIONS", "POST"], HandleSharedRequest);
        return app;
    }


private static async Task<IResult> HandleSharedRequest(HttpContext context, CellBridgeDocumentService service)
{
    if (!DocumentUrlPaths.TryRequestPath(context, out var path)) return Results.BadRequest();
    const string marker = "/_vti_bin/cellstorage.svc";
    var protocol = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
    if (protocol >= 0 && (protocol + marker.Length == path.Length || path[protocol + marker.Length] == '/'))
    {
        if (HttpMethods.IsOptions(context.Request.Method)) return Results.Ok();
        return HttpMethods.IsPost(context.Request.Method)
            ? await HandleCellStoragePost(context, service) : Results.NotFound();
    }
    // These names belong to protocol discovery and cannot identify a document.
    if (path.Split('/').Any(s => s.Equals("_vti_bin", StringComparison.OrdinalIgnoreCase) ||
        s.Equals("_vti_inf.html", StringComparison.OrdinalIgnoreCase) || s.Equals("_cellbridge", StringComparison.OrdinalIgnoreCase)))
        return Results.NotFound();
    if (HttpMethods.IsOptions(context.Request.Method)) return Discovery(context);
    if (HttpMethods.IsPost(context.Request.Method)) return Results.NotFound();
    var actor = CellBridgeActor.FromPrincipal(context.User);
    if (actor is null) return Results.Unauthorized();
    DocumentReadMetadata? state;
    try { state = await service.FindDocumentMetadataByPathKeyAsync(StorageIds.PathKey(path), actor, context.RequestAborted); }
    catch (UnauthorizedAccessException) { return Results.NotFound(); }
    if (state is null) return Results.NotFound();
    context.Response.Headers.CacheControl = "private, no-store";
    context.Response.ContentLength = state.Content.Length;
    context.Response.Headers.ETag = state.Etag;
    context.Response.Headers.LastModified = state.ModifiedUtc.ToString("R");
    DocumentDelivery.Apply(context, state.Path);
    if (!HttpMethods.IsHead(context.Request.Method))
    {
        await using var source = await service.Provider.Content.OpenReadAsync(state.Content, context.RequestAborted);
        await source.CopyToAsync(context.Response.Body, context.RequestAborted);
    }
    return Results.Empty;

}

private static string PublicOrigin(HttpContext context) => CellBridgePublicOrigin.Resolve(context)
    ?? throw new InvalidOperationException("Configure PublicOrigin or restrict AllowedHosts before advertising protocol URLs.");

private static IResult Discovery(HttpContext context)
{
    context.Response.Headers["X-MSFSSHTTP"] = "1.0";
    context.Response.Headers["X-MS-AuthDomainSupport"] = "False";
    context.Response.Headers["X-FORMS_BASED_AUTH_ACCEPTED"] = "t";
    context.Response.Headers["X-MSGETWEBURL"] = PublicOrigin(context) + context.Request.PathBase.ToUriComponent() + "/shared/";
    context.Response.Headers["Public-Extension"] = "http://schemas.microsoft.com/repl-2";
    context.Response.Headers["Allow"] = "OPTIONS,GET,HEAD,POST";
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
    if (!DocumentUrlPaths.TryRequestPath(ctx, out _)) return Results.BadRequest();
    var actor = CellBridgeActor.FromPrincipal(ctx.User);
    if (actor is null) return Results.Unauthorized();
    var admission = ctx.RequestServices.GetRequiredService<ProtocolRequestAdmission>();
    var lease = admission.TryAcquire(actor.Identity.Subject, out var rejection);
    if (lease is null) return Results.StatusCode(rejection);
    var originalToken = ctx.RequestAborted;
    var deadline = CancellationTokenSource.CreateLinkedTokenSource(originalToken);
    deadline.CancelAfter(ctx.RequestServices.GetRequiredService<CellBridgeOptions>().RequestTimeout);
    ctx.RequestAborted = deadline.Token;
    try
    {
        IResult result;
        try { result = await HandleAdmittedCellStoragePost(ctx, service); }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !originalToken.IsCancellationRequested)
        {
            if (ctx.Response.HasStarted) { ctx.Abort(); result = Results.Empty; }
            else result = Results.StatusCode(408);
        }
        return new AdmittedResult(result, lease, deadline, originalToken);
    }
    catch
    {
        ctx.RequestAborted = originalToken;
        deadline.Dispose();
        lease.Dispose();
        throw;
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
        await captures.WriteTextAsync(captureId, ".response.content-type.txt", "text/xml; charset=utf-8", ctx.RequestAborted);
    }
    return new SoapResponseResult(envelope);
}

private sealed class SoapResponseResult(string envelope) : IResult
{
    public async Task ExecuteAsync(HttpContext context)
    {
        context.Response.ContentType = "text/xml; charset=utf-8";
        context.Response.ContentLength = Encoding.UTF8.GetByteCount(envelope);
        await context.Response.WriteAsync(envelope, Encoding.UTF8, context.RequestAborted);
    }
}

}
