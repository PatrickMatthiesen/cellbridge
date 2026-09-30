using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;
using Microsoft.Protocols.TestSuites.SharedAdapter;
using CellBridge.FssHttp;

namespace CellBridge.Interop.Tests;

/// <summary>
/// Small SOAP/MTOM client for the FSSHTTP Cell endpoint. It intentionally
/// carries the FSSHTTPB request as an opaque binary attachment and parses the
/// response with the vendored Microsoft stack.
/// </summary>
public sealed class CellStorageClient
{
    public const string ProtocolNamespace = "http://schemas.microsoft.com/sharepoint/soap/";
    public const string SoapAction = CellStorageRequest.ExecuteAction;

    private readonly HttpClient _http;
    private readonly Uri _endpoint;

    public CellStorageClient(HttpClient http, Uri endpoint)
    {
        _http = http;
        _endpoint = endpoint;
    }

    public async Task<CellStorageCallResult> SendCellAsync(
        string fileUrl,
        FsshttpbCellRequest cellRequest,
        Guid partitionId,
        CancellationToken cancellationToken = default)
        => await SendCellAsync(fileUrl, cellRequest, (Guid?)partitionId, getFileProps: false, cancellationToken);

    /// <summary>
    /// Sends a Cell request for a selected partition. File contents use the
    /// FSSHTTP GetFileProps selector and therefore omit PartitionID.
    /// </summary>
    public async Task<CellStorageCallResult> SendCellAsync(
        string fileUrl,
        FsshttpbCellRequest cellRequest,
        Guid? partitionId,
        bool getFileProps,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cellRequest);
        var binary = cellRequest.SerializeToByteList().ToArray();
        var boundary = "urn:uuid:" + Guid.NewGuid().ToString("D");
        var rootId = "http://tempuri.org/request/0";
        var binaryId = "http://tempuri.org/request/1";
        var correlationId = Guid.NewGuid();
        var soap = BuildSoap(fileUrl, partitionId, getFileProps, binary.Length, correlationId, binaryId);
        var body = BuildMtomBody(boundary, rootId, binaryId, soap, binary);

        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = new ByteArrayContent(body),
        };
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(
            $"multipart/related; type=\"application/xop+xml\"; boundary=\"{boundary}\"; start=\"<{rootId}>\"; start-info=\"text/xml\"");
        request.Headers.TryAddWithoutValidation("SOAPAction", $"\"{SoapAction}\"");

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var responseBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        var responseContentType = response.Content.Headers.ContentType?.ToString() ?? "";
        var (responseSoap, responseBinary) = ExtractResponse(responseBytes, responseContentType);
        return new CellStorageCallResult(response, responseSoap, responseBinary);
    }

    private static string BuildSoap(string fileUrl, Guid? partitionId, bool getFileProps, int binaryLength, Guid correlationId, string binaryId)
    {
        var include = new XElement(XName.Get("Include", "http://www.w3.org/2004/08/xop/include"),
            new XAttribute("href", "cid:" + binaryId));
        var data = new XElement(XName.Get("SubRequestData", ProtocolNamespace),
            new XAttribute("BinaryDataSize", binaryLength), include);
        if (partitionId is not null)
            data.Add(new XAttribute("PartitionID", partitionId.Value));
        if (getFileProps)
            data.Add(new XAttribute("GetFileProps", "true"));
        var subRequest = new XElement(XName.Get("SubRequest", ProtocolNamespace),
            new XAttribute("Type", "Cell"), new XAttribute("SubRequestToken", "1"), data);
        var fileRequest = new XElement(XName.Get("Request", ProtocolNamespace),
            new XAttribute("Url", fileUrl), new XAttribute("RequestToken", "1"),
            new XAttribute("UserAgent", FsshttpbCellRequest.UserAgentGuid), subRequest);
        var collection = new XElement(XName.Get("RequestCollection", ProtocolNamespace),
            new XAttribute("CorrelationId", correlationId), fileRequest);
        var execute = new XElement(XName.Get("ExecuteCellStorageRequest", ProtocolNamespace),
            new XElement(XName.Get("RequestVersion", ProtocolNamespace),
                new XAttribute("Version", "2"), new XAttribute("MinorVersion", "0")), collection);
        var body = new XElement(XName.Get("Body", "http://schemas.xmlsoap.org/soap/envelope/"), execute);
        var doc = new XDocument(new XElement(XName.Get("Envelope", "http://schemas.xmlsoap.org/soap/envelope/"),
            new XAttribute(XNamespace.Xmlns + "soap", "http://schemas.xmlsoap.org/soap/envelope/"),
            new XAttribute(XNamespace.Xmlns + "xop", "http://www.w3.org/2004/08/xop/include"), body));
        return doc.ToString(SaveOptions.DisableFormatting);
    }

    private static byte[] BuildMtomBody(string boundary, string rootId, string binaryId, string soap, byte[] binary)
    {
        using var output = new MemoryStream();
        WriteAscii(output, $"--{boundary}\r\n");
        WriteAscii(output, $"Content-Type: application/xop+xml; charset=utf-8; type=\"text/xml\"\r\nContent-Transfer-Encoding: 8bit\r\nContent-ID: <{rootId}>\r\n\r\n");
        var soapBytes = Encoding.UTF8.GetBytes(soap);
        output.Write(soapBytes);
        WriteAscii(output, "\r\n");
        WriteAscii(output, $"--{boundary}\r\nContent-Type: application/octet-stream\r\nContent-Transfer-Encoding: binary\r\nContent-ID: <{binaryId}>\r\n\r\n");
        output.Write(binary);
        WriteAscii(output, "\r\n");
        WriteAscii(output, $"--{boundary}--\r\n");
        return output.ToArray();
    }

    private static (string Soap, byte[]? Binary) ExtractResponse(byte[] body, string contentType)
    {
        if (!contentType.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase))
            return (Encoding.UTF8.GetString(body), null);

        var parts = MtomMessageParser.Parse(body, contentType);
        var soapPart = parts.FirstOrDefault(p => p.ContentType.Contains("xop+xml", StringComparison.OrdinalIgnoreCase)) ?? parts[0];
        var soap = Encoding.UTF8.GetString(soapPart.Content);
        var doc = XDocument.Parse(soap);
        var include = doc.Descendants(XName.Get("Include", "http://www.w3.org/2004/08/xop/include")).FirstOrDefault();
        byte[]? binary = null;
        if (include is not null)
        {
            var id = ((string?)include.Attribute("href") ?? "").Trim();
            if (id.StartsWith("cid:", StringComparison.OrdinalIgnoreCase)) id = id[4..];
            binary = parts.FirstOrDefault(p => string.Equals(p.ContentId, id.Trim('<', '>'), StringComparison.OrdinalIgnoreCase))?.Content;
        }
        return (soap, binary);
    }

    private static void WriteAscii(Stream output, string value) => output.Write(Encoding.ASCII.GetBytes(value));
}

public sealed class CellStorageCallResult
{
    internal CellStorageCallResult(HttpResponseMessage response, string soap, byte[]? binary)
    {
        StatusCode = response.StatusCode;
        Soap = soap;
        Binary = binary;
    }

    public System.Net.HttpStatusCode StatusCode { get; }
    public string Soap { get; }
    public byte[]? Binary { get; }
    public FsshttpbResponse ParseBinaryResponse() =>
        Binary is null ? throw new InvalidDataException("Cell response did not contain an MTOM binary part.") :
        FsshttpbResponse.DeserializeResponseFromByteArray(Binary, 0);
}
