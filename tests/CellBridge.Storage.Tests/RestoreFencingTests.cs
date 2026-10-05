using System.Collections.Immutable;
using CellBridge.AspNetCore;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using CellBridge.Web;

namespace CellBridge.Storage.Tests;

public sealed class RestoreFencingTests
{
    [Fact]
    public async Task LostCommitReplyResolvesExactReceiptAndPinsExternalRevision()
    {
        var provider = Memory(); var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/lost-ack.docx", MinimalDocx.Create(), TestActor.Value))!;
        await provider.State.TransitionAsync(state.ResourceId, (current, _) => new StateTransition<bool>(current with
        { Publication = new(Guid.NewGuid(), "external-id", "etag", 1, []) }, true));
        var lost = new CellBridgeDocumentService(new(new LostReply(provider.State), provider.Content));
        var result = await lost.RestoreRevisionAsync(state.ResourceId, 1, 1, "lost-reply", TestActor.Value);
        Assert.True(result.IsReplay);
        var current = (await provider.State.FindByResourceIdAsync(state.ResourceId))!;
        Assert.Equal(2, current.Revisions.Length); Assert.Single(current.RestoreReceipts);
        var pending = Assert.Single(current.Publication!.Pending);
        Assert.Equal(result.Revision.Content, pending.Content);
        Assert.Contains(pending.Content, StorageReferences.Handles(current));
        Assert.Equal(state.Coordination.Generation, current.Coordination.Generation);
    }

    [Theory]
    [InlineData("permission")]
    [InlineData("metadata")]
    [InlineData("lifecycle")]
    [InlineData("lock")]
    public async Task PreparationCannotOverrideNewAuthorityOrMetadata(string mutation)
    {
        var provider = Memory(); var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/fence.docx", MinimalDocx.Create(), TestActor.Value))!;
        var gate = new PausedRead(provider.Content);
        var preparing = new CellBridgeDocumentService(new(provider.State, gate)).RestoreRevisionAsync(state.ResourceId, 1, 1,
            "fenced", TestActor.Value).AsTask();
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var changed = await provider.State.TransitionAsync(state.ResourceId, (current, _) =>
        {
            var next = mutation switch
            {
                "permission" => current with { Security = current.Security with { Owner = "revoked" } },
                "metadata" => current with { Partitions = current.Partitions.Select(p => p.Kind == 1
                    ? p with { Knowledge = p.Knowledge + 1, InlineContent = [42] } : p).ToImmutableArray() },
                "lifecycle" => current with { LifecycleGeneration = current.LifecycleGeneration + 1 },
                _ => current with { Coordination = current.Coordination with
                    { Exclusive = new("lock", null, DateTime.UtcNow.AddMinutes(1), 0, null, TestActor.Value.Identity.Subject) } },
            };
            return new StateTransition<DocumentState>(next, next);
        });
        gate.Resume.TrySetResult();
        if (mutation == "permission") await Assert.ThrowsAsync<UnauthorizedAccessException>(() => preparing);
        else await Assert.ThrowsAnyAsync<InvalidOperationException>(() => preparing);
        var after = (await provider.State.FindByResourceIdAsync(state.ResourceId))!;
        Assert.Equal(changed.ContentVersion, after.ContentVersion); Assert.Single(after.Revisions); Assert.Empty(after.RestoreReceipts);
        Assert.Equal(changed.Partitions, after.Partitions);
    }

    [Fact]
    public async Task ConcurrentSaveAndRestoreHaveOneWinnerFromTheSameGraph()
    {
        var provider = Memory(); var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/save-race.docx", MinimalDocx.Create(), TestActor.Value))!;
        var save = service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents,
            RevisionHistoryTests.Request(state, MinimalDocx.Create("concurrent")), new Dictionary<string, string>(), TestActor.Value).AsTask();
        var restore = service.RestoreRevisionAsync(state.ResourceId, 1, 1, "race", TestActor.Value).AsTask();
        CellExecution saved = await save;
        bool restoreSucceeded;
        try { await restore; restoreSucceeded = true; } catch (InvalidOperationException) { restoreSucceeded = false; }
        Assert.NotEqual(restoreSucceeded, !saved.Response.SubResponses[0].Status);
        var final = (await provider.State.FindByResourceIdAsync(state.ResourceId))!;
        Assert.Equal(2, final.Revisions.Length); Assert.Equal(2U, final.ContentVersion);
    }

    private static StorageProvider Memory() => new(new InMemoryStateStore(), new InMemoryContentStore());
    private sealed class PausedRead(IContentStore inner) : IContentStore
    {
        private int _entered;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Durable => false; public bool Shared => false;
        public ValueTask<ContentHandle> WriteAsync(Stream stream, CancellationToken ct = default) => inner.WriteAsync(stream, ct);
        public async ValueTask<Stream> OpenReadAsync(ContentHandle handle, CancellationToken ct = default)
        {
            if (Interlocked.Exchange(ref _entered, 1) == 0) { Entered.TrySetResult(); await Resume.Task.WaitAsync(ct); }
            return await inner.OpenReadAsync(handle, ct);
        }
    }
    private sealed class LostReply(IDocumentStateStore inner) : IDocumentStateStore
    {
        public bool Durable => false; public bool Shared => false;
        public ValueTask<DocumentState?> FindByResourceIdAsync(Guid id, CancellationToken ct = default) => inner.FindByResourceIdAsync(id, ct);
        public ValueTask<DocumentState?> FindByPathKeyAsync(string key, CancellationToken ct = default) => inner.FindByPathKeyAsync(key, ct);
        public ValueTask<IReadOnlyList<DocumentSummary>> ListAsync(int offset, int limit, CancellationToken ct = default) => inner.ListAsync(offset, limit, ct);
        public ValueTask<bool> TryCreateAsync(DocumentState state, CancellationToken ct = default) => inner.TryCreateAsync(state, ct);
        public ValueTask CheckHealthAsync(CancellationToken ct = default) => inner.CheckHealthAsync(ct);
        public async ValueTask<T> TransitionAsync<T>(Guid id, Func<DocumentState, DateTime, StateTransition<T>> callback, CancellationToken ct = default)
        {
            bool committed = false;
            var result = await inner.TransitionAsync(id, (state, now) => { var next = callback(state, now); committed = next.Next is not null; return next; }, ct);
            if (committed) throw new StorageUnavailableException("Injected lost reply.");
            return result;
        }
    }
}
