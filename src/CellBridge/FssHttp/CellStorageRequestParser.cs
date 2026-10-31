using System.Xml;
using System.Xml.Linq;

namespace CellBridge.FssHttp;

/// <summary>
/// Parses MS-FSSHTTP cell storage service SOAP request envelopes.
/// [MS-FSSHTTP] section 2.2.2.1.
/// </summary>
public static class CellStorageRequestParser
{
    /// <summary>Maximum number of characters accepted in a SOAP request document.</summary>
    public const long MaximumSoapCharacters = 16 * 1024 * 1024;

    private static readonly XNamespace Tns = CellStorageRequest.Namespace;
    private static readonly XNamespace XopIncludeNs = "http://www.w3.org/2004/08/xop/include";

    /// <summary>
    /// Parses a SOAP envelope containing an ExecuteCellStorageRequest into a
    /// <see cref="CellStorageRequest"/>.
    /// </summary>
    public static CellStorageRequest Parse(string soapXml)
    {
        ArgumentNullException.ThrowIfNull(soapXml);
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaximumSoapCharacters,
        };
        using var textReader = new StringReader(soapXml);
        using var xmlReader = XmlReader.Create(textReader, settings);
        var doc = XDocument.Load(xmlReader);
        var envelope = doc.Root;
        var parsedSoapNamespace = envelope?.Name.NamespaceName;
        if (envelope?.Name.LocalName != "Envelope" || parsedSoapNamespace is not
            ("" or CellStorageRequest.Soap11Namespace or CellStorageRequest.Soap12Namespace))
            throw new InvalidDataException("SOAP envelope namespace is not supported.");
        var soapNamespace = parsedSoapNamespace is CellStorageRequest.Soap11Namespace or CellStorageRequest.Soap12Namespace
            ? parsedSoapNamespace
            : CellStorageRequest.Soap11Namespace;
        var bodies = envelope.Elements().Where(e => e.Name.LocalName == "Body" &&
            (parsedSoapNamespace == "" ? e.Name.NamespaceName == "" : e.Name == XName.Get("Body", soapNamespace))).ToArray();
        if (bodies.Length != 1)
            throw new InvalidDataException("SOAP envelope must contain exactly one Body element.");
        var body = bodies[0];

        var execute = body.Elements().FirstOrDefault(e =>
            e.Name.LocalName == "ExecuteCellStorageRequest");
        var protocolContainer = execute ?? body;

        var request = new CellStorageRequest();
        request.SoapNamespace = soapNamespace;

        var requestVersion = SingleRequiredElement(protocolContainer, "RequestVersion");
        request.Version = ParseRequiredUInt16(requestVersion, "Version");
        request.MinorVersion = ParseRequiredUInt16(requestVersion, "MinorVersion");

        var requestCollection = SingleRequiredElement(protocolContainer, "RequestCollection");

        request.CorrelationId = ParseRequiredGuid(requestCollection, "CorrelationId");

        var requestElements = requestCollection.Elements().Where(e => e.Name.LocalName == "Request").ToArray();
        if (requestElements.Length == 0)
            throw new InvalidDataException("RequestCollection must contain at least one Request element.");
        var requestTokens = new HashSet<uint>();
        foreach (var requestElement in requestElements)
        {
            string url = RequiredAttribute(requestElement, "Url");
            if (url.Length == 0) throw new InvalidDataException("Request Url must not be empty.");
            uint requestToken = ParseRequiredUInt32(requestElement, "RequestToken");
            if (!requestTokens.Add(requestToken))
                throw new InvalidDataException($"Duplicate RequestToken {requestToken}.");
            var fileRequest = new FssHttpRequest
            {
                Url = url,
                UseResourceId = ParseOptionalBoolean(requestElement, "UseResourceID") ?? false,
                ResourceId = (string?)requestElement.Attribute("ResourceID"),
                RequestToken = requestToken,
                UserAgent = ParseOptionalGuid(requestElement, "UserAgent"),
            };

            var subRequestElements = requestElement.Elements().Where(e => e.Name.LocalName == "SubRequest").ToArray();
            if (subRequestElements.Length == 0)
                throw new InvalidDataException("Request must contain at least one SubRequest element.");
            var subRequestTokens = new HashSet<uint>();
            foreach (var subRequestElement in subRequestElements)
            {
                uint subRequestToken = ParseRequiredUInt32(subRequestElement, "SubRequestToken");
                if (!subRequestTokens.Add(subRequestToken))
                    throw new InvalidDataException($"Duplicate SubRequestToken {subRequestToken}.");
                uint? dependsOn = ParseOptionalUInt32(subRequestElement, "DependsOn");
                string? dependencyType = (string?)subRequestElement.Attribute("DependencyType");
                if ((dependsOn is null) != (dependencyType is null) ||
                    dependencyType is not null && !DependencyTypes.Contains(dependencyType))
                    throw new InvalidDataException("DependsOn and a valid DependencyType must be specified together.");
                var subRequest = new FssHttpSubRequest
                {
                    Type = ParseSubRequestType(RequiredAttribute(subRequestElement, "Type")),
                    SubRequestToken = subRequestToken,
                    DependsOn = dependsOn,
                    DependencyType = dependencyType,
                };

                var dataElements = subRequestElement.Elements().Where(e => e.Name.LocalName == "SubRequestData").ToArray();
                if (dataElements.Length > 1)
                    throw new InvalidDataException("SubRequest must not contain duplicate SubRequestData elements.");
                var subRequestData = dataElements.SingleOrDefault();
                if (subRequestData is not null)
                {
                    foreach (var attribute in subRequestData.Attributes())
                    {
                        subRequest.SubRequestDataAttributes[attribute.Name.LocalName] = attribute.Value;
                    }

                    // Capture any Include href (XOP MTOM reference) or inline content.
                    var include = subRequestData.Element(XopIncludeNs + "Include");
                    if (include is not null)
                    {
                        subRequest.SubRequestDataAttributes["IncludeHref"] =
                            (string?)include.Attribute("href") ?? string.Empty;
                    }

                    subRequest.SubRequestDataXml = subRequestData.ToString(SaveOptions.DisableFormatting);
                }

                ValidateSubRequestData(subRequest, subRequestData is not null);

                fileRequest.SubRequests.Add(subRequest);
            }

            request.Requests.Add(fileRequest);
        }

        return request;
    }

    private static readonly HashSet<string> DependencyTypes = new(StringComparer.Ordinal)
    {
        "OnExecute", "OnSuccess", "OnFail", "OnNotSupported", "OnSuccessOrNotSupported",
    };

    private static void ValidateSubRequestData(FssHttpSubRequest request, bool hasData)
    {
        if (request.Type is SubRequestType.WhoAmI or SubRequestType.ServerTime or SubRequestType.GetDocMetaInfo or
            SubRequestType.GetVersions or SubRequestType.LockStatus)
        {
            if (hasData)
                throw new InvalidDataException($"{request.Type} must not contain SubRequestData.");
            return;
        }

        if (request.Type != SubRequestType.Cell && !hasData)
            throw new InvalidDataException($"{request.Type} requires SubRequestData.");
        if (request.Type != SubRequestType.Cell || !hasData) return;

        if (!CellSubRequestDataValidation.TryValidate(request.SubRequestDataAttributes, requireBinaryDataSize: true,
                out string message))
            throw new InvalidDataException(message);
    }

    private static XElement SingleRequiredElement(XElement parent, string localName)
    {
        var matches = parent.Elements().Where(e => e.Name.LocalName == localName).ToArray();
        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new InvalidDataException($"ExecuteCellStorageRequest is missing {localName}."),
            _ => throw new InvalidDataException($"ExecuteCellStorageRequest contains duplicate {localName} elements."),
        };
    }

    private static string RequiredAttribute(XElement element, string name) =>
        (string?)element.Attribute(name) ?? throw new InvalidDataException($"{element.Name.LocalName} is missing required {name}.");

    private static uint ParseRequiredUInt16(XElement element, string name)
    {
        string text = RequiredAttribute(element, name);
        if (!CellSubRequestDataValidation.TryParseXmlUnsignedShort(text, out ushort value))
            throw new InvalidDataException($"{element.Name.LocalName}.{name} must be an unsigned 16-bit integer.");
        return value;
    }

    private static uint ParseRequiredUInt32(XElement element, string name)
    {
        string text = RequiredAttribute(element, name);
        if (!CellSubRequestDataValidation.TryParseXmlUnsignedInt(text, out uint value))
            throw new InvalidDataException($"{element.Name.LocalName}.{name} must be an unsigned 32-bit integer.");
        return value;
    }

    private static uint? ParseOptionalUInt32(XElement element, string name) =>
        element.Attribute(name) is null ? null : ParseRequiredUInt32(element, name);

    private static Guid ParseRequiredGuid(XElement element, string name)
    {
        string text = RequiredAttribute(element, name);
        if (!Guid.TryParse(text, out var value))
            throw new InvalidDataException($"{element.Name.LocalName}.{name} must be a GUID.");
        return value;
    }

    private static Guid? ParseOptionalGuid(XElement element, string name) =>
        element.Attribute(name) is null ? null : ParseRequiredGuid(element, name);

    private static bool? ParseOptionalBoolean(XElement element, string name)
    {
        string? text = (string?)element.Attribute(name);
        if (text is null) return null;
        if (!CellSubRequestDataValidation.TryParseXmlBoolean(text, out bool value))
            throw new InvalidDataException($"{element.Name.LocalName}.{name} must be an XML boolean.");
        return value;
    }

    private static SubRequestType ParseSubRequestType(string? value) => value?.Trim() switch
    {
        "Cell" => SubRequestType.Cell,
        "Coauth" => SubRequestType.Coauth,
        "SchemaLock" => SubRequestType.SchemaLock,
        "WhoAmI" => SubRequestType.WhoAmI,
        "ServerTime" => SubRequestType.ServerTime,
        "ExclusiveLock" => SubRequestType.ExclusiveLock,
        "EditorsTable" => SubRequestType.EditorsTable,
        "GetDocMetaInfo" => SubRequestType.GetDocMetaInfo,
        "GetVersions" => SubRequestType.GetVersions,
        "FileOperation" => SubRequestType.FileOperation,
        "Versioning" => SubRequestType.Versioning,
        "AmIAlone" => SubRequestType.AmIAlone,
        "LockStatus" => SubRequestType.LockStatus,
        "Properties" => SubRequestType.Properties,
        _ => throw new InvalidDataException($"Unknown SubRequest Type '{value}'."),
    };
}
