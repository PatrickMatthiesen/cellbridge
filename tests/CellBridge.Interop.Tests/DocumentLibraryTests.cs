#nullable enable
using System.Net.Http.Json;
using System.Text;
using System.Xml.Linq;

namespace CellBridge.Interop.Tests;

public sealed class DocumentLibraryTests
{
    [LiveInteropFact]
    public async Task MultipleDocumentsDownloadAndTwoClientsShareOneDocument()
    {
        var endpoint = new Uri(Environment.GetEnvironmentVariable("OFFICECOLLABSERVER_INTEROP_ENDPOINT")!);
        using var http = LiveInteropHttp.Create(endpoint);
        // Other live checks save shared fixtures concurrently. Give this test
        // its own documents so catalog lengths and editor counts stay stable.
        var sessionName = "Session test " + Guid.NewGuid().ToString("N");
        var downloadName = "Download test " + Guid.NewGuid().ToString("N");
        foreach (var name in new[] { sessionName, downloadName })
        {
            using var created = await http.PostAsJsonAsync("/api/documents", new { name, type = "docx" });
            created.EnsureSuccessStatusCode();
        }
        var before = await http.GetFromJsonAsync<List<Listing>>("/api/documents");
        Assert.NotNull(before);
        var first = Assert.Single(before, item => item.Path == "/shared/" + sessionName + ".docx");
        var other = Assert.Single(before, item => item.Path == "/shared/" + downloadName + ".docx");
        await Task.WhenAll(new[] { first, other }.Select(async item =>
        {
            var path = string.Join('/', item.Path.Split('/').Select(Uri.EscapeDataString));
            using var response = await http.GetAsync(path);
            response.EnsureSuccessStatusCode();
            Assert.Equal(item.Size, (await response.Content.ReadAsByteArrayAsync()).LongLength);
            Assert.Equal(item.ContentType, response.Content.Headers.ContentType?.MediaType);
            using var head = await http.SendAsync(new HttpRequestMessage(HttpMethod.Head, path));
            head.EnsureSuccessStatusCode();
            Assert.Equal(response.Headers.ETag, head.Headers.ETag);
            Assert.Equal(item.Size, head.Content.Headers.ContentLength);
            using var download = await http.GetAsync(path + "?download=true");
            download.EnsureSuccessStatusCode();
            Assert.Equal("attachment", download.Content.Headers.ContentDisposition?.DispositionType);
        }));

        var clients = new[] { Guid.NewGuid(), Guid.NewGuid() };
        // The schema identifier used by the captured Word request.
        const string schema = "29358EC1-E813-4793-8E70-ED0344E7B73C";
        XNamespace ns = CellStorageClient.ProtocolNamespace;
        XNamespace soap = "http://schemas.xmlsoap.org/soap/envelope/";
        async Task<XDocument> Send(Guid client, string operation)
        {
            var xml = new XElement(soap + "Envelope", new XElement(soap + "Body",
                new XElement(ns + "RequestVersion", new XAttribute("Version", 2), new XAttribute("MinorVersion", 2)),
                new XElement(ns + "RequestCollection", new XElement(ns + "Request",
                    new XAttribute("Url", new Uri(endpoint, first.Path)), new XAttribute("RequestToken", 1),
                    new XElement(ns + "SubRequest", new XAttribute("Type", "Coauth"), new XAttribute("SubRequestToken", 1),
                        new XElement(ns + "SubRequestData", new XAttribute("CoauthRequestType", operation),
                            new XAttribute("ClientID", client), new XAttribute("SchemaLockID", schema),
                            new XAttribute("Timeout", 3600)))))));
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(xml.ToString(SaveOptions.DisableFormatting), Encoding.UTF8, "text/xml"),
            };
            request.Headers.TryAddWithoutValidation("SOAPAction", CellStorageClient.SoapAction);
            using var response = await http.SendAsync(request);
            response.EnsureSuccessStatusCode();
            var result = XDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.All(result.Descendants(ns + "SubResponse"), sub => Assert.Equal("Success", (string?)sub.Attribute("ErrorCode")));
            return result;
        }

        try
        {
            var joins = await Task.WhenAll(clients.Select(client => Send(client, "JoinCoauthoring")));
            Assert.Contains(joins, result => result.Descendants(ns + "SubResponseData")
                .Any(data => (string?)data.Attribute("CoauthStatus") == "Coauthoring"));
            var during = (await http.GetFromJsonAsync<List<Listing>>("/api/documents"))!;
            Assert.Equal(first.ActiveEditors + 2, during.Single(item => item.Path == first.Path).ActiveEditors);
            Assert.Equal(other.ActiveEditors, during.Single(item => item.Path == other.Path).ActiveEditors);
        }
        finally
        {
            await Task.WhenAll(clients.Select(client => Send(client, "ExitCoauthoring")));
        }
        var after = (await http.GetFromJsonAsync<List<Listing>>("/api/documents"))!;
        Assert.Equal(first.ActiveEditors, after.Single(item => item.Path == first.Path).ActiveEditors);
    }

    private sealed record Listing(string Path, string ContentType, long Size, int ActiveEditors);
}
