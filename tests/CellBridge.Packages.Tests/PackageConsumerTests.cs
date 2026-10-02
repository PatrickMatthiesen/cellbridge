using System.Net;
using System.Text;
using CellBridge.AspNetCore;
using CellBridge.FssHttpB;
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
        builder.Services.AddCellBridge(provider, requireDurability: false);

        await using var app = builder.Build();
        app.MapCellBridge();
        await app.StartAsync();

        var documents = app.Services.GetRequiredService<CellBridgeDocumentService>();
        byte[] bytes = [1, 2, 3, 4];
        var document = await documents.CreateAsync("/shared/example.bin", bytes);
        Assert.NotNull(document);

        using var client = app.GetTestClient();
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
        Assert.Contains("ErrorCode=\"Success\"", await response.Content.ReadAsStringAsync());

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
