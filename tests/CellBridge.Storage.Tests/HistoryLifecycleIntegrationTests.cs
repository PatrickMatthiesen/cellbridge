using System.Text.Json;
using CellBridge.AspNetCore;
using CellBridge.FssHttp;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using CellBridge.Storage.PostgreSql;
using CellBridge.TestFixtures;
using Npgsql;

namespace CellBridge.Storage.Tests;

[Collection("PostgreSQL")]
public sealed class HistoryLifecycleIntegrationTests
{
    [Fact]
    public Task MemoryHistoryRenameAndRecreationKeepDistinctIdentities() =>
        Check(StorageTestDocuments.Memory());

    [PostgreSqlFact]
    public async Task PostgreSqlHistoryRenameAndRecreationKeepDistinctIdentities()
    {
        await using var source = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("ConnectionStrings__cellbridge")!);
        await Check(new(new PostgreSqlStateStore(source), new PostgreSqlContentStore(source)));
    }

    private static async Task Check(StorageProvider provider)
    {
        var service = new CellBridgeDocumentService(provider);
        var lifecycle = provider.Documents;
        var folder = "/" + Guid.NewGuid().ToString("N") + "/";
        var original = (await service.CreateAsync(folder + "a.docx", MinimalDocx.Create("original"), TestActor.Value))!;
        var saved = await RevisionHistoryTests.Save(service, original, "saved");
        await service.RestoreRevisionAsync(original.ResourceId, 1, saved.LatestRevisionNumber, "original-restore", TestActor.Value);
        await service.RenameDocumentAsync(original.ResourceId, "b.docx", TestActor.Value);
        var renamed = (await provider.Documents.FindAsync(new DocumentIdentity.Resource(original.ResourceId)))!;
        Assert.Equal(3,renamed.Counters.Revisions);
        Assert.Equal(1,renamed.Counters.RestoreReceipts);
        Assert.False((await lifecycle.ChangeLifecycleAsync(new DocumentLifecycleCommand.Delete(renamed.ResourceId,
            renamed.LifecycleGeneration + 1,renamed.StateVersion))).Applied);
        Assert.True((await lifecycle.ChangeLifecycleAsync(new DocumentLifecycleCommand.Delete(renamed.ResourceId,
            renamed.LifecycleGeneration,renamed.StateVersion))).Applied);
        var retired = (await lifecycle.FindAsync(new DocumentIdentity.Resource(renamed.ResourceId,true)))!;
        var retainedSnapshot = (await lifecycle.ExportDocumentAsync(retired.ResourceId))!;
        var historyJson = JsonSerializer.Serialize(retainedSnapshot.ApplicationRevisions);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.RenameDocumentAsync(retired.ResourceId,"deleted.docx",TestActor.Value).AsTask());
        Assert.Null(await service.CreateAsync(original.Path,MinimalDocx.Create(),TestActor.Value));
        var replacement = await IndexedDocumentPublicationBuilder.CreateAsync(Guid.NewGuid(),renamed.Path,MinimalDocx.Create("fresh"),
            retired.Security,provider.Content,provider.Limits,lifecycleGeneration:2);
        // A replacement cannot import another incarnation's historical package or revision header.
        var oldRevision = retainedSnapshot.ApplicationRevisions[0];
        var invalid = replacement with { InitialRevision = oldRevision };
        await Assert.ThrowsAsync<InvalidDataException>(() => lifecycle.ChangeLifecycleAsync(new DocumentLifecycleCommand.Recreate(retired.ResourceId,1,retired.StateVersion,invalid)).AsTask());
        var relabeled = oldRevision with { ResourceId = replacement.Head.ResourceId,LifecycleGeneration=2,
            CreatedUtc=replacement.Head.ModifiedUtc };
        await Assert.ThrowsAsync<InvalidDataException>(() => lifecycle.ChangeLifecycleAsync(new DocumentLifecycleCommand.Recreate(retired.ResourceId,1,retired.StateVersion,
            replacement with { InitialRevision = relabeled })).AsTask());
        replacement = JsonSerializer.Deserialize<PreparedDocumentCreate>(JsonSerializer.Serialize(replacement))!;
        Assert.True((await lifecycle.ChangeLifecycleAsync(new DocumentLifecycleCommand.Recreate(retired.ResourceId,1,retired.StateVersion,replacement))).Applied);
        var fresh = (await lifecycle.FindAsync(new DocumentIdentity.Resource(replacement.Head.ResourceId)))!;
        Assert.Equal(fresh.ResourceId,Assert.Single((await lifecycle.ReadRevisionsAsync(fresh.ResourceId,0,10))!.Revisions).ResourceId);
        Assert.Equal(0,fresh.Counters.RestoreReceipts);
        var retained = (await lifecycle.ExportDocumentAsync(retired.ResourceId))!;
        Assert.Equal(historyJson,JsonSerializer.Serialize(retained.ApplicationRevisions));
        Assert.Contains(retained.PathClaims,claim => claim == original.PathKey);
        Assert.Null(await service.CreateAsync(original.Path,MinimalDocx.Create(),TestActor.Value));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.GetRevisionAsync(retired.ResourceId,1,TestActor.Value).AsTask());

        var processor = new CellBridgeRequestProcessor(service);
        Assert.Equal("InvalidCoauthSession", await Soap(SubRequestType.Versioning, false,
            ("VersioningRequestType", "RestoreVersion"), ("Version", "1.0")));
        Assert.Equal("InvalidCoauthSession", await Soap(SubRequestType.FileOperation, false,
            ("FileOperation", "Rename"), ("NewFileName", "B.docx")));
        Assert.Equal("Success", await Soap(SubRequestType.Versioning, true,
            ("VersioningRequestType", "RestoreVersion"), ("Version", "1.0")));
        Assert.Equal("Success", await Soap(SubRequestType.FileOperation, true,
            ("FileOperation", "Rename"), ("NewFileName", "B.docx")));
        var caseOnly = (await provider.Documents.FindAsync(new DocumentIdentity.Resource(fresh.ResourceId)))!;
        Assert.Equal(folder + "B.docx", caseOnly.Path);
        Assert.Equal(fresh.PathKey, caseOnly.PathKey);
        Assert.DoesNotContain((await lifecycle.ExportDocumentAsync(caseOnly.ResourceId))!.PathClaims,claim => claim != caseOnly.PathKey);
        Assert.Equal(2,caseOnly.Counters.Revisions);

        async Task<string?> Soap(SubRequestType type, bool explicitId, params (string Key, string Value)[] attributes)
        {
            var operation = new FssHttpSubRequest { Type = type, SubRequestToken = 1 };
            foreach (var (key, value) in attributes) operation.SubRequestDataAttributes[key] = value;
            var request = new CellStorageRequest { Requests = { new FssHttpRequest { Url = replacement.Head.Path,
                UseResourceId = explicitId, ResourceId = explicitId ? replacement.Head.ResourceId.ToString() : null,
                SubRequests = { operation } } } };
            return (await processor.ExecuteAsync(request, "https://host.test", TestActor.Value)).Response.Responses[0].SubResponses[0].ErrorCode;
        }
    }
}
