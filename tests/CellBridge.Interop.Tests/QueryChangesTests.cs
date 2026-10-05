using Microsoft.Protocols.TestSuites.SharedAdapter;

namespace CellBridge.Interop.Tests;

public sealed class QueryChangesTests
{
    [Fact]
    public void QueryChangesRequest_ContainsManifestAndCellChangeFlags()
    {
        var request = InteropRequestFactory.QueryChanges(11);
        var bytes = request.SerializeToByteList();

        Assert.True(bytes.Count > 40);
        Assert.Equal((ushort)12, request.ProtocolVersion);
        Assert.Equal((ushort)11, request.MinimumVersion);
    }

    [Fact]
    public void QueryChangesResponse_ParserCanReadGeneratedServerResponse()
    {
        var response = CellBridge.FssHttpB.StorageManifestBuilder.BuildQueryChangesResponse(
            requestId: 11, fileContent: System.Text.Encoding.UTF8.GetBytes("interop"));

        var parsed = FsshttpbResponse.DeserializeResponseFromByteArray(response.ToByteArray(), 0);
        Assert.False(parsed.Status);
        Assert.Single(parsed.CellSubResponses);
        Assert.Equal((ulong)RequestTypes.QueryChanges, parsed.CellSubResponses[0].RequestType.DecodedValue);
        Assert.NotNull(parsed.DataElementPackage);
        Assert.NotEmpty(parsed.DataElementPackage.DataElements);
    }
}

public sealed class LiveQueryChangesTests
{
    [LiveInteropFact]
    public async Task MixedPartitionQueriesAndIndependentKnowledgePassMicrosoftMtomClient()
    {
        var endpoint = new Uri(Environment.GetEnvironmentVariable("OFFICECOLLABSERVER_INTEROP_ENDPOINT")!);
        using var http = LiveInteropHttp.Create(endpoint);
        // Captured Word replay joins editors on /shared/test.docx concurrently.
        // Independent-knowledge assertions need a document that other tests do not mutate.
        var name = "mixed-queries-" + Guid.NewGuid().ToString("N");
        using var created = await System.Net.Http.Json.HttpClientJsonExtensions.PostAsJsonAsync(http,
            "/api/documents", new { name, type = "docx" });
        created.EnsureSuccessStatusCode();
        var path = "/shared/" + name + ".docx";
        var client = new CellStorageClient(http, endpoint);
        Guid metadata = new("383ADC0B-E66E-4438-95E6-E39EF9720122");
        Guid editors = new("7808F4DD-2385-49D6-B7CE-37ACA5E43602");
        var request = InteropRequestFactory.QueryChanges(1);
        var first = Assert.IsType<QueryChangesCellSubRequest>(request.SubRequests[0]);
        first.IsPartitionIDGUIDUsed = true; first.PartitionIdGUID = Guid.Empty;
        foreach (var (id, target) in new[] { (2UL, metadata), (3UL, editors), (4UL, metadata), (5UL, editors) })
            request.SubRequests.Add(new QueryChangesCellSubRequest(id)
            { IsPartitionIDGUIDUsed = true, PartitionIdGUID = target, IncludeStorageManifest = 1, IncludeCellChanges = 1 });
        var call = await client.SendCellAsync(path, request, metadata);
        Assert.Equal(System.Net.HttpStatusCode.OK, call.StatusCode);
        var parsed = call.ParseBinaryResponse();
        Assert.Equal(5, parsed.CellSubResponses.Count);
        Assert.All(parsed.CellSubResponses, s => Assert.False(s.Status));
        var indexes = parsed.CellSubResponses.Select(s => s.GetSubResponseData<QueryChangesSubResponseData>().StorageIndexExtendedGUID).ToArray();
        Assert.Equal(indexes[1].SerializeToByteList(), indexes[3].SerializeToByteList());
        Assert.Equal(indexes[2].SerializeToByteList(), indexes[4].SerializeToByteList());
        var ids = parsed.DataElementPackage.DataElements.Select(e => e.DataElementExtendedGUID.SerializeToByteList().ToArray()).ToArray();
        Assert.Equal(ids.Length, ids.Select(Convert.ToHexString).Distinct().Count());
        foreach (var index in indexes) Assert.Contains(ids, id => id.SequenceEqual(index.SerializeToByteList()));
        var knownRequest = InteropRequestFactory.QueryChanges(6); knownRequest.SubRequests.Clear();
        for (int i = 1; i <= 2; i++)
            knownRequest.SubRequests.Add(new QueryChangesCellSubRequest((ulong)(6 + i))
            { IsPartitionIDGUIDUsed = true, PartitionIdGUID = i == 1 ? metadata : editors,
                IncludeStorageManifest = 1, IncludeCellChanges = 1,
                Knowledge = parsed.CellSubResponses[i].GetSubResponseData<QueryChangesSubResponseData>().Knowledge });
        var knownCall = await client.SendCellAsync(path, knownRequest, Guid.Empty);
        Assert.Equal(System.Net.HttpStatusCode.OK, knownCall.StatusCode);
        var known = knownCall.ParseBinaryResponse();
        Assert.All(known.CellSubResponses, s => Assert.False(s.Status));
        Assert.Empty(known.DataElementPackage.DataElements);
    }

    [LiveInteropFact]
    public async Task RepeatedQueriesShareOnePackageReadableByMicrosoftClient()
    {
        var endpoint = new Uri(Environment.GetEnvironmentVariable("OFFICECOLLABSERVER_INTEROP_ENDPOINT")!);
        using var http = LiveInteropHttp.Create();
        var client = new CellStorageClient(http, endpoint);
        var initial = (await client.SendCellAsync("/shared/test.docx", InteropRequestFactory.QueryChanges(1), partitionId: null, getFileProps: true)).ParseBinaryResponse();
        var request = InteropRequestFactory.QueryChanges(11);
        Assert.IsType<QueryChangesCellSubRequest>(request.SubRequests[0]).Knowledge =
            Assert.IsType<QueryChangesSubResponseData>(initial.CellSubResponses[0].SubResponseData).Knowledge;
        request.SubRequests.Add(InteropRequestFactory.QueryChanges(22).SubRequests[0]);
        var response = await client.SendCellAsync("/shared/test.docx", request, partitionId: null, getFileProps: true);
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        var parsed = response.ParseBinaryResponse();
        Assert.False(parsed.Status);
        Assert.Equal(2, parsed.CellSubResponses.Count);
        Assert.All(parsed.CellSubResponses, s => Assert.False(s.Status));
        Assert.Equal(initial.DataElementPackage.DataElements.Count, parsed.DataElementPackage.DataElements.Count);
        Assert.All(parsed.CellSubResponses, s => Assert.IsType<QueryChangesSubResponseData>(s.SubResponseData));
    }

    [LiveInteropFact]
    public async Task FileKnowledgeProducedAndConsumedByMicrosoftClientReturnsNoKnownPayloads()
    {
        var endpoint = new Uri(Environment.GetEnvironmentVariable("OFFICECOLLABSERVER_INTEROP_ENDPOINT")!);
        using var http = LiveInteropHttp.Create();
        var client = new CellStorageClient(http, endpoint);
        var first = await client.SendCellAsync("/shared/test.docx", InteropRequestFactory.QueryChanges(1),
            partitionId: null, getFileProps: true);
        Assert.Equal(System.Net.HttpStatusCode.OK, first.StatusCode);
        var response = first.ParseBinaryResponse();
        Assert.NotEmpty(response.DataElementPackage.DataElements);
        var data = Assert.IsType<QueryChangesSubResponseData>(response.CellSubResponses[0].SubResponseData);
        var request = InteropRequestFactory.QueryChanges(2);
        Assert.IsType<QueryChangesCellSubRequest>(request.SubRequests[0]).Knowledge = data.Knowledge;
        var known = await client.SendCellAsync("/shared/test.docx", request, partitionId: null, getFileProps: true);
        Assert.Equal(System.Net.HttpStatusCode.OK, known.StatusCode);
        var parsed = known.ParseBinaryResponse();
        Assert.False(parsed.Status);
        Assert.Single(parsed.CellSubResponses);
        Assert.Empty(parsed.DataElementPackage.DataElements);
    }

    [LiveInteropFact]
    public async Task QueryAccessAndQueryChanges_UseIndependentSoapMtomClient()
    {
        var endpointText = Environment.GetEnvironmentVariable("OFFICECOLLABSERVER_INTEROP_ENDPOINT");
        Assert.False(string.IsNullOrWhiteSpace(endpointText));

        using var http = LiveInteropHttp.Create();
        var client = new CellStorageClient(http, new Uri(endpointText));
        var partition = new Guid("383ADC0B-E66E-4438-95E6-E39EF9720122");

        var access = await client.SendCellAsync("/shared/test.docx", InteropRequestFactory.QueryAccess(1), partition);
        Assert.Equal(System.Net.HttpStatusCode.OK, access.StatusCode);
        var accessResponse = access.ParseBinaryResponse();
        Assert.False(accessResponse.Status);
        Assert.Single(accessResponse.CellSubResponses);
        Assert.Equal((ulong)RequestTypes.QueryAccess, accessResponse.CellSubResponses[0].RequestType.DecodedValue);

        var changes = await client.SendCellAsync("/shared/test.docx", InteropRequestFactory.QueryChanges(2), partition);
        Assert.Equal(System.Net.HttpStatusCode.OK, changes.StatusCode);
        var changesResponse = changes.ParseBinaryResponse();
        Assert.False(changesResponse.Status);
        Assert.Single(changesResponse.CellSubResponses);
        Assert.Equal((ulong)RequestTypes.QueryChanges, changesResponse.CellSubResponses[0].RequestType.DecodedValue);
        Assert.NotNull(changesResponse.DataElementPackage);
    }
}
