using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;
using CellBridge.AspNetCore;
using CellBridge.FssHttp;
using CellBridge.FssHttpB;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace CellBridge.FssHttp.Tests;

public sealed class MtomEndpointTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EndpointStreamsCompleteMultipartAndCaptureMatchesTransmittedBytes(bool captureEnabled)
    {
        string captureDirectory = Path.Combine(Path.GetTempPath(), "cellbridge-mtom-" + Guid.NewGuid().ToString("N"));
        try
        {
            var provider = new StorageProvider(new InMemoryStateStore(), new InMemoryContentStore());
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            TestActor.Register(builder.Services);
            builder.Services.AddCellBridge(provider, requireDurability: false,
                configure: options => options.CaptureDirectory = captureEnabled ? captureDirectory : null);
            await using var app = builder.Build();
            app.UseAuthentication(); app.UseAuthorization(); app.MapCellBridge();
            var service = app.Services.GetRequiredService<CellBridgeDocumentService>();
            var state = (await service.CreateAsync("/shared/stream.bin", new byte[65536], TestActor.Value))!;
            await app.StartAsync();
            using var client = app.GetTestClient();
            var query = new FsshttpbCellRequest { SubRequests = { new(RequestTypes.QueryChanges)
                { RequestId = 1, Data = new QueryChangesSubRequestData { IncludeStorageManifest = true, IncludeCellChanges = true } } } };
            var binary = query.ToByteArray();
            XNamespace protocol = CellStorageRequest.Namespace;
            XNamespace soap = "http://schemas.xmlsoap.org/soap/envelope/";
            XNamespace xop = "http://www.w3.org/2004/08/xop/include";
            var envelope = new XElement(soap + "Envelope", new XElement(soap + "Body",
                new XElement(protocol + "RequestVersion", new XAttribute("Version", 2), new XAttribute("MinorVersion", 2)),
                new XElement(protocol + "RequestCollection", new XAttribute("CorrelationId", Guid.NewGuid()), new XElement(protocol + "Request",
                    new XAttribute("Url", "http://localhost" + state.Path), new XAttribute("RequestToken", 1),
                    new XElement(protocol + "SubRequest", new XAttribute("Type", "Cell"), new XAttribute("SubRequestToken", 1),
                        new XElement(protocol + "SubRequestData", new XAttribute("GetFileProps", "true"),
                            new XAttribute("BinaryDataSize", binary.Length), new XElement(xop + "Include", new XAttribute("href", "cid:binary"))))))));
            using var requestBody = new MemoryStream();
            requestBody.Write(Encoding.UTF8.GetBytes("--request\r\nContent-Type: application/xop+xml\r\nContent-ID: <root>\r\n\r\n" +
                envelope.ToString(SaveOptions.DisableFormatting) + "\r\n--request\r\nContent-Type: application/octet-stream\r\nContent-ID: <binary>\r\n\r\n"));
            requestBody.Write(binary);
            requestBody.Write(Encoding.ASCII.GetBytes("\r\n--request--\r\n"));
            using var request = new HttpRequestMessage(HttpMethod.Post, "/_vti_bin/cellstorage.svc")
                { Content = new ByteArrayContent(requestBody.ToArray()) };
            request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("multipart/related; boundary=request; start=\"<root>\"");
            request.Headers.Add("X-Test-User", "writer");
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadAsByteArrayAsync();
            Assert.Equal(body.LongLength, response.Content.Headers.ContentLength);
            var contentType = response.Content.Headers.ContentType!.ToString();
            var parts = MtomMessageParser.Parse(body, contentType);
            Assert.Equal(2, parts.Count);
            var cellResponse = FsshttpbResponse.Deserialize(new BinaryReaderEx(parts[1].ContentMemory));
            Assert.False(Assert.Single(cellResponse.SubResponses).Status);
            if (captureEnabled)
            {
                var captured = Assert.Single(Directory.GetFiles(captureDirectory, "*.response.bin"));
                Assert.Equal(body, await File.ReadAllBytesAsync(captured));
                var capturedType = await File.ReadAllTextAsync(Path.ChangeExtension(captured, ".content-type.txt"));
                Assert.Equal(contentType, MediaTypeHeaderValue.Parse(capturedType).ToString());
            }
            else
                Assert.False(Directory.Exists(captureDirectory));
        }
        finally
        {
            if (Directory.Exists(captureDirectory)) Directory.Delete(captureDirectory, recursive: true);
        }
    }
}
