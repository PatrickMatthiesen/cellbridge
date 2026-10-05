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

public sealed class HistoryLifecycleIntegrationTests
{
    [Fact]
    public Task MemoryHistoryRenameAndRecreationKeepDistinctIdentities() =>
        Check(new(new InMemoryStateStore(), new InMemoryContentStore()));

    [PostgreSqlFact]
    public async Task PostgreSqlHistoryRenameAndRecreationKeepDistinctIdentities()
    {
        await using var source = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("ConnectionStrings__cellbridge")!);
        await Check(new(new PostgreSqlStateStore(source), new PostgreSqlContentStore(source)));
    }

    private static async Task Check(StorageProvider provider)
    {
        var service = new CellBridgeDocumentService(provider);
        var lifecycle = (IDocumentLifecycleStore)provider.State;
        var renameStore = (IAtomicDocumentRenameStore)provider.State;
        var folder = "/" + Guid.NewGuid().ToString("N") + "/";
        var original = (await service.CreateAsync(folder + "a.docx", MinimalDocx.Create("original"), TestActor.Value))!;
        var saved = await RevisionHistoryTests.Save(service, original, "saved");
        await service.RestoreRevisionAsync(original.ResourceId, 1, RevisionHistory.Latest(saved), "original-restore", TestActor.Value);
        await service.RenameDocumentAsync(original.ResourceId, "b.docx", TestActor.Value);
        var renamed = (await provider.State.FindByResourceIdAsync(original.ResourceId))!;
        Assert.Equal(3, renamed.Revisions.Length);
        Assert.Single(renamed.RestoreReceipts);
        await Assert.ThrowsAsync<InvalidOperationException>(() => renameStore.RenameAsync(original.ResourceId,
            (current, _) => new StateTransition<bool>(current with { LifecycleGeneration = current.LifecycleGeneration + 1 }, true)).AsTask());
        Assert.True(await lifecycle.TryDeleteAsync(renamed.ResourceId, renamed.LifecycleGeneration, renamed.StateVersion));
        var retired = (await lifecycle.FindLifecycleAsync(renamed.ResourceId))!;
        var historyJson = JsonSerializer.Serialize(retired.Revisions);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => renameStore.RenameAsync(retired.ResourceId,
            (current, _) => new StateTransition<bool>(current, true)).AsTask());
        Assert.Null(await service.CreateAsync(original.Path, MinimalDocx.Create(), TestActor.Value));
        var replacement = await new DocumentStore().Put(renamed.Path, MinimalDocx.Create("fresh")).CaptureAsync(provider.Content);
        replacement = RevisionHistory.Initialize(replacement with { LifecycleGeneration = 2, Security = retired.Security });
        Assert.False(await lifecycle.TryRecreateAsync(retired.ResourceId, 1, retired.StateVersion,
            replacement with { Revisions = retired.Revisions }));
        Assert.False(await lifecycle.TryRecreateAsync(retired.ResourceId, 1, retired.StateVersion,
            replacement with { RestoreReceipts = retired.RestoreReceipts }));
        var relabeled = retired.Revisions[0] with { ResourceId = replacement.ResourceId, LifecycleGeneration = 2,
            CreatedUtc = replacement.ModifiedUtc };
        Assert.False(await lifecycle.TryRecreateAsync(retired.ResourceId, 1, retired.StateVersion,
            replacement with { Revisions = [relabeled] }));
        // JSON decoding changes array identities; coherent values must remain admissible.
        replacement = JsonSerializer.Deserialize<DocumentState>(JsonSerializer.Serialize(replacement))!;
        Assert.True(await lifecycle.TryRecreateAsync(retired.ResourceId, 1, retired.StateVersion, replacement));
        var fresh = (await provider.State.FindByResourceIdAsync(replacement.ResourceId))!;
        Assert.Equal(fresh.ResourceId, Assert.Single(fresh.Revisions).ResourceId);
        Assert.Empty(fresh.RestoreReceipts);
        var retained = (await lifecycle.FindLifecycleAsync(retired.ResourceId))!;
        Assert.Equal(historyJson, JsonSerializer.Serialize(retained.Revisions));
        Assert.Equal(original.PathKey, Assert.Single(retained.RetiredPathKeys));
        Assert.Null(await service.CreateAsync(original.Path, MinimalDocx.Create(), TestActor.Value));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.GetRevisionAsync(retired.ResourceId, 1, TestActor.Value).AsTask());

        var processor = new CellBridgeRequestProcessor(service);
        Assert.Equal("InvalidCoauthSession", await Soap(SubRequestType.Versioning, false,
            ("VersioningRequestType", "RestoreVersion"), ("Version", "1.0")));
        Assert.Equal("InvalidCoauthSession", await Soap(SubRequestType.FileOperation, false,
            ("FileOperation", "Rename"), ("NewFileName", "B.docx")));
        Assert.Equal("Success", await Soap(SubRequestType.Versioning, true,
            ("VersioningRequestType", "RestoreVersion"), ("Version", "1.0")));
        Assert.Equal("Success", await Soap(SubRequestType.FileOperation, true,
            ("FileOperation", "Rename"), ("NewFileName", "B.docx")));
        var caseOnly = (await provider.State.FindByResourceIdAsync(fresh.ResourceId))!;
        Assert.Equal(folder + "B.docx", caseOnly.Path);
        Assert.Equal(fresh.PathKey, caseOnly.PathKey);
        Assert.Empty(caseOnly.RetiredPathKeys);
        Assert.Equal(2, caseOnly.Revisions.Length);

        async Task<string?> Soap(SubRequestType type, bool explicitId, params (string Key, string Value)[] attributes)
        {
            var operation = new FssHttpSubRequest { Type = type, SubRequestToken = 1 };
            foreach (var (key, value) in attributes) operation.SubRequestDataAttributes[key] = value;
            var request = new CellStorageRequest { Requests = { new FssHttpRequest { Url = replacement.Path,
                UseResourceId = explicitId, ResourceId = explicitId ? replacement.ResourceId.ToString() : null,
                SubRequests = { operation } } } };
            return (await processor.ExecuteAsync(request, "https://host.test", TestActor.Value)).Response.Responses[0].SubResponses[0].ErrorCode;
        }
    }
}
