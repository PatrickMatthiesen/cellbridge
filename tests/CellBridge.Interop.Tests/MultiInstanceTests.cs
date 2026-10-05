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
    public async Task TwoHostsPersistTransitionsAndSerializeCompetingCoauthorConversions()
    {
        var first = new Uri(Environment.GetEnvironmentVariable("OFFICECOLLABSERVER_INTEROP_ENDPOINT")!);
        var peer = new Uri(Environment.GetEnvironmentVariable("OFFICECOLLABSERVER_INTEROP_PEER")!);
        using var http = LiveInteropHttp.Create();
        string name = "lock transitions " + Guid.NewGuid().ToString("N");
        using var created = await http.PostAsJsonAsync(new Uri(first, "/api/documents"), new { name, type = "docx" });
        created.EnsureSuccessStatusCode();
        var fileUrl = new Uri(first, "/shared/" + Uri.EscapeDataString(name + ".docx"));
        string schema = Guid.NewGuid().ToString(), a = Guid.NewGuid().ToString(), b = Guid.NewGuid().ToString();
        string lockA = Guid.NewGuid().ToString(), lockB = Guid.NewGuid().ToString(), transition = "";
        Assert.Equal("Success", (await Send(first, "ExclusiveLock", "ExclusiveLockRequestType", "GetLock", a, lockA)).Code);
        var converted = await Send(peer, "ExclusiveLock", "ExclusiveLockRequestType", "ConvertToSchemaJoinCoauth", a, lockA);
        Assert.Equal("Success", converted.Code);
        Assert.Equal("Alone", (string?)converted.Data?.Attribute("CoauthStatus"));
        transition = (string)converted.Data!.Attribute("TransitionID")!;
        Assert.Equal("Success", (await Send(first, "Coauth", "CoauthRequestType", "JoinCoauthoring", b, lockB)).Code);
        Assert.Equal("False", (string?)(await Send(peer, "AmIAlone", null, null, a, lockA)).Data?.Attribute("AmIAlone"));
        Assert.Equal("Success", (await Send(first, "Coauth", "CoauthRequestType", "MarkTransitionComplete", a, lockA)).Code);
        // Both callers request release on failure. One exits; the remaining
        // coauthor can then obtain exclusive access, on either host.
        var competing = await Task.WhenAll(
            Send(first, "Coauth", "CoauthRequestType", "ConvertToExclusive", a, lockA),
            Send(peer, "Coauth", "CoauthRequestType", "ConvertToExclusive", b, lockB));
        Assert.Single(competing, result => result.Code == "Success");
        Assert.Single(competing, result => result.Code == "ExitCoauthSessionAsConvertToExclusiveFailed");
        int winner = competing[0].Code == "Success" ? 0 : 1;
        Assert.Equal("Success", (await Send(peer, "ExclusiveLock", "ExclusiveLockRequestType", "ReleaseLock",
            winner == 0 ? a : b, winner == 0 ? lockA : lockB)).Code);

        async Task<(string? Code, XElement? Data)> Send(Uri endpoint, string type, string? key,
            string? operation, string client, string exclusive)
        {
            XNamespace ns = "http://schemas.microsoft.com/sharepoint/soap/";
            XNamespace soap = "http://schemas.xmlsoap.org/soap/envelope/";
            var data = new XElement(ns + "SubRequestData", new XAttribute("ClientID", client),
                new XAttribute("SchemaLockID", schema), new XAttribute("ExclusiveLockID", exclusive),
                new XAttribute("Timeout", 3600));
            if (key is not null) data.Add(new XAttribute(key, operation!));
            if (type == "Coauth" && operation == "ConvertToExclusive")
                data.Add(new XAttribute("ReleaseLockOnConversionToExclusiveFailure", "true"));
            if (type == "AmIAlone") data.Add(new XAttribute("TransitionID", transition));
            var message = new XElement(soap + "Envelope", new XElement(soap + "Body", new XElement(ns + "RequestCollection",
                new XElement(ns + "Request", new XAttribute("Url", fileUrl), new XAttribute("RequestToken", 1),
                    new XElement(ns + "SubRequest", new XAttribute("Type", type), new XAttribute("SubRequestToken", 1), data)))));
            using var response = await http.PostAsync(endpoint, new StringContent(message.ToString(), Encoding.UTF8, "text/xml"));
            response.EnsureSuccessStatusCode();
            var result = XDocument.Parse(await response.Content.ReadAsStringAsync()).Descendants(ns + "SubResponse").Single();
            return ((string?)result.Attribute("ErrorCode"), result.Element(ns + "SubResponseData"));
        }
    }

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
