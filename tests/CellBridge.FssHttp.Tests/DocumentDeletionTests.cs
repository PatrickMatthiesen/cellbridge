using CellBridge.AspNetCore;
using CellBridge.FssHttp;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;

namespace CellBridge.FssHttp.Tests;

public sealed class DocumentDeletionTests
{
    [Fact]
    public async Task OwnerCanDeleteAndTheDocumentIncarnationRemainsTombstoned()
    {
        var provider = TestStorage.Memory();
        var service = new CellBridgeDocumentService(provider);
        var created = (await service.CreateAsync("/shared/delete.docx", [1, 2, 3], TestActor.Value))!;

        var result = await service.DeleteDocumentAsync(created.ResourceId, created.LifecycleGeneration,
            created.StateVersion, TestActor.Value);

        Assert.Equal(DocumentDeletionStatus.Deleted, result);
        Assert.Null(await provider.Documents.FindAsync(new DocumentIdentity.Path(created.PathKey)));
        var tombstone = await provider.Documents.FindAsync(new DocumentIdentity.Resource(created.ResourceId, IncludeDeleted: true));
        Assert.NotNull(tombstone);
        Assert.True(tombstone.IsDeleted);
        Assert.Equal(created.LifecycleGeneration, tombstone.LifecycleGeneration);
    }

    [Theory]
    [InlineData(DocumentAccess.Read)]
    [InlineData(DocumentAccess.Read | DocumentAccess.Write)]
    public async Task NonOwnerCannotDeleteReadOrWriteGrantedDocument(DocumentAccess grant)
    {
        var provider = TestStorage.Memory();
        var service = new CellBridgeDocumentService(provider);
        var created = (await service.CreateAsync("/shared/delete-grant.docx", [1], TestActor.Value))!;
        await TestStorage.SetPermissionsAsync(provider, created.ResourceId, created.Security with
        {
            Grants = created.Security.Grants.Add("tests:editor", grant),
        });
        var current = await provider.Documents.FindAsync(new DocumentIdentity.Resource(created.ResourceId));
        Assert.NotNull(current);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () => await service.DeleteDocumentAsync(
            created.ResourceId, current.LifecycleGeneration, current.StateVersion,
            new CellBridgeActor(new("tests:editor", "editor", "Editor"))));
        var editor = new CellBridgeActor(new("tests:editor", "editor", "Editor"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () => await service.DeleteDocumentAsync(
            created.ResourceId, current.LifecycleGeneration + 1, current.StateVersion,
            editor));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () => await service.DeleteDocumentAsync(
            created.ResourceId, current.LifecycleGeneration, current.StateVersion + 1,
            editor));

        var stillThere = await provider.Documents.FindAsync(new DocumentIdentity.Resource(created.ResourceId));
        Assert.NotNull(stillThere);
        Assert.False(stillThere.IsDeleted);
    }

    [Fact]
    public async Task OwnerReadCeilingCannotDeleteDocument()
    {
        var provider = TestStorage.Memory();
        var service = new CellBridgeDocumentService(provider);
        var created = (await service.CreateAsync("/shared/delete-read-ceiling.docx", [1], TestActor.Value))!;
        var readOnlyOwner = TestActor.Value with
        {
            AccessLimit = new DocumentAccessLimit(created.ResourceId, DocumentAccess.Read),
        };

        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () => await service.DeleteDocumentAsync(
            created.ResourceId, created.LifecycleGeneration, created.StateVersion, readOnlyOwner));
        Assert.False((await provider.Documents.FindAsync(new DocumentIdentity.Resource(created.ResourceId)))!.IsDeleted);
    }

    [Fact]
    public async Task FreshExternalWritePolicyRevocationPreventsDelete()
    {
        var authority = new MutableDeleteAuthority();
        var provider = TestStorage.Memory();
        var service = new CellBridgeDocumentService(provider, documentAuthorizationProvider: authority);
        var created = (await service.CreateExternalAsync(Guid.NewGuid(), "/shared/delete-external.docx", [1],
            new("tenant", "workspace"), TestActor.Value))!;
        authority.WriteAllowed = false;

        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () => await service.DeleteDocumentAsync(
            created.ResourceId, created.LifecycleGeneration, created.StateVersion, TestActor.Value));
        Assert.False((await provider.Documents.FindAsync(new DocumentIdentity.Resource(created.ResourceId)))!.IsDeleted);
    }

    [Fact]
    public async Task ActiveEditorPreventsDelete()
    {
        var provider = TestStorage.Memory();
        var service = new CellBridgeDocumentService(provider);
        var created = (await service.CreateAsync("/shared/delete-active-editor.docx", [1], TestActor.Value))!;
        var joined = await service.ExecuteControlAsync(created.ResourceId, new FssHttpSubRequest
        {
            Type = SubRequestType.EditorsTable,
            SubRequestToken = 1,
            SubRequestDataAttributes =
            {
                ["EditorsTableRequestType"] = "JoinEditingSession",
                ["ClientID"] = Guid.NewGuid().ToString("D"),
                ["Timeout"] = "60",
                ["AsEditor"] = "true",
            },
        }, TestActor.Value, CancellationToken.None);
        Assert.True(joined.Changed);

        var current = await provider.Documents.FindAsync(new DocumentIdentity.Resource(created.ResourceId));
        Assert.NotNull(current);
        Assert.Equal(DocumentDeletionStatus.Conflict, await service.DeleteDocumentAsync(created.ResourceId,
            current.LifecycleGeneration, current.StateVersion, TestActor.Value));
        Assert.False((await provider.Documents.FindAsync(new DocumentIdentity.Resource(created.ResourceId)))!.IsDeleted);
    }

    [Fact]
    public async Task DeleteRejectsStaleStateAndConcurrentDeletesHaveOneWinner()
    {
        var provider = TestStorage.Memory();
        var service = new CellBridgeDocumentService(provider);
        var created = (await service.CreateAsync("/shared/delete-race.docx", [1], TestActor.Value))!;
        var renamed = await service.RenameDocumentAsync(created.ResourceId, "renamed.docx", TestActor.Value);

        Assert.Equal(DocumentDeletionStatus.Conflict, await service.DeleteDocumentAsync(created.ResourceId,
            created.LifecycleGeneration, created.StateVersion, TestActor.Value));

        var first = service.DeleteDocumentAsync(renamed.ResourceId, renamed.LifecycleGeneration,
            renamed.StateVersion, TestActor.Value).AsTask();
        var second = service.DeleteDocumentAsync(renamed.ResourceId, renamed.LifecycleGeneration,
            renamed.StateVersion, TestActor.Value).AsTask();
        var results = await Task.WhenAll(first, second);

        Assert.Single(results, status => status == DocumentDeletionStatus.Deleted);
        Assert.All(results, status => Assert.True(status is DocumentDeletionStatus.Deleted or
            DocumentDeletionStatus.NotFound or DocumentDeletionStatus.Conflict));
    }

    [Fact]
    public async Task LifecycleCompareAndSwapRejectsChangedAuthorityAndCoordination()
    {
        var provider = TestStorage.Memory();
        var service = new CellBridgeDocumentService(provider);
        var created = (await service.CreateAsync("/shared/delete-cas.docx", [1], TestActor.Value))!;
        var before = await provider.Documents.ReadControlAsync(created.ResourceId);
        Assert.NotNull(before);

        await TestStorage.SetPermissionsAsync(provider, created.ResourceId, created.Security with
        {
            Grants = created.Security.Grants.Add("tests:reader", DocumentAccess.Read),
        });
        var afterPermissions = await provider.Documents.ReadControlAsync(created.ResourceId);
        Assert.NotNull(afterPermissions);

        var staleAuthority = await provider.Documents.ChangeLifecycleAsync(new DocumentLifecycleCommand.Delete(
            created.ResourceId, created.LifecycleGeneration, created.StateVersion)
        {
            ExpectedAuthorityGeneration = before.Head.AuthorityGeneration,
        });
        Assert.False(staleAuthority.Applied);

        var lockResult = await new SharedDocumentLocks(service).ApplyAsync(created.ResourceId,
            HostLockOperation.Acquire, "test-delete-cas-lock", TestActor.Value);
        Assert.True(lockResult.Success);
        var staleCoordination = await provider.Documents.ChangeLifecycleAsync(new DocumentLifecycleCommand.Delete(
            created.ResourceId, created.LifecycleGeneration, created.StateVersion)
        {
            ExpectedCoordinationGeneration = afterPermissions.Coordination.Generation,
        });
        Assert.False(staleCoordination.Applied);
    }

    private sealed class MutableDeleteAuthority : IDocumentAuthorizationProvider
    {
        public string PolicyDomain => "tests:delete";
        public bool WriteAllowed { get; set; } = true;

        public ValueTask<DocumentAuthorizationDecision> AuthorizeAsync(DocumentAuthorizationRequest request,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(new DocumentAuthorizationDecision(
                request.Action != DocumentAction.Write || WriteAllowed));
    }
}
