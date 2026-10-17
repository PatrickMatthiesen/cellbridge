using System.Text.Json;
using CellBridge.AspNetCore;
using CellBridge.FssHttp;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using CellBridge.Storage.PostgreSql;
using CellBridge.Web;
using Npgsql;

namespace CellBridge.Storage.Tests;

public sealed class WholeFileSaveTests
{
    private static StorageProvider Memory() => new(new InMemoryStateStore(), new InMemoryContentStore());

    [Fact]
    public Task SaveReadBackHistoryAndReplay() => CheckHistory(Memory());

    [PostgreSqlFact]
    public async Task PostgreSqlIndependentProvidersRetainGraphHistoryAndReceipt()
    {
        var connection = Environment.GetEnvironmentVariable("ConnectionStrings__cellbridge")!;
        await using var first = NpgsqlDataSource.Create(connection);
        await using var second = NpgsqlDataSource.Create(connection);
        await CheckHistory(new(new PostgreSqlStateStore(first), new PostgreSqlContentStore(first)),
            new(new PostgreSqlStateStore(second), new PostgreSqlContentStore(second)));
    }

    private static async Task CheckHistory(StorageProvider provider, StorageProvider? peer = null)
    {
        peer ??= provider;
        var service = new CellBridgeDocumentService(provider);
        var before = (await service.CreateAsync("/whole-" + Guid.NewGuid().ToString("N") + ".docx",
            MinimalDocx.Create("original"), TestActor.Value))!;
        var publisher = new ExternalRevisionPublisher(provider, new UnusedDestination());
        Assert.True(await publisher.BindAsync(before.ResourceId, before.LifecycleGeneration, before.StateVersion,
            Guid.NewGuid(), "external", "baseline"));
        Assert.True((await new SharedDocumentLocks(service).ApplyAsync(before.ResourceId,
            HostLockOperation.Acquire, "lock", TestActor.Value)).Success);
        var bytes = MinimalDocx.Create("whole replacement");
        var accepted = await Save(service, before.ResourceId, bytes, "first");
        Assert.False(accepted.IsReplay);
        Assert.Equal(before.ContentVersion + 1, accepted.Revision.ContentVersion);
        var saved = (await peer.State.FindByResourceIdAsync(before.ResourceId))!;
        Assert.Equal(bytes, await peer.Content.ReadVerifiedAsync(saved.Content));
        Assert.Equal(bytes, (await DocumentPartition.RestoreFileGraphAsync(saved, peer.Content, peer.Limits)).Materialize());
        Assert.Equal(2, saved.Revisions.Length);
        Assert.Equal(JsonSerializer.Serialize(before.Revisions[0]), JsonSerializer.Serialize(saved.Revisions[0]));
        Assert.All(before.Partitions[0].Elements, e => Assert.Contains(saved.Partitions[0].Elements,
            x => x.Id == e.Id && x.Payload == e.Payload));
        Assert.Equal(before.Partitions[0].Identity, saved.Partitions[0].Identity);
        Assert.True(saved.Partitions[0].Knowledge > before.Partitions[0].Knowledge);
        Assert.Single(saved.Publication!.Pending);
        Assert.Single(saved.WholeFileSaveReceipts);
        var later = await Save(new(peer), before.ResourceId, MinimalDocx.Create("later"), "second");
        var replay = await Save(new(peer), before.ResourceId, bytes, "first");
        Assert.True(replay.IsReplay);
        Assert.Equal(JsonSerializer.Serialize(accepted.Revision), JsonSerializer.Serialize(replay.Revision));
        saved = (await peer.State.FindByResourceIdAsync(before.ResourceId))!;
        Assert.Equal(later.Revision.ContentVersion, saved.ContentVersion);
        Assert.Equal(3, saved.Revisions.Length);
        Assert.Equal(2, saved.Publication!.Pending.Length);
        await Assert.ThrowsAsync<WholeFileSaveConflictException>(() => Save(service, before.ResourceId,
            MinimalDocx.Create("different retry"), "first"));
        var noOp = await Save(service, before.ResourceId, MinimalDocx.Create("later"), "no-op");
        Assert.Equal(later.Revision.ContentVersion, noOp.Revision.ContentVersion);
        Assert.Equal(3, (await provider.State.FindByResourceIdAsync(before.ResourceId))!.Revisions.Length);
        // A receipt replays after lock expiry without acquiring fresh write authority.
        await provider.State.TransitionAsync(before.ResourceId, (s, now) => new StateTransition<bool>(s with
        { Coordination = s.Coordination with { HostLock = s.Coordination.HostLock! with { ExpiresUtc = now.AddSeconds(-1) } } }, true));
        Assert.True((await Save(service, before.ResourceId, bytes, "first")).IsReplay);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("LOCK")]
    [InlineData("other")]
    public async Task MissingOrMismatchedTokenCannotWriteNonemptyDocument(string? token)
    {
        var provider = Memory(); var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/locked.docx", MinimalDocx.Create(), TestActor.Value))!;
        await new SharedDocumentLocks(service).ApplyAsync(state.ResourceId, HostLockOperation.Acquire, "lock", TestActor.Value);
        await Assert.ThrowsAsync<DocumentOperationLockException>(() => Save(service, state.ResourceId,
            MinimalDocx.Create("replacement"), "key", token));
        Assert.Equal(state.Content, (await provider.State.FindByResourceIdAsync(state.ResourceId))!.Content);
    }

    [Fact]
    public async Task EmptyUnlockedFileCanBeWrittenButExplicitExpiredTokenCannotDowngrade()
    {
        var provider = Memory(); var service = new CellBridgeDocumentService(provider);
        var empty = (await service.CreateAsync("/empty.docx", [], TestActor.Value))!;
        var result = await Save(service, empty.ResourceId, MinimalDocx.Create(), "empty", null);
        Assert.Equal(2u, result.Revision.ContentVersion);
        await new SharedDocumentLocks(service).ApplyAsync(empty.ResourceId, HostLockOperation.Acquire, "lock", TestActor.Value);
        await provider.State.TransitionAsync(empty.ResourceId, (s, now) => new StateTransition<bool>(s with
        { Coordination = s.Coordination with { HostLock = s.Coordination.HostLock! with { ExpiresUtc = now.AddSeconds(-1) } } }, true));
        await Assert.ThrowsAsync<DocumentOperationLockException>(() => Save(service, empty.ResourceId,
            MinimalDocx.Create("expired"), "expired"));
    }

    [Theory]
    [InlineData("refresh")]
    [InlineData("reacquire")]
    [InlineData("expire")]
    [InlineData("revoke")]
    [InlineData("compete")]
    public async Task ChangesDuringPreparationCannotOverwriteCurrentState(string change)
    {
        var provider = Memory(); var service = new CellBridgeDocumentService(provider);
        var original = (await service.CreateAsync("/race.docx", MinimalDocx.Create(), TestActor.Value))!;
        var locks = new SharedDocumentLocks(service);
        await locks.ApplyAsync(original.ResourceId, HostLockOperation.Acquire, "lock", TestActor.Value);
        var paused = new PausedContent(provider.Content);
        var racing = Save(new(new(provider.State, paused)), original.ResourceId, MinimalDocx.Create("stale"), "stale");
        await paused.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        if (change == "refresh") await locks.ApplyAsync(original.ResourceId, HostLockOperation.Refresh, "lock", TestActor.Value);
        if (change == "reacquire")
        {
            await locks.ApplyAsync(original.ResourceId, HostLockOperation.Release, "lock", TestActor.Value);
            await locks.ApplyAsync(original.ResourceId, HostLockOperation.Acquire, "lock", TestActor.Value);
        }
        if (change == "expire") await provider.State.TransitionAsync(original.ResourceId, (s, now) => new StateTransition<bool>(s with
        { Coordination = s.Coordination with { HostLock = s.Coordination.HostLock! with { ExpiresUtc = now.AddSeconds(-1) } } }, true));
        if (change == "revoke") await provider.State.TransitionAsync(original.ResourceId, (s, now) => new StateTransition<bool>(
            DocumentPermissionUpdates.Apply(s, now, s.Security with { Owner = "revoked" }), true));
        if (change == "compete") await Save(service, original.ResourceId, MinimalDocx.Create("winner"), "winner");
        var expected = (await provider.State.FindByResourceIdAsync(original.ResourceId))!;
        paused.Continue.SetResult();
        if (change == "revoke") await Assert.ThrowsAsync<UnauthorizedAccessException>(() => racing);
        else await Assert.ThrowsAnyAsync<InvalidOperationException>(() => racing);
        var actual = (await provider.State.FindByResourceIdAsync(original.ResourceId))!;
        Assert.Equal(expected.Content, actual.Content);
        Assert.Equal(expected.ContentVersion, actual.ContentVersion);
        Assert.Equal(expected.Revisions.Length, actual.Revisions.Length);
    }

    [Fact]
    public async Task PermissionCeilingAndPolicyDenyNewWritesAndReceiptReplay()
    {
        var provider = Memory(); var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/permission.docx", MinimalDocx.Create(), TestActor.Value))!;
        await new SharedDocumentLocks(service).ApplyAsync(state.ResourceId, HostLockOperation.Acquire, "lock", TestActor.Value);
        var bytes = MinimalDocx.Create("accepted");
        await Save(service, state.ResourceId, bytes, "accepted");
        var limited = TestActor.Value with { AccessLimit = new(state.ResourceId, DocumentAccess.Read) };
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.SaveWholeFileAsync(state.ResourceId,
            new MemoryStream(bytes), "accepted", limited, "lock").AsTask());
        await provider.State.TransitionAsync(state.ResourceId, (s, _) => new StateTransition<bool>(s with
            { Security = s.Security with { Owner = "other" } }, true));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Save(service, state.ResourceId, bytes, "accepted"));
    }

    [Fact]
    public async Task FssHttpLeaseExcludesWholeFileSave()
    {
        var provider = Memory(); var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/foreign.docx", [], TestActor.Value))!;
        await provider.State.TransitionAsync(state.ResourceId, (s, now) => new StateTransition<bool>(s with
        { Coordination = s.Coordination with { Exclusive = new("foreign", null, now.AddMinutes(5), 0, null) } }, true));
        await Assert.ThrowsAsync<DocumentOperationLockException>(() => Save(service, state.ResourceId,
            MinimalDocx.Create(), "foreign", null));
        await Assert.ThrowsAsync<DocumentOperationLockException>(() => Save(service, state.ResourceId,
            MinimalDocx.Create(), "foreign", "foreign"));
    }

    [Fact]
    public async Task ReceiptBudgetRejectsNewSavesWithoutDroppingRetryProtection()
    {
        var provider = new StorageProvider(new InMemoryStateStore(), new InMemoryContentStore(),
            new StorageLimits { MaxWholeFileSaveReceipts = 1 });
        var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/receipt-budget.docx", MinimalDocx.Create(), TestActor.Value))!;
        await new SharedDocumentLocks(service).ApplyAsync(state.ResourceId, HostLockOperation.Acquire, "lock", TestActor.Value);
        var bytes = MinimalDocx.Create("accepted");
        await Save(service, state.ResourceId, bytes, "accepted");
        var before = (await provider.State.FindByResourceIdAsync(state.ResourceId))!;
        await Assert.ThrowsAsync<StorageQuotaExceededException>(() => Save(service, state.ResourceId,
            MinimalDocx.Create("rejected"), "new"));
        Assert.True((await Save(service, state.ResourceId, bytes, "accepted")).IsReplay);
        Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(await provider.State.FindByResourceIdAsync(state.ResourceId)));
    }

    [Fact]
    public async Task ProtocolSaveAfterWholeFileReplacementUsesItsNewGraphAndRejectsStaleIndex()
    {
        var provider = Memory(); var service = new CellBridgeDocumentService(provider);
        var before = (await service.CreateAsync("/protocol-after-whole.docx", MinimalDocx.Create(), TestActor.Value))!;
        var stale = RevisionHistoryTests.Request(before, MinimalDocx.Create("stale protocol bytes"));
        var locks = new SharedDocumentLocks(service);
        await locks.ApplyAsync(before.ResourceId, HostLockOperation.Acquire, "lock", TestActor.Value);
        await Save(service, before.ResourceId, MinimalDocx.Create("whole-file save"), "whole");
        await locks.ApplyAsync(before.ResourceId, HostLockOperation.Release, "lock", TestActor.Value);
        var saved = (await provider.State.FindByResourceIdAsync(before.ResourceId))!;
        var refused = await service.ExecuteAsync(before.ResourceId, DocumentPartitionKind.FileContents, stale,
            new Dictionary<string, string>(), TestActor.Value);
        Assert.True(Assert.Single(refused.Response.SubResponses).Status);
        Assert.Equal(saved.Content, refused.State.Content);
        var final = await RevisionHistoryTests.Save(service, saved, "new protocol bytes");
        Assert.Equal(3, final.Revisions.Length);
        Assert.Equal(MinimalDocx.Create("new protocol bytes"),
            (await DocumentPartition.RestoreFileGraphAsync(final, provider.Content, provider.Limits)).Materialize());
    }

    private static async Task<WholeFileSaveResult> Save(CellBridgeDocumentService service, Guid id, byte[] bytes,
        string key, string? token = "lock")
    {
        using var source = new MemoryStream(bytes);
        return await service.SaveWholeFileAsync(id, source, key, TestActor.Value, token);
    }

    private sealed class UnusedDestination : IExternalRevisionDestination
    {
        public ValueTask<ExternalDeliveryResult> CompareExchangeAsync(ExternalDeliveryRequest request, Stream content,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("No delivery expected.");
    }

    private sealed class PausedContent(IContentStore inner) : IContentStore
    {
        private int _writes;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Durable => inner.Durable;
        public bool Shared => inner.Shared;
        public async ValueTask<ContentHandle> WriteAsync(Stream source, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _writes) == 1) { Entered.SetResult(); await Continue.Task.WaitAsync(cancellationToken); }
            return await inner.WriteAsync(source, cancellationToken);
        }
        public ValueTask<Stream> OpenReadAsync(ContentHandle handle, CancellationToken cancellationToken = default) => inner.OpenReadAsync(handle, cancellationToken);
    }
}
