using System.Net.Http.Json;
using System.Text;
using System.Xml.Linq;

namespace CellBridge.Interop.Tests;

public sealed class DependencyTests
{
    [LiveInteropFact]
    public async Task UnsupportedSoapOperationAllowsFallbackDependencies()
    {
        var endpoint = new Uri(Environment.GetEnvironmentVariable("OFFICECOLLABSERVER_INTEROP_ENDPOINT")!);
        using var http = LiveInteropHttp.Create();
        var name = "dependencies-" + Guid.NewGuid().ToString("N") + ".docx";
        using var created = await http.PostAsJsonAsync(new Uri(endpoint, "/api/documents"), new { name, type = "docx" });
        created.EnsureSuccessStatusCode();
        var url = new Uri(endpoint, "/shared/" + name).AbsoluteUri;
        var soap = $"""
            <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/">
              <s:Body><ExecuteCellStorageRequest xmlns="http://schemas.microsoft.com/sharepoint/soap/">
                <RequestVersion Version="2" MinorVersion="2" />
                <RequestCollection><Request Url="{url}" RequestToken="1">
                  <SubRequest Type="Properties" SubRequestToken="1"><SubRequestData /></SubRequest>
                  <SubRequest Type="WhoAmI" SubRequestToken="2" DependsOn="1" DependencyType="OnNotSupported"><SubRequestData /></SubRequest>
                  <SubRequest Type="WhoAmI" SubRequestToken="3" DependsOn="1" DependencyType="OnSuccessOrNotSupported"><SubRequestData /></SubRequest>
                  <SubRequest Type="WhoAmI" SubRequestToken="4" DependsOn="1" DependencyType="OnSuccess"><SubRequestData /></SubRequest>
                  <SubRequest Type="WhoAmI" SubRequestToken="5" DependsOn="2" DependencyType="OnNotSupported"><SubRequestData /></SubRequest>
                  <SubRequest Type="WhoAmI" SubRequestToken="6" DependsOn="5" DependencyType="OnExecute"><SubRequestData /></SubRequest>
                  <SubRequest Type="WhoAmI" SubRequestToken="7" DependsOn="4" DependencyType="OnExecute"><SubRequestData /></SubRequest>
                  <SubRequest Type="WhoAmI" SubRequestToken="8" DependsOn="7" DependencyType="OnExecute"><SubRequestData /></SubRequest>
                </Request></RequestCollection>
              </ExecuteCellStorageRequest></s:Body>
            </s:Envelope>
            """;
        using var response = await http.PostAsync(endpoint, new StringContent(soap, Encoding.UTF8, "text/xml"));
        response.EnsureSuccessStatusCode();
        var xml = XDocument.Parse(await response.Content.ReadAsStringAsync());
        var replies = xml.Descendants().Where(e => e.Name.LocalName == "SubResponse")
            .ToDictionary(e => (string)e.Attribute("SubRequestToken"));
        Assert.Equal("NotSupported", (string)replies["1"].Attribute("ErrorCode"));
        foreach (var token in new[] { "2", "3" })
        {
            Assert.Equal("Success", (string)replies[token].Attribute("ErrorCode"));
            Assert.False(string.IsNullOrWhiteSpace((string)replies[token].Elements().Single().Attribute("UserName")));
            Assert.Equal((string)replies["2"].Elements().Single().Attribute("UserName"), (string)replies[token].Elements().Single().Attribute("UserName"));
        }
        Assert.Equal("DependentOnlyOnSuccessRequestFailed", (string)replies["4"].Attribute("ErrorCode"));
        Assert.Null(replies["4"].Elements().SingleOrDefault()?.Attribute("UserName"));
        Assert.Equal("DependentOnlyOnNotSupportedRequestGetSupported", (string)replies["5"].Attribute("ErrorCode"));
        // An evaluated OnNotSupported fallback permits OnExecute continuation.
        Assert.Equal("Success", (string)replies["6"].Attribute("ErrorCode"));
        Assert.Equal((string)replies["2"].Elements().Single().Attribute("UserName"), (string)replies["6"].Elements().Single().Attribute("UserName"));
        foreach (var token in new[] { "7", "8" })
        {
            Assert.Equal("DependentRequestNotExecuted", (string)replies[token].Attribute("ErrorCode"));
            Assert.Null(replies[token].Elements().SingleOrDefault()?.Attribute("UserName"));
        }
    }
}
