#nullable enable
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Xml.Linq;
using Microsoft.Protocols.TestSuites.SharedAdapter;

namespace CellBridge.Interop.Tests;

[Collection("WebDemo integration")]
// Stateful S02 join/departure extension; see NOTICE-Interop-TestSuites.md.
public sealed class EditorKnowledgeInteropTests
{
    private static readonly Guid EditorsPartition = new("7808F4DD-2385-49D6-B7CE-37ACA5E43602");

    [MultiInstanceFact]
    public async Task EditorMembershipAndTransferredKnowledgeTrackJoinsAndDeparturesAcrossHosts()
    {
        var endpoint = new Uri(Environment.GetEnvironmentVariable("OFFICECOLLABSERVER_INTEROP_ENDPOINT")!);
        var peer = new Uri(Environment.GetEnvironmentVariable("OFFICECOLLABSERVER_INTEROP_PEER")!);
        using var firstHttp = LiveInteropHttp.Create(endpoint);
        using var secondHttp = LiveInteropHttp.Create(peer);
        var name = "editor-knowledge-" + Guid.NewGuid().ToString("N");
        using var created = await firstHttp.PostAsJsonAsync(new Uri(endpoint, "/api/documents"), new { name, type = "docx" });
        created.EnsureSuccessStatusCode();
        var path = "/shared/" + name + ".docx";
        var file = new Uri(LiveInteropHttp.PublicOrigin(endpoint), path);
        var first = new CellStorageClient(firstHttp, endpoint);
        var second = new CellStorageClient(secondHttp, peer);
        var schema = Guid.NewGuid();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var initial = await Query(first);
        AssertEditors(initial);
        var initialKnowledge = KnowledgeOf(initial);
        Assert.Empty((await Query(second, initialKnowledge)).DataElementPackage.DataElements);

        await Coauth(firstHttp, endpoint, a, "JoinCoauthoring");
        var one = await Query(second, initialKnowledge);
        AssertEditors(one, a);
        var secondKnowledge = KnowledgeOf(one);
        Assert.Empty((await Query(first, secondKnowledge)).DataElementPackage.DataElements);

        await Coauth(secondHttp, peer, b, "JoinCoauthoring");
        // Exercise stale knowledge at two different revisions; earlier probes
        // deliberately transfer knowledge between clients.
        var bothForFirst = await Query(first, initialKnowledge);
        var bothForSecond = await Query(second, secondKnowledge);
        AssertEditors(bothForFirst, a, b);
        AssertEditors(bothForSecond, a, b);
        var firstKnowledge = KnowledgeOf(bothForFirst);
        secondKnowledge = KnowledgeOf(bothForSecond);
        Assert.Empty((await Query(second, secondKnowledge)).DataElementPackage.DataElements);

        await Coauth(firstHttp, endpoint, a, "ExitCoauthoring");
        var remaining = await Query(second, secondKnowledge);
        AssertEditors(remaining, b);
        await Coauth(secondHttp, peer, b, "ExitCoauthoring");
        var departed = await Query(first, firstKnowledge);
        AssertEditors(departed);
        Assert.Empty((await Query(second, KnowledgeOf(departed))).DataElementPackage.DataElements);

        async Task<FsshttpbResponse> Query(CellStorageClient client, Knowledge? knowledge = null)
        {
            var request = InteropRequestFactory.QueryChanges(1);
            var query = Assert.IsType<QueryChangesCellSubRequest>(Assert.Single(request.SubRequests));
            query.IsPartitionIDGUIDUsed = true;
            query.PartitionIdGUID = EditorsPartition;
            if (knowledge is not null) query.Knowledge = knowledge;
            var result = await client.SendCellAsync(file.AbsoluteUri, request, EditorsPartition);
            Assert.Equal(HttpStatusCode.OK, result.StatusCode);
            var response = result.ParseBinaryResponse();
            Assert.False(response.Status);
            var sub = Assert.Single(response.CellSubResponses);
            Assert.False(sub.Status);
            Assert.Equal(1UL, sub.RequestID.DecodedValue);
            Assert.Equal((ulong)RequestTypes.QueryChanges, sub.RequestType.DecodedValue);
            Assert.False(Assert.IsType<QueryChangesSubResponseData>(sub.SubResponseData).PartialResult);
            return response;
        }

        async Task Coauth(HttpClient http, Uri target, Guid client, string operation)
        {
            XNamespace ns = CellStorageClient.ProtocolNamespace;
            XNamespace soap = "http://schemas.xmlsoap.org/soap/envelope/";
            var xml = new XElement(soap + "Envelope", new XElement(soap + "Body",
                new XElement(ns + "RequestVersion", new XAttribute("Version", 2), new XAttribute("MinorVersion", 2)),
                new XElement(ns + "RequestCollection", new XAttribute("CorrelationId", Guid.NewGuid()),
                    new XElement(ns + "Request", new XAttribute("Url", file), new XAttribute("RequestToken", 1),
                        new XElement(ns + "SubRequest", new XAttribute("Type", "Coauth"), new XAttribute("SubRequestToken", 1),
                            new XElement(ns + "SubRequestData", new XAttribute("CoauthRequestType", operation),
                                new XAttribute("ClientID", client), new XAttribute("SchemaLockID", schema), new XAttribute("Timeout", 3600)))))));
            using var message = new HttpRequestMessage(HttpMethod.Post, target)
            { Content = new StringContent(xml.ToString(), Encoding.UTF8, "text/xml") };
            message.Headers.TryAddWithoutValidation("SOAPAction", CellStorageClient.SoapAction);
            using var response = await http.SendAsync(message);
            response.EnsureSuccessStatusCode();
            var sub = Assert.Single(XDocument.Parse(await response.Content.ReadAsStringAsync()).Descendants(ns + "SubResponse"));
            Assert.Equal("1", (string?)sub.Attribute("SubRequestToken"));
            Assert.Equal("1", (string?)sub.Parent!.Attribute("RequestToken"));
            Assert.Equal("Success", (string?)sub.Attribute("ErrorCode"));
        }
    }

    private static Knowledge KnowledgeOf(FsshttpbResponse response) =>
        Assert.IsType<QueryChangesSubResponseData>(Assert.Single(response.CellSubResponses).SubResponseData).Knowledge;

    private static void AssertEditors(FsshttpbResponse response, params Guid[] expected)
    {
        // This bounded table fits in one FSSHTTPD chunk. Locate its stream in
        // independently decoded object data, then check the fixed stream header.
        byte[] header = [0x1A, 0x5A, 0x3A, 0x30, 0, 0, 0, 0];
        var objects = response.DataElementPackage.DataElements
            .Where(element => element.DataElementType == DataElementType.ObjectGroupDataElementData)
            .SelectMany(element => element.GetData<ObjectGroupDataElementData>().ObjectGroupData.ObjectGroupObjectDataList);
        var stream = Assert.Single(objects, item => item.Data.Content.Take(header.Length).SequenceEqual(header)).Data.Content.ToArray();
        using var compressed = new MemoryStream(stream, header.Length, stream.Length - header.Length);
        using var deflate = new DeflateStream(compressed, CompressionMode.Decompress);
        var xml = XDocument.Load(deflate);
        Assert.Equal("EditorsTable", xml.Root!.Name.LocalName);
        var actual = xml.Root.Elements("Editor").Select(editor => Guid.Parse(editor.Element("CacheID")!.Value)).Order().ToArray();
        Assert.Equal(expected.Order().ToArray(), actual);
    }
}
