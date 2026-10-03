#nullable enable
using System.Net.Http.Json;
using System.Text;
using System.Xml.Linq;

namespace CellBridge.Interop.Tests;

public sealed class IdentityRoutingTests
{
    [LiveInteropFact]
    public async Task EncodedUrlAndResourceIdKeepReleaseOnTheOriginalFile()
    {
        var endpoint = new Uri(Environment.GetEnvironmentVariable("OFFICECOLLABSERVER_INTEROP_ENDPOINT")!);
        using var http = LiveInteropHttp.Create(endpoint);
        var name = "Excel encoding check " + Guid.NewGuid().ToString("N")[..8];
        using var created = await http.PostAsJsonAsync("/api/documents", new { name, type = "xlsx" });
        created.EnsureSuccessStatusCode();
        var url = new Uri(endpoint, "/shared/" + Uri.EscapeDataString(name + ".xlsx")).AbsoluteUri;
        var original = await http.GetByteArrayAsync(url);
        using var firstHead = await http.SendAsync(new HttpRequestMessage(HttpMethod.Head, url));
        var etag = firstHead.Headers.ETag;
        XNamespace ns = CellStorageClient.ProtocolNamespace;
        var firstLock = Guid.NewGuid().ToString("B");
        var secondLock = Guid.NewGuid().ToString("B");

        async Task<XElement> Send(string requestUrl, string operation, string lockId, string? resourceId = null)
        {
            XNamespace s = "http://schemas.xmlsoap.org/soap/envelope/";
            var file = new XElement(ns + "Request", new XAttribute("Url", requestUrl),
                new XAttribute("UseResourceID", "true"), new XAttribute("RequestToken", 1));
            if (resourceId is not null) file.Add(new XAttribute("ResourceID", resourceId));
            file.Add(new XElement(ns + "SubRequest", new XAttribute("Type", "ExclusiveLock"),
                new XAttribute("SubRequestToken", 1), new XElement(ns + "SubRequestData",
                    new XAttribute("ExclusiveLockRequestType", operation), new XAttribute("ExclusiveLockID", lockId),
                    new XAttribute("Timeout", 60))));
            var xml = new XElement(s + "Envelope", new XElement(s + "Body",
                new XElement(ns + "RequestCollection", file)));
            using var response = await http.PostAsync(endpoint, new StringContent(xml.ToString(), Encoding.UTF8, "text/xml"));
            response.EnsureSuccessStatusCode();
            return XDocument.Parse(await response.Content.ReadAsStringAsync()).Descendants(ns + "Response").Single();
        }

        static void Success(XElement response)
        {
            Assert.Null(response.Attribute("ErrorCode"));
            Assert.Equal("Success", (string?)response.Elements().Single().Attribute("ErrorCode"));
        }

        var acquired = await Send(url, "GetLock", firstLock);
        Success(acquired);
        var id = (string)acquired.Attribute("ResourceID")!;
        Assert.Equal("true", (string?)acquired.Attribute("UrlIsEncoded"));
        var release = await Send(url.Replace("%20", "%2520"), "ReleaseLock", firstLock, id);
        Success(release);
        Assert.Equal(url, (string?)release.Attribute("Url"));
        Assert.Equal(id, (string?)release.Attribute("ResourceID"));
        Success(await Send(url, "GetLock", secondLock, id));
        Success(await Send(url, "ReleaseLock", secondLock, id));

        var unknown = await Send(url.Replace("%20", "%2520"), "GetLock", firstLock);
        Assert.Equal("FileNotExistsOrCannotBeCreated", (string?)unknown.Attribute("ErrorCode"));
        var unknownId = await Send(url, "GetLock", firstLock, Guid.NewGuid().ToString("N"));
        Assert.Equal("FileNotExistsOrCannotBeCreated", (string?)unknownId.Attribute("ErrorCode"));
        Assert.Equal(original, await http.GetByteArrayAsync(url));
        using var lastHead = await http.SendAsync(new HttpRequestMessage(HttpMethod.Head, url));
        Assert.Equal(etag, lastHead.Headers.ETag);
        using var missing = await http.GetAsync(url.Replace("%20", "%2520"));
        Assert.Equal(System.Net.HttpStatusCode.NotFound, missing.StatusCode);
    }
}
