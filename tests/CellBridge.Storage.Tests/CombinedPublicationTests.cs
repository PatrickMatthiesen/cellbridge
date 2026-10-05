using System.Text.Json;
using CellBridge.AspNetCore;
using CellBridge.FssHttpB;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using CellBridge.Storage.PostgreSql;
using CellBridge.Tests;
using CellBridge.Web;
using Npgsql;

namespace CellBridge.Storage.Tests;

public sealed class CombinedPublicationTests
{
    [Fact]
    public Task MemoryMetadataFileRestoreAndDeliveryShareOnePublicationBoundary() => CheckPublication(Memory());

    [PostgreSqlFact]
    public async Task PostgreSqlMetadataFileRestoreAndDeliveryShareOnePublicationBoundary()
    {
        await using var source = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("ConnectionStrings__cellbridge")!);
        await CheckPublication(new(new PostgreSqlStateStore(source), new PostgreSqlContentStore(source)));
    }

    private static async Task CheckPublication(StorageProvider provider)
    {
        var service = new CellBridgeDocumentService(provider);
        var initial = (await service.CreateAsync("/combined-" + Guid.NewGuid() + ".docx", MinimalDocx.Create(), TestActor.Value))!;
        Assert.True(await new ExternalRevisionPublisher(provider, new UnusedDestination()).BindAsync(
            initial.ResourceId, initial.LifecycleGeneration, initial.StateVersion, Guid.NewGuid(), "remote", "r0"));
        var metadataRequest = MetadataPublicationTests.Initial(new GraphFixture([1, 2, 3], blob: true));
        var metadata = await Metadata(service, initial.ResourceId, metadataRequest);
        Success(metadata);
        Assert.Equal(initial.ContentVersion, metadata.State.ContentVersion);
        Assert.Equal(2, metadata.State.Revisions.Length);
        Assert.Empty(metadata.State.Publication!.Pending);
        var opaque = JsonSerializer.Serialize(metadata.State.Partitions.Single(p => p.Kind == 1));
        var selected = RevisionHistory.Latest(metadata.State);

        var fileRequest = RevisionHistoryTests.Request(metadata.State, MinimalDocx.Create("changed file"));
        var file = await service.ExecuteAsync(initial.ResourceId, DocumentPartitionKind.FileContents,
            fileRequest, new Dictionary<string, string>(), TestActor.Value);
        Success(file);
        Assert.Equal(opaque, JsonSerializer.Serialize(file.State.Partitions.Single(p => p.Kind == 1)));
        Assert.Equal(3, file.State.Revisions.Length);
        Assert.Single(file.State.Publication!.Pending);
        Assert.Equal(2, file.State.Receipts.Length);
        var fileReplay = await service.ExecuteAsync(initial.ResourceId, DocumentPartitionKind.FileContents,
            fileRequest, new Dictionary<string, string>(), TestActor.Value);
        Assert.True(Assert.Single(fileReplay.AcceptedSaves).IsReplay);
        var metadataReplay = await Metadata(service, initial.ResourceId, metadataRequest);
        Success(metadataReplay);
        Assert.Equal(Convert.ToHexString(metadata.Response.ToByteArray()), Convert.ToHexString(metadataReplay.Response.ToByteArray()));
        Assert.Equal(3, metadataReplay.State.Revisions.Length);
        Assert.Single(metadataReplay.State.Publication!.Pending);

        var restore = await service.RestoreRevisionAsync(initial.ResourceId, selected,
            RevisionHistory.Latest(file.State), "combined-restore", TestActor.Value);
        Assert.False(restore.IsReplay);
        var restored = (await provider.State.FindByResourceIdAsync(initial.ResourceId))!;
        Assert.Equal(initial.Content, restored.Content);
        Assert.Equal(file.State.ContentVersion + 1, restored.ContentVersion);
        Assert.Equal(4, restored.Revisions.Length);
        Assert.Equal(2, restored.Publication!.Pending.Length);
        Assert.NotEqual(restored.Publication.Pending[0].OperationId, restored.Publication.Pending[1].OperationId);
        Assert.All(restored.Publication.Pending, r => Assert.Contains(r.Content, StorageReferences.Handles(restored)));
        var replay = await service.RestoreRevisionAsync(initial.ResourceId, selected,
            RevisionHistory.Latest(file.State), "combined-restore", TestActor.Value);
        Assert.True(replay.IsReplay);
        Assert.Equal(JsonSerializer.Serialize(restored), JsonSerializer.Serialize(await provider.State.FindByResourceIdAsync(initial.ResourceId)));
        Assert.False(await ((IDocumentLifecycleStore)provider.State).TryDeleteAsync(
            initial.ResourceId, restored.LifecycleGeneration, restored.StateVersion));
    }

    [Fact]
    public Task MemoryRecreatedResourceSupportsAllPublicationPaths() => CheckRecreation(Memory());

    [PostgreSqlFact]
    public async Task PostgreSqlRecreatedResourceSupportsAllPublicationPaths()
    {
        await using var source = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("ConnectionStrings__cellbridge")!);
        await CheckRecreation(new(new PostgreSqlStateStore(source), new PostgreSqlContentStore(source)));
    }

    private static async Task CheckRecreation(StorageProvider provider)
    {
        var service = new CellBridgeDocumentService(provider);
        var folder = "/combined-" + Guid.NewGuid() + "/";
        var original = (await service.CreateAsync(folder + "old.docx", MinimalDocx.Create(), TestActor.Value))!;
        await service.RenameDocumentAsync(original.ResourceId, "current.docx", TestActor.Value);
        var renamed = (await provider.State.FindByResourceIdAsync(original.ResourceId))!;
        var lifecycle = (IDocumentLifecycleStore)provider.State;
        Assert.True(await lifecycle.TryDeleteAsync(original.ResourceId, renamed.LifecycleGeneration, renamed.StateVersion));
        var retired = (await lifecycle.FindLifecycleAsync(original.ResourceId))!;
        var replacement = await new DocumentStore().Put(renamed.Path, MinimalDocx.Create("replacement")).CaptureAsync(provider.Content);
        replacement = replacement with { LifecycleGeneration = 2, Security = renamed.Security };
        Assert.True(await lifecycle.TryRecreateAsync(original.ResourceId, 1, retired.StateVersion, replacement));
        var current = (await provider.State.FindByResourceIdAsync(replacement.ResourceId))!;
        var saved = await RevisionHistoryTests.Save(service, current, "after recreation");
        Assert.Equal(2, Assert.Single(saved.Receipts).LifecycleGeneration);
        var metadata = await Metadata(service, replacement.ResourceId,
            MetadataPublicationTests.Initial(new GraphFixture([4, 5], blob: true)));
        Success(metadata);
        Assert.Equal(2, metadata.State.LifecycleGeneration);
        await service.RenameDocumentAsync(replacement.ResourceId, "CURRENT.docx", TestActor.Value);
        var final = (await provider.State.FindByResourceIdAsync(replacement.ResourceId))!;
        Assert.Equal(folder + "CURRENT.docx", final.Path);
        Assert.Equal(renamed.PathKey, final.PathKey);
        Assert.Null(await service.CreateAsync(original.Path, MinimalDocx.Create(), TestActor.Value));
        Assert.Null(await service.CreateAsync(final.Path, MinimalDocx.Create(), TestActor.Value));
        var other = (await service.CreateAsync(folder + "other.docx", MinimalDocx.Create(), TestActor.Value))!;
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RenameDocumentAsync(other.ResourceId, "old.docx", TestActor.Value).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RenameDocumentAsync(other.ResourceId, "current.docx", TestActor.Value).AsTask());
        Assert.Null(await provider.State.FindByResourceIdAsync(original.ResourceId));
        Assert.True((await lifecycle.FindLifecycleAsync(original.ResourceId))!.IsDeleted);
    }

    [Fact]
    public Task CombinedOutboxQuotaFailureDoesNotPublishHistoryOrReceipts() => CheckQuota(Memory(new() { MaxPendingExternalRevisions = 1 }));

    [PostgreSqlFact]
    public Task PostgreSqlCombinedOutboxQuotaFailureDoesNotPublishHistoryOrReceipts() =>
        BudgetAndQueryTests.WithDatabase(new() { MaxPendingExternalRevisions = 1 }, async source =>
            await CheckQuota(new(new PostgreSqlStateStore(source), new PostgreSqlContentStore(source),
                limits: new() { MaxPendingExternalRevisions = 1 })));

    private static async Task CheckQuota(StorageProvider provider)
    {
        var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/quota-" + Guid.NewGuid() + ".docx", MinimalDocx.Create(), TestActor.Value))!;
        Assert.True(await new ExternalRevisionPublisher(provider, new UnusedDestination()).BindAsync(
            state.ResourceId, 1, state.StateVersion, Guid.NewGuid(), "remote", "r0"));
        var metadata = await Metadata(service, state.ResourceId, MetadataPublicationTests.Initial(new GraphFixture([8], blob: true)));
        Success(metadata);
        var selected = RevisionHistory.Latest(metadata.State);
        var saved = await RevisionHistoryTests.Save(service, metadata.State, "pending delivery");
        var before = JsonSerializer.Serialize(await provider.State.FindByResourceIdAsync(state.ResourceId));
        await Assert.ThrowsAsync<StorageQuotaExceededException>(() => service.RestoreRevisionAsync(
            state.ResourceId, selected, RevisionHistory.Latest(saved), "quota", TestActor.Value).AsTask());
        Assert.Equal(before, JsonSerializer.Serialize(await provider.State.FindByResourceIdAsync(state.ResourceId)));
        var rejected = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents,
            RevisionHistoryTests.Request(saved, MinimalDocx.Create("over quota")), new Dictionary<string, string>(), TestActor.Value);
        Assert.True(Assert.Single(rejected.Response.SubResponses).Status);
        Assert.Equal(before, JsonSerializer.Serialize(await provider.State.FindByResourceIdAsync(state.ResourceId)));
    }

    private static ValueTask<CellExecution> Metadata(CellBridgeDocumentService service, Guid id, FsshttpbCellRequest request) =>
        service.ExecuteAsync(id, DocumentPartitionKind.Metadata, request, new Dictionary<string, string>(), TestActor.Value);
    private static void Success(CellExecution result) => Assert.False(Assert.Single(result.Response.SubResponses).Status,
        result.Response.SubResponses[0].Error?.ErrorMessage ?? result.LockError);
    private static StorageProvider Memory(StorageLimits? limits = null) => new(new InMemoryStateStore(), new InMemoryContentStore(), limits: limits);
    private sealed class UnusedDestination : IExternalRevisionDestination
    {
        public ValueTask<ExternalDeliveryResult> CompareExchangeAsync(ExternalDeliveryRequest request, Stream verifiedContent,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("These tests must leave revisions pending.");
    }
}
