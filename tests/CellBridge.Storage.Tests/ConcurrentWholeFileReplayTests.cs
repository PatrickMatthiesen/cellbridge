using System.Text.Json;
using CellBridge.AspNetCore;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.PostgreSql;
using CellBridge.TestFixtures;
using Npgsql;

namespace CellBridge.Storage.Tests;

[Collection("PostgreSQL")]
public sealed class ConcurrentWholeFileReplayTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public Task ConcurrentRetryResolvesAcceptedReceiptWithoutPublishingAgain(bool atPublication, bool differentContent) =>
        CheckAsync(StorageTestDocuments.Memory(), atPublication, differentContent);

    [PostgreSqlFact]
    public async Task PostgreSqlConcurrentRetryResolvesReceiptAtBothBoundaries()
    {
        await using var source = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("ConnectionStrings__cellbridge")!);
        var content = new PostgreSqlContentStore(source);
        var provider = new StorageProvider(new PostgreSqlStateStore(source, validationContent: content), content);
        foreach (var atPublication in new[] { false, true })
            foreach (var differentContent in new[] { false, true })
                await CheckAsync(provider, atPublication, differentContent);
    }

    private static async Task CheckAsync(StorageProvider provider, bool atPublication, bool differentContent)
    {
        var winner = new CellBridgeDocumentService(provider);
        var head = (await winner.CreateAsync("/retry-" + Guid.NewGuid().ToString("N") + ".docx",
            MinimalDocx.Create("original"), TestActor.Value))!;
        Assert.True((await new SharedDocumentLocks(winner).ApplyAsync(head.ResourceId,
            HostLockOperation.Acquire, "lock", TestActor.Value)).Success);
        head = (await provider.Documents.ReadControlAsync(head.ResourceId))!.Head;
        Assert.True((await provider.Documents.UpdateDeliveryAsync(new DocumentDeliveryCommand.Bind(head.ResourceId,
            head.LifecycleGeneration, head.StateVersion, new(Guid.NewGuid(), "external", "baseline", 1)))).Applied);
        var paused = new PausedRepository(provider.Documents, atPublication);
        var loser = new CellBridgeDocumentService(new(paused, provider.Content));
        var bytes = MinimalDocx.Create("accepted");
        using var retryInput = new MemoryStream(differentContent ? MinimalDocx.Create("different") : bytes);
        var retry = loser.SaveWholeFileAsync(head.ResourceId, retryInput, "same-key", TestActor.Value, "lock").AsTask();
        WholeFileSaveResult accepted;
        try
        {
            await paused.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            using var input = new MemoryStream(bytes);
            accepted = await winner.SaveWholeFileAsync(head.ResourceId, input, "same-key", TestActor.Value, "lock");
        }
        finally { paused.Continue.TrySetResult(); }
        if (differentContent)
            await Assert.ThrowsAsync<WholeFileSaveConflictException>(() => retry.WaitAsync(TimeSpan.FromSeconds(10)));
        else
        {
            var replay = await retry.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(replay.IsReplay);
            Assert.Equal(JsonSerializer.Serialize(accepted.Revision), JsonSerializer.Serialize(replay.Revision));
        }
        var snapshot = (await provider.Documents.ExportDocumentAsync(head.ResourceId))!;
        Assert.Equal(2, snapshot.ApplicationRevisions.Length);
        Assert.Single(snapshot.Receipts);
        Assert.Single(snapshot.Deliveries);
        Assert.Equal(1, snapshot.Current.Head.Counters.WholeFileSaveReceipts);
        Assert.Equal(bytes, await StorageTestDocuments.PackageAsync(provider, snapshot.Current.Head.Package));
    }

    private sealed class PausedRepository(IDocumentRepository inner, bool atPublication) : RepositoryTestProxy(inner)
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private async Task Pause(CancellationToken ct)
        { Entered.TrySetResult(); await Continue.Task.WaitAsync(ct); }
        public override async ValueTask<SavePreparationRead?> PrepareSaveAsync(Guid id, SavePreparationRequest request, CancellationToken ct = default)
        {
            if (!atPublication) await Pause(ct);
            return await base.PrepareSaveAsync(id, request, ct);
        }
        public override async ValueTask<PublicationResult> PublishAsync(PreparedDocumentPublication publication, CancellationToken ct = default)
        {
            if (atPublication) await Pause(ct);
            return await base.PublishAsync(publication, ct);
        }
    }
}
