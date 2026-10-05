using System.Collections.Immutable;
using System.Text.Json;
using System.Xml.Linq;
using CellBridge.AspNetCore;
using CellBridge.FssHttp;
using CellBridge.FssHttpB;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using CellBridge.Storage.PostgreSql;
using CellBridge.Web;
using Npgsql;

namespace CellBridge.Storage.Tests;

public sealed class RevisionHistoryTests
{
    [Fact]
    public Task SaveRestoreRetryAndMetadataHistory() => CheckHistory(Memory());

    [PostgreSqlFact]
    public async Task DurableHistorySurvivesIndependentProviderRecreation()
    {
        await using var source = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("ConnectionStrings__cellbridge")!);
        await CheckHistory(new(new PostgreSqlStateStore(source), new PostgreSqlContentStore(source)),
            () => new(new PostgreSqlStateStore(source), new PostgreSqlContentStore(source)));
    }

    private static async Task CheckHistory(StorageProvider provider, Func<StorageProvider>? recreate = null)
    {
        var service = new CellBridgeDocumentService(provider);
        var original = (await service.CreateAsync("/shared/" + Guid.NewGuid().ToString("N") + ".docx", MinimalDocx.Create("original"), TestActor.Value))!;
        var first = Assert.Single(original.Revisions);
        var saved = await Save(service, original, "second");
        Assert.Equal(2, saved.Revisions.Length);
        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(saved.Revisions[0]));
        var restarted = new CellBridgeDocumentService(recreate?.Invoke() ?? provider);
        var restored = await restarted.RestoreRevisionAsync(original.ResourceId, first.RevisionNumber,
            RevisionHistory.Latest(saved), "restore-first", TestActor.Value);
        Assert.False(restored.IsReplay);
        Assert.Equal(saved.ContentVersion + 1, restored.Revision.ContentVersion);
        Assert.Equal(first.Content, restored.Revision.Content);
        var after = (await provider.State.FindByResourceIdAsync(original.ResourceId))!;
        Assert.Equal(3, after.Revisions.Length);
        var graph = await DocumentPartition.RestoreFileGraphAsync(after, provider.Content, provider.Limits);
        Assert.Equal(await provider.Content.ReadVerifiedAsync(first.Content), graph.Materialize());
        Assert.NotEqual(saved.Partitions[0].StorageIndex, after.Partitions[0].StorageIndex);
        Assert.All(saved.Partitions[0].Elements, e => Assert.Contains(after.Partitions[0].Elements, x => x.Id == e.Id && x.Payload == e.Payload));
        var later = await Save(service, after, "third");
        var replay = await restarted.RestoreRevisionAsync(original.ResourceId, first.RevisionNumber,
            RevisionHistory.Latest(saved), "restore-first", TestActor.Value);
        Assert.True(replay.IsReplay);
        Assert.Equal(JsonSerializer.Serialize(restored.Revision), JsonSerializer.Serialize(replay.Revision));
        Assert.Equal(later.ContentVersion, (await provider.State.FindByResourceIdAsync(original.ResourceId))!.ContentVersion);
        await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.RestoreRevisionAsync(original.ResourceId,
            first.RevisionNumber, RevisionHistory.Latest(later), "restore-first", TestActor.Value).AsTask());
        Assert.Equal(4, (await restarted.ListRevisionsAsync(original.ResourceId, TestActor.Value)).Count);
        // A metadata-only publication gets independent history identity without changing the file counter.
        var metadata = await provider.State.TransitionAsync(original.ResourceId, (current, now) =>
        {
            var next = current with { Partitions = current.Partitions.Select(p => p.Kind == 1
                ? p with { Knowledge = p.Knowledge + 1, InlineContent = [1, 2, 3] } : p).ToImmutableArray() };
            next = RevisionHistory.Append(current, next, TestActor.Value.Identity, now);
            return new StateTransition<DocumentState>(next, next);
        });
        Assert.Equal(later.ContentVersion, metadata.Revisions[^1].ContentVersion);
        Assert.Equal(RevisionHistory.Latest(later) + 1, RevisionHistory.Latest(metadata));
    }

    [Fact]
    public async Task RevocationAndReaderCeilingsApplyToHistoryAndRetries()
    {
        var provider = Memory(); var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/denied.docx", MinimalDocx.Create(), TestActor.Value))!;
        var reader = TestActor.Value with { AccessLimit = new(state.ResourceId, DocumentAccess.Read) };
        Assert.Single(await service.ListRevisionsAsync(state.ResourceId, reader));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.RestoreRevisionAsync(state.ResourceId, 1, 1, "key", reader).AsTask());
        await service.RestoreRevisionAsync(state.ResourceId, 1, 1, "key", TestActor.Value);
        await provider.State.TransitionAsync(state.ResourceId, (current, _) => new StateTransition<bool>(current with
        { Security = current.Security with { Owner = "different-owner" } }, true));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetRevisionAsync(state.ResourceId, 1, TestActor.Value).AsTask());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.RestoreRevisionAsync(state.ResourceId, 1, 1, "key", TestActor.Value).AsTask());
    }

    [Fact]
    public async Task ConcurrentRestorePublishesOnceAndStaleSaveCannotOverwriteIt()
    {
        var provider = Memory(); var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/concurrent.docx", MinimalDocx.Create(), TestActor.Value))!;
        var staleSave = Request(state, MinimalDocx.Create("stale"));
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => service.RestoreRevisionAsync(state.ResourceId,
            1, 1, "same-key", TestActor.Value).AsTask()));
        Assert.Single(results, r => !r.IsReplay);
        Assert.All(results, r => Assert.Equal(2UL, r.Revision.RevisionNumber));
        var rejected = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents, staleSave,
            new Dictionary<string, string>(), TestActor.Value);
        Assert.True(Assert.Single(rejected.Response.SubResponses).Status);
        Assert.Equal(2, rejected.State.Revisions.Length);
    }

    [Fact]
    public async Task HistoryLimitRejectsWithoutChangingPublishedState()
    {
        var provider = new StorageProvider(new InMemoryStateStore(), new InMemoryContentStore(), new StorageLimits { MaxHistoryRevisions = 1 });
        var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/quota.docx", MinimalDocx.Create(), TestActor.Value))!;
        await Assert.ThrowsAsync<StorageQuotaExceededException>(() => service.RestoreRevisionAsync(state.ResourceId, 1, 1, "key", TestActor.Value).AsTask());
        Assert.Equal(state, await provider.State.FindByResourceIdAsync(state.ResourceId));
    }

    [Fact]
    public async Task FormatTwoCapturePreservesHistoryAndLegacySeedsOnlyKnownCurrent()
    {
        var provider = Memory(); var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/codec.docx", MinimalDocx.Create(), TestActor.Value))!;
        await service.RestoreRevisionAsync(state.ResourceId, 1, 1, "key", TestActor.Value);
        state = (await provider.State.FindByResourceIdAsync(state.ResourceId))!;
        var decoded = JsonSerializer.Deserialize<DocumentState>(JsonSerializer.Serialize(state))!;
        var captured = await (await StoredDocument.RestoreAsync(decoded, provider.Content)).CaptureAsync(provider.Content);
        Assert.Equal(JsonSerializer.Serialize(decoded.Revisions), JsonSerializer.Serialize(captured.Revisions));
        Assert.Equal(decoded.RestoreReceipts, captured.RestoreReceipts);
        var legacyJson = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(state))!.AsObject();
        legacyJson.Remove("Revisions"); legacyJson.Remove("RestoreReceipts"); legacyJson.Remove("LifecycleGeneration");
        var legacy = JsonSerializer.Deserialize<DocumentState>(legacyJson.ToJsonString())!;
        Assert.Empty(legacy.Revisions);
        var seeded = RevisionHistory.Initialize(legacy);
        Assert.Equal(state.ContentVersion, Assert.Single(seeded.Revisions).ContentVersion);
        Assert.Equal(1, legacy.LifecycleGeneration);
    }

    internal static async Task<DocumentState> Save(CellBridgeDocumentService service, DocumentState state, string text)
    {
        var result = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents,
            Request(state, MinimalDocx.Create(text)), new Dictionary<string, string>(), TestActor.Value);
        Assert.False(Assert.Single(result.Response.SubResponses).Status);
        return result.State;
    }

    internal static FsshttpbCellRequest Request(DocumentState state, byte[] content)
    {
        ExGuid Fresh() => new(1, Guid.NewGuid());
        var partition = state.Partitions.Single(p => p.Kind == 0);
        var identity = new StorageManifestBuilder.StableIdentity(Fresh(), Fresh(), Fresh(), Fresh(), Fresh(), Fresh(), Fresh(),
            new CellId(StorageIds.Restore(partition.Identity.CellLong), StorageIds.Restore(partition.Identity.CellShort)), Guid.NewGuid());
        var graph = FileContentPartitionBuilder.BuildQueryChangesResponse(1, content, identity, partition.Knowledge + 100);
        var index = ((QueryChangesSubResponseData)graph.SubResponses[0].Data!).StorageIndexExtendedGuid;
        return new() { DataElementPackage = graph.DataElementPackage, SubRequests =
        { new(RequestTypes.PutChanges) { RequestId = 1, Data = new PutChangesSubRequestData
            { StorageIndex = index, ExpectedStorageIndex = StorageIds.Restore(partition.StorageIndex!) } } } };
    }

    private static StorageProvider Memory() => new(new InMemoryStateStore(), new InMemoryContentStore());
}
