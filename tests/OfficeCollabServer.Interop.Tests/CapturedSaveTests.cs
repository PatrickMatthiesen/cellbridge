using System.Text;
using System.Net.Http.Json;
using System.Text.Json;
using System.Xml.Linq;
using OfficeCollabServer.FssHttp;
using OfficeCollabServer.FssHttpB;

namespace OfficeCollabServer.Interop.Tests;

public sealed class CapturedSaveTests
{
    [LiveInteropFact]
    public Task TwoCapturedSavesUpdateHttpDownloadAndReopenedGraph() =>
        Replay("docx", ["save-first", "save-second"]);

    [LiveInteropFact]
    public Task RecordedExcelSaveUpdatesHttpDownloadAndReopenedGraph() =>
        Replay("xlsx", ["excel-save-20260928"]);

    private static async Task Replay(string type, string[] saves)
    {
        var endpoint = new Uri(Environment.GetEnvironmentVariable("OFFICECOLLABSERVER_INTEROP_ENDPOINT")!);
        using var http = new HttpClient();
        var name = "Save regression " + Guid.NewGuid().ToString("N")[..8];
        using var created = await http.PostAsJsonAsync(new Uri(endpoint, "/api/documents"), new { name, type });
        created.EnsureSuccessStatusCode();
        var fileUrl = new Uri(endpoint, "/shared/" + Uri.EscapeDataString(name + "." + type)).AbsoluteUri;
        var initial = await Send(Query());
        var index = ((QueryChangesSubResponseData)initial.SubResponses[0].Data!).StorageIndexExtendedGuid;
        byte[]? first = null;
        foreach (var fixtureName in saves)
        {
            byte[] binaryBytes;
            if (type == "xlsx") binaryBytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", fixtureName + ".bin"));
            else
            {
                using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", fixtureName + ".json")));
                var side = fixture.RootElement.GetProperty("request");
                var parts = MtomMessageParser.Parse(Convert.FromBase64String(side.GetProperty("bodyBase64").GetString()!), side.GetProperty("contentType").GetString()!);
                var binary = Assert.Single(parts, p => p.ContentType.Contains("application/octet-stream"));
                binaryBytes = binary.Content;
            }
            var request = FsshttpbCellRequest.Deserialize(new BinaryReaderEx(binaryBytes));
            ((PutChangesSubRequestData)request.SubRequests[0].Data!).ExpectedStorageIndex = index;
            var saved = await Send(request);
            Assert.False(saved.SubResponses[0].Status, saved.SubResponses[0].Error?.ErrorMessage);
            var reopened = await Send(Query());
            index = ((QueryChangesSubResponseData)reopened.SubResponses[0].Data!).StorageIndexExtendedGuid;
            var graph = PartitionGraphSnapshot.Create(reopened.DataElementPackage!.DataElements, index);
            var downloaded = await http.GetByteArrayAsync(fileUrl);
            Assert.Equal(graph.Materialize(), downloaded);
            if (first is null) first = downloaded;
            else Assert.False(first.SequenceEqual(downloaded));
        }

        async Task<FsshttpbResponse> Send(FsshttpbCellRequest request)
        {
            const string ns = "http://schemas.microsoft.com/sharepoint/soap/";
            var soap = new XElement(XName.Get("Envelope", "http://schemas.xmlsoap.org/soap/envelope/"),
                new XElement(XName.Get("Body", "http://schemas.xmlsoap.org/soap/envelope/"),
                    new XElement(XName.Get("RequestVersion", ns), new XAttribute("Version", 2), new XAttribute("MinorVersion", 2)),
                    new XElement(XName.Get("RequestCollection", ns),
                        new XElement(XName.Get("Request", ns), new XAttribute("Url", fileUrl), new XAttribute("RequestToken", 1),
                            new XElement(XName.Get("SubRequest", ns), new XAttribute("Type", "Cell"), new XAttribute("SubRequestToken", 1),
                                new XElement(XName.Get("SubRequestData", ns), request.ToBase64()))))));
            using var message = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(soap.ToString(), Encoding.UTF8, "text/xml"),
            };
            message.Headers.TryAddWithoutValidation("SOAPAction", "\"" + CellStorageRequest.ExecuteAction + "\"");
            using var response = await http.SendAsync(message);
            response.EnsureSuccessStatusCode();
            var xml = XDocument.Parse(await response.Content.ReadAsStringAsync());
            var sub = xml.Descendants().Single(x => x.Name.LocalName == "SubResponse");
            Assert.Equal("Success", sub.Attribute("ErrorCode")?.Value);
            var bytes = Convert.FromBase64String(sub.Elements().Single(x => x.Name.LocalName == "SubResponseData").Value);
            var independent = Microsoft.Protocols.TestSuites.SharedAdapter.FsshttpbResponse.DeserializeResponseFromByteArray(bytes, 0);
            Assert.False(independent.Status);
            return FsshttpbResponse.Deserialize(new BinaryReaderEx(bytes));
        }
    }

    private static FsshttpbCellRequest Query()
    {
        var request = new FsshttpbCellRequest();
        request.SubRequests.Add(new FsshttpbCellSubRequest(RequestTypes.QueryChanges)
        {
            RequestId = 1,
            Data = new QueryChangesSubRequestData { IncludeStorageManifest = true, IncludeCellChanges = true },
        });
        return request;
    }
}
