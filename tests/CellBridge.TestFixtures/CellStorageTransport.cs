using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;
using CellBridge.FssHttp;

namespace CellBridge.TestFixtures;

/// <summary>Sends SOAP/MTOM framing while leaving binary encoding and parsing to its caller.</summary>
public sealed class CellStorageTransport(HttpClient http, Uri endpoint)
{
    public const string ProtocolNamespace = "http://schemas.microsoft.com/sharepoint/soap/";
    public const string SoapAction = CellStorageRequest.ExecuteAction;

    public async Task<CellStorageWireResponse> SendCellAsync(string fileUrl, byte[] binary,
        Guid? partitionId, bool getFileProps, Guid userAgent, CancellationToken cancellationToken = default)
    {
        var boundary = "urn:uuid:" + Guid.NewGuid().ToString("D");
        var rootId = "http://tempuri.org/request/0";
        var binaryId = "http://tempuri.org/request/1";
        var soap = BuildSoap(fileUrl, partitionId, getFileProps, binary.Length, Guid.NewGuid(), binaryId, userAgent);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        { Content = new ByteArrayContent(BuildMtomBody(boundary, rootId, binaryId, soap, binary)) };
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(
            $"multipart/related; type=\"application/xop+xml\"; boundary=\"{boundary}\"; start=\"<{rootId}>\"; start-info=\"text/xml\"");
        request.Headers.TryAddWithoutValidation("SOAPAction", $"\"{SoapAction}\"");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        var (responseSoap, responseBinary) = ExtractResponse(bytes, response.Content.Headers.ContentType?.ToString() ?? "");
        return new(response.StatusCode, responseSoap, responseBinary);
    }

    private static string BuildSoap(string fileUrl, Guid? partitionId, bool getFileProps, int binaryLength, Guid correlationId, string binaryId, Guid userAgent)
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
            new XAttribute("UserAgent", userAgent), subRequest);
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

public sealed record CellStorageWireResponse(HttpStatusCode StatusCode, string Soap, byte[]? Binary);
