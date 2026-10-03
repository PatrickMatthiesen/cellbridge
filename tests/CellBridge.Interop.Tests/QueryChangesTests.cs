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
