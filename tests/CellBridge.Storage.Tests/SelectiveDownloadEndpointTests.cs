using System.Net;
using CellBridge.AspNetCore;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using CellBridge.Tests;
using CellBridge.TestFixtures;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;

namespace CellBridge.Storage.Tests;

public sealed class SelectiveDownloadEndpointTests
{
    [Fact]
    public async Task CurrentAndHistoricalDownloadsUseSelectiveReadsAndHeadSkipsContent()
    {
        var memoryContent = new InMemoryContentStore();
        var raw = new InMemoryDocumentRepository(memoryContent);
        var seedProvider = new StorageProvider(raw, memoryContent);
        var seedService = new CellBridgeDocumentService(seedProvider);
        var originalBytes = MinimalDocx.Create("history");
        var state = (await seedService.CreateAsync("/shared/selective-download.docx", originalBytes, TestActor.Value))!;
        var currentBytes = MinimalDocx.Create("current");
        state = (await seedService.ExecuteAsync(state.ResourceId,DocumentPartitionKind.FileContents,
            await StorageTestDocuments.SaveRequestAsync(seedService,state,currentBytes),new Dictionary<string,string>(),TestActor.Value)).State;

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
        Assert.True(countedContent.OpenReadCount > 0);
        var currentReads = countedContent.OpenReadCount;

        var historyPath = $"/_cellbridge/history/{state.ResourceId:D}/{state.LifecycleGeneration}/1";
        using var historyHead = await client.SendAsync(new(HttpMethod.Head, historyPath));
        Assert.Equal(HttpStatusCode.OK, historyHead.StatusCode);
        Assert.Equal(originalBytes.Length, historyHead.Content.Headers.ContentLength);
        Assert.Empty(await historyHead.Content.ReadAsByteArrayAsync());
        Assert.Equal(currentReads, countedContent.OpenReadCount);

        using var historyGet = await client.GetAsync(historyPath);
        Assert.Equal(HttpStatusCode.OK, historyGet.StatusCode);
        Assert.Equal(originalBytes, await historyGet.Content.ReadAsByteArrayAsync());
        Assert.True(countedContent.OpenReadCount > currentReads);
        var totalReads = countedContent.OpenReadCount;
        Assert.Equal(0, throwingState.FullReadCount);

        Assert.True((await raw.ExecuteControlAsync(new DocumentControlCommand.SetPermissions(state.ResourceId,StorageTestDocuments.Admission(state),
            state.Security with { Owner = "revoked" },state.AuthorityGeneration))).Changed);
        using var deniedCurrent = await client.GetAsync(state.Path);
        using var deniedHistory = await client.GetAsync(historyPath);
        Assert.Equal(HttpStatusCode.NotFound, deniedCurrent.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, deniedHistory.StatusCode);
        Assert.Equal(totalReads, countedContent.OpenReadCount);
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
