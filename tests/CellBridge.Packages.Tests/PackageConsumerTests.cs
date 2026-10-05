using System.Net;
using System.Text;
using CellBridge.AspNetCore;
using CellBridge.FssHttpB;
using CellBridge.FssHttp;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.Conformance;
using CellBridge.Storage.InMemory;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace CellBridge.Packages.Tests;

public sealed class PackageConsumerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RequestByteLimitRejectsKnownAndStreamingBodiesBeforeProtocolParsing(bool unknownLength)
    {
        var provider = new StorageProvider(new InMemoryStateStore(), new InMemoryContentStore());
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        TestActor.Register(builder.Services);
        builder.Services.AddCellBridge(provider, requireDurability: false, configure: o => o.MaxRequestBytes = 32);
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapCellBridge();
        await app.StartAsync();
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Test-User", "writer");
        using HttpContent content = unknownLength
            ? new StreamContent(new NonSeekableReadStream(new byte[33])) : new ByteArrayContent(new byte[33]);
        content.Headers.ContentType = new("text/xml");
        using var response = await client.PostAsync("/_vti_bin/cellstorage.svc", content);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Empty(await provider.State.ListAsync(0, 10));
        await app.StopAsync();
    }

    private sealed class NonSeekableReadStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }

    [Fact]
    public async Task CustomProviderPassesConformance()
    {
        var provider = new StorageProvider(
            new CustomStateProvider(),
            new InMemoryContentStore());

        await ProviderConformance.VerifyAsync(provider);
    }

    [Fact]
    public async Task PackagedHostServesDiscoveryDownloadAndCellRequests()
    {
        var provider = new StorageProvider(
            new CustomStateProvider(),
            new InMemoryContentStore());

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        TestActor.Register(builder.Services);
        builder.Services.AddCellBridge(provider, requireDurability: false);

        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapCellBridge();
        await app.StartAsync();

        var documents = app.Services.GetRequiredService<CellBridgeDocumentService>();
        byte[] bytes = [1, 2, 3, 4];
        var id = Guid.NewGuid();
        var document = await documents.CreateAsync(id, "/shared/example.bin", bytes, TestActor.Value);
        Assert.Equal(id, document!.ResourceId);
        Assert.NotNull(document);

        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Test-User", "writer");
        using var discovery = await client.SendAsync(new(HttpMethod.Options, "/shared"));
        Assert.Equal(HttpStatusCode.OK, discovery.StatusCode);
        Assert.True(discovery.Headers.Contains("X-MSFSSHTTP"));

        Assert.Equal(bytes, await client.GetByteArrayAsync("/shared/example.bin"));

        using var head = await client.SendAsync(new(HttpMethod.Head, "/shared/example.bin"));
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Equal(document.Etag, head.Headers.ETag?.ToString());
        Assert.Equal(bytes.Length, head.Content.Headers.ContentLength);

        using var request = new StringContent(CreateQueryAccessRequest(), Encoding.UTF8, "text/xml");
        using var response = await client.PostAsync("/_vti_bin/cellstorage.svc", request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var wire = await response.Content.ReadAsStringAsync();
        Assert.Contains("ErrorCode=\"Success\"", wire);
        var parsed = CellStorageRequestParser.Parse(CreateQueryAccessRequest());
        var processor = app.Services.GetRequiredService<CellBridgeRequestProcessor>();
        var direct = await processor.ExecuteAsync(parsed, "http://localhost", TestActor.Value);
        Assert.Equal(direct.Response.ToSoapEnvelope(), wire);
        Assert.Empty(direct.AcceptedSaves);
        var readOnly = await processor.ExecuteAsync(parsed, "http://localhost", TestActor.Value with
            { AccessLimit = new(id, DocumentAccess.Read) });
        var binary = FsshttpbResponse.Deserialize(new BinaryReaderEx(readOnly.Response.Responses[0].SubResponses[0].SubResponseDataBase64!));
        Assert.Null(Assert.IsType<QueryAccessSubResponseData>(binary.SubResponses[0].Data).ReadAccessError);
        Assert.Equal(CellBridgeAuthorization.AccessDeniedHResult,
            Assert.IsType<QueryAccessSubResponseData>(binary.SubResponses[0].Data).WriteAccessError!.ErrorCode);

        await app.StopAsync();
    }

    private static string CreateQueryAccessRequest()
    {
        var cell = new FsshttpbCellRequest();
        cell.SubRequests.Add(new(RequestTypes.QueryAccess)
        {
            RequestId = 1,
            Data = new QueryAccessSubRequestData()
        });

        return $"""
            <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
              <soap:Body>
                <RequestVersion xmlns="http://schemas.microsoft.com/sharepoint/soap/" Version="2" MinorVersion="2"/>
                <RequestCollection xmlns="http://schemas.microsoft.com/sharepoint/soap/" CorrelationId="{Guid.NewGuid():D}">
                  <Request Url="http://localhost/shared/example.bin" RequestToken="1">
                    <SubRequest Type="Cell" SubRequestToken="1">
                      <SubRequestData>{cell.ToBase64()}</SubRequestData>
                    </SubRequest>
                  </Request>
                </RequestCollection>
              </soap:Body>
            </soap:Envelope>
            """;
    }
}
