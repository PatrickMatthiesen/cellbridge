using System.Text;
using CellBridge.FssHttpB;

namespace CellBridge.FssHttp;

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
/// A cell storage service request message. [MS-FSSHTTP] section 2.2.2.1.
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

    /// <summary>The SOAP 1.1 envelope namespace used by default.</summary>
    public const string Soap11Namespace = "http://schemas.xmlsoap.org/soap/envelope/";

    /// <summary>The SOAP 1.2 envelope namespace.</summary>
    public const string Soap12Namespace = "http://www.w3.org/2003/05/soap-envelope";

    /// <summary>The protocol version carried in RequestVersion (major).</summary>
    public uint Version { get; set; } = 2;

    /// <summary>The minor version carried in RequestVersion.</summary>
    public uint MinorVersion { get; set; }

    /// <summary>The CorrelationId from RequestCollection.</summary>
    public Guid CorrelationId { get; set; }

    /// <summary>The per-file requests.</summary>
    public List<FssHttpRequest> Requests { get; set; } = new();

    /// <summary>The SOAP envelope namespace used by the request.</summary>
    public string SoapNamespace { get; set; } = Soap11Namespace;

    /// <summary>The media type for a standalone SOAP request.</summary>
    public string SoapContentType => SoapNamespace == Soap12Namespace
        ? $"application/soap+xml; charset=utf-8; action=\"{ExecuteAction}\""
        : "text/xml; charset=utf-8";

    /// <summary>Serializes the request with inline base64 Cell data.</summary>
    public string ToSoapEnvelope() => CellStorageRequestSerializer.Serialize(this, null);

    /// <summary>Serializes the request with Cell data in separate XOP parts.</summary>
    public (string ContentType, byte[] Body) ToMtomMessage()
    {
        var message = PrepareMtomMessage();
        return (message.ContentType, message.ToArray());
    }

    /// <summary>Prepares a streaming MTOM request. The binary buffers are borrowed until writing completes.</summary>
    public MtomResponseMessage PrepareMtomMessage()
    {
        var parts = new List<ReadOnlyMemory<byte>>();
        return MtomResponseMessage.Create(
            CellStorageRequestSerializer.Serialize(this, parts), SoapNamespace, parts);
    }
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

    /// <summary>Additional Request attributes, such as UserAgentClient and Build.</summary>
    public Dictionary<string, string> RequestAttributes { get; } = new(StringComparer.Ordinal);

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

    /// <summary>The complete SubRequestData element as XML, if supplied.</summary>
    public string? SubRequestDataXml { get; set; }

    private ReadOnlyMemory<byte>? _binary;
    private byte[]? _binaryArray;
    /// <summary>The raw Cell payload to encode inline or as an XOP attachment.</summary>
    public ReadOnlyMemory<byte>? SubRequestDataBinaryMemory
    {
        get => _binaryArray is null ? _binary : _binaryArray.AsMemory();
        set { _binary = value; _binaryArray = null; }
    }
    public byte[]? SubRequestDataBinary
    {
        get => _binaryArray ??= _binary?.ToArray();
        set { _binaryArray = value; _binary = value is null ? null : value.AsMemory(); }
    }

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

    /// <summary>Optional detail for a version-level error.</summary>
    public string? VersionErrorMessage { get; set; }

    /// <summary>The fault returned in place of an MS-FSSHTTP response.</summary>
    public CellStorageSoapFault? Fault { get; set; }

    /// <summary>The WebUrl for the ResponseCollection.</summary>
    public string WebUrl { get; set; } = string.Empty;

    /// <summary>Whether WebUrl is already URL encoded.</summary>
    public bool WebUrlIsEncoded { get; set; }

    /// <summary>The per-file responses.</summary>
    public List<FssHttpResponse> Responses { get; set; } = new();

    /// <summary>The SOAP envelope namespace used by the response.</summary>
    public string SoapNamespace { get; set; } = CellStorageRequest.Soap11Namespace;

    /// <summary>The SOAP media type for a standalone XML response.</summary>
    public string SoapContentType => SoapNamespace == CellStorageRequest.Soap12Namespace
        ? "application/soap+xml; charset=utf-8"
        : "text/xml; charset=utf-8";

    /// <summary>Serializes this response to a SOAP envelope string.</summary>
    public string ToSoapEnvelope() => SerializeSoapEnvelope(null);

    private string SerializeSoapEnvelope(List<ReadOnlyMemory<byte>>? binaryParts)
    {
        var sb = new StringBuilder();
        if (binaryParts is null)
            sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        sb.Append("<soap:Envelope xmlns:soap=\"").Append(XmlEscape(SoapNamespace)).Append("\" ");
        sb.Append("xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" ");
        sb.Append("xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\"");
        if (binaryParts is not null)
            sb.Append(" xmlns:xop=\"http://www.w3.org/2004/08/xop/include\"");
        sb.Append('>');
        sb.Append("<soap:Body>");
        if (Fault is not null)
        {
            sb.Append(Fault.Xml).Append("</soap:Body></soap:Envelope>");
            return sb.ToString();
        }
        sb.Append("<ResponseVersion xmlns=\"").Append(CellStorageRequest.Namespace)
            .Append("\" Version=\"").Append(Version)
            .Append("\" MinorVersion=\"").Append(MinorVersion).Append("\"");
        if (VersionErrorCode is not null)
        {
            sb.Append(" ErrorCode=\"").Append(XmlEscape(VersionErrorCode)).Append("\"");
        }
        if (VersionErrorMessage is not null)
        {
            sb.Append(" ErrorMessage=\"").Append(XmlEscape(VersionErrorMessage)).Append("\"");
        }

        sb.Append(" />");
        sb.Append("<ResponseCollection xmlns=\"").Append(CellStorageRequest.Namespace)
            .Append("\" WebUrl=\"").Append(XmlEscape(WebUrl))
            .Append("\" WebUrlIsEncoded=\"").Append(WebUrlIsEncoded ? "true" : "false").Append("\">");

        foreach (var response in Responses)
        {
            // Encoded paths must not be advertised as unencoded: Office would
            // otherwise turn each '%' into '%25' on its next request.
            var responseUrl = response.UrlIsEncoded && Uri.TryCreate(response.Url, UriKind.Absolute, out var absoluteUrl)
                && absoluteUrl.Scheme is "http" or "https" ? absoluteUrl.AbsoluteUri : response.Url;
            sb.Append("<Response Url=\"").Append(XmlEscape(responseUrl))
              .Append("\" UrlIsEncoded=\"").Append(response.UrlIsEncoded ? "true" : "false").Append('"');
            if (response.RequestToken is not null)
            {
                sb.Append(" RequestToken=\"").Append(response.RequestToken.Value).Append("\"");
            }

            sb.Append(" HealthScore=\"").Append(response.HealthScore).Append('"');
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
                sb.Append(" ErrorCode=\"").Append(XmlEscape(response.ErrorCode)).Append("\"");
                if (!string.Equals(response.ErrorCode, "Success", StringComparison.Ordinal))
                {
                    sb.Append(" ErrorMessage=\"")
                        .Append(XmlEscape(response.ErrorMessage ?? response.ErrorCode))
                        .Append('"');
                }
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
                    sb.Append(" ErrorCode=\"").Append(XmlEscape(subResponse.ErrorCode)).Append("\"");
                }
                if (subResponse.HResult is not null)
                {
                    sb.Append(" HResult=\"").Append(XmlEscape(subResponse.HResult)).Append("\"");
                }
                if (subResponse.ErrorMessage is not null)
                {
                    sb.Append(" ErrorMessage=\"").Append(XmlEscape(subResponse.ErrorMessage)).Append('"');
                }
                if (subResponse.ServerCorrelationId is not null)
                {
                    sb.Append(" ServerCorrelationId=\"").Append(XmlEscape(subResponse.ServerCorrelationId)).Append("\"");
                }

                sb.Append(">");
                if (subResponse.SubResponseXml is not null)
                {
                    sb.Append(subResponse.SubResponseXml);
                }
                else if (subResponse.SubResponseDataXml is not null ||
                    subResponse.SubResponseDataBase64 is not null ||
                    subResponse.SubResponseDataAttributes.Count > 0 ||
                    subResponse.EmitEmptySubResponseData)
                {
                    sb.Append("<SubResponseData");
                    foreach (var attr in subResponse.SubResponseDataAttributes)
                    {
                        sb.Append(' ').Append(attr.Key).Append("=\"")
                          .Append(XmlEscape(attr.Value)).Append('"');
                    }

                    if (subResponse.SubResponseDataXml is not null)
                        sb.Append('>').Append(subResponse.SubResponseDataXml).Append("</SubResponseData>");
                    else if (subResponse.SubResponseDataBase64 is { } binary)
                    {
                        sb.Append('>');
                        if (binaryParts is not null && binary.Length > 0)
                        {
                            var contentId = MtomResponseMessage.ContentId(binaryParts.Count + 1);
                            binaryParts.Add(binary);
                            sb.Append("<xop:Include href=\"cid:").Append(contentId).Append("\" />");
                        }
                        else
                            sb.Append(Convert.ToBase64String(binary));
                        sb.Append("</SubResponseData>");
                    }
                    else
                        sb.Append(" />");
                }
                if (subResponse.SubResponseStreamInvalid)
                    sb.Append("<SubResponseStreamInvalid />");
                sb.Append("</SubResponse>");
            }

            sb.Append("</Response>");
        }

        sb.Append("</ResponseCollection>");
        sb.Append("</soap:Body>");
        sb.Append("</soap:Envelope>");
        return sb.ToString();
    }

    /// <summary>Creates an MTOM response matching Word's multipart request.</summary>
    public (string ContentType, byte[] Body) ToMtomMessage()
    {
        var message = PrepareMtomMessage();
        return (message.ContentType, message.ToArray());
    }

    /// <summary>
    /// Prepares one multipart message for buffering, capture, or streaming.
    /// Binary arrays are borrowed and must not be mutated until writing completes.
    /// Changes to response collections after preparation do not change the message.
    /// </summary>
    public MtomResponseMessage PrepareMtomMessage()
    {
        var binaryParts = new List<ReadOnlyMemory<byte>>();
        // Validate raw XML fragments before any output. Binary payloads have
        // already become XOP references, so this parses only the small envelope.
        var xml = MtomEnvelopeXml.Normalize(SerializeSoapEnvelope(binaryParts));
        return MtomResponseMessage.Create(xml, SoapNamespace, binaryParts);
    }

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

    /// <summary>Whether Url is already URL encoded.</summary>
    public bool UrlIsEncoded { get; set; } = true;

    /// <summary>The server health score.</summary>
    public uint HealthScore { get; set; }

    /// <summary>The request token echoed from the request.</summary>
    public ulong? RequestToken { get; set; }

    /// <summary>An optional response-level error code.</summary>
    public string? ErrorCode { get; set; }

    /// <summary>Required detail when <see cref="ErrorCode"/> is a non-success response error.</summary>
    public string? ErrorMessage { get; set; }

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
    /// <summary>The subrequest type, when assigned by the server, matched by token, or inferred from the payload.</summary>
    public SubRequestType? Type { get; set; }

    /// <summary>The echoed subrequest correlation token.</summary>
    public ulong? SubRequestToken { get; set; }

    /// <summary>An optional error code (success when null).</summary>
    public string? ErrorCode { get; set; }

    /// <summary>Optional HRESULT returned with a protocol subresponse error.</summary>
    public string? HResult { get; set; }

    /// <summary>Optional detail for a subresponse error.</summary>
    public string? ErrorMessage { get; set; }

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
    /// Raw binary payload carried by SubResponseData as base64 or XOP.
    /// </summary>
    public byte[]? SubResponseDataBase64 { get; set; }

    /// <summary>The typed FSSHTTPB response decoded by the SOAP parser.</summary>
    public FsshttpbResponse? BinaryResponse { get; internal set; }

    /// <summary>Whether the server marked the Cell stream invalid.</summary>
    public bool SubResponseStreamInvalid { get; set; }

    /// <summary>SubResponseData attributes (e.g. CoauthID, LockType, LockScope).</summary>
    public Dictionary<string, string> SubResponseDataAttributes { get; } = new(StringComparer.Ordinal);
}

/// <summary>A SOAP fault with the complete fault element, including any detail.</summary>
public sealed record CellStorageSoapFault(string? Code, string? Reason, string Xml);
