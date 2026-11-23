using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text;
using System.Xml;
using CellBridge.Client;
using CellBridge.FssHttp;
using CellBridge.Storage.Abstractions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CellBridge.AspNetCore;

/// <summary>The canonical identity and URL of an authorized document.</summary>
public sealed record CellBridgeResolvedDocument(Guid ResourceId, long LifecycleGeneration, string Path, string CanonicalUrl);

/// <summary>Admits trusted in-process callers to the same protocol engine and request limits as the HTTP host.</summary>
public sealed partial class CellBridgeInProcessHost
{
    private readonly IServiceProvider services;
    private readonly CellBridgeOptions options;
    private readonly ProtocolRequestAdmission admission;
    private readonly CellBridgeDocumentService documents;
    private readonly CellBridgeRequestProcessor processor;
    private readonly ProtocolCaptureStore captures;
    private readonly ILogger<CellBridgeInProcessHost> logger;

    internal CellBridgeInProcessHost(IServiceProvider services)
    {
        this.services = services;
        options = services.GetRequiredService<CellBridgeOptions>();
        admission = services.GetRequiredService<ProtocolRequestAdmission>();
        documents = services.GetRequiredService<CellBridgeDocumentService>();
        processor = services.GetRequiredService<CellBridgeRequestProcessor>();
        captures = services.GetRequiredService<ProtocolCaptureStore>();
        logger = services.GetRequiredService<ILogger<CellBridgeInProcessHost>>();
    }

    /// <summary>Resolves the origin this host would use to advertise protocol URLs for the HTTP request.</summary>
    public static string? ResolvePublicOrigin(HttpContext context) => CellBridgePublicOrigin.Resolve(context);

    /// <summary>Executes a request with an authenticated host actor. Host rejections carry an HTTP status code.</summary>
    public Task<CellStorageExecution> ExecuteAsync(CellStorageRequest request, CellBridgeActor actor, string publicOrigin,
        string applicationPathBase = "", CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RunAdmittedAsync(actor, publicOrigin, applicationPathBase, (origin, _, token) =>
            ExecuteAdmittedAsync(request, actor, origin, applicationPathBase, token), cancellationToken);
    }

    /// <summary>Resolves a resource ID or normalized storage path key, and checks the requested access.</summary>
    /// <remarks>Returns null when the document is missing or access is denied and download concealment is enabled.
    /// Other denials throw an HTTP status exception with status 403.</remarks>
    public Task<CellBridgeResolvedDocument?> ResolveDocumentAsync(DocumentIdentity identity, CellBridgeActor actor,
        string publicOrigin, string applicationPathBase = "", DocumentAccess requiredAccess = DocumentAccess.Read,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (requiredAccess is not (DocumentAccess.Read or DocumentAccess.Write or (DocumentAccess.Read | DocumentAccess.Write)))
            throw new ArgumentOutOfRangeException(nameof(requiredAccess));
        return RunAdmittedAsync<CellBridgeResolvedDocument?>(actor, publicOrigin, applicationPathBase,
            async (_, publicBase, token) =>
            {
                var observed = await documents.Provider.Documents.FindAsync(identity, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (observed is null || observed.IsDeleted) return null;
                var authorized = await documents.AuthorizeActorAsync(actor, observed, requiredAccess, token, fresh: true)
                    .ConfigureAwait(false);
                var current = await documents.RevalidateReadStateAsync(observed, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (current is null || current.IsDeleted || current.LifecycleGeneration != observed.LifecycleGeneration) return null;
                if (!documents.Access(authorized, current).HasFlag(requiredAccess))
                {
                    if (options.ConcealUnauthorizedDownloads) return null;
                    throw Rejected(HttpStatusCode.Forbidden, "Document access denied.");
                }
                return new(current.ResourceId, current.LifecycleGeneration, current.Path,
                    DocumentRequestResolver.CanonicalUrl(current, publicBase));
            }, cancellationToken);
    }

    /// <summary>Creates an actor-bound connector. When Office RTC is unavailable the connector returns null.</summary>
    public ICellBridgeRealTimeConnector CreateRealTimeConnector(CellBridgeActor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return new InProcessOfficeRealTimeConnector(this, actor);
    }

    internal bool TryResolveOrigin(string? candidate, out string origin)
    {
        origin = "";
        if (string.IsNullOrWhiteSpace(candidate) || candidate != candidate.Trim() || candidate.Contains('\\') ||
            !Uri.TryCreate(candidate, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            uri.AbsolutePath != "/" || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            return false;

        var authorityStart = candidate.IndexOf("://", StringComparison.Ordinal) + 3;
        if (authorityStart < 3 || candidate.AsSpan(authorityStart).TrimEnd('/').Contains('/'))
            return false;

        // Preserve the original host text and port, as the HTTP origin resolver does.
        var host = new HostString(candidate[authorityStart..].TrimEnd('/'));
        var canonical = CellBridgePublicOrigin.Resolve(services, uri.Scheme, host);
        if (canonical is null || !Uri.TryCreate(canonical, UriKind.Absolute, out var parsed) || parsed != uri)
            return false;
        origin = canonical;
        return true;
    }

    internal bool TryGetRealTimeServices([NotNullWhen(true)] out OfficeRealTimeAccess? access,
        [NotNullWhen(true)] out IOfficeRealTimeSessionStore? sessions)
    {
        access = null;
        sessions = null;
        if (!options.EnableOfficeRealTime || !documents.SupportsOfficeRealTimeSnapshots) return false;
        access = services.GetRequiredService<OfficeRealTimeAccess>();
        sessions = services.GetRequiredService<IOfficeRealTimeSessionStore>();
        return true;
    }

    private async Task<T> RunAdmittedAsync<T>(CellBridgeActor actor, string publicOrigin, string applicationPathBase,
        Func<string, string, CancellationToken, Task<T>> run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (string.IsNullOrWhiteSpace(actor.Identity.Subject))
            throw Rejected(HttpStatusCode.Unauthorized, "An authenticated CellBridge actor is required.");
        if (!TryResolveOrigin(publicOrigin, out var origin))
            throw Rejected(HttpStatusCode.BadRequest, "The public origin is invalid for this CellBridge host.");

        string mount;
        try { mount = DocumentUrlPaths.ValidateMount(applicationPathBase); }
        catch (ArgumentException)
        { throw Rejected(HttpStatusCode.BadRequest, "The CellBridge application path is invalid."); }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(options.RequestTimeout);
        try
        {
            var admitted = await admission.AcquireAsync(actor.Identity.Subject, deadline.Token).ConfigureAwait(false);
            using var lease = admitted.Lease;
            if (lease is null)
                throw Rejected((HttpStatusCode)admitted.RejectionStatus, "CellBridge is busy. Retry the request.");
            deadline.Token.ThrowIfCancellationRequested();
            return await run(origin, origin + mount, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw Rejected(HttpStatusCode.RequestTimeout, "The CellBridge request timed out.");
        }
    }

    private async Task<CellStorageExecution> ExecuteAdmittedAsync(CellStorageRequest request, CellBridgeActor actor,
        string origin, string applicationPathBase, CancellationToken token)
    {
        string envelope;
        string contentType;
        MtomResponseMessage? mtom = null;
        try
        {
            long binaryBytes = 0;
            bool hasBinaryParts = false;
            foreach (var subRequest in request.Requests.SelectMany(file => file.SubRequests))
            {
                if (subRequest.SubRequestDataBinaryMemory is not { } binary) continue;
                hasBinaryParts = true;
                binaryBytes += binary.Length;
                if (binaryBytes > options.MaxRequestBytes) throw BodyTooLarge();
            }

            List<ReadOnlyMemory<byte>>? parts = hasBinaryParts ? [] : null;
            envelope = CellStorageRequestSerializer.Serialize(request, parts);
            if (parts is not null)
                mtom = MtomResponseMessage.Create(envelope, request.SoapNamespace, parts);

            long bodyLength = mtom?.ContentLength ?? Encoding.UTF8.GetByteCount(envelope);
            if (bodyLength > options.MaxRequestBytes) throw BodyTooLarge();
            if (mtom is not null && (mtom.PartCount > options.MaxMtomParts ||
                mtom.MaxPartHeaderBytes > options.MaxMtomHeaderBytes))
                throw Rejected(HttpStatusCode.BadRequest, "The request exceeds the CellBridge MTOM limits.");

            // Use the wire model for execution. XML attributes and binary sizes are normalized by the
            // same codec as the HTTP transport. Keep the original binary memories to avoid a full body copy.
            contentType = mtom?.ContentType ?? request.SoapContentType;
            request = CellStorageRequestParser.Parse(envelope);
            if (parts is not null)
            {
                var attachments = parts.Select((content, index) => (content, id: "cid:" + MtomResponseMessage.ContentId(index + 1)))
                    .ToDictionary(part => part.id, part => part.content, StringComparer.OrdinalIgnoreCase);
                foreach (var subRequest in request.Requests.SelectMany(file => file.SubRequests))
                {
                    if (subRequest.SubRequestDataAttributes.TryGetValue("IncludeHref", out var href) &&
                        attachments.TryGetValue(href, out var content))
                        subRequest.SubRequestDataBinaryMemory = content;
                }
            }
        }
        catch (Exception error) when (error is InvalidDataException or FormatException or XmlException or ArgumentException)
        {
            logger.LogWarning("Invalid in-process FSSHTTP request ({ErrorType})", error.GetType().Name);
            throw Rejected(HttpStatusCode.BadRequest, "The FSSHTTP request is invalid.");
        }

        token.ThrowIfCancellationRequested();
        var captureId = Guid.NewGuid().ToString("N");
        logger.LogInformation("In-process FSSHTTP request id={CaptureId} files={FileCount}", captureId, request.Requests.Count);
        if (captures.IsEnabled)
            await CaptureMessageAsync(captureId, "request", contentType, envelope, mtom, token).ConfigureAwait(false);

        var execution = await processor.ExecuteAsync(request, origin, actor, token, applicationPathBase).ConfigureAwait(false);
        logger.LogInformation("In-process FSSHTTP response id={CaptureId} files={FileCount}", captureId, execution.Response.Responses.Count);
        if (captures.IsEnabled && !token.IsCancellationRequested)
        {
            try
            {
                var response = execution.Response;
                await captures.WriteTextAsync(captureId, ".summary.json", WireCaptureSummary.Create(request, response), token)
                    .ConfigureAwait(false);
                var responseMtom = mtom is null ? null : response.PrepareMtomMessage();
                await CaptureMessageAsync(captureId, "response", responseMtom?.ContentType ?? response.SoapContentType,
                    responseMtom is null ? response.ToSoapEnvelope() : null, responseMtom, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested && !execution.AcceptedSaves.IsEmpty)
            {
                // Diagnostic I/O must not make an accepted save receipt inaccessible.
            }
        }
        if (!execution.IsCancelled && execution.AcceptedSaves.IsEmpty) token.ThrowIfCancellationRequested();
        return execution;
    }

    private async Task CaptureMessageAsync(string captureId, string direction, string contentType,
        string? envelope, MtomResponseMessage? mtom, CancellationToken token)
    {
        if (mtom is null)
            await captures.WriteTextAsync(captureId, $".{direction}.bin", envelope!, token).ConfigureAwait(false);
        else
            await captures.WriteStreamingAsync(captureId, $".{direction}.bin", mtom.ContentLength,
                stream => mtom.WriteToAsync(stream, token).AsTask(), token).ConfigureAwait(false);

        await captures.WriteTextAsync(captureId, $".{direction}.content-type.txt", contentType, token).ConfigureAwait(false);
    }

    private static HttpRequestException BodyTooLarge() =>
        Rejected(HttpStatusCode.RequestEntityTooLarge, "The request exceeds the CellBridge body limit.");

    private static HttpRequestException Rejected(HttpStatusCode statusCode, string message) =>
        new(message, null, statusCode);
}
