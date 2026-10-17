using CellBridge.AspNetCore;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;

namespace CellBridge.DocumentLibrary.Tests;

public sealed class UploadRecoveryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DefinitivePathConflictDiscardsOnlyTheRejectedUploadAndDoesNotBlockStartup(bool cached)
    {
        using var files = new TemporaryDirectory();
        await using var harness = new DocumentLibraryHarness(files.Path, cached: cached);
        var baseline = TestOfficeFile.Docx("existing state without a destination manifest");
        var existing = await new CellBridgeDocumentService(harness.Provider).CreateAsync("/shared/conflict.docx",
            baseline, DocumentLibraryHarness.Owner);
        Assert.NotNull(existing);

        var rejected = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.UploadAsync(
            TestOfficeFile.Docx("rejected upload"), "conflict.docx"));

        Assert.Contains("rejected the document path or resource ID", rejected.Message);
        Assert.Empty(await harness.Destination.ListAsync());
        Assert.Equal(existing, await harness.Provider.State.FindByResourceIdAsync(existing.ResourceId));
        await harness.Library.InitializeAsync();
        var accepted = await harness.UploadAsync(TestOfficeFile.Docx("subsequent accepted upload"), "accepted.docx");
        await harness.Library.InitializeAsync();
        var manifest = Assert.Single(await harness.Destination.ListAsync());
        Assert.Equal(accepted.ResourceId, manifest.ResourceId);
        Assert.True(manifest.CellBridgeBound);
        Assert.False(manifest.PendingCreation);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UncertainCreateExceptionRetainsStagingAndRecoversOnlyWhenStateCommitted(bool commitBeforeThrow)
    {
        using var files = new TemporaryDirectory();
        var store = new ThrowOnCreateStore(new InMemoryStateStore(), commitBeforeThrow);
        await using var harness = new DocumentLibraryHarness(files.Path,
            new(store, new InMemoryContentStore()), cached: true);
        await Assert.ThrowsAsync<IOException>(() => harness.UploadAsync(TestOfficeFile.Docx("uncertain create")));
        var staged = Assert.Single(await harness.Destination.ListAsync());
        Assert.True(staged.PendingCreation);
        Assert.False(staged.CellBridgeBound);
        var state = await store.FindByResourceIdAsync(staged.ResourceId);
        if (commitBeforeThrow)
        {
            Assert.NotNull(state);
            await harness.Library.InitializeAsync();
            var recovered = await harness.Destination.GetAsync(staged.ResourceId);
            Assert.True(recovered.CellBridgeBound);
            Assert.False(recovered.PendingCreation);
            Assert.NotNull((await store.FindByResourceIdAsync(staged.ResourceId))!.Publication);
        }
        else
        {
            Assert.Null(state);
            var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Library.InitializeAsync());
            Assert.Contains("cannot be imported automatically", refused.Message);
            Assert.True((await harness.Destination.GetAsync(staged.ResourceId)).PendingCreation);
        }
    }

    private sealed class ThrowOnCreateStore(IDocumentStateStore inner, bool commitBeforeThrow) : IDocumentStateStore
    {
        public bool Durable => inner.Durable;
        public bool Shared => inner.Shared;
        public ValueTask<DocumentState?> FindByResourceIdAsync(Guid resourceId, CancellationToken cancellationToken = default) =>
            inner.FindByResourceIdAsync(resourceId, cancellationToken);
        public ValueTask<DocumentState?> FindByPathKeyAsync(string pathKey, CancellationToken cancellationToken = default) =>
            inner.FindByPathKeyAsync(pathKey, cancellationToken);
        public ValueTask<IReadOnlyList<DocumentSummary>> ListAsync(int offset, int limit, CancellationToken cancellationToken = default) =>
            inner.ListAsync(offset, limit, cancellationToken);
        public async ValueTask<bool> TryCreateAsync(DocumentState state, CancellationToken cancellationToken = default)
        {
            if (commitBeforeThrow) Assert.True(await inner.TryCreateAsync(state, cancellationToken));
            throw new IOException("Create outcome was not observed.");
        }
        public ValueTask<T> TransitionAsync<T>(Guid resourceId, Func<DocumentState, DateTime, StateTransition<T>> transition,
            CancellationToken cancellationToken = default) => inner.TransitionAsync(resourceId, transition, cancellationToken);
        public ValueTask CheckHealthAsync(CancellationToken cancellationToken = default) => inner.CheckHealthAsync(cancellationToken);
    }
}
