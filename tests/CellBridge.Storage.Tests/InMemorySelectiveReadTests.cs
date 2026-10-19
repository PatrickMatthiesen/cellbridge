using CellBridge.AspNetCore;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using CellBridge.Tests;
using CellBridge.Web;

namespace CellBridge.Storage.Tests;

public sealed class InMemorySelectiveReadTests
{
    [Fact]
    public async Task RevisionPagesAreNewestFirstAndCarryOneCurrentFence()
    {
        var provider = new StorageProvider(new InMemoryStateStore(), new InMemoryContentStore());
        var service = new CellBridgeDocumentService(provider);
        var initial = (await service.CreateAsync("/shared/selective-history.docx", MinimalDocx.Create("first"), TestActor.Value))!;
        var latest = await provider.State.TransitionAsync(initial.ResourceId, (current, now) =>
        {
            var next = current with { ContentVersion = current.ContentVersion + 1, ModifiedUtc = now };
            next = RevisionHistory.Append(current, next, TestActor.Value.Identity, now);
            return new StateTransition<DocumentState>(next, next);
        });
        var reads = (IDocumentReadStore)provider.State;

        var firstPage = (await reads.ReadRevisionSummariesAsync(initial.ResourceId, 0, 1))!;
        var secondPage = (await reads.ReadRevisionSummariesAsync(initial.ResourceId, 1, 1))!;
        var pastEnd = (await reads.ReadRevisionSummariesAsync(initial.ResourceId, int.MaxValue, 1))!;

        var stored = (await provider.State.FindByResourceIdAsync(initial.ResourceId))!;
        Assert.Equal(stored.StateVersion, firstPage.Current.StateVersion);
        Assert.Equal(stored.LifecycleGeneration, firstPage.Current.LifecycleGeneration);
        Assert.Equal(stored.Security, firstPage.Current.Security);
        Assert.Equal(2UL, firstPage.Current.LatestRevisionNumber);
        Assert.Equal(2UL, Assert.Single(firstPage.Revisions).RevisionNumber);
        Assert.Equal(1, firstPage.NextOffset);
        Assert.Equal(1UL, Assert.Single(secondPage.Revisions).RevisionNumber);
        Assert.Null(secondPage.NextOffset);
        Assert.Empty(pastEnd.Revisions);
        Assert.Null(pastEnd.NextOffset);

        var selected = (await reads.ReadRevisionSummaryAsync(initial.ResourceId, 1))!;
        Assert.Equal(stored.StateVersion, selected.Current.StateVersion);
        Assert.Equal(1UL, selected.Revision!.RevisionNumber);
        Assert.Equal(initial.Content, selected.Revision.Content);
        Assert.Null((await reads.ReadRevisionSummaryAsync(initial.ResourceId, 99))!.Revision);
    }

    [Fact]
    public async Task MetadataReadsUseCurrentPathAndOmitDeletedDocuments()
    {
        var store = new InMemoryStateStore();
        var provider = new StorageProvider(store, new InMemoryContentStore());
        var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/shared/selective-path.docx", MinimalDocx.Create(), TestActor.Value))!;
        var reads = (IDocumentReadStore)store;

        var byId = (await reads.FindReadMetadataByResourceIdAsync(state.ResourceId))!;
        var byPath = (await reads.FindReadMetadataByPathKeyAsync(state.PathKey))!;
        Assert.Equal(byId, byPath);
        Assert.Null(await reads.FindReadMetadataByResourceIdAsync(Guid.NewGuid()));
        Assert.Null(await reads.FindReadMetadataByPathKeyAsync("missing"));

        var lifecycle = (IDocumentLifecycleStore)store;
        Assert.True(await lifecycle.TryDeleteAsync(state.ResourceId, state.LifecycleGeneration, state.StateVersion));
        Assert.Null(await reads.FindReadMetadataByResourceIdAsync(state.ResourceId));
        Assert.Null(await reads.FindReadMetadataByPathKeyAsync(state.PathKey));
        Assert.Null(await reads.ReadRevisionSummariesAsync(state.ResourceId, 0, 10));
        Assert.Null(await reads.ReadRevisionSummaryAsync(state.ResourceId, 1));
    }

    [Fact]
    public async Task LegacyEmptyHistoryUsesInitializedRevisionMetadataWithoutChangingStoredState()
    {
        var source = new StorageProvider(new InMemoryStateStore(), new InMemoryContentStore());
        var service = new CellBridgeDocumentService(source);
        var original = (await service.CreateAsync("/shared/selective-legacy.docx", MinimalDocx.Create(), TestActor.Value))!;
        var legacy = original with { Revisions = [] };
        var store = new InMemoryStateStore();
        Assert.True(await store.TryCreateAsync(legacy));

        var read = (await ((IDocumentReadStore)store).ReadRevisionSummariesAsync(original.ResourceId, 0, 10))!;

        var revision = Assert.Single(read.Revisions);
        Assert.Equal(Math.Max(1UL, original.ContentVersion), revision.RevisionNumber);
        Assert.Equal(original.Content, revision.Content);
        Assert.Equal(original.ModifiedUtc, revision.CreatedUtc);
        Assert.Equal(original.Security.ModifiedBy ?? original.Security.CreatedBy, revision.Author);
        Assert.Empty((await store.FindByResourceIdAsync(original.ResourceId))!.Revisions);
    }

    [Fact]
    public async Task RevisionReadArgumentsAreValidatedWithoutLimitOverflow()
    {
        var provider = new StorageProvider(new InMemoryStateStore(), new InMemoryContentStore());
        var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/shared/selective-arguments.docx", MinimalDocx.Create(), TestActor.Value))!;
        var reads = (IDocumentReadStore)provider.State;

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => reads.ReadRevisionSummariesAsync(state.ResourceId, -1, 1).AsTask());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => reads.ReadRevisionSummariesAsync(state.ResourceId, 0, 0).AsTask());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => reads.ReadRevisionSummariesAsync(state.ResourceId, 0, int.MaxValue).AsTask());
        var all = (await reads.ReadRevisionSummariesAsync(state.ResourceId, 0, 256))!;
        Assert.Single(all.Revisions);
        Assert.Null(all.NextOffset);
    }
}
