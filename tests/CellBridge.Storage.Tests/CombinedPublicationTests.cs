using System.Text.Json;
using CellBridge.AspNetCore;
using CellBridge.FssHttpB;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using CellBridge.Storage.PostgreSql;
using CellBridge.Tests;
using CellBridge.TestFixtures;
using Npgsql;

namespace CellBridge.Storage.Tests;

[Collection("PostgreSQL")]
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
        var metadataRequest = StorageTestDocuments.MetadataRequest(new GraphFixture([1, 2, 3], blob: true));
        var metadata = await Metadata(service, initial.ResourceId, metadataRequest);
        Success(metadata);
        Assert.Equal(initial.ContentVersion, metadata.State.ContentVersion);
        Assert.Equal(2, metadata.State.Counters.Revisions);
        Assert.Equal(0, metadata.State.Counters.PendingDeliveries);
        var opaque = JsonSerializer.Serialize((await provider.Documents.ReadPartitionAsync(initial.ResourceId, new(1)))!.Partition);
        var selected = metadata.State.LatestRevisionNumber;

        var fileRequest = await StorageTestDocuments.SaveRequestAsync(service, metadata.State, MinimalDocx.Create("changed file"));
        var file = await service.ExecuteAsync(initial.ResourceId, DocumentPartitionKind.FileContents,
            fileRequest, new Dictionary<string, string>(), TestActor.Value);
        Success(file);
        Assert.Equal(opaque, JsonSerializer.Serialize((await provider.Documents.ReadPartitionAsync(initial.ResourceId, new(1)))!.Partition));
        Assert.Equal(3, file.State.Counters.Revisions);
        Assert.Equal(1, file.State.Counters.PendingDeliveries);
        Assert.Equal(2, file.State.Counters.SaveReceipts);
        var fileReplay = await service.ExecuteAsync(initial.ResourceId, DocumentPartitionKind.FileContents,
            fileRequest, new Dictionary<string, string>(), TestActor.Value);
        Assert.True(Assert.Single(fileReplay.AcceptedSaves).IsReplay);
        var metadataReplay = await Metadata(service, initial.ResourceId, metadataRequest);
        Success(metadataReplay);
        Assert.Equal(Convert.ToHexString(metadata.Response.ToByteArray()), Convert.ToHexString(metadataReplay.Response.ToByteArray()));
        Assert.Equal(3, metadataReplay.State.Counters.Revisions);
        Assert.Equal(1, metadataReplay.State.Counters.PendingDeliveries);

        var restore = await service.RestoreRevisionAsync(initial.ResourceId, selected,
            file.State.LatestRevisionNumber, "combined-restore", TestActor.Value);
        Assert.False(restore.IsReplay);
        var restored = (await provider.Documents.FindAsync(new DocumentIdentity.Resource(initial.ResourceId)))!;
        Assert.Equal(initial.Package.Sha256, restored.Package.Sha256);
        Assert.Equal(file.State.ContentVersion + 1, restored.ContentVersion);
        Assert.Equal(4, restored.Counters.Revisions);
        Assert.Equal(2, restored.Counters.PendingDeliveries);
        var deliveries = (await provider.Documents.ExportDocumentAsync(restored.ResourceId))!.Deliveries;
        Assert.NotEqual(deliveries[0].OperationId, deliveries[1].OperationId);
        foreach (var delivery in deliveries)
            Assert.Equal(delivery.Package.Length, (await StorageTestDocuments.PackageAsync(provider, delivery.Package)).LongLength);
        var replay = await service.RestoreRevisionAsync(initial.ResourceId, selected,
            file.State.LatestRevisionNumber, "combined-restore", TestActor.Value);
        Assert.True(replay.IsReplay);
        Assert.Equal(JsonSerializer.Serialize(restored), JsonSerializer.Serialize(await provider.Documents.FindAsync(new DocumentIdentity.Resource(initial.ResourceId))));
        Assert.False((await provider.Documents.ChangeLifecycleAsync(new DocumentLifecycleCommand.Delete(
            initial.ResourceId, restored.LifecycleGeneration, restored.StateVersion))).Applied);
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
        var renamed = (await provider.Documents.FindAsync(new DocumentIdentity.Resource(original.ResourceId)))!;
        var lifecycle = provider.Documents;
        Assert.True((await lifecycle.ChangeLifecycleAsync(new DocumentLifecycleCommand.Delete(original.ResourceId,
            renamed.LifecycleGeneration, renamed.StateVersion))).Applied);
        var retired = (await lifecycle.FindAsync(new DocumentIdentity.Resource(original.ResourceId, true)))!;
        var replacement = await IndexedDocumentPublicationBuilder.CreateAsync(Guid.NewGuid(), renamed.Path,
            MinimalDocx.Create("replacement"), renamed.Security, provider.Content, provider.Limits, lifecycleGeneration: 2);
        Assert.True((await lifecycle.ChangeLifecycleAsync(new DocumentLifecycleCommand.Recreate(original.ResourceId, 1, retired.StateVersion, replacement))).Applied);
        var current = (await provider.Documents.FindAsync(new DocumentIdentity.Resource(replacement.Head.ResourceId)))!;
        var saved = await RevisionHistoryTests.Save(service, current, "after recreation");
        Assert.Equal(2, Assert.Single((await provider.Documents.ExportDocumentAsync(saved.ResourceId))!.Receipts).Operation.LifecycleGeneration);
        var metadata = await Metadata(service, replacement.Head.ResourceId,
            StorageTestDocuments.MetadataRequest(new GraphFixture([4, 5], blob: true)));
        Success(metadata);
        Assert.Equal(2, metadata.State.LifecycleGeneration);
        await service.RenameDocumentAsync(replacement.Head.ResourceId, "CURRENT.docx", TestActor.Value);
        var final = (await provider.Documents.FindAsync(new DocumentIdentity.Resource(replacement.Head.ResourceId)))!;
        Assert.Equal(folder + "CURRENT.docx", final.Path);
        Assert.Equal(renamed.PathKey, final.PathKey);
        Assert.Null(await service.CreateAsync(original.Path, MinimalDocx.Create(), TestActor.Value));
        Assert.Null(await service.CreateAsync(final.Path, MinimalDocx.Create(), TestActor.Value));
        var other = (await service.CreateAsync(folder + "other.docx", MinimalDocx.Create(), TestActor.Value))!;
        await Assert.ThrowsAsync<DocumentOperationLockException>(() => service.RenameDocumentAsync(other.ResourceId, "old.docx", TestActor.Value).AsTask());
        await Assert.ThrowsAsync<DocumentOperationLockException>(() => service.RenameDocumentAsync(other.ResourceId, "current.docx", TestActor.Value).AsTask());
        Assert.Null(await provider.Documents.FindAsync(new DocumentIdentity.Resource(original.ResourceId)));
        Assert.True((await lifecycle.FindAsync(new DocumentIdentity.Resource(original.ResourceId, true)))!.IsDeleted);
    }

    [Fact]
    public Task CombinedOutboxQuotaFailureDoesNotPublishHistoryOrReceipts() => CheckQuota(Memory(new() { MaxPendingExternalRevisions = 1 }));

    [PostgreSqlFact]
    public Task PostgreSqlCombinedOutboxQuotaFailureDoesNotPublishHistoryOrReceipts() =>
        EfPostgreSqlMigrationTests.WithInitializedDatabaseAsync(new() { MaxPendingExternalRevisions = 1 }, async source =>
            await CheckQuota(new(new PostgreSqlStateStore(source, new() { MaxPendingExternalRevisions = 1 }), new PostgreSqlContentStore(source),
                limits: new() { MaxPendingExternalRevisions = 1 })));

    private static async Task CheckQuota(StorageProvider provider)
    {
        var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/quota-" + Guid.NewGuid() + ".docx", MinimalDocx.Create(), TestActor.Value))!;
        Assert.True(await new ExternalRevisionPublisher(provider, new UnusedDestination()).BindAsync(
            state.ResourceId, 1, state.StateVersion, Guid.NewGuid(), "remote", "r0"));
        var metadata = await Metadata(service, state.ResourceId, StorageTestDocuments.MetadataRequest(new GraphFixture([8], blob: true)));
        Success(metadata);
        var selected = metadata.State.LatestRevisionNumber;
        var saved = await RevisionHistoryTests.Save(service, metadata.State, "pending delivery");
        var before = JsonSerializer.Serialize(await provider.Documents.FindAsync(new DocumentIdentity.Resource(state.ResourceId)));
        await Assert.ThrowsAsync<StorageQuotaExceededException>(() => service.RestoreRevisionAsync(
            state.ResourceId, selected, saved.LatestRevisionNumber, "quota", TestActor.Value).AsTask());
        Assert.Equal(before, JsonSerializer.Serialize(await provider.Documents.FindAsync(new DocumentIdentity.Resource(state.ResourceId))));
        var rejected = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents,
            await StorageTestDocuments.SaveRequestAsync(service, saved, MinimalDocx.Create("over quota")), new Dictionary<string, string>(), TestActor.Value);
        Assert.True(Assert.Single(rejected.Response.SubResponses).Status);
        Assert.Equal(before, JsonSerializer.Serialize(await provider.Documents.FindAsync(new DocumentIdentity.Resource(state.ResourceId))));
    }

    private static ValueTask<CellExecution> Metadata(CellBridgeDocumentService service, Guid id, FsshttpbCellRequest request) =>
        service.ExecuteAsync(id, DocumentPartitionKind.Metadata, request, new Dictionary<string, string>(), TestActor.Value);
    private static void Success(CellExecution result) => Assert.False(Assert.Single(result.Response.SubResponses).Status,
        result.Response.SubResponses[0].Error?.ErrorMessage ?? result.LockError);
    private static StorageProvider Memory(StorageLimits? limits = null) => StorageTestDocuments.Memory(limits: limits);
    private sealed class UnusedDestination : IExternalRevisionDestination
    {
        public ValueTask<ExternalDeliveryResult> CompareExchangeAsync(ExternalDeliveryRequest request, Stream verifiedContent,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("These tests must leave revisions pending.");
    }
}
