using System.Net;
using CellBridge.AspNetCore;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using CellBridge.Tests;
using CellBridge.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;

namespace CellBridge.Storage.Tests;

public sealed class SelectiveDownloadEndpointTests
{
    [Fact]
    public async Task CurrentAndHistoricalDownloadsUseSelectiveReadsAndHeadSkipsContent()
    {
        var raw = new InMemoryStateStore();
        var memoryContent = new InMemoryContentStore();
        var seedProvider = new StorageProvider(raw, memoryContent);
        var seedService = new CellBridgeDocumentService(seedProvider);
        var originalBytes = MinimalDocx.Create("history");
        var state = (await seedService.CreateAsync("/shared/selective-download.docx", originalBytes, TestActor.Value))!;
        var currentBytes = MinimalDocx.Create("current");
        ContentHandle currentHandle;
        await using (var source = new MemoryStream(currentBytes))
            currentHandle = await memoryContent.WriteAsync(source);
        await raw.TransitionAsync(state.ResourceId, (current, now) =>
        {
            var next = current with
            {
                Content = currentHandle,
                ContentVersion = current.ContentVersion + 1,
                ModifiedUtc = now,
            };
            next = RevisionHistory.Append(current, next, TestActor.Value.Identity, now);
            return new StateTransition<bool>(next, true);
        });

        var countedContent = new CountingContentStore(memoryContent);
        var throwingState = new SelectiveOnlyStateStore(raw);
        var provider = new StorageProvider(throwingState, countedContent);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddCellBridge(provider, requireDurability: false);
        TestActor.Register(builder.Services);
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapCellBridge();
        await app.StartAsync();
        using var client = TestActor.CreateClient(app.GetTestServer(), "writer");

        using var currentHead = await client.SendAsync(new(HttpMethod.Head, state.Path));
        Assert.Equal(HttpStatusCode.OK, currentHead.StatusCode);
        Assert.Equal(currentBytes.Length, currentHead.Content.Headers.ContentLength);
        Assert.Empty(await currentHead.Content.ReadAsByteArrayAsync());
        Assert.Equal(0, countedContent.OpenReadCount);

        using var currentGet = await client.GetAsync(state.Path);
        Assert.Equal(HttpStatusCode.OK, currentGet.StatusCode);
        Assert.Equal(currentBytes, await currentGet.Content.ReadAsByteArrayAsync());
        Assert.Equal(1, countedContent.OpenReadCount);

        var historyPath = $"/_cellbridge/history/{state.ResourceId:D}/{state.LifecycleGeneration}/1";
        using var historyHead = await client.SendAsync(new(HttpMethod.Head, historyPath));
        Assert.Equal(HttpStatusCode.OK, historyHead.StatusCode);
        Assert.Equal(originalBytes.Length, historyHead.Content.Headers.ContentLength);
        Assert.Empty(await historyHead.Content.ReadAsByteArrayAsync());
        Assert.Equal(1, countedContent.OpenReadCount);

        using var historyGet = await client.GetAsync(historyPath);
        Assert.Equal(HttpStatusCode.OK, historyGet.StatusCode);
        Assert.Equal(originalBytes, await historyGet.Content.ReadAsByteArrayAsync());
        Assert.Equal(2, countedContent.OpenReadCount);
        Assert.Equal(0, throwingState.FullReadCount);

        await raw.TransitionAsync(state.ResourceId, (current, _) => new StateTransition<bool>(current with
            { Security = current.Security with { Owner = "revoked" } }, true));
        using var deniedCurrent = await client.GetAsync(state.Path);
        using var deniedHistory = await client.GetAsync(historyPath);
        Assert.Equal(HttpStatusCode.NotFound, deniedCurrent.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, deniedHistory.StatusCode);
        Assert.Equal(2, countedContent.OpenReadCount);
        Assert.Equal(0, throwingState.FullReadCount);
    }

    private sealed class CountingContentStore(IContentStore inner) : IContentStore
    {
        private int _openReadCount;
        public bool Durable => inner.Durable;
        public bool Shared => inner.Shared;
        public int OpenReadCount => Volatile.Read(ref _openReadCount);
        public ValueTask<ContentHandle> WriteAsync(Stream source, CancellationToken cancellationToken = default) =>
            inner.WriteAsync(source, cancellationToken);
        public ValueTask<Stream> OpenReadAsync(ContentHandle handle, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _openReadCount);
            return inner.OpenReadAsync(handle, cancellationToken);
        }
    }
}
