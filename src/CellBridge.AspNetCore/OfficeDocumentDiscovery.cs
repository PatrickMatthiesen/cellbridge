using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using CellBridge.FssHttp;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using Microsoft.AspNetCore.Http;

namespace CellBridge.AspNetCore;

internal static class OfficeDocumentDiscovery
{
    private const int MaxRequestBytes = 16 * 1024;
    private const int MaxProperties = 64;
    private const int MaxXmlNodes = 128;
    private const int MaxXmlDepth = 8;
    private static readonly XNamespace Dav = "DAV:";
    private static readonly XNamespace CloudDocuments = "http://schemas.microsoft.com/clouddocuments";
    private static readonly XNamespace Replication = "http://schemas.microsoft.com/repl/";

    internal static async Task<IResult> PropFindAsync(HttpContext context, CellBridgeDocumentService service,
        string path, CellBridgeActor actor)
    {
        context.Response.Headers.CacheControl = "private, no-store";

        // This endpoint describes one file. Missing Depth is rejected rather than
        // guessed, and neither collections nor recursive walks are supported.
        if (path.EndsWith('/') || !string.Equals(context.Request.Headers["Depth"].ToString(), "0", StringComparison.Ordinal))
            return Results.BadRequest();

        IReadOnlyList<XName>? requested;
        try
        {
            requested = await ReadRequestedPropertiesAsync(context.Request, context.RequestAborted);
        }
        catch (InvalidDataException)
        {
            return Results.BadRequest();
        }
        catch (XmlException)
        {
            return Results.BadRequest();
        }

        DocumentHead? head;
        try
        {
            // Resolve and authorize only after all request parsing awaits finish.
            head = await service.FindDocumentMetadataByPathKeyAsync(StorageIds.PathKey(path), actor,
                context.RequestAborted);
        }
        catch (UnauthorizedAccessException) { return Results.NotFound(); }
        catch (KeyNotFoundException) { return Results.NotFound(); }
        if (head is null) return Results.NotFound();

        var origin = CellBridgePublicOrigin.Resolve(context);
        if (origin is null) return Results.BadRequest();
        if (!string.IsNullOrEmpty(context.Request.PathBase.Value))
            origin = origin.TrimEnd('/') + context.Request.PathBase.ToUriComponent();
        var href = DocumentRequestResolver.CanonicalUrl(head, origin);
        context.Response.Headers["Public-Extension"] = "http://schemas.microsoft.com/repl-2";

        var supported = BuildProperties(head);
        var names = requested is null ? supported.Keys.ToArray() : requested.Distinct().ToArray();
        var found = names.Where(supported.ContainsKey).Select(name => supported[name]).ToArray();
        var missing = names.Where(name => !supported.ContainsKey(name)).Select(name => new XElement(name)).ToArray();

        var response = new XElement(Dav + "response",
            new XElement(Dav + "href", href));
        if (found.Length > 0)
            response.Add(new XElement(Dav + "propstat",
                new XElement(Dav + "prop", found),
                new XElement(Dav + "status", "HTTP/1.1 200 OK")));
        if (missing.Length > 0)
            response.Add(new XElement(Dav + "propstat",
                new XElement(Dav + "prop", missing),
                new XElement(Dav + "status", "HTTP/1.1 404 Not Found")));

        var document = new XDocument(new XElement(Dav + "multistatus", response));
        context.Response.StatusCode = StatusCodes.Status207MultiStatus;
        context.Response.ContentType = "application/xml; charset=utf-8";
        await using var writer = XmlWriter.Create(context.Response.Body, new XmlWriterSettings
        {
            Async = true,
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            OmitXmlDeclaration = true,
        });
        await document.WriteToAsync(writer, context.RequestAborted);
        await writer.FlushAsync();
        return Results.Empty;
    }

    private static async Task<IReadOnlyList<XName>?> ReadRequestedPropertiesAsync(HttpRequest request,
        CancellationToken cancellationToken)
    {
        if (request.ContentLength is > MaxRequestBytes) throw new InvalidDataException("PROPFIND body is too large.");
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        while (true)
        {
            var read = await request.Body.ReadAsync(chunk, cancellationToken);
            if (read == 0) break;
            if (buffer.Length + read > MaxRequestBytes) throw new InvalidDataException("PROPFIND body is too large.");
            buffer.Write(chunk, 0, read);
        }
        if (buffer.Length == 0) throw new InvalidDataException("PROPFIND body is required.");
        buffer.Position = 0;

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaxRequestBytes,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
        };
        using var reader = XmlReader.Create(buffer, settings);
        var nodeCount = 0;
        while (reader.Read())
        {
            if (++nodeCount > MaxXmlNodes || reader.Depth > MaxXmlDepth)
                throw new InvalidDataException("PROPFIND XML exceeds the supported structure limits.");
        }
        buffer.Position = 0;
        using var parsedReader = XmlReader.Create(buffer, settings);
        var xml = XDocument.Load(parsedReader, LoadOptions.None);
        var root = xml.Root;
        if (root?.Name != Dav + "propfind" || root.Attributes().Any(attribute => !attribute.IsNamespaceDeclaration) ||
            root.Elements().Count() != 1)
            throw new InvalidDataException("Expected one DAV propfind selector.");

        var selector = root.Elements().Single();
        if (selector.Name == Dav + "allprop")
        {
            if (selector.Attributes().Any(attribute => !attribute.IsNamespaceDeclaration) ||
                selector.HasElements || !string.IsNullOrWhiteSpace(selector.Value))
                throw new InvalidDataException("DAV allprop must be empty.");
            return null;
        }

        if (selector.Name != Dav + "prop" || selector.Attributes().Any(attribute => !attribute.IsNamespaceDeclaration) ||
            selector.Elements().Count() is 0 or > MaxProperties || selector.Elements().Any(element =>
                element.HasElements || !string.IsNullOrWhiteSpace(element.Value) ||
                element.Attributes().Any(attribute => !attribute.IsNamespaceDeclaration)))
            throw new InvalidDataException("Only a bounded DAV prop selector is supported.");
        return selector.Elements().Select(element => element.Name).ToArray();
    }

    private static Dictionary<XName, XElement> BuildProperties(DocumentHead head)
    {
        var properties = new Dictionary<XName, XElement>
        {
            [Dav + "displayname"] = new(Dav + "displayname", Path.GetFileName(head.Path)),
            [Dav + "getcontentlength"] = new(Dav + "getcontentlength", head.Package.Length.ToString(CultureInfo.InvariantCulture)),
            [Dav + "getetag"] = new(Dav + "getetag", head.Etag),
            [Dav + "getlastmodified"] = new(Dav + "getlastmodified", head.ModifiedUtc.ToUniversalTime().ToString("R", CultureInfo.InvariantCulture)),
            [Dav + "creationdate"] = new(Dav + "creationdate", head.CreatedUtc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)),
            [Dav + "resourcetype"] = new(Dav + "resourcetype"),
            [Dav + "ResourceId"] = new(Dav + "ResourceId", $"{{{head.ResourceId:D}}}"),
            [CloudDocuments + "resourceId"] = new(CloudDocuments + "resourceId", $"{{{head.ResourceId:D}}}"),
            [Replication + "repl-uid"] = new(Replication + "repl-uid", $"rid:{{{head.ResourceId:D}}}"),
            [Replication + "resourcetag"] = new(Replication + "resourcetag",
                $"rt:{head.ResourceId:D}@{head.ContentVersion.ToString("D11", CultureInfo.InvariantCulture)}"),
        };
        var contentType = head.Path.ToLowerInvariant() switch
        {
            var path when path.EndsWith(".docx", StringComparison.Ordinal) => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            var path when path.EndsWith(".xlsx", StringComparison.Ordinal) => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            var path when path.EndsWith(".pptx", StringComparison.Ordinal) => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            _ => null,
        };
        if (contentType is not null) properties[Dav + "getcontenttype"] = new(Dav + "getcontenttype", contentType);
        return properties;
    }
}
