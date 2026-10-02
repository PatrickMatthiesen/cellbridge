using System.Net;
using System.Text;
using CellBridge.AspNetCore;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.Conformance;
using CellBridge.Storage.InMemory;
using Microsoft.AspNetCore.TestHost;

// A consuming application controls provider composition. This example exercises
// a custom state provider without referencing CellBridge.Web or repository fixtures.
var provider = new StorageProvider(new CustomStateProvider(), new InMemoryContentStore());
await ProviderConformance.VerifyAsync(provider);
var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseTestServer();
builder.Services.AddCellBridge(provider, requireDurability: false);
await using var app = builder.Build();
app.MapCellBridge();
await app.StartAsync();
var service = app.Services.GetRequiredService<CellBridgeDocumentService>();
byte[] bytes = [1, 2, 3, 4];
var document = await service.CreateAsync("/shared/example.bin", bytes) ?? throw new InvalidOperationException("Create failed.");
using var client = app.GetTestClient();
using var discovery = await client.SendAsync(new(HttpMethod.Options, "/shared"));
if (discovery.StatusCode != HttpStatusCode.OK || !discovery.Headers.Contains("X-MSFSSHTTP")) throw new InvalidOperationException("Discovery failed.");
if (!(await client.GetByteArrayAsync("/shared/example.bin")).SequenceEqual(bytes)) throw new InvalidOperationException("Download failed.");
using var head = await client.SendAsync(new(HttpMethod.Head, "/shared/example.bin"));
if (head.Headers.ETag?.ToString() != document.Etag || head.Content.Headers.ContentLength != bytes.Length)
    throw new InvalidOperationException("HEAD metadata differs from its document snapshot.");
var cell = new CellBridge.FssHttpB.FsshttpbCellRequest();
cell.SubRequests.Add(new(CellBridge.FssHttpB.RequestTypes.QueryAccess)
{ RequestId = 1, Data = new CellBridge.FssHttpB.QueryAccessSubRequestData() });
var soap = $"""
    <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
    <soap:Body><RequestVersion xmlns="http://schemas.microsoft.com/sharepoint/soap/" Version="2" MinorVersion="2"/>
    <RequestCollection xmlns="http://schemas.microsoft.com/sharepoint/soap/" CorrelationId="{Guid.NewGuid():D}">
    <Request Url="http://localhost/shared/example.bin" RequestToken="1">
    <SubRequest Type="Cell" SubRequestToken="1"><SubRequestData>{cell.ToBase64()}</SubRequestData></SubRequest>
    </Request></RequestCollection></soap:Body></soap:Envelope>
    """;
using var response = await client.PostAsync("/_vti_bin/cellstorage.svc", new StringContent(soap, Encoding.UTF8, "text/xml"));
var envelope = await response.Content.ReadAsStringAsync();
if (!response.IsSuccessStatusCode || !envelope.Contains("ErrorCode=\"Success\"")) throw new InvalidOperationException("Cell endpoint failed: " + envelope);
Console.WriteLine("Packed NuGet consumer passed provider conformance, discovery, download, HEAD and Cell QueryAccess.");
await app.StopAsync();

sealed class CustomStateProvider : IDocumentStateStore
{
    private readonly InMemoryStateStore _inner = new();
    public bool Durable => false;
    public bool Shared => false;
    public ValueTask<DocumentState?> FindByResourceIdAsync(Guid id, CancellationToken ct = default) => _inner.FindByResourceIdAsync(id, ct);
    public ValueTask<DocumentState?> FindByPathKeyAsync(string key, CancellationToken ct = default) => _inner.FindByPathKeyAsync(key, ct);
    public ValueTask<IReadOnlyList<DocumentSummary>> ListAsync(int offset, int limit, CancellationToken ct = default) => _inner.ListAsync(offset, limit, ct);
    public ValueTask<bool> TryCreateAsync(DocumentState state, CancellationToken ct = default) => _inner.TryCreateAsync(state, ct);
    public ValueTask<T> TransitionAsync<T>(Guid id, Func<DocumentState, DateTime, StateTransition<T>> callback, CancellationToken ct = default) => _inner.TransitionAsync(id, callback, ct);
    public ValueTask CheckHealthAsync(CancellationToken ct = default) => _inner.CheckHealthAsync(ct);
}
