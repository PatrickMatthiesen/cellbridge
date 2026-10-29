using System.Security.Cryptography;
using System.Text.Json;
using CellBridge.AspNetCore;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.PostgreSql;
using CellBridge.TestFixtures;
using CellBridge.Tests;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CellBridge.Storage.Tests;

[Collection("PostgreSQL")]
public sealed class PostgreSqlAtomicDocumentSaveTests
{
    [PostgreSqlFact]
    public Task DurableStagingPrecedesOneAtomicHeadHistoryReceiptAndCounterPublication() => WithDatabase(async source =>
    {
        var provider = Provider(source); var head = await Locked(provider);
        var plan = await PublicationTestPlans.WholeFileAsync(provider, head.ResourceId, MinimalDocx.Create("atomic"));
        foreach (var handle in plan.Partitions.SelectMany(partition => partition.AddedElements).Select(element => element.Payload))
            Assert.Equal(handle.Length, (await provider.Content.ReadVerifiedAsync(handle)).LongLength);
        Assert.Single((await provider.Documents.ReadRevisionsAsync(head.ResourceId, 0, 10))!.Revisions);
        var accepted = await provider.Documents.PublishAsync(plan); Assert.Equal(PublicationStatus.Accepted, accepted.Status);
        var snapshot = (await provider.Documents.ExportDocumentAsync(head.ResourceId))!;
        Assert.Equal(2, snapshot.ApplicationRevisions.Length); Assert.Single(snapshot.Receipts);
        Assert.Equal(snapshot.Current.Head.Counters.StoredBytes, NormalizedDocumentRecovery.StoredBytes(snapshot));
        Assert.Equal(PublicationStatus.Replay, (await provider.Documents.PublishAsync(plan with { Admission = StorageTestDocuments.Admission(accepted.Current) })).Status);
    });
    [PostgreSqlFact]
    public Task SharedImmutableContentDoesNotSerializeIndependentDocumentPublications() => WithDatabase(async source =>
    {
        var provider = Provider(source); var first = await Locked(provider); var second = await Locked(provider);
        var firstPlan = await PublicationTestPlans.WholeFileAsync(provider, first.ResourceId, MinimalDocx.Create("first"), "first");
        var secondPlan = await PublicationTestPlans.WholeFileAsync(provider, second.ResourceId, MinimalDocx.Create("second"), "second");
        using var commonInput = new MemoryStream(RandomNumberGenerator.GetBytes(4096)); var common = await provider.Content.WriteAsync(commonInput);
        firstPlan = firstPlan with { NewDocumentReferences = firstPlan.NewDocumentReferences.Add(common) };
        secondPlan = secondPlan with { NewDocumentReferences = secondPlan.NewDocumentReferences.Add(common) };
        var pause = new NormalizedHeadPause(first.ResourceId);
        IDocumentRepository held = new PostgreSqlStateStore(new DbContextOptionsBuilder<CellBridgeDbContext>().UseNpgsql(source).AddInterceptors(pause).Options);
        var pending = held.PublishAsync(firstPlan).AsTask();
        try
        {
            await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(PublicationStatus.Accepted, (await provider.Documents.PublishAsync(secondPlan).AsTask().WaitAsync(TimeSpan.FromSeconds(5))).Status);
            Assert.False(pending.IsCompleted);
        }
        finally { pause.Continue.TrySetResult(); }
        Assert.Equal(PublicationStatus.Accepted, (await pending).Status);
        Assert.Equal(4096, (await provider.Content.ReadVerifiedAsync(common)).Length);
    });
    [PostgreSqlFact]
    public Task RejectedPublicationKeepsTheHeadAndReceiptsWhileStagedContentRemainsAnOrphan() => WithDatabase(async source =>
    {
        var provider = Provider(source); var head = await Locked(provider);
        var plan = await PublicationTestPlans.WholeFileAsync(provider, head.ResourceId, MinimalDocx.Create("rejected"));
        var before = await provider.Documents.ExportDocumentAsync(head.ResourceId);
        IDocumentRepository restricted = new PostgreSqlStateStore(source, new() { MaxHistoryRevisions = 1 });
        await Assert.ThrowsAsync<StorageQuotaExceededException>(() => restricted.PublishAsync(plan).AsTask());
        Assert.Equal(PortabilityTests.CanonicalSnapshots([before!]), PortabilityTests.CanonicalSnapshots([(await provider.Documents.ExportDocumentAsync(head.ResourceId))!]));
        Assert.True((await new StorageMaintenance(source).CollectOrphansAsync()).OrphanObjects > 0);
    });
    [PostgreSqlFact]
    public Task CancellationAfterHeadSqlRollsBackPublicationAndPreservesTheCallersToken() => WithDatabase(async source =>
    {
        var provider = Provider(source); var head = await Locked(provider);
        var plan = await PublicationTestPlans.WholeFileAsync(provider, head.ResourceId, MinimalDocx.Create("cancelled"));
        var before = await provider.Documents.ExportDocumentAsync(head.ResourceId);
        var pause = new NormalizedHeadPause(head.ResourceId);
        IDocumentRepository held = new PostgreSqlStateStore(new DbContextOptionsBuilder<CellBridgeDbContext>().UseNpgsql(source).AddInterceptors(pause).Options);
        using var cancellation = new CancellationTokenSource(); var pending = held.PublishAsync(plan, cancellation.Token).AsTask();
        try
        {
            await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); cancellation.Cancel();
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending); Assert.Equal(cancellation.Token, error.CancellationToken);
        }
        finally { pause.Continue.TrySetResult(); }
        Assert.Equal(PortabilityTests.CanonicalSnapshots([before!]), PortabilityTests.CanonicalSnapshots([(await provider.Documents.ExportDocumentAsync(head.ResourceId))!]));
    });
    [PostgreSqlFact]
    public Task StalePreparationLeavesTheDocumentUnchangedAndItsStagedContentDurable() => WithDatabase(async source =>
    {
        var provider = Provider(source); var head = await Locked(provider);
        var plan = await PublicationTestPlans.WholeFileAsync(provider, head.ResourceId, MinimalDocx.Create("stale"));
        var before = await provider.Documents.ExportDocumentAsync(head.ResourceId);
        var rejected = await provider.Documents.PublishAsync(plan with { Fence = plan.Fence with { PreparedStateVersion = head.StateVersion - 1 } });
        Assert.Equal(PublicationStatus.Reprepare, rejected.Status);
        Assert.Equal(PortabilityTests.CanonicalSnapshots([before!]), PortabilityTests.CanonicalSnapshots([(await provider.Documents.ExportDocumentAsync(head.ResourceId))!]));
        foreach (var handle in plan.NewDocumentReferences) Assert.Equal(handle.Length, (await provider.Content.ReadVerifiedAsync(handle)).LongLength);
    });
    [PostgreSqlFact]
    public Task LostCommitReplyRetainsAcceptedRootsAndIsResolvedWithoutPublishingTwice() => WithDatabase(async source =>
    {
        var provider = Provider(source); var head = await Locked(provider);
        var plan = await PublicationTestPlans.WholeFileAsync(provider, head.ResourceId, MinimalDocx.Create("accepted"));
        var commands = new NormalizedHeadPause(head.ResourceId) { Pause = false };
        IDocumentRepository uncertain = new PostgreSqlStateStore(new DbContextOptionsBuilder<CellBridgeDbContext>().UseNpgsql(source)
            .AddInterceptors(commands, new NormalizedLostCommitReply(commands)).Options);
        await Assert.ThrowsAsync<StorageUnavailableException>(() => uncertain.PublishAsync(plan).AsTask());
        var current = (await provider.Documents.ReadControlAsync(head.ResourceId))!.Head;
        Assert.Equal(PublicationStatus.Replay, (await provider.Documents.PublishAsync(plan with { Admission = StorageTestDocuments.Admission(current) })).Status);
        var after = (await provider.Documents.ExportDocumentAsync(head.ResourceId))!; Assert.Equal(2, after.ApplicationRevisions.Length); Assert.Single(after.Receipts);
    });
    private static Task WithDatabase(Func<NpgsqlDataSource, Task> test) => BudgetAndQueryTests.WithDatabase(new(), test);
    private static StorageProvider Provider(NpgsqlDataSource source) => new(new PostgreSqlStateStore(source), new PostgreSqlContentStore(source));
    private static async Task<DocumentHead> Locked(StorageProvider provider)
    {
        var service = new CellBridgeDocumentService(provider); var head = (await service.CreateAsync("/atomic-" + Guid.NewGuid().ToString("N") + ".docx", MinimalDocx.Create(), TestActor.Value))!;
        Assert.True((await new SharedDocumentLocks(service).ApplyAsync(head.ResourceId, HostLockOperation.Acquire, "host", TestActor.Value)).Success);
        return (await provider.Documents.FindAsync(new DocumentIdentity.Resource(head.ResourceId)))!;
    }
}
