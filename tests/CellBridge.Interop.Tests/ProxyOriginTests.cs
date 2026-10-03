using System.Net.Http.Json;
using System.Xml.Linq;

namespace CellBridge.Interop.Tests;

public sealed class ProxyOriginTests
{
    [LiveInteropFact]
    public async Task LoopbackHttpsProxyPreservesOfficeResponseOrigin()
    {
        var endpoint = new Uri(Environment.GetEnvironmentVariable("OFFICECOLLABSERVER_INTEROP_ENDPOINT")!);
        using var http = LiveInteropHttp.Create();
        var name = "proxy-origin-" + Guid.NewGuid().ToString("N") + ".docx";
        var created = await http.PostAsJsonAsync(new Uri(endpoint, "/api/documents"), new { name, type = "docx" });
        created.EnsureSuccessStatusCode();
        const string publicOrigin = "https://office-test.example.ts.net";
        var fileUrl = publicOrigin + "/shared/" + name;
        var soap = $"""
            <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/">
              <s:Body><ExecuteCellStorageRequest xmlns="http://schemas.microsoft.com/sharepoint/soap/">
                <RequestVersion Version="2" MinorVersion="2" />
                <RequestCollection><Request Url="{fileUrl}" RequestToken="1">
                  <SubRequest Type="WhoAmI" SubRequestToken="1"><SubRequestData /></SubRequest>
                </Request></RequestCollection>
              </ExecuteCellStorageRequest></s:Body>
            </s:Envelope>
            """;
        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(soap, System.Text.Encoding.UTF8, "text/xml"),
        };
        message.Headers.Add("X-Forwarded-Proto", "https");
        message.Headers.Add("X-Forwarded-Host", "office-test.example.ts.net");
        using var response = await http.SendAsync(message);
        response.EnsureSuccessStatusCode();
        var xml = XDocument.Parse(await response.Content.ReadAsStringAsync());
        var collection = xml.Descendants().Single(e => e.Name.LocalName == "ResponseCollection");
        Assert.Equal(publicOrigin, (string)collection.Attribute("WebUrl"));
        Assert.Equal(fileUrl, (string)xml.Descendants().Single(e => e.Name.LocalName == "Response").Attribute("Url"));
        Assert.Equal("Success", (string)xml.Descendants().Single(e => e.Name.LocalName == "SubResponse").Attribute("ErrorCode"));
    }
}
