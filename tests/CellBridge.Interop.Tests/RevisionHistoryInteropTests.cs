#nullable enable
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Xml.Linq;

namespace CellBridge.Interop.Tests;

public sealed class RevisionHistoryInteropTests
{
    [LiveInteropFact]
    public async Task TwoHostsListDownloadRestoreAndRenameTheSameDurableHistory()
    {
        var endpoint = new Uri(Environment.GetEnvironmentVariable("OFFICECOLLABSERVER_INTEROP_ENDPOINT")!);
        var peerSetting = Environment.GetEnvironmentVariable("OFFICECOLLABSERVER_INTEROP_PEER");
        if (string.IsNullOrWhiteSpace(peerSetting)) return;
        var peer = new Uri(peerSetting);
        using var first = LiveInteropHttp.Create(endpoint); using var second = LiveInteropHttp.Create(peer);
        var name = "History " + Guid.NewGuid().ToString("N")[..12];
        using var created = await first.PostAsJsonAsync("/api/documents", new { name, type = "docx" });
        created.EnsureSuccessStatusCode();
        var url = new Uri(endpoint, "/shared/" + Uri.EscapeDataString(name + ".docx")).AbsoluteUri;
        var original = await first.GetByteArrayAsync(url);
        XNamespace ns = CellStorageClient.ProtocolNamespace;
        async Task<XElement> Send(HttpClient http, Uri target, string type, string? id = null, params (string Key, string Value)[] attrs)
        {
            XNamespace soap = "http://schemas.xmlsoap.org/soap/envelope/";
            var request = new XElement(ns + "Request", new XAttribute("Url", url), new XAttribute("RequestToken", 1));
            if (id is not null) request.Add(new XAttribute("UseResourceID", "true"), new XAttribute("ResourceID", id));
            var subRequest = new XElement(ns + "SubRequest", new XAttribute("Type", type), new XAttribute("SubRequestToken", 1));
            if (type != "GetVersions") subRequest.Add(new XElement(ns + "SubRequestData", attrs.Select(a => new XAttribute(a.Key, a.Value))));
            request.Add(subRequest);
            var envelope = new XElement(soap + "Envelope", new XElement(soap + "Body",
                new XElement(ns + "RequestVersion", new XAttribute("Version", 2), new XAttribute("MinorVersion", 2)),
                new XElement(ns + "RequestCollection", new XAttribute("CorrelationId", Guid.NewGuid()), request)));
            using var response = await http.PostAsync(target, new StringContent(envelope.ToString(), Encoding.UTF8, "text/xml"));
            response.EnsureSuccessStatusCode();
            return XDocument.Parse(await response.Content.ReadAsStringAsync()).Descendants(ns + "Response").Single();
        }
        var listed = await Send(first, endpoint, "GetVersions");
        var id = (string)listed.Attribute("ResourceID")!;
        var result = listed.Descendants().Single(e => e.Name.LocalName == "result");
        var oldPath = new Uri((string)result.Attribute("url")!).PathAndQuery;
        Assert.Equal(original, await second.GetByteArrayAsync(oldPath));
        var restore = await Send(second, peer, "Versioning", id, ("VersioningRequestType", "RestoreVersion"), ("Version", "1.0"));
        Assert.Equal("Success", (string?)restore.Elements().Single().Attribute("ErrorCode"));
        var newest = await Send(first, endpoint, "GetVersions", id);
        Assert.Equal(2, newest.Descendants().Count(e => e.Name.LocalName == "result"));
        Assert.Equal(original, await second.GetByteArrayAsync(oldPath));
        var renamedName = name + " renamed #%.docx";
        var rename = await Send(second, peer, "FileOperation", id, ("FileOperation", "Rename"), ("NewFileName", renamedName));
        Assert.Equal("Success", (string?)rename.Elements().Single().Attribute("ErrorCode"));
        Assert.Equal(id, (string?)rename.Attribute("ResourceID"));
        Assert.EndsWith(Uri.EscapeDataString(renamedName), (string?)rename.Attribute("Url"));
        Assert.Equal(original, await first.GetByteArrayAsync("/shared/" + Uri.EscapeDataString(renamedName)));
        using var missing = await first.GetAsync(url);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(original, await first.GetByteArrayAsync(oldPath));
    }
}
