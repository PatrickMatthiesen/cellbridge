using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Xml;
using System.Xml.Linq;
using CellBridge.FssHttpB;

namespace CellBridge.FssHttp;

/// <summary>Parses MS-FSSHTTP SOAP envelopes and resolves MTOM/XOP Cell data.</summary>
public static class CellStorageResponseParser
{
    private static readonly XNamespace Protocol = CellStorageRequest.Namespace;
    private static readonly XName IncludeName = CellStorageRequestParser.XopIncludeNs + "Include";

    /// <summary>Parses a SOAP envelope. Supply the request to resolve subrequest types by token.</summary>
    public static CellStorageResponse Parse(string soapXml, CellStorageRequest? request = null)
    {
        using var source = new StringReader(soapXml);
        using var reader = XmlReader.Create(source, ReaderSettings());
        return Parse(reader, request, null);
    }

    /// <summary>Parses an HTTP response body with a SOAP or multipart/related content type.</summary>
    public static CellStorageResponse Parse(ReadOnlyMemory<byte> body, string contentType, CellStorageRequest? request = null)
    {
        Dictionary<string, MtomPart>? attachments = null;
        var mediaType = MediaTypeHeaderValue.Parse(contentType);
        if (string.Equals(mediaType.MediaType, "multipart/related", StringComparison.OrdinalIgnoreCase))
        {
            var parts = MtomMessageParser.ParseViews(body, contentType);
            var start = mediaType.Parameters.FirstOrDefault(p =>
                string.Equals(p.Name, "start", StringComparison.OrdinalIgnoreCase))?.Value?.Trim('"', '<', '>');
            var root = start is null ? parts[0] : parts.FirstOrDefault(p =>
                string.Equals(p.ContentId, start, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException("MTOM root Content-ID does not identify a MIME part.");
            body = root.ContentMemory;
            attachments = parts.Where(p => p != root && !string.IsNullOrEmpty(p.ContentId))
                .ToDictionary(p => p.ContentId!, StringComparer.OrdinalIgnoreCase);
            mediaType = MediaTypeHeaderValue.Parse(root.ContentType);
        }

        using var source = MemoryMarshal.TryGetArray(body, out var segment) && segment.Array is { } array
            ? new MemoryStream(array, segment.Offset, segment.Count, writable: false)
            : new MemoryStream(body.ToArray(), writable: false);
        if (mediaType.CharSet is { } charset)
        {
            using var text = new StreamReader(source, System.Text.Encoding.GetEncoding(charset.Trim('"')));
            using var textReader = XmlReader.Create(text, ReaderSettings());
            return Parse(textReader, request, attachments);
        }

        using var reader = XmlReader.Create(source, ReaderSettings());
        return Parse(reader, request, attachments);
    }

    private static XmlReaderSettings ReaderSettings() => new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersInDocument = CellStorageRequestParser.MaximumSoapCharacters,
    };

    private static CellStorageResponse Parse(XmlReader reader, CellStorageRequest? request,
        Dictionary<string, MtomPart>? attachments)
    {
        var envelope = XDocument.Load(reader, LoadOptions.PreserveWhitespace).Root;
        var soapNamespace = envelope?.Name.NamespaceName;
        if (envelope?.Name.LocalName != "Envelope" ||
            soapNamespace is not (CellStorageRequest.Soap11Namespace or CellStorageRequest.Soap12Namespace))
            throw new InvalidDataException("SOAP envelope namespace is not supported.");
        XNamespace soap = soapNamespace;
        var body = SingleElement(envelope, soap + "Body")
            ?? throw new InvalidDataException("SOAP envelope is missing Body.");
        var response = new CellStorageResponse { SoapNamespace = soapNamespace };
        if (SingleElement(body, soap + "Fault") is { } fault)
        {
            bool soap12 = soapNamespace == CellStorageRequest.Soap12Namespace;
            var standalone = new XElement(fault);
            // Fault codes can use prefixes declared outside the Fault element.
            foreach (var ancestor in fault.Ancestors())
            {
                foreach (var declaration in ancestor.Attributes().Where(a => a.IsNamespaceDeclaration))
                {
                    if (standalone.Attribute(declaration.Name) is null)
                        standalone.Add(new XAttribute(declaration));
                }
            }
            response.Fault = new CellStorageSoapFault(
                soap12 ? fault.Element(soap + "Code")?.Element(soap + "Value")?.Value : fault.Element("faultcode")?.Value,
                soap12 ? fault.Element(soap + "Reason")?.Element(soap + "Text")?.Value : fault.Element("faultstring")?.Value,
                standalone.ToString(SaveOptions.DisableFormatting));
            return response;
        }

        var container = SingleElement(body, Protocol + "ExecuteCellStorageRequestResponse") ?? body;
        var version = SingleElement(container, Protocol + "ResponseVersion")
            ?? throw new InvalidDataException("SOAP response is missing ResponseVersion.");
        response.Version = ReadUnsigned(version, "Version", ushort.MaxValue)
            ?? throw new InvalidDataException("ResponseVersion is missing Version.");
        response.MinorVersion = ReadUnsigned(version, "MinorVersion", ushort.MaxValue)
            ?? throw new InvalidDataException("ResponseVersion is missing MinorVersion.");
        response.VersionErrorCode = (string?)version.Attribute("ErrorCode");
        response.VersionErrorMessage = (string?)version.Attribute("ErrorMessage");

        var collection = SingleElement(container, Protocol + "ResponseCollection");
        if (collection is null)
        {
            if (response.VersionErrorCode is null or "Success")
                throw new InvalidDataException("SOAP response is missing ResponseCollection.");
            return response;
        }

        response.WebUrl = (string?)collection.Attribute("WebUrl") ?? string.Empty;
        response.WebUrlIsEncoded = ReadBoolean(collection, "WebUrlIsEncoded") ?? false;
        var requestsByToken = request?.Requests.ToDictionary(r => r.RequestToken);
        foreach (var element in collection.Elements(Protocol + "Response"))
        {
            var file = new FssHttpResponse
            {
                Url = (string?)element.Attribute("Url") ?? string.Empty,
                UrlIsEncoded = ReadBoolean(element, "UrlIsEncoded") ?? false,
                RequestToken = ReadUnsigned(element, "RequestToken"),
                HealthScore = ReadUnsigned(element, "HealthScore") ?? 0,
                IntervalOverride = ReadUnsigned(element, "IntervalOverride"),
                ErrorCode = (string?)element.Attribute("ErrorCode"),
                ErrorMessage = (string?)element.Attribute("ErrorMessage"),
            };
            if ((string?)element.Attribute("ResourceID") is { } resourceId)
            {
                if (!Guid.TryParse(resourceId, out var id))
                    throw new InvalidDataException("Response.ResourceID must be a GUID.");
                file.ResourceId = id;
            }

            var fileRequest = file.RequestToken is { } token ? requestsByToken?.GetValueOrDefault(token) : null;
            var subRequests = fileRequest?.SubRequests.Where(s => s.SubRequestToken.HasValue)
                .ToDictionary(s => s.SubRequestToken!.Value, s => s.Type);
            foreach (var sub in element.Elements(Protocol + "SubResponse"))
                file.SubResponses.Add(ParseSubResponse(sub, subRequests, attachments));
            response.Responses.Add(file);
        }
        return response;
    }

    private static FssHttpSubResponse ParseSubResponse(XElement element,
        Dictionary<ulong, SubRequestType>? subRequests, Dictionary<string, MtomPart>? attachments)
    {
        var response = new FssHttpSubResponse
        {
            SubRequestToken = ReadUnsigned(element, "SubRequestToken"),
            ErrorCode = (string?)element.Attribute("ErrorCode"),
            HResult = (string?)element.Attribute("HResult"),
            ErrorMessage = (string?)element.Attribute("ErrorMessage"),
            ServerCorrelationId = (string?)element.Attribute("ServerCorrelationId"),
        };
        if (response.SubRequestToken is { } token && subRequests is not null &&
            subRequests.TryGetValue(token, out var type))
            response.Type = type;

        var data = SingleElement(element, Protocol + "SubResponseData");
        var invalid = SingleElement(element, Protocol + "SubResponseStreamInvalid");
        response.SubResponseStreamInvalid = invalid is not null;
        var otherElements = element.Elements().Where(e => e != data && e != invalid).ToArray();
        if (otherElements.Length > 0)
            response.SubResponseXml = Fragment(otherElements);
        if (otherElements.Any(e => e.Name == Protocol + "GetVersionsResponse"))
            response.Type ??= SubRequestType.GetVersions;
        if (invalid is not null)
            response.Type ??= SubRequestType.Cell;

        if (data is null)
            return response;

        foreach (var attr in data.Attributes().Where(a => a.Name.Namespace == XNamespace.None && !a.IsNamespaceDeclaration))
            response.SubResponseDataAttributes.Add(attr.Name.LocalName, attr.Value);

        if (SingleElement(data, IncludeName) is { } include)
        {
            if (response.Type is not (null or SubRequestType.Cell) || data.Elements().Count() != 1 ||
                !string.IsNullOrWhiteSpace(data.Value))
                throw new InvalidDataException("XOP Include must be the only content of a Cell SubResponseData element.");

            string href = (string?)include.Attribute("href") ?? string.Empty;
            if (!href.StartsWith("cid:", StringComparison.OrdinalIgnoreCase) ||
                attachments is null ||
                !attachments.TryGetValue(Uri.UnescapeDataString(href[4..]).Trim('<', '>'), out var part))
                throw new InvalidDataException("XOP Include does not reference an MTOM attachment.");
            response.SubResponseDataBase64 = part.Content;
        }
        else if (data.HasElements || (response.Type is not (null or SubRequestType.Cell) &&
            !string.IsNullOrWhiteSpace(data.Value)))
        {
            response.SubResponseDataXml = Fragment(data.Nodes());
        }
        else if (!string.IsNullOrWhiteSpace(data.Value))
        {
            try { response.SubResponseDataBase64 = Convert.FromBase64String(data.Value); }
            catch (FormatException ex) { throw new InvalidDataException("Invalid Cell response base64.", ex); }
        }
        else
        {
            response.EmitEmptySubResponseData = true;
        }

        if (response.SubResponseDataBase64 is { } binary)
        {
            response.Type ??= SubRequestType.Cell;
            if (!response.SubResponseStreamInvalid)
                response.BinaryResponse = FsshttpbResponse.FromBytes(binary);
        }
        return response;
    }

    private static string Fragment(IEnumerable<XNode> nodes) =>
        string.Concat(nodes.Select(n => n.ToString(SaveOptions.DisableFormatting)));

    private static XElement? SingleElement(XElement parent, XName name)
    {
        using var elements = parent.Elements(name).GetEnumerator();
        if (!elements.MoveNext())
            return null;
        var element = elements.Current;
        if (elements.MoveNext())
            throw new InvalidDataException($"Duplicate {name.LocalName} element.");
        return element;
    }

    private static uint? ReadUnsigned(XElement element, string name, uint max = uint.MaxValue)
    {
        if ((string?)element.Attribute(name) is not { } text)
            return null;
        if (!CellSubRequestDataValidation.TryParseXmlUnsignedInt(text, out uint value) || value > max)
            throw new InvalidDataException($"Invalid {element.Name.LocalName}.{name}.");
        return value;
    }

    private static bool? ReadBoolean(XElement element, string name)
    {
        if ((string?)element.Attribute(name) is not { } text)
            return null;
        // SharePoint also emits True and False for the URL encoding flags.
        if (!bool.TryParse(text, out bool value) && !CellSubRequestDataValidation.TryParseXmlBoolean(text, out value))
            throw new InvalidDataException($"Invalid {element.Name.LocalName}.{name}.");
        return value;
    }
}
