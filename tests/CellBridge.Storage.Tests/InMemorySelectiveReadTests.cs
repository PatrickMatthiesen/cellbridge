using CellBridge.AspNetCore;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using CellBridge.Tests;
using CellBridge.TestFixtures;

namespace CellBridge.Storage.Tests;

public sealed class InMemorySelectiveReadTests
{
    [Fact]
    public async Task RevisionPagesAreNewestFirstAndCarryOneCurrentFence()
    {
        var provider = StorageTestDocuments.Memory();
        var service = new CellBridgeDocumentService(provider);
        var initial = (await service.CreateAsync("/shared/selective-history.docx", MinimalDocx.Create("first"), TestActor.Value))!;
        var saved = await service.ExecuteAsync(initial.ResourceId, DocumentPartitionKind.FileContents,
            await StorageTestDocuments.SaveRequestAsync(service, initial, MinimalDocx.Create("second")),
            new Dictionary<string, string>(), TestActor.Value);
        Assert.False(Assert.Single(saved.Response.SubResponses).Status);
        var reads = provider.Documents;

        var firstPage = (await reads.ReadRevisionsAsync(initial.ResourceId, 0, 1))!;
        var secondPage = (await reads.ReadRevisionsAsync(initial.ResourceId, 1, 1))!;
        var pastEnd = (await reads.ReadRevisionsAsync(initial.ResourceId, int.MaxValue, 1))!;

        var stored = (await provider.Documents.FindAsync(new DocumentIdentity.Resource(initial.ResourceId)))!;
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

        var selected = (await reads.ReadRevisionAsync(initial.ResourceId, 1))!;
        Assert.Equal(stored.StateVersion, selected.Current.StateVersion);
        Assert.Equal(1UL, selected.Revision!.RevisionNumber);
        Assert.Equal(initial.Package, selected.Revision.Package);
        Assert.Null((await reads.ReadRevisionAsync(initial.ResourceId, 99))!.Revision);
    }

    [Fact]
    public async Task MetadataReadsUseCurrentPathAndOmitDeletedDocuments()
    {
        var provider = StorageTestDocuments.Memory();
        var store = provider.Documents;
        var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/shared/selective-path.docx", MinimalDocx.Create(), TestActor.Value))!;
        var reads = store;

        var byId = (await reads.FindAsync(new DocumentIdentity.Resource(state.ResourceId)))!;
        var byPath = (await reads.FindAsync(new DocumentIdentity.Path(state.PathKey)))!;
        Assert.Equal(byId, byPath);
        Assert.Null(await reads.FindAsync(new DocumentIdentity.Resource(Guid.NewGuid())));
        Assert.Null(await reads.FindAsync(new DocumentIdentity.Path("missing")));

        Assert.True((await store.ChangeLifecycleAsync(new DocumentLifecycleCommand.Delete(state.ResourceId, state.LifecycleGeneration, state.StateVersion))).Applied);
        Assert.Null(await reads.FindAsync(new DocumentIdentity.Resource(state.ResourceId)));
        Assert.Null(await reads.FindAsync(new DocumentIdentity.Path(state.PathKey)));
        Assert.Null(await reads.ReadRevisionsAsync(state.ResourceId, 0, 10));
        var retained = (await reads.ReadRevisionAsync(state.ResourceId, 1))!;
        Assert.True(retained.Current.IsDeleted);
        Assert.Equal(state.Package, retained.Revision!.Package);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.GetRevisionSummaryAsync(state.ResourceId, 1, TestActor.Value).AsTask());
    }

    [Fact]
    public async Task ExplicitLegacyMigrationInitializesOnlyTheKnownRevisionWithoutMutatingInput()
    {
        var content = new InMemoryContentStore();
        var original = await LegacyStorageFixture.CreateAsync(content, "/shared/selective-legacy.docx", MinimalDocx.Create());
        var legacy = original with { Revisions = [] };
        var migration = await LegacyDocumentProjection.PrepareAsync(legacy, content, new());
        var revision = Assert.Single(migration.Revisions).Revision;
        Assert.Equal(Math.Max(1UL, original.ContentVersion), revision.RevisionNumber);
        Assert.Equal(original.Content.Sha256, revision.Package.Sha256);
        Assert.Equal(original.ModifiedUtc, revision.CreatedUtc);
        Assert.Equal(original.Security.ModifiedBy ?? original.Security.CreatedBy, revision.Author);
        Assert.Empty(legacy.Revisions);
    }

    [Fact]
    public async Task RevisionReadArgumentsAreValidatedWithoutLimitOverflow()
    {
        var provider = StorageTestDocuments.Memory();
        var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/shared/selective-arguments.docx", MinimalDocx.Create(), TestActor.Value))!;
        var reads = provider.Documents;

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => reads.ReadRevisionsAsync(state.ResourceId, -1, 1).AsTask());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => reads.ReadRevisionsAsync(state.ResourceId, 0, 0).AsTask());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => reads.ReadRevisionsAsync(state.ResourceId, 0, int.MaxValue).AsTask());
        var all = (await reads.ReadRevisionsAsync(state.ResourceId, 0, 256))!;
        Assert.Single(all.Revisions);
        Assert.Null(all.NextOffset);
    }
}
