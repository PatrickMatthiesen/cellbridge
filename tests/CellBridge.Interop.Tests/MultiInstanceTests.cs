#nullable enable
using System.Net.Http.Json;
using System.Text;
using System.Xml.Linq;

namespace CellBridge.Interop.Tests;

public sealed class MultiInstanceFactAttribute : FactAttribute
{
    public MultiInstanceFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("OFFICECOLLABSERVER_INTEROP_PEER")))
            Skip = "Set OFFICECOLLABSERVER_INTEROP_ENDPOINT and OFFICECOLLABSERVER_INTEROP_PEER to two hosts using shared storage.";
    }
}

public class MultiInstanceTests
{
    [MultiInstanceFact]
    public async Task TwoHostsShareDocumentIdentityBytesAndExclusiveLeaseDecisions()
    {
        var first = new Uri(Environment.GetEnvironmentVariable("OFFICECOLLABSERVER_INTEROP_ENDPOINT")!);
        var peer = new Uri(Environment.GetEnvironmentVariable("OFFICECOLLABSERVER_INTEROP_PEER")!);
        using var http = LiveInteropHttp.Create();
        var name = "two instances " + Guid.NewGuid().ToString("N");
        using var created = await http.PostAsJsonAsync(new Uri(first, "/api/documents"), new { name, type = "docx" });
        created.EnsureSuccessStatusCode();
        var path = "/shared/" + Uri.EscapeDataString(name + ".docx");
        Assert.Equal(await http.GetByteArrayAsync(new Uri(first, path)), await http.GetByteArrayAsync(new Uri(peer, path)));
        using var a = await http.SendAsync(new(HttpMethod.Head, new Uri(first, path)));
        using var b = await http.SendAsync(new(HttpMethod.Head, new Uri(peer, path)));
        Assert.Equal(a.Headers.ETag, b.Headers.ETag);
        var lockId = Guid.NewGuid().ToString("D");
        Assert.Equal("Success", await Lock(first, "GetLock", lockId));
        Assert.Equal("FileAlreadyLockedOnServer", await Lock(peer, "GetLock", Guid.NewGuid().ToString("D")));
        Assert.Equal("Success", await Lock(peer, "ReleaseLock", lockId));
        Assert.Equal("Success", await Lock(peer, "GetLock", lockId));
        Assert.Equal("Success", await Lock(first, "ReleaseLock", lockId));

        async Task<string?> Lock(Uri endpoint, string operation, string id)
        {
            XNamespace ns = "http://schemas.microsoft.com/sharepoint/soap/";
            XNamespace soap = "http://schemas.xmlsoap.org/soap/envelope/";
            var message = new XElement(soap + "Envelope", new XElement(soap + "Body",
                new XElement(ns + "RequestCollection", new XElement(ns + "Request",
                    new XAttribute("Url", new Uri(first, path)), new XAttribute("RequestToken", 1),
                    new XElement(ns + "SubRequest", new XAttribute("Type", "ExclusiveLock"),
                        new XAttribute("SubRequestToken", 1), new XElement(ns + "SubRequestData",
                            new XAttribute("ExclusiveLockRequestType", operation), new XAttribute("ExclusiveLockID", id),
                            new XAttribute("Timeout", 60)))))));
            using var response = await http.PostAsync(endpoint, new StringContent(message.ToString(), Encoding.UTF8, "text/xml"));
            response.EnsureSuccessStatusCode();
            var xml = XDocument.Parse(await response.Content.ReadAsStringAsync());
            return (string?)xml.Descendants(ns + "SubResponse").Single().Attribute("ErrorCode");
        }
    }
}
