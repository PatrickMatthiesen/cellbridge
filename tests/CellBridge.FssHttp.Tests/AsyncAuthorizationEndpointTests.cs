using System.Net;
using CellBridge.AspNetCore;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace CellBridge.FssHttp.Tests;

public sealed class AsyncAuthorizationEndpointTests
{
    [Fact]
    public async Task SharedAndHistoryDownloadsHideHeadersAndBytesAfterRevocation()
    {
        var authority = new MutableAuthority();
        var provider = TestStorage.Memory();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        TestActor.Register(builder.Services);
        builder.Services.AddSingleton<IDocumentAuthorizationProvider>(authority);
        builder.Services.AddCellBridge(provider, requireDurability: false);
        var app = builder.Build();
        app.UseAuthentication(); app.UseAuthorization(); app.MapCellBridge();
        var service = app.Services.GetRequiredService<CellBridgeDocumentService>();
        var id = Guid.NewGuid();
        var state = (await service.ImportExternalAsync(id, "/shared/async.docx", [7, 8, 9],
            new("tenant", "workspace"), TestActor.Value.Identity, TestActor.Value))!;
        await app.StartAsync();
        using var client = TestActor.CreateClient(app.GetTestServer(), "reader");
        try
        {
            var paths = new[] { "/shared/async.docx", $"/_cellbridge/history/{id:D}/{state.LifecycleGeneration}/1" };
            foreach (var path in paths)
            {
                using var get = await client.GetAsync(path);
                Assert.Equal(HttpStatusCode.OK, get.StatusCode);
                Assert.Equal(new byte[] { 7, 8, 9 }, await get.Content.ReadAsByteArrayAsync());
                using var head = await client.SendAsync(new(HttpMethod.Head, path));
                Assert.Equal(HttpStatusCode.OK, head.StatusCode);
            }

            authority.Allow = false;
            foreach (var path in paths)
            foreach (var method in new[] { HttpMethod.Get, HttpMethod.Head })
            {
                using var result = await client.SendAsync(new(method, path));
                Assert.Equal(HttpStatusCode.NotFound, result.StatusCode);
                Assert.Null(result.Headers.ETag);
                Assert.Null(result.Content.Headers.LastModified);
                Assert.Empty(await result.Content.ReadAsByteArrayAsync());
            }
        }
        finally { await app.DisposeAsync(); }
    }

    private sealed class MutableAuthority : IDocumentAuthorizationProvider
    {
        public string PolicyDomain => "tests:async-endpoints";
        public bool Allow { get; set; } = true;
        public ValueTask<DocumentAuthorizationDecision> AuthorizeAsync(DocumentAuthorizationRequest request,
            CancellationToken cancellationToken = default)
        {
            var now = DateTimeOffset.UtcNow;
            return ValueTask.FromResult(new DocumentAuthorizationDecision(Allow, now, now.AddMinutes(1)));
        }
    }
}
