using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CellBridge.AspNetCore;

internal static class SharedAccessEndpoint
{
    private const int MaximumBodyBytes = 16 * 1024;
    private const string SharePointNamespace = "http://schemas.microsoft.com/sharepoint/soap/";
    private const string Soap11Namespace = "http://schemas.xmlsoap.org/soap/envelope/";
    private const string Soap12Namespace = "http://www.w3.org/2003/05/soap-envelope";
    private const string Action = "http://schemas.microsoft.com/sharepoint/soap/IsOnlyClient";

    internal static void MapSharedAccess(this RouteGroupBuilder routes)
    {
        routes.MapPost("/_vti_bin/sharedaccess.asmx", HandleAsync);
    }

    internal static async Task HandleAsync(HttpContext context, CellBridgeDocumentService service)
    {
        var actor = CellBridgeActor.FromPrincipal(context.User);
        if (actor is null)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        var admission = context.RequestServices.GetRequiredService<ProtocolRequestAdmission>();
        using var lease = admission.TryAcquire(actor.Identity.Subject, out var rejection);
        if (lease is null)
        {
            context.Response.StatusCode = rejection;
            return;
        }

        var originalToken = context.RequestAborted;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(originalToken);
        deadline.CancelAfter(context.RequestServices.GetRequiredService<CellBridgeOptions>().RequestTimeout);
        context.RequestAborted = deadline.Token;
        context.Response.Headers.CacheControl = "private, no-store";

        var captureId = Guid.NewGuid().ToString("N");
        var captures = context.RequestServices.GetRequiredService<ProtocolCaptureStore>();
        var options = context.RequestServices.GetRequiredService<CellBridgeOptions>();
        var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("CellBridge.AspNetCore.SharedAccessEndpoint");
        try
        {
            if (!TryGetSoapVersionAndValidateAction(context.Request, out var soapNamespace,
                    out var contentType, out var status))
            {
                context.Response.StatusCode = status;
                return;
            }

            var maximumBodyBytes = Math.Min(MaximumBodyBytes, options.MaxRequestBytes);
            if (context.Request.ContentLength > maximumBodyBytes)
            {
                context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                return;
            }

            byte[] body;
            try
            {
                body = await ProtocolRequestBody.ReadAsync(context.Request.Body, maximumBodyBytes, context.RequestAborted);
            }
            catch (ProtocolRequestBody.RequestBodyLimitException)
            {
                context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                return;
            }

            if (options.CaptureDirectory is not null)
            {
                await captures.WriteAsync(captureId, ".request.bin", body, context.RequestAborted);
                await captures.WriteTextAsync(captureId, ".request.content-type.txt", contentType, context.RequestAborted);
            }

            if (!TryParseRequest(body, soapNamespace!, out var resourceId))
            {
                logger.LogInformation("Shared access IsOnlyClient request rejected: malformed XML or operation data");
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            // IsOnlyClient's id is the file's TransitionID. CellBridge uses the
            // stable ResourceId as its TransitionID, so identity lookup is exact.
            var onlyClient = await service.IsOnlyClientAsync(resourceId, actor, context.RequestAborted);
            if (onlyClient is null)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                if (options.CaptureDirectory is not null)
                    await captures.WriteTextAsync(captureId, ".summary.json",
                        Summary("NotFound", null), context.RequestAborted);
                return;
            }

            var response = BuildResponse(soapNamespace!, onlyClient.Value);
            if (options.CaptureDirectory is not null)
            {
                await captures.WriteAsync(captureId, ".response.bin", response, context.RequestAborted);
                await captures.WriteTextAsync(captureId, ".response.content-type.txt", ResponseContentType(soapNamespace!), context.RequestAborted);
                await captures.WriteTextAsync(captureId, ".summary.json",
                    Summary("Success", onlyClient.Value), context.RequestAborted);
            }

            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = ResponseContentType(soapNamespace!);
            context.Response.ContentLength = response.Length;
            await context.Response.Body.WriteAsync(response, context.RequestAborted);
            logger.LogInformation("Shared access IsOnlyClient completed for an authorized document");
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !originalToken.IsCancellationRequested)
        {
            if (context.Response.HasStarted) context.Abort();
            else { context.Response.Clear(); context.Response.StatusCode = StatusCodes.Status408RequestTimeout; }
        }
        finally
        {
            context.RequestAborted = originalToken;
        }
    }

    private static bool TryGetSoapVersionAndValidateAction(HttpRequest request, out string? soapNamespace,
        out string contentType, out int status)
    {
        soapNamespace = null;
        contentType = request.ContentType ?? string.Empty;
        status = StatusCodes.Status415UnsupportedMediaType;
        if (!MediaTypeHeaderValue.TryParse(contentType, out var parsed)) return false;

        if (string.Equals(parsed.MediaType, "text/xml", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryReadSoap11Action(request.Headers["SOAPAction"].ToString(), out var action) || action != Action)
            {
                status = StatusCodes.Status400BadRequest;
                return false;
            }

            soapNamespace = Soap11Namespace;
            status = StatusCodes.Status200OK;
            return true;
        }

        if (string.Equals(parsed.MediaType, "application/soap+xml", StringComparison.OrdinalIgnoreCase))
        {
            var actionParameter = parsed.Parameters.FirstOrDefault(parameter =>
                string.Equals(parameter.Name, "action", StringComparison.OrdinalIgnoreCase));
            if (actionParameter?.Value is not { } actionValue || Unquote(actionValue) != Action)
            {
                status = StatusCodes.Status400BadRequest;
                return false;
            }

            soapNamespace = Soap12Namespace;
            status = StatusCodes.Status200OK;
            return true;
        }

        return false;
    }

    private static bool TryReadSoap11Action(string value, out string action)
    {
        action = string.Empty;
        value = value.Trim();
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"') value = value[1..^1];
        if (value.Length == 0 || value.Contains('"')) return false;
        action = value;
        return true;
    }

    private static string Unquote(string value)
    {
        value = value.Trim();
        return value.Length >= 2 && value[0] == '"' && value[^1] == '"' ? value[1..^1] : value;
    }

    private static bool TryParseRequest(ReadOnlyMemory<byte> body, string soapNamespace, out Guid resourceId)
    {
        resourceId = Guid.Empty;
        try
        {
            using var stream = new MemoryStream(body.ToArray(), writable: false);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaximumBodyBytes,
                MaxCharactersFromEntities = 0,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true,
                IgnoreWhitespace = true,
                CloseInput = false,
            });
            var document = XDocument.Load(reader, LoadOptions.None);
            var envelope = document.Root;
            if (envelope?.Name != XName.Get("Envelope", soapNamespace) || HasNonNamespaceAttributes(envelope) ||
                envelope.Elements().Count() != 1) return false;

            var bodyElement = envelope.Elements().Single();
            if (bodyElement.Name != XName.Get("Body", soapNamespace) || HasNonNamespaceAttributes(bodyElement) ||
                bodyElement.Elements().Count() != 1) return false;

            var operation = bodyElement.Elements().Single();
            if (operation.Name != XName.Get("IsOnlyClient", SharePointNamespace) ||
                HasNonNamespaceAttributes(operation) || operation.Elements().Count() != 1) return false;

            var id = operation.Elements().Single();
            if (id.Name != XName.Get("id", SharePointNamespace) || HasNonNamespaceAttributes(id) ||
                id.HasElements || id.Value.Length is 0 or > 64) return false;

            return Guid.TryParseExact(id.Value, "D", out resourceId) ||
                Guid.TryParseExact(id.Value, "B", out resourceId);
        }
        catch (Exception error) when (error is XmlException or InvalidOperationException or ArgumentException)
        {
            return false;
        }
    }

    private static bool HasNonNamespaceAttributes(XElement element) =>
        element.Attributes().Any(attribute => !attribute.IsNamespaceDeclaration);

    private static byte[] BuildResponse(string soapNamespace, bool onlyClient)
    {
        XNamespace soap = soapNamespace;
        XNamespace sharepoint = SharePointNamespace;
        var document = new XDocument(new XElement(soap + "Envelope",
            new XElement(soap + "Body",
                new XElement(sharepoint + "IsOnlyClientResponse",
                    new XElement(sharepoint + "IsOnlyClientResult", onlyClient ? "true" : "false")))));
        using var output = new MemoryStream();
        using (var writer = XmlWriter.Create(output, new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(false),
            OmitXmlDeclaration = true,
            Indent = false,
        }))
        {
            document.Save(writer);
        }
        return output.ToArray();
    }

    private static string ResponseContentType(string soapNamespace) => soapNamespace == Soap12Namespace
        ? "application/soap+xml; charset=utf-8"
        : "text/xml; charset=utf-8";

    private static string Summary(string outcome, bool? onlyClient) =>
        JsonSerializer.Serialize(new { protocol = "MS-SHDACCWS", operation = "IsOnlyClient", outcome, result = onlyClient });
}
