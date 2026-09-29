using System.Text;
using System.Xml.Linq;

namespace OfficeCollabServer.FssHttp;

/// <summary>
/// The cell storage service subrequest types. [MS-FSSHTTP] section 2.2.4.5
/// (SubRequestAttributeType).
/// </summary>
public enum SubRequestType
{
    Cell,
    Coauth,
    SchemaLock,
    WhoAmI,
    ServerTime,
    ExclusiveLock,
    EditorsTable,
    GetDocMetaInfo,
    GetVersions,
    FileOperation,
    Versioning,
    AmIAlone,
    LockStatus,
    Properties,
}

/// <summary>
/// A parsed cell storage service request message. [MS-FSSHTTP] section 2.2.2.1.
/// </summary>
public sealed class CellStorageRequest
{
    /// <summary>The SOAP action for ExecuteCellStorageRequest.</summary>
    public const string ExecuteAction =
        "http://schemas.microsoft.com/sharepoint/soap/ICellStorages/ExecuteCellStorageRequest";

    /// <summary>The SOAP action for ExecuteCellStorageQuery.</summary>
    public const string QueryAction =
        "http://schemas.microsoft.com/sharepoint/soap/ICellStorages/ExecuteCellStorageQuery";

    /// <summary>The protocol namespace.</summary>
    public const string Namespace = "http://schemas.microsoft.com/sharepoint/soap/";

    /// <summary>The protocol version carried in RequestVersion (major).</summary>
    public uint Version { get; set; } = 2;

    /// <summary>The minor version carried in RequestVersion.</summary>
    public uint MinorVersion { get; set; }

    /// <summary>The CorrelationId from RequestCollection.</summary>
    public Guid CorrelationId { get; set; }

    /// <summary>The per-file requests.</summary>
    public List<FssHttpRequest> Requests { get; set; } = new();

    /// <summary>Whether the request used the wire form with protocol elements directly under SOAP Body.</summary>
    public bool UsesDirectBody { get; set; }
}

/// <summary>
/// A Request element: a synchronization request for one file.
/// [MS-FSSHTTP] section 2.2.3.2.
/// </summary>
public sealed class FssHttpRequest
{
    /// <summary>Whether ResourceID takes precedence over Url for existing files.</summary>
    public bool UseResourceId { get; set; }

    /// <summary>The client's invariant file identity, when supplied.</summary>
    public string? ResourceId { get; set; }

    /// <summary>The URL of the file.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>The request token that uniquely identifies this request.</summary>
    public ulong RequestToken { get; set; }

    /// <summary>The UserAgent attribute.</summary>
    public Guid? UserAgent { get; set; }

    /// <summary>The subrequests for this file.</summary>
    public List<FssHttpSubRequest> SubRequests { get; set; } = new();
}

/// <summary>
/// A SubRequest element. [MS-FSSHTTP] section 2.2.3.8.
/// </summary>
public sealed class FssHttpSubRequest
{
    /// <summary>The subrequest type.</summary>
    public SubRequestType Type { get; set; }

    /// <summary>The token Word uses to correlate this subrequest response.</summary>
    public ulong? SubRequestToken { get; set; }

    /// <summary>The token this request depends on, if present.</summary>
    public ulong? DependsOn { get; set; }

    /// <summary>The dependency condition, such as OnNotSupported or OnExecute.</summary>
    public string? DependencyType { get; set; }

    /// <summary>The SubRequestData content (raw inner XML, if any).</summary>
    public string? SubRequestDataXml { get; set; }

    /// <summary>Binary content referenced by an xop:Include element.</summary>
    public byte[]? SubRequestDataBinary { get; set; }

    /// <summary>SubRequestData attributes (e.g. CoauthID, ExclusiveLockID, BinaryDataSize).</summary>
    public Dictionary<string, string> SubRequestDataAttributes { get; } = new(StringComparer.Ordinal);
}

/// <summary>
/// A parsed cell storage service response message. [MS-FSSHTTP] section 2.2.2.2.
/// </summary>
public sealed class CellStorageResponse
{
    /// <summary>The ResponseVersion values echoed back.</summary>
    public uint Version { get; set; } = 2;

    /// <summary>The minor version.</summary>
    public uint MinorVersion { get; set; }

    /// <summary>An optional version-level error code.</summary>
    public string? VersionErrorCode { get; set; }

    /// <summary>The WebUrl for the ResponseCollection.</summary>
    public string WebUrl { get; set; } = string.Empty;

    /// <summary>The per-file responses.</summary>
    public List<FssHttpResponse> Responses { get; set; } = new();

    /// <summary>Whether to serialize protocol elements directly under SOAP Body.</summary>
    public bool UsesDirectBody { get; set; }

    /// <summary>Serializes this response to a SOAP envelope string.</summary>
    public string ToSoapEnvelope()
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        sb.Append("<soap:Envelope xmlns:soap=\"http://schemas.xmlsoap.org/soap/envelope/\" ");
        sb.Append("xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" ");
        sb.Append("xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\">");
        sb.Append("<soap:Body>");
        if (!UsesDirectBody)
        {
            sb.Append("<ExecuteCellStorageResponse xmlns=\"").Append(CellStorageRequest.Namespace).Append("\">");
        }
                sb.Append("<ResponseVersion xmlns=\"").Append(CellStorageRequest.Namespace)
                    .Append("\" Version=\"").Append(Version)
          .Append("\" MinorVersion=\"").Append(MinorVersion).Append("\"");
        if (VersionErrorCode is not null)
        {
            sb.Append(" ErrorCode=\"").Append(VersionErrorCode).Append("\"");
        }

        sb.Append(" />");
                sb.Append("<ResponseCollection");
                if (UsesDirectBody)
                {
                        sb.Append(" xmlns=\"").Append(CellStorageRequest.Namespace).Append("\"");
                }

                sb.Append(" WebUrl=\"").Append(XmlEscape(WebUrl))
          .Append("\" WebUrlIsEncoded=\"false\">");

        foreach (var response in Responses)
        {
            // Encoded paths must not be advertised as unencoded: Office would
            // otherwise turn each '%' into '%25' on its next request.
            var responseUrl = Uri.TryCreate(response.Url, UriKind.Absolute, out var absoluteUrl)
                && absoluteUrl.Scheme is "http" or "https" ? absoluteUrl.AbsoluteUri : response.Url;
            sb.Append("<Response Url=\"").Append(XmlEscape(responseUrl))
              .Append("\" UrlIsEncoded=\"true\"");
            if (response.RequestToken is not null)
            {
                sb.Append(" RequestToken=\"").Append(response.RequestToken.Value).Append("\"");
            }

            sb.Append(" HealthScore=\"0\"");
            if (response.IntervalOverride is not null)
            {
                sb.Append(" IntervalOverride=\"").Append(response.IntervalOverride.Value).Append('"');
            }
            if (response.ResourceId is not null)
            {
                sb.Append(" ResourceID=\"").Append(response.ResourceId.Value.ToString("N")).Append('"');
            }
            if (response.ErrorCode is not null)
            {
                sb.Append(" ErrorCode=\"").Append(response.ErrorCode).Append("\"");
            }

            sb.Append(">");

            foreach (var subResponse in response.SubResponses)
            {
                sb.Append("<SubResponse");
                if (subResponse.SubRequestToken is not null)
                {
                    sb.Append(" SubRequestToken=\"").Append(subResponse.SubRequestToken.Value).Append("\"");
                }
                if (subResponse.ErrorCode is not null)
                {
                    sb.Append(" ErrorCode=\"").Append(subResponse.ErrorCode).Append("\"");
                }
                if (subResponse.HResult is not null)
                {
                    sb.Append(" HResult=\"").Append(XmlEscape(subResponse.HResult)).Append("\"");
                }
                if (subResponse.ServerCorrelationId is not null)
                {
                    sb.Append(" ServerCorrelationId=\"").Append(XmlEscape(subResponse.ServerCorrelationId)).Append("\"");
                }

                sb.Append(">");
                if (subResponse.SubResponseXml is not null)
                {
                    // GetVersions is the one standard response whose payload
                    // is a nested XML result rather than SubResponseData.
                    // The builder creates this fragment with LINQ to XML;
                    // keep it as XML here so its protocol shape is preserved.
                    sb.Append(subResponse.SubResponseXml);
                }
                else if (subResponse.SubResponseDataXml is not null)
                {
                    sb.Append("<SubResponseData>")
                        .Append(subResponse.SubResponseDataXml)
                        .Append("</SubResponseData>");
                }
                else if (subResponse.SubResponseDataBase64 is not null)
                {
                    // Inline the binary payload as base64 text content of
                    // SubResponseData (non-MTOM form). Word accepts this when
                    // the request was not sent with MTOM.
                    sb.Append("<SubResponseData");
                    foreach (var attr in subResponse.SubResponseDataAttributes)
                    {
                        sb.Append(' ').Append(attr.Key).Append("=\"")
                          .Append(XmlEscape(attr.Value)).Append('"');
                    }

                    sb.Append(">");
                    sb.Append(Convert.ToBase64String(subResponse.SubResponseDataBase64));
                    sb.Append("</SubResponseData>");
                }
                else if (subResponse.SubResponseDataAttributes.Count > 0)
                {
                    sb.Append("<SubResponseData");
                    foreach (var attr in subResponse.SubResponseDataAttributes)
                    {
                        sb.Append(' ').Append(attr.Key).Append("=\"")
                          .Append(XmlEscape(attr.Value)).Append('"');
                    }

                    sb.Append(" />");
                }
                else if (subResponse.EmitEmptySubResponseData)
                {
                    sb.Append("<SubResponseData />");
                }

                sb.Append("</SubResponse>");
            }

            sb.Append("</Response>");
        }

        sb.Append("</ResponseCollection>");
        if (!UsesDirectBody)
        {
            sb.Append("</ExecuteCellStorageResponse>");
        }
        sb.Append("</soap:Body>");
        sb.Append("</soap:Envelope>");
        return sb.ToString();
    }

    /// <summary>Creates an MTOM response matching Word's multipart request.</summary>
    public (string ContentType, byte[] Body) ToMtomMessage()
    {
        var boundary = "urn:uuid:" + Guid.NewGuid().ToString().ToUpperInvariant();
        var rootContentId = "http://tempuri.org/0";
        var xml = XDocument.Parse(ToSoapEnvelope(), LoadOptions.PreserveWhitespace);
        XNamespace xop = "http://www.w3.org/2004/08/xop/include";
        var binaryParts = new List<(string ContentId, byte[] Data)>();
        var index = 0;

        foreach (var dataElement in xml.Descendants().Where(e =>
                     e.Name.LocalName == "SubResponseData" &&
                     !string.IsNullOrWhiteSpace(e.Value)))
        {
            byte[] data;
            try
            {
                data = Convert.FromBase64String(dataElement.Value.Trim());
            }
            catch (FormatException)
            {
                continue;
            }

            var contentId = $"http://tempuri.org/{++index}";
            binaryParts.Add((contentId, data));
            dataElement.RemoveNodes();
            dataElement.Add(new XElement(xop + "Include", new XAttribute("href", "cid:" + contentId)));
        }

        xml.Root!.Add(new XAttribute(XNamespace.Xmlns + "xop", xop));
        var soapBytes = Encoding.UTF8.GetBytes(xml.ToString(SaveOptions.DisableFormatting));
        using var output = new MemoryStream();
        // Match the WCF/SharePoint preamble and root-part headers captured from
        // a successful Word exchange. Word rejected our previous framing with
        // "Xml stream exists": false, before interpreting any subresponses.
        WriteAscii(output, $"\r\n--{boundary}\r\n");
        WriteAscii(output, $"Content-ID: <{rootContentId}>\r\nContent-Transfer-Encoding: 8bit\r\nContent-Type: application/xop+xml;charset=utf-8;type=\"text/xml\"\r\n\r\n");
        output.Write(soapBytes);
        WriteAscii(output, "\r\n");

        foreach (var part in binaryParts)
        {
            WriteAscii(output, $"--{boundary}\r\nContent-ID: <{part.ContentId}>\r\nContent-Transfer-Encoding: binary\r\nContent-Type: application/octet-stream\r\n\r\n");
            output.Write(part.Data);
            WriteAscii(output, "\r\n");
        }

        WriteAscii(output, $"--{boundary}--\r\n");
        return ($"multipart/related; type=\"application/xop+xml\"; boundary=\"{boundary}\"; start=\"<{rootContentId}>\"; start-info=\"text/xml\"", output.ToArray());
    }

    private static void WriteAscii(Stream stream, string value) =>
        stream.Write(Encoding.ASCII.GetBytes(value));

    private static string XmlEscape(string value) => value
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;")
        .Replace("\"", "&quot;");
}

/// <summary>
/// A Response element for one file. [MS-FSSHTTP] section 2.2.3.5.
/// </summary>
public sealed class FssHttpResponse
{
    /// <summary>The URL of the file (echoed from the request).</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>The request token echoed from the request.</summary>
    public ulong? RequestToken { get; set; }

    /// <summary>An optional response-level error code.</summary>
    public string? ErrorCode { get; set; }

    /// <summary>The server override interval in seconds.</summary>
    public uint? IntervalOverride { get; set; }

    /// <summary>The stable document resource identifier, formatted without braces.</summary>
    public Guid? ResourceId { get; set; }

    /// <summary>The subresponses.</summary>
    public List<FssHttpSubResponse> SubResponses { get; set; } = new();
}

/// <summary>
/// A SubResponse element. [MS-FSSHTTP] section 2.2.3.10.
/// </summary>
public sealed class FssHttpSubResponse
{
    /// <summary>The subrequest type this response is for.</summary>
    public SubRequestType Type { get; set; }

    /// <summary>The echoed subrequest correlation token.</summary>
    public ulong? SubRequestToken { get; set; }

    /// <summary>An optional error code (success when null).</summary>
    public string? ErrorCode { get; set; }

    /// <summary>Optional HRESULT returned with a protocol subresponse error.</summary>
    public string? HResult { get; set; }

    /// <summary>
    /// Optional SharePoint correlation identifier. [MS-FSSHTTP] does not
    /// require this diagnostic attribute, but SharePoint emits it on several
    /// successful subresponses.
    /// </summary>
    public string? ServerCorrelationId { get; set; }

    /// <summary>Whether to emit an empty SubResponseData element.</summary>
    public bool EmitEmptySubResponseData { get; set; }

    /// <summary>
    /// Raw XML payload placed directly under SubResponse. This is used by the
    /// GetVersions response, whose schema is GetVersionsResponse rather than
    /// SubResponseData.
    /// </summary>
    public string? SubResponseXml { get; set; }

    /// <summary>
    /// XML content placed inside SubResponseData, used by GetDocMetaInfo.
    /// The value must be an XML fragment, not an escaped string.
    /// </summary>
    public string? SubResponseDataXml { get; set; }

    /// <summary>
    /// Base64-encoded binary payload for the SubResponseData (used by Cell
    /// subresponses carrying FSSHTTPB data).
    /// </summary>
    public byte[]? SubResponseDataBase64 { get; set; }

    /// <summary>SubResponseData attributes (e.g. CoauthID, LockType, LockScope).</summary>
    public Dictionary<string, string> SubResponseDataAttributes { get; } = new(StringComparer.Ordinal);
}
