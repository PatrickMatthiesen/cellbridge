using System.Text.Json;
using CellBridge.AspNetCore;
using CellBridge.FssHttp;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using CellBridge.Storage.PostgreSql;
using CellBridge.TestFixtures;
using Npgsql;

namespace CellBridge.Storage.Tests;

[Collection("PostgreSQL")]
public sealed class DocumentMoveTests
{
    [Fact]
    public Task MemoryMovePreservesSavedDocumentAndReservations() => CheckIdentity(Memory());

    [PostgreSqlFact]
    public async Task PostgreSqlMoveSurvivesIndependentProviderAndDataSource()
    {
        var connection = Environment.GetEnvironmentVariable("ConnectionStrings__cellbridge")!;
        Guid id;
        string oldKey, newKey;
        byte[] expectedContent;
        DocumentHead moved;
        await using (var source = NpgsqlDataSource.Create(connection))
        {
            var provider = PostgreSql(source);
            (id, oldKey, newKey, moved, expectedContent) = await CheckIdentity(provider);
        }
        await using var reopenedSource = NpgsqlDataSource.Create(connection);
        var reopened = PostgreSql(reopenedSource);
        var actual = (await reopened.Documents.FindAsync(new DocumentIdentity.Resource(id)))!;
        Assert.Equal(JsonSerializer.Serialize(moved), JsonSerializer.Serialize(actual));
        Assert.Null(await reopened.Documents.FindAsync(new DocumentIdentity.Path(oldKey)));
        Assert.Equal(id, (await reopened.Documents.FindAsync(new DocumentIdentity.Path(newKey)))!.ResourceId);
        Assert.Equal(expectedContent, await StorageTestDocuments.PackageAsync(reopened, actual.Package));
    }

    private static async Task<(Guid Id, string OldKey, string NewKey, DocumentHead Moved, byte[] Content)> CheckIdentity(StorageProvider provider)
    {
        var service = new CellBridgeDocumentService(provider);
        var root = "/" + Guid.NewGuid().ToString("N");
        var initial = (await service.CreateAsync(root + "/source/file.docx", MinimalDocx.Create("first"), TestActor.Value))!;
        var saved = await RepositoryTestPublications.SaveAsync(service, initial, MinimalDocx.Create("second"));
        var revisions = (await provider.Documents.ReadRevisionsAsync(initial.ResourceId, 0, 256))!.Revisions;
        var destination = root + "/new folder/æ # %2F.docx";
        var moved = await service.MoveDocumentAsync(initial.ResourceId, destination, TestActor.Value);
        var committed = (await provider.Documents.FindAsync(new DocumentIdentity.Resource(initial.ResourceId)))!;
        Assert.Equal(JsonSerializer.Serialize(committed), JsonSerializer.Serialize(moved));
        Assert.Equal(initial.ResourceId, moved.ResourceId);
        Assert.Equal(destination, moved.Path);
        Assert.Equal(saved.Package, moved.Package);
        Assert.Equal(saved.ContentVersion, moved.ContentVersion);
        Assert.Equal(JsonSerializer.Serialize(revisions), JsonSerializer.Serialize((await provider.Documents.ReadRevisionsAsync(initial.ResourceId, 0, 256))!.Revisions));
        Assert.Equal(saved.LifecycleGeneration, moved.LifecycleGeneration);
        Assert.Equal(saved.Security, moved.Security);
        Assert.Null(await provider.Documents.FindAsync(new DocumentIdentity.Path(initial.PathKey)));
        Assert.Equal(initial.ResourceId, (await provider.Documents.FindAsync(new DocumentIdentity.Path(moved.PathKey)))!.ResourceId);
        Assert.Contains(initial.PathKey, (await provider.Documents.ExportDocumentAsync(initial.ResourceId))!.PathClaims);
        Assert.Null(await service.CreateAsync(initial.Path, MinimalDocx.Create(), TestActor.Value));
        return (initial.ResourceId, initial.PathKey, moved.PathKey, moved,
            await StorageTestDocuments.PackageAsync(provider, saved.Package));
    }

    [Fact]
    public Task MemoryNoOpCaseOnlyMoveBackAndConflicts() => CheckPaths(Memory());

    [PostgreSqlFact]
    public async Task PostgreSqlNoOpCaseOnlyMoveBackAndConflicts()
    {
        await using var source = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("ConnectionStrings__cellbridge")!);
        await CheckPaths(PostgreSql(source));
    }

    private static async Task CheckPaths(StorageProvider provider)
    {
        var service = new CellBridgeDocumentService(provider);
        var root = "/" + Guid.NewGuid().ToString("N");
        var original = (await service.CreateAsync(root + "/a/file.docx", MinimalDocx.Create(), TestActor.Value))!;
        var same = await service.MoveDocumentAsync(original.ResourceId, original.Path, TestActor.Value);
        Assert.Equal(original.StateVersion, same.StateVersion);
        Assert.Equal(0, same.Counters.RetiredPaths);
        var caseOnly = await service.MoveDocumentAsync(original.ResourceId, root + "/a/FILE.docx", TestActor.Value);
        Assert.Equal(original.PathKey, caseOnly.PathKey);
        Assert.Equal(original.StateVersion + 1, caseOnly.StateVersion);
        Assert.Equal(0, caseOnly.Counters.RetiredPaths);
        var next = await service.MoveDocumentAsync(original.ResourceId, root + "/b/other.docx", TestActor.Value);
        Assert.Equal(1, next.Counters.RetiredPaths);
        Assert.Contains(original.PathKey, (await provider.Documents.ExportDocumentAsync(original.ResourceId))!.PathClaims);
        var occupied = (await service.CreateAsync(root + "/b/occupied.docx", MinimalDocx.Create(), TestActor.Value))!;
        var occupiedError = await Assert.ThrowsAsync<DocumentOperationLockException>(() => service.MoveDocumentAsync(original.ResourceId, occupied.Path, TestActor.Value).AsTask());
        Assert.Equal("FileAlreadyExists", occupiedError.ErrorCode);
        var retiredError = await Assert.ThrowsAsync<DocumentOperationLockException>(() => service.MoveDocumentAsync(occupied.ResourceId, original.Path, TestActor.Value).AsTask());
        Assert.Equal("FileAlreadyExists", retiredError.ErrorCode);
        Assert.Null(await service.CreateAsync(original.Path, MinimalDocx.Create(), TestActor.Value));
        var back = await service.MoveDocumentAsync(original.ResourceId, original.Path, TestActor.Value);
        Assert.Equal(original.PathKey, back.PathKey);
        Assert.Contains(next.PathKey, (await provider.Documents.ExportDocumentAsync(original.ResourceId))!.PathClaims);
        Assert.Null(await service.CreateAsync(next.Path, MinimalDocx.Create(), TestActor.Value));
    }

    [Fact]
    public Task MemoryPolicyCoordinationAndLocks() => CheckAuthority(Memory());

    [PostgreSqlFact]
    public async Task PostgreSqlPolicyCoordinationAndLocks()
    {
        await using var source = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("ConnectionStrings__cellbridge")!);
        await CheckAuthority(PostgreSql(source));
    }

    private static async Task CheckAuthority(StorageProvider provider)
    {
        var policy = new MovePolicy();
        var service = new CellBridgeDocumentService(provider, authorizationPolicy: policy);
        var actor = new CellBridgeActor(new("move:writer", "writer", "Writer"));
        var root = "/" + Guid.NewGuid().ToString("N");
        var initial = (await service.CreateAsync(root + "/file.docx", MinimalDocx.Create(), TestActor.Value))!;
        Assert.Equal(policy.Binding, initial.Security.AuthorizationPolicy);
        var result = await service.ExecuteAsync(initial.ResourceId, DocumentPartitionKind.FileContents,
            await StorageTestDocuments.SaveRequestAsync(service, initial, MinimalDocx.Create("saved"), actor), new Dictionary<string, string>(), actor);
        Assert.False(Assert.Single(result.Response.SubResponses).Status);
        var saved = result.State;
        var join = HostIntegrationTests.File(initial.ResourceId, SubRequestType.Coauth);
        var attributes = join.SubRequests[0].SubRequestDataAttributes;
        attributes["CoauthRequestType"] = "JoinCoauthoring";
        attributes["Timeout"] = "3600";
        attributes["ClientID"] = Guid.NewGuid().ToString();
        attributes["SchemaLockID"] = Guid.NewGuid().ToString();
        var joined = await new CellBridgeRequestProcessor(service).ExecuteAsync(
            new() { Requests = { join } }, "https://host.test", actor);
        Assert.Equal("Success", joined.Response.Responses[0].SubResponses[0].ErrorCode);
        saved = (await provider.Documents.FindAsync(new DocumentIdentity.Resource(initial.ResourceId)))!;
        var beforeControl = (await provider.Documents.ReadControlAsync(initial.ResourceId))!;
        var revisions = (await provider.Documents.ReadRevisionsAsync(initial.ResourceId, 0, 256))!.Revisions;
        Assert.Single(beforeControl.Editors);
        Assert.Single(beforeControl.Coordination.SchemaOwners);
        var path = root + "/other/file.docx";
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.MoveDocumentAsync(initial.ResourceId, path, TestActor.Value).AsTask());
        Assert.Equal(JsonSerializer.Serialize(saved), JsonSerializer.Serialize(await provider.Documents.FindAsync(new DocumentIdentity.Resource(initial.ResourceId))));
        var moved = await service.MoveDocumentAsync(initial.ResourceId, path, actor, attributes);
        Assert.Equal(policy.Binding, moved.Security.AuthorizationPolicy);
        var movedControl = (await provider.Documents.ReadControlAsync(initial.ResourceId))!;
        Assert.Equal(JsonSerializer.Serialize(beforeControl.Coordination), JsonSerializer.Serialize(movedControl.Coordination));
        Assert.Equal(JsonSerializer.Serialize(beforeControl.Editors), JsonSerializer.Serialize(movedControl.Editors));
        Assert.Equal(saved.Package, moved.Package);
        Assert.Equal(JsonSerializer.Serialize(revisions), JsonSerializer.Serialize((await provider.Documents.ReadRevisionsAsync(initial.ResourceId, 0, 256))!.Revisions));
        attributes["CoauthRequestType"] = "ExitCoauthoring";
        Assert.Equal("Success", (await new CellBridgeRequestProcessor(service).ExecuteAsync(
            new() { Requests = { join } }, "https://host.test", actor)).Response.Responses[0].SubResponses[0].ErrorCode);
        Assert.True((await new SharedDocumentLocks(service).ApplyAsync(initial.ResourceId,
            HostLockOperation.Acquire, "host-token", actor)).Success);
        var locked = (await provider.Documents.FindAsync(new DocumentIdentity.Resource(initial.ResourceId)))!;
        await Assert.ThrowsAsync<DocumentOperationLockException>(() => service.MoveDocumentAsync(initial.ResourceId, path, actor).AsTask());
        await Assert.ThrowsAsync<DocumentOperationLockException>(() => service.MoveDocumentAsync(initial.ResourceId, root + "/third.docx", actor).AsTask());
        Assert.Equal(JsonSerializer.Serialize(locked), JsonSerializer.Serialize(await provider.Documents.FindAsync(new DocumentIdentity.Resource(initial.ResourceId))));
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative/file.docx")]
    [InlineData("https://host.test/file.docx")]
    [InlineData("/")]
    [InlineData("/a/")]
    [InlineData("/a//file.docx")]
    [InlineData("/a/./file.docx")]
    [InlineData("/a/../file.docx")]
    [InlineData("/a\\file.docx")]
    [InlineData("/a:bad/file.docx")]
    [InlineData("/a/file?.docx")]
    [InlineData("/a/file\u0001.docx")]
    [InlineData("/_vti_bin/file.docx")]
    [InlineData("/a/_VTI_INF.HTML")]
    [InlineData("/a/_CellBridge/file.docx")]
    public async Task RejectsInvalidPathWithoutChangingDocument(string path)
    {
        var provider = Memory();
        var service = new CellBridgeDocumentService(provider);
        var initial = (await service.CreateAsync("/valid/file.docx", MinimalDocx.Create(), TestActor.Value))!;
        await Assert.ThrowsAsync<ArgumentException>(() => service.MoveDocumentAsync(initial.ResourceId, path, TestActor.Value).AsTask());
        Assert.Equal(initial, await provider.Documents.FindAsync(new DocumentIdentity.Resource(initial.ResourceId)));
    }

    [Fact]
    public async Task RejectsOverlongSegmentWithoutChangingDocument()
    {
        var provider = Memory(); var service = new CellBridgeDocumentService(provider);
        var initial = (await service.CreateAsync("/valid/file.docx", MinimalDocx.Create(), TestActor.Value))!;
        await Assert.ThrowsAsync<ArgumentException>(() => service.MoveDocumentAsync(initial.ResourceId,
            "/valid/" + new string('a', 256), TestActor.Value).AsTask());
        Assert.Equal(initial, await provider.Documents.FindAsync(new DocumentIdentity.Resource(initial.ResourceId)));
    }

    [Fact]
    public Task MemoryConcurrentMoveAndCreateHaveOneOwner() => CheckRace(Memory());

    [PostgreSqlFact]
    public async Task PostgreSqlConcurrentMoveAndCreateHaveOneOwner()
    {
        await using var source = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("ConnectionStrings__cellbridge")!);
        await CheckRace(PostgreSql(source));
    }

    private static async Task CheckRace(StorageProvider provider)
    {
        var service = new CellBridgeDocumentService(provider);
        for (var i = 0; i < 4; i++)
        {
            var root = "/" + Guid.NewGuid().ToString("N");
            var original = (await service.CreateAsync(root + "/old/file.docx", MinimalDocx.Create(), TestActor.Value))!;
            var destination = root + "/new/file.docx";
            var move = TryMove(service, original.ResourceId, destination);
            var create = service.CreateAsync(destination, MinimalDocx.Create(), TestActor.Value).AsTask();
            await Task.WhenAll(move, create).WaitAsync(TimeSpan.FromSeconds(15));
            Assert.NotEqual(await move, await create is not null);
            Assert.Equal(await move ? original.ResourceId : (await create)!.ResourceId,
                (await provider.Documents.FindAsync(new DocumentIdentity.Path(destination.ToUpperInvariant())))!.ResourceId);
            if (await move) Assert.Null(await service.CreateAsync(original.Path, MinimalDocx.Create(), TestActor.Value));
        }
    }

    [Fact]
    public Task MemoryQuotaFailureRollsBackMove() => CheckQuota(Memory(new() { MaxRetiredPathKeys = 1 }));

    [PostgreSqlFact]
    public Task PostgreSqlQuotaFailureRollsBackMove() =>
        RepositoryTestDatabase.WithDatabase(new() { MaxRetiredPathKeys = 1 }, async source =>
            await CheckQuota(new(new PostgreSqlStateStore(source, new() { MaxRetiredPathKeys = 1 }),
                new PostgreSqlContentStore(source), new() { MaxRetiredPathKeys = 1 })));

    private static async Task CheckQuota(StorageProvider provider)
    {
        var service = new CellBridgeDocumentService(provider);
        var root = "/" + Guid.NewGuid().ToString("N");
        var initial = (await service.CreateAsync(root + "/a/file.docx", MinimalDocx.Create(), TestActor.Value))!;
        await service.MoveDocumentAsync(initial.ResourceId, root + "/b/file.docx", TestActor.Value);
        var before = (await provider.Documents.FindAsync(new DocumentIdentity.Resource(initial.ResourceId)))!;
        await Assert.ThrowsAsync<StorageQuotaExceededException>(() => service.MoveDocumentAsync(initial.ResourceId,
            root + "/c/file.docx", TestActor.Value).AsTask());
        Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(await provider.Documents.FindAsync(new DocumentIdentity.Resource(initial.ResourceId))));
        Assert.Null(await provider.Documents.FindAsync(new DocumentIdentity.Path((root + "/c/file.docx").ToUpperInvariant())));
    }

    private static async Task<bool> TryMove(CellBridgeDocumentService service, Guid id, string path)
    {
        try { await service.MoveDocumentAsync(id, path, TestActor.Value); return true; }
        catch (DocumentOperationLockException e) when (e.ErrorCode == "FileAlreadyExists") { return false; }
    }

    private static StorageProvider Memory(StorageLimits? limits = null) =>
        StorageTestDocuments.Memory(limits: limits);
    private static StorageProvider PostgreSql(NpgsqlDataSource source) =>
        new(new PostgreSqlStateStore(source), new PostgreSqlContentStore(source));

    private sealed class MovePolicy : ICellBridgeAuthorizationPolicy
    {
        public DocumentAuthorizationBinding Binding { get; } = new("move:test", 1, 1);
        public string PolicyDomain => Binding.PolicyDomain;
        public DocumentAuthorizationBinding BindNewDocument(Guid resourceId) => Binding;
        public ICellBridgeAuthorizationSnapshot? Resolve(DocumentAuthorizationView state) =>
            state.Security.AuthorizationPolicy == Binding ? new Snapshot(state.ResourceId, Binding) : null;
        private sealed record Snapshot(Guid ResourceId, DocumentAuthorizationBinding Binding) : ICellBridgeAuthorizationSnapshot
        {
            public DocumentAccess Evaluate(string subject) => subject == "move:writer" ? DocumentAccess.Write : DocumentAccess.None;
        }
    }
}
