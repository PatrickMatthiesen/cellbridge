using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using CellBridge.Storage.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;

namespace CellBridge.AspNetCore;

/// <summary>Supplies the authenticated document URL shown by the native Office sharing dialog.</summary>
/// <remarks>The default provider returns the library's canonical document URL and grants no access.</remarks>
public interface ICellBridgeNativeDocumentLinkProvider
{
    Uri? GetDocumentUrl(HttpContext context, DocumentHead document);
}

/// <summary>Renders the bounded, read-only native Office Share dialog routes observed in Word.</summary>
public static class NativeSharingDialogEndpoint
{
    private const string Route = "/_layouts/15/sharedialog.aspx";
    private const int DefaultPageSize = 100;
    private const int MaximumOfficePostBytes = 16 * 1024;

    /// <summary>Maps Share dialogs for listing only readable documents, copying a file URL and viewing owner grants.</summary>
    public static RouteGroupBuilder MapNativeSharingDialog(this RouteGroupBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        routes.MapMethods(Route, ["GET", "POST"], HandleAsync);
        return routes;
    }

    private static async Task<IResult> HandleAsync(HttpContext context, CellBridgeDocumentService service)
    {
        var nonce = SetResponseHeaders(context.Response);
        var actor = CellBridgeActor.FromPrincipal(context.User);
        if (actor is null) return Results.Unauthorized();

        var originalToken = context.RequestAborted;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(originalToken);
        var options = context.RequestServices.GetRequiredService<CellBridgeOptions>();
        deadline.CancelAfter(options.RequestTimeout);
        ProtocolRequestAdmission.AdmissionResult admitted;
        try
        {
            admitted = await context.RequestServices.GetRequiredService<ProtocolRequestAdmission>()
                .AcquireAsync(actor.Identity.Subject, deadline.Token);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !originalToken.IsCancellationRequested)
        {
            return Results.StatusCode(StatusCodes.Status408RequestTimeout);
        }
        using var lease = admitted.Lease;
        if (lease is null)
        {
            context.Response.Headers.RetryAfter = "1";
            return Results.StatusCode(admitted.RejectionStatus);
        }

        context.RequestAborted = deadline.Token;
        try
        {
            if (HttpMethods.IsPost(context.Request.Method))
            {
                var maximumBodyBytes = Math.Min(options.MaxRequestBytes, MaximumOfficePostBytes);
                if (!await DiscardPostBodyAsync(context.Request, maximumBodyBytes, deadline.Token))
                    return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            }

            if (context.Request.Query.TryGetValue("resourceId", out var ids))
            {
                if (ids.Count != 1 || !Guid.TryParse(ids[0], out var resourceId) || resourceId == Guid.Empty)
                    return Html(nonce, "<h1>Choose a document</h1><p>The selected document is unavailable. Return to the document list and choose another file.</p>");
                return await RenderDocumentAsync(context, service, actor, resourceId, nonce);
            }

            var offset = 0;
            if (context.Request.Query.TryGetValue("offset", out var offsets) &&
                (offsets.Count != 1 || !int.TryParse(offsets[0], NumberStyles.None, CultureInfo.InvariantCulture, out offset) || offset < 0))
                return Html(nonce, "<h1>Choose a document</h1><p>The document list position is invalid. Return to the document list and try again.</p>");

            var page = await AuthorizedDocumentCatalog.ReadAsync(service, actor, offset, DefaultPageSize, deadline.Token);
            var html = new StringBuilder();
            html.Append("<h1>Choose a document</h1><p>Choose a document you can access. Copying its link does not change who can access it.</p>");
            if (page.Documents.Count == 0)
            {
                html.Append("<p>No documents you can access are available in the shared library.</p>");
            }
            else
            {
                html.Append("<ul class=\"documents\">");
                foreach (var document in page.Documents)
                {
                    var fileName = document.Path[(document.Path.LastIndexOf('/') + 1)..];
                    var href = SelectionUrl(context, document.ResourceId);
                    html.Append("<li><a href=\"").Append(HtmlEncoder.Default.Encode(href)).Append("\">")
                        .Append(HtmlEncoder.Default.Encode(fileName)).Append("</a></li>");
                }
                html.Append("</ul>");
            }

            if (offset > 0) html.Append("<p><a href=\"").Append(HtmlEncoder.Default.Encode(ListUrl(context, Math.Max(0, offset - DefaultPageSize))))
                .Append("\">Previous</a></p>");
            if (page.NextOffset is { } next) html.Append("<p><a href=\"").Append(HtmlEncoder.Default.Encode(ListUrl(context, next)))
                .Append("\">Next</a></p>");
            return Html(nonce, html.ToString());
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !originalToken.IsCancellationRequested)
        {
            return Results.StatusCode(StatusCodes.Status408RequestTimeout);
        }
        finally
        {
            context.RequestAborted = originalToken;
        }
    }

    private static async Task<IResult> RenderDocumentAsync(HttpContext context, CellBridgeDocumentService service,
        CellBridgeActor actor, Guid resourceId, string nonce)
    {
        DocumentHead document;
        try { document = await service.GetDocumentMetadataAsync(resourceId, actor, context.RequestAborted); }
        catch (UnauthorizedAccessException) { return UnavailableDocument(nonce); }
        catch (KeyNotFoundException) { return UnavailableDocument(nonce); }

        var linkProvider = context.RequestServices.GetService<ICellBridgeNativeDocumentLinkProvider>();
        var documentUrl = linkProvider is null ? CanonicalUrl(context, document) : linkProvider.GetDocumentUrl(context, document);
        if (!IsSafeUrl(documentUrl))
            return Html(nonce, "<h1>Copy link</h1><p>A link for this document is not configured. Ask the library owner for help.</p><p><a href=\"" +
                HtmlEncoder.Default.Encode(ListUrl(context, 0)) + "\">Choose another document</a></p>");

        var html = new StringBuilder();
        html.Append("<h1>Copy link</h1><p>This link opens the document for people who already have access. It does not grant access.</p>")
            .Append("<p><label for=\"document-link\">Document link</label><br><textarea class=\"document-link\" id=\"document-link\" readonly rows=\"3\">")
            .Append(HtmlEncoder.Default.Encode(documentUrl!.AbsoluteUri)).Append("</textarea></p>")
            .Append("<button id=\"copy-link\" type=\"button\">Copy link</button> <span id=\"copy-result\" role=\"status\" aria-live=\"polite\"></span>")
            .Append("<p><a href=\"").Append(HtmlEncoder.Default.Encode(ListUrl(context, 0))).Append("\">Choose another document</a></p>");

        if (string.Equals(document.Security.Owner, actor.Identity.Subject, StringComparison.Ordinal))
        {
            try
            {
                var snapshot = await service.GetSharingAsync(document.ResourceId, document.LifecycleGeneration, actor, context.RequestAborted);
                if (snapshot is not null)
                    await AppendAccessListAsync(html, context, snapshot, actor.Identity, context.RequestAborted);
            }
            catch (NotSupportedException)
            {
                html.Append("<h2>People with access</h2><p>The configured document permissions do not expose an access list here.</p>");
            }
            catch (UnauthorizedAccessException)
            {
                html.Append("<h2>People with access</h2><p>The configured document permissions do not expose an access list here.</p>");
            }
        }
        else
        {
            html.Append("<h2>People with access</h2><p>The file owner can view the full access list.</p>");
        }

        html.Append("<script nonce=\"").Append(HtmlEncoder.Default.Encode(nonce)).Append("\">(function(){var button=document.getElementById('copy-link');var field=document.getElementById('document-link');var result=document.getElementById('copy-result');button.onclick=function(){field.focus();field.select();var copied=false;try{copied=document.execCommand('copy');}catch(e){}result.textContent=copied?'Link copied.':'Select and copy the link above.';};})();</script>");
        return Html(nonce, html.ToString());
    }

    private static async Task AppendAccessListAsync(StringBuilder html, HttpContext context,
        DocumentSharingSnapshot sharing, SubjectIdentity owner, CancellationToken cancellationToken)
    {
        var resolver = context.RequestServices.GetService<ICellBridgeSharingRecipientResolver>();
        var resolved = new List<(string Name, string Role)> { (DisplayName(owner), "Owner") };
        foreach (var grant in sharing.Grants.OrderBy(value => value.Key, StringComparer.Ordinal))
        {
            if (grant.Key == sharing.OwnerSubject) continue;
            var recipient = resolver is null ? null : await resolver.ResolveAsync(grant.Key, cancellationToken);
            if (recipient is null || !StringComparer.Ordinal.Equals(recipient.Subject, grant.Key))
            {
                html.Append("<h2>People with access</h2><p>Some existing grants could not be resolved to a user, so the list is incomplete.</p>");
                return;
            }
            var role = grant.Value switch
            {
                DocumentAccess.Read => "Can view",
                DocumentAccess.Read | DocumentAccess.Write => "Can edit",
                _ => "No access",
            };
            resolved.Add((string.IsNullOrWhiteSpace(recipient.DisplayName) ? recipient.Login : recipient.DisplayName, role));
        }

        html.Append("<h2>People with access</h2><ul>");
        foreach (var person in resolved)
            html.Append("<li>").Append(HtmlEncoder.Default.Encode(person.Name)).Append(": ")
                .Append(HtmlEncoder.Default.Encode(person.Role)).Append("</li>");
        html.Append("</ul>");
    }

    private static Uri? CanonicalUrl(HttpContext context, DocumentHead document)
    {
        var origin = CellBridgePublicOrigin.Resolve(context);
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var baseUri)) return null;
        return new UriBuilder(baseUri) { Path = (context.Request.PathBase.Value ?? "") + document.Path }.Uri;
    }

    private static bool IsSafeUrl(Uri? url) => url is { IsAbsoluteUri: true } &&
        (url.Scheme == Uri.UriSchemeHttps || url.Scheme == Uri.UriSchemeHttp) &&
        url.UserInfo.Length == 0 && url.Query.Length == 0 && url.Fragment.Length == 0;

    private static string SelectionUrl(HttpContext context, Guid resourceId) =>
        QueryHelpers.AddQueryString(context.Request.PathBase + Route, "resourceId", resourceId.ToString("D"));

    private static string ListUrl(HttpContext context, int offset) => offset <= 0
        ? context.Request.PathBase + Route
        : QueryHelpers.AddQueryString(context.Request.PathBase + Route, "offset", offset.ToString(CultureInfo.InvariantCulture));

    private static IResult UnavailableDocument(string nonce) => Html(nonce, "<h1>Document unavailable</h1><p>This document is unavailable or you no longer have access. Choose a document you can access.</p>");

    private static IResult Html(string nonce, string body) => Results.Content(
        "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\"><title>Document sharing</title><style nonce=\"" +
        HtmlEncoder.Default.Encode(nonce) + "\">body{box-sizing:border-box;max-width:48rem;margin:0 auto;padding:1rem;color:#202124;font:16px/1.5 Arial,Helvetica,sans-serif}h1{font-size:1.35rem}h2{font-size:1.1rem}.documents{padding-left:1.25rem}.documents li{margin:.45rem 0;overflow-wrap:anywhere}a{color:#0759a5}button{padding:.55rem .9rem;border:1px solid #555;border-radius:.2rem;background:#f5f5f5;color:#111;font:inherit;cursor:pointer}button:focus,a:focus,textarea:focus{outline:3px solid #145ea8;outline-offset:2px}.document-link{box-sizing:border-box;display:block;width:100%;max-width:100%;white-space:pre-wrap;overflow-wrap:anywhere;word-break:break-all;font:inherit}</style></head><body>" +
        body + "</body></html>", "text/html; charset=utf-8");

    private static string DisplayName(SubjectIdentity identity) =>
        string.IsNullOrWhiteSpace(identity.DisplayName) ? identity.Login : identity.DisplayName;

    private static string SetResponseHeaders(HttpResponse response)
    {
        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18));
        response.Headers.CacheControl = "private, no-store";
        response.Headers.ContentSecurityPolicy = $"default-src 'none'; script-src 'nonce-{nonce}'; style-src 'nonce-{nonce}'; base-uri 'none'; form-action 'self'; object-src 'none'";
        response.Headers["X-Content-Type-Options"] = "nosniff";
        response.Headers.Remove("X-Frame-Options");
        return nonce;
    }

    private static async Task<bool> DiscardPostBodyAsync(HttpRequest request, long maximumBytes,
        CancellationToken cancellationToken)
    {
        if (request.ContentLength is > 0 && request.ContentLength > maximumBytes) return false;
        var buffer = new byte[4096];
        long readTotal = 0;
        while (true)
        {
            var read = await request.Body.ReadAsync(buffer, cancellationToken);
            if (read == 0) return true;
            if (read > maximumBytes - readTotal) return false;
            readTotal += read;
        }
    }
}
