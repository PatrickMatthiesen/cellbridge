using System.Text.Json;
using CellBridge.AspNetCore;
using CellBridge.FssHttp;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using CellBridge.Storage.PostgreSql;
using CellBridge.Web;
using Npgsql;

namespace CellBridge.Storage.Tests;

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
        DocumentState moved;
        await using (var source = NpgsqlDataSource.Create(connection))
        {
            var provider = PostgreSql(source);
            (id, oldKey, newKey, moved, expectedContent) = await CheckIdentity(provider);
        }
        await using var reopenedSource = NpgsqlDataSource.Create(connection);
        var reopened = PostgreSql(reopenedSource);
        var actual = (await reopened.State.FindByResourceIdAsync(id))!;
        Assert.Equal(JsonSerializer.Serialize(moved), JsonSerializer.Serialize(actual));
        Assert.Null(await reopened.State.FindByPathKeyAsync(oldKey));
        Assert.Equal(id, (await reopened.State.FindByPathKeyAsync(newKey))!.ResourceId);
        Assert.Equal(expectedContent, await reopened.Content.ReadVerifiedAsync(actual.Content));
    }

    private static async Task<(Guid Id, string OldKey, string NewKey, DocumentState Moved, byte[] Content)> CheckIdentity(StorageProvider provider)
    {
        var service = new CellBridgeDocumentService(provider);
        var root = "/" + Guid.NewGuid().ToString("N");
        var initial = (await service.CreateAsync(root + "/source/file.docx", MinimalDocx.Create("first"), TestActor.Value))!;
        var saved = await RevisionHistoryTests.Save(service, initial, "second");
        var destination = root + "/new folder/æ # %2F.docx";
        var moved = await service.MoveDocumentAsync(initial.ResourceId, destination, TestActor.Value);
        var committed = (await provider.State.FindByResourceIdAsync(initial.ResourceId))!;
        Assert.Equal(JsonSerializer.Serialize(committed), JsonSerializer.Serialize(moved));
        Assert.Equal(initial.ResourceId, moved.ResourceId);
        Assert.Equal(destination, moved.Path);
        Assert.Equal(saved.Content, moved.Content);
        Assert.Equal(saved.ContentVersion, moved.ContentVersion);
        Assert.Equal(JsonSerializer.Serialize(saved.Revisions), JsonSerializer.Serialize(moved.Revisions));
        Assert.Equal(saved.LifecycleGeneration, moved.LifecycleGeneration);
        Assert.Equal(saved.Security, moved.Security);
        Assert.Null(await provider.State.FindByPathKeyAsync(initial.PathKey));
        Assert.Equal(initial.ResourceId, (await provider.State.FindByPathKeyAsync(moved.PathKey))!.ResourceId);
        Assert.Contains(initial.PathKey, moved.RetiredPathKeys);
        Assert.Null(await service.CreateAsync(initial.Path, MinimalDocx.Create(), TestActor.Value));
        return (initial.ResourceId, initial.PathKey, moved.PathKey, moved,
            await provider.Content.ReadVerifiedAsync(saved.Content));
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
        Assert.Empty(same.RetiredPathKeys);
        var caseOnly = await service.MoveDocumentAsync(original.ResourceId, root + "/a/FILE.docx", TestActor.Value);
        Assert.Equal(original.PathKey, caseOnly.PathKey);
        Assert.Equal(original.StateVersion + 1, caseOnly.StateVersion);
        Assert.Empty(caseOnly.RetiredPathKeys);
        var next = await service.MoveDocumentAsync(original.ResourceId, root + "/b/other.docx", TestActor.Value);
        Assert.Equal(new[] { original.PathKey }, next.RetiredPathKeys.ToArray());
        var occupied = (await service.CreateAsync(root + "/b/occupied.docx", MinimalDocx.Create(), TestActor.Value))!;
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.MoveDocumentAsync(original.ResourceId, occupied.Path, TestActor.Value).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.MoveDocumentAsync(occupied.ResourceId, original.Path, TestActor.Value).AsTask());
        Assert.Null(await service.CreateAsync(original.Path, MinimalDocx.Create(), TestActor.Value));
        var back = await service.MoveDocumentAsync(original.ResourceId, original.Path, TestActor.Value);
        Assert.Equal(original.PathKey, back.PathKey);
        Assert.Contains(next.PathKey, back.RetiredPathKeys);
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
            RevisionHistoryTests.Request(initial, MinimalDocx.Create("saved")), new Dictionary<string, string>(), actor);
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
        saved = (await provider.State.FindByResourceIdAsync(initial.ResourceId))!;
        Assert.Single(saved.Editors);
        Assert.Single(saved.Coordination.SchemaOwners);
        var path = root + "/other/file.docx";
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.MoveDocumentAsync(initial.ResourceId, path, TestActor.Value).AsTask());
        Assert.Equal(JsonSerializer.Serialize(saved), JsonSerializer.Serialize(await provider.State.FindByResourceIdAsync(initial.ResourceId)));
        var moved = await service.MoveDocumentAsync(initial.ResourceId, path, actor, attributes);
        Assert.Equal(policy.Binding, moved.Security.AuthorizationPolicy);
        Assert.Equal(JsonSerializer.Serialize(saved.Coordination), JsonSerializer.Serialize(moved.Coordination));
        Assert.Equal(JsonSerializer.Serialize(saved.Editors), JsonSerializer.Serialize(moved.Editors));
        Assert.Equal(saved.Content, moved.Content);
        Assert.Equal(JsonSerializer.Serialize(saved.Revisions), JsonSerializer.Serialize(moved.Revisions));
        var locked = await provider.State.TransitionAsync(initial.ResourceId, (current, _) =>
        {
            var next = current with { Coordination = current.Coordination with
                { HostLock = new("host-token", actor.Identity.Subject, DateTime.UtcNow.AddMinutes(1)) } };
            return new StateTransition<DocumentState>(next, next);
        });
        locked = (await provider.State.FindByResourceIdAsync(initial.ResourceId))!;
        await Assert.ThrowsAsync<DocumentOperationLockException>(() => service.MoveDocumentAsync(initial.ResourceId, path, actor).AsTask());
        await Assert.ThrowsAsync<DocumentOperationLockException>(() => service.MoveDocumentAsync(initial.ResourceId, root + "/third.docx", actor).AsTask());
        Assert.Equal(JsonSerializer.Serialize(locked), JsonSerializer.Serialize(await provider.State.FindByResourceIdAsync(initial.ResourceId)));
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
        Assert.Equal(initial, await provider.State.FindByResourceIdAsync(initial.ResourceId));
    }

    [Fact]
    public async Task RejectsOverlongSegmentWithoutChangingDocument()
    {
        var provider = Memory(); var service = new CellBridgeDocumentService(provider);
        var initial = (await service.CreateAsync("/valid/file.docx", MinimalDocx.Create(), TestActor.Value))!;
        await Assert.ThrowsAsync<ArgumentException>(() => service.MoveDocumentAsync(initial.ResourceId,
            "/valid/" + new string('a', 256), TestActor.Value).AsTask());
        Assert.Equal(initial, await provider.State.FindByResourceIdAsync(initial.ResourceId));
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
                (await provider.State.FindByPathKeyAsync(destination.ToUpperInvariant()))!.ResourceId);
            if (await move) Assert.Null(await service.CreateAsync(original.Path, MinimalDocx.Create(), TestActor.Value));
        }
    }

    [Fact]
    public Task MemoryQuotaFailureRollsBackMove() => CheckQuota(Memory(new() { MaxRetiredPathKeys = 1 }));

    [PostgreSqlFact]
    public Task PostgreSqlQuotaFailureRollsBackMove() =>
        BudgetAndQueryTests.WithDatabase(new() { MaxRetiredPathKeys = 1 }, async source =>
            await CheckQuota(new(new PostgreSqlStateStore(source, new() { MaxRetiredPathKeys = 1 }),
                new PostgreSqlContentStore(source), new() { MaxRetiredPathKeys = 1 })));

    private static async Task CheckQuota(StorageProvider provider)
    {
        var service = new CellBridgeDocumentService(provider);
        var root = "/" + Guid.NewGuid().ToString("N");
        var initial = (await service.CreateAsync(root + "/a/file.docx", MinimalDocx.Create(), TestActor.Value))!;
        await service.MoveDocumentAsync(initial.ResourceId, root + "/b/file.docx", TestActor.Value);
        var before = (await provider.State.FindByResourceIdAsync(initial.ResourceId))!;
        await Assert.ThrowsAsync<StorageQuotaExceededException>(() => service.MoveDocumentAsync(initial.ResourceId,
            root + "/c/file.docx", TestActor.Value).AsTask());
        Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(await provider.State.FindByResourceIdAsync(initial.ResourceId)));
        Assert.Null(await provider.State.FindByPathKeyAsync((root + "/c/file.docx").ToUpperInvariant()));
    }

    private static async Task<bool> TryMove(CellBridgeDocumentService service, Guid id, string path)
    {
        try { await service.MoveDocumentAsync(id, path, TestActor.Value); return true; }
        catch (InvalidOperationException) { return false; }
    }

    private static StorageProvider Memory(StorageLimits? limits = null) =>
        new(new InMemoryStateStore(), new InMemoryContentStore(), limits: limits);
    private static StorageProvider PostgreSql(NpgsqlDataSource source) =>
        new(new PostgreSqlStateStore(source), new PostgreSqlContentStore(source));

    private sealed class MovePolicy : ICellBridgeAuthorizationPolicy
    {
        public DocumentAuthorizationBinding Binding { get; } = new("move:test", 1, 1);
        public string PolicyDomain => Binding.PolicyDomain;
        public DocumentAuthorizationBinding BindNewDocument(Guid resourceId) => Binding;
        public ICellBridgeAuthorizationSnapshot? Resolve(DocumentState state) =>
            state.Security.AuthorizationPolicy == Binding ? new Snapshot(state.ResourceId, Binding) : null;
        private sealed record Snapshot(Guid ResourceId, DocumentAuthorizationBinding Binding) : ICellBridgeAuthorizationSnapshot
        {
            public DocumentAccess Evaluate(string subject) => subject == "move:writer" ? DocumentAccess.Write : DocumentAccess.None;
        }
    }
}
