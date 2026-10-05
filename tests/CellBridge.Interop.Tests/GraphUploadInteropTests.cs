using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using CellBridge.FssHttp;
using CellBridge.FssHttpB;
using CellBridge.Tests;
using Reference = Microsoft.Protocols.TestSuites.SharedAdapter;

namespace CellBridge.Interop.Tests;

public sealed class GraphUploadInteropTests
{
    [MultiInstanceFact]
    public async Task CompleteMetadataAndDistinctMappingPatchesAreCoherentAcrossTwoHttpHosts()
    {
        var endpoint = new Uri(Environment.GetEnvironmentVariable("OFFICECOLLABSERVER_INTEROP_ENDPOINT")!);
        var peer = new Uri(Environment.GetEnvironmentVariable("OFFICECOLLABSERVER_INTEROP_PEER")!);
        using var http = LiveInteropHttp.Create();
        var name = "metadata-graph-" + Guid.NewGuid().ToString("N");
        using var created = await http.PostAsJsonAsync(new Uri(endpoint, "/api/documents"), new { name, type = "docx" });
        created.EnsureSuccessStatusCode();
        var path = new Uri(endpoint, "/shared/" + name + ".docx").ToString();
        var before = await http.GetByteArrayAsync(path);
        var target = new Guid("383ADC0B-E66E-4438-95E6-E39EF9720122");
        var f = new GraphFixture("opaque metadata, not a ZIP file"u8.ToArray(), blob: true);
        var request = new FsshttpbCellRequest { DataElementPackage = new(), SubRequests = { new(RequestTypes.PutChanges)
            { RequestId = 1, TargetPartitionId = target, Data = new PutChangesSubRequestData { StorageIndex = f.Index, HasAdditionalFlags = true, AdditionalFlagsBits = 1 } } } };
        request.DataElementPackage.DataElements.AddRange(f.Elements);
        var saved = await Send(http, endpoint, path, request, target);
        Assert.False(Assert.Single(saved.Reference.CellSubResponses).Status);
        var replay = await Send(http, peer, path, request, target);
        Assert.Equal(saved.Bytes, replay.Bytes);
        var read = await Send(http, peer, path, Query(2, target), target);
        var expected = read.Server.DataElementPackage!.DataElements.Single(e => e.DataElementExtendedGuid.Equals(f.Index));
        var left = f.CreatePatch(expected, false);
        var right = f.CreatePatch(expected, true);
        var patches = await Task.WhenAll(Send(http, endpoint, path, left, target), Send(http, peer, path, right, target));
        Assert.All(patches, result => Assert.False(Assert.Single(result.Reference.CellSubResponses).Status));
        var mixed = Query(10, target); mixed.SubRequests.Add(Query(20, target).SubRequests[0]);
        mixed.SubRequests.Add(Query(30, Guid.Empty).SubRequests[0]);
        var reopened = await Send(http, peer, path, mixed, target);
        Assert.All(reopened.Reference.CellSubResponses, s => Assert.False(s.Status));
        Assert.Equal(reopened.Reference.DataElementPackage.DataElements.Count,
            reopened.Reference.DataElementPackage.DataElements.Select(e => Convert.ToHexString(e.DataElementExtendedGUID.SerializeToByteList().ToArray())).Distinct().Count());
        var metadataIndex = Assert.IsType<QueryChangesSubResponseData>(reopened.Server.SubResponses[0].Data).StorageIndexExtendedGuid;
        var graph = GenericPartitionGraphSnapshot.Create(reopened.Server.DataElementPackage!.DataElements, metadataIndex);
        Assert.NotEqual(f.Revision, graph.GetCurrentRevision(f.Cell));
        Assert.NotEqual(f.OtherRevision, graph.GetCurrentRevision(f.OtherCell));
        Assert.Equal(f.Bytes, Assert.Single(graph.GetObjectPartitions(f.Cell, f.Child)).Content);
        Assert.Equal(before, await http.GetByteArrayAsync(new Uri(peer, new Uri(path).AbsolutePath)));
        var stale = await Send(http, endpoint, path, f.CreatePatch(expected, false), target);
        Assert.True(Assert.Single(stale.Reference.CellSubResponses).Status);
        Assert.Equal((ulong)CellErrorCode.CoherencyFailure, Assert.Single(stale.Server.SubResponses).Error!.ErrorCode);
    }

    [MultiInstanceFact]
    public async Task RequestedAppliedIndexIsIndependentlyDecodedAndReplayedAcrossHosts()
    {
        var endpoint = new Uri(Environment.GetEnvironmentVariable("OFFICECOLLABSERVER_INTEROP_ENDPOINT")!);
        var peer = new Uri(Environment.GetEnvironmentVariable("OFFICECOLLABSERVER_INTEROP_PEER")!);
        using var http = LiveInteropHttp.Create();
        var name = "applied-index-" + Guid.NewGuid().ToString("N");
        using var created = await http.PostAsJsonAsync(new Uri(endpoint, "/api/documents"), new { name, type = "docx" });
        created.EnsureSuccessStatusCode();
        var path = new Uri(endpoint, "/shared/" + name + ".docx").ToString();
        var initial = await Send(http, endpoint, path, Query(10), Guid.Empty);
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures/save-first.json")));
        var side = fixture.RootElement.GetProperty("request");
        var parts = MtomMessageParser.Parse(Convert.FromBase64String(side.GetProperty("bodyBase64").GetString()!), side.GetProperty("contentType").GetString()!);
        var request = FsshttpbCellRequest.Deserialize(new(Assert.Single(parts, p => p.ContentType.Contains("application/octet-stream")).Content));
        var put = Assert.IsType<PutChangesSubRequestData>(request.SubRequests[0].Data);
        put.ExpectedStorageIndex = Assert.IsType<QueryChangesSubResponseData>(initial.Server.SubResponses[0].Data).StorageIndexExtendedGuid;
        put.AdditionalFlagsBits |= 1;
        var saved = await Send(http, endpoint, path, request, Guid.Empty);
        var reference = Assert.IsType<Reference.PutChangesSubResponseData>(Assert.Single(saved.Reference.CellSubResponses).SubResponseData);
        var id = reference.PutChangesResponse.AppliedStorageIndexID;
        Assert.NotEqual(0u, id.Type);
        var index = Assert.Single(saved.Reference.DataElementPackage.DataElements);
        Assert.Equal(id.SerializeToByteList(), index.DataElementExtendedGUID.SerializeToByteList());
        Assert.NotNull(reference.Knowledge);
        var replay = await Send(http, peer, path, request, Guid.Empty);
        Assert.Equal(saved.Bytes, replay.Bytes);
        var queries = Query(20); queries.SubRequests.Add(Query(30).SubRequests[0]);
        var reopened = await Send(http, peer, path, queries, Guid.Empty);
        Assert.All(reopened.Reference.CellSubResponses, s => Assert.False(s.Status));
        Assert.Contains(reopened.Reference.DataElementPackage.DataElements,
            e => e.DataElementExtendedGUID.SerializeToByteList().SequenceEqual(id.SerializeToByteList()));
    }

    internal static FsshttpbCellRequest Query(ulong id, Guid? target = null) => new()
    { SubRequests = { new(RequestTypes.QueryChanges) { RequestId = id, TargetPartitionId = target,
        Data = new QueryChangesSubRequestData { IncludeStorageManifest = true, IncludeCellChanges = true } } } };

    internal static async Task<(byte[] Bytes, FsshttpbResponse Server, Reference.FsshttpbResponse Reference)> Send(
        HttpClient http, Uri endpoint, string path, FsshttpbCellRequest request, Guid partition)
    {
        const string ns = "http://schemas.microsoft.com/sharepoint/soap/";
        var soap = new XElement(XName.Get("Envelope", "http://schemas.xmlsoap.org/soap/envelope/"),
            new XElement(XName.Get("Body", "http://schemas.xmlsoap.org/soap/envelope/"),
                new XElement(XName.Get("RequestVersion", ns), new XAttribute("Version", 2), new XAttribute("MinorVersion", 2)),
                new XElement(XName.Get("RequestCollection", ns),
                    new XElement(XName.Get("Request", ns), new XAttribute("Url", path), new XAttribute("RequestToken", 1),
                        new XElement(XName.Get("SubRequest", ns), new XAttribute("Type", "Cell"), new XAttribute("SubRequestToken", 1),
                            new XElement(XName.Get("SubRequestData", ns), new XAttribute("PartitionID", partition), request.ToBase64()))))));
        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint)
        { Content = new StringContent(soap.ToString(), Encoding.UTF8, "text/xml") };
        message.Headers.TryAddWithoutValidation("SOAPAction", "\"" + CellStorageRequest.ExecuteAction + "\"");
        using var response = await http.SendAsync(message);
        response.EnsureSuccessStatusCode();
        var xml = XDocument.Parse(await response.Content.ReadAsStringAsync());
        var sub = xml.Descendants().Single(x => x.Name.LocalName == "SubResponse");
        Assert.Equal("Success", sub.Attribute("ErrorCode")?.Value);
        var bytes = Convert.FromBase64String(sub.Elements().Single(x => x.Name.LocalName == "SubResponseData").Value);
        var independent = Reference.FsshttpbResponse.DeserializeResponseFromByteArray(bytes, 0);
        Assert.False(independent.Status);
        return (bytes, FsshttpbResponse.Deserialize(new(bytes)), independent);
    }
}
