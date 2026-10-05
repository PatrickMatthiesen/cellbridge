using System.Text;
using CellBridge.FssHttp;
using CellBridge.FssHttpB;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
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
        services.AddSingleton<CellBridgeRequestProcessor>();
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

    var execution = await ctx.RequestServices.GetRequiredService<CellBridgeRequestProcessor>()
        .ExecuteAsync(request, $"{ctx.Request.Scheme}://{ctx.Request.Host}", actor, ctx.RequestAborted);
    var response = execution.Response;

    if (options.CaptureDirectory is { } summaryDirectory)
        await File.WriteAllTextAsync(Path.Combine(summaryDirectory, captureId + ".summary.json"),
            WireCaptureSummary.Create(request, response), ctx.RequestAborted);

    if (ctx.Request.ContentType?.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase) == true)
    {
        var mtom = response.PrepareMtomMessage();
        if (options.CaptureDirectory is { } responseCaptureDirectory)
        {
            await using (var capture = new FileStream(Path.Combine(responseCaptureDirectory, captureId + ".response.bin"),
                FileMode.Create, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
                await mtom.WriteToAsync(capture, ctx.RequestAborted);
            await File.WriteAllTextAsync(Path.Combine(responseCaptureDirectory, captureId + ".response.content-type.txt"), mtom.ContentType, ctx.RequestAborted);
        }
        ctx.Response.ContentType = mtom.ContentType;
        ctx.Response.ContentLength = mtom.ContentLength;
        log.LogInformation("FSSHTTP response id={CaptureId} bytes={Length}", captureId, mtom.ContentLength);
        await mtom.WriteToAsync(ctx.Response.Body, ctx.RequestAborted);
        return Results.Empty;
    }

    string envelope = response.ToSoapEnvelope();
    log.LogInformation("FSSHTTP response id={CaptureId} chars={Length}", captureId, envelope.Length);
    if (options.CaptureDirectory is { } soapCaptureDirectory)
    {
        await File.WriteAllTextAsync(Path.Combine(soapCaptureDirectory, captureId + ".response.bin"), envelope);
        await File.WriteAllTextAsync(Path.Combine(soapCaptureDirectory, captureId + ".response.content-type.txt"), "text/xml; charset=utf-8");
    }
    return Results.Content(envelope, "text/xml; charset=utf-8");
}

}
