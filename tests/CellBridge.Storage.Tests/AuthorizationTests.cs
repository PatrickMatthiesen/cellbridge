using System.Collections.Immutable;
using System.Text.Json;
using CellBridge.AspNetCore;
using CellBridge.FssHttp;
using CellBridge.FssHttpB;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using CellBridge.TestFixtures;

namespace CellBridge.Storage.Tests;

public sealed class AuthorizationTests
{
    private static readonly CellBridgeActor Other = new(new("tests:other", "other", "Other user"));

    [Fact]
    public async Task UnprivilegedCreationAndResolutionFailClosed()
    {
        var provider = Provider();
        var service = new CellBridgeDocumentService(provider);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.CreateAsync("/shared/denied.docx", MinimalDocx.Create(), Other).AsTask());
        Assert.Empty(await provider.Documents.ListAsync(0, 100));
        var state = (await service.CreateAsync("/shared/allowed.docx", MinimalDocx.Create(), TestActor.Value))!;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ResolveAsync(new FssHttpRequest
        { Url = state.Path }, Other).AsTask());
    }

    [Fact]
    public async Task FileQueriesRecheckReadPermissionBeforeReadingPayloads()
    {
        var content = new PausingContentStore();
        var provider = StorageTestDocuments.Memory(content);
        var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/shared/query-access.docx", MinimalDocx.Create(), TestActor.Value))!;
        var request = new FsshttpbCellRequest();
        request.SubRequests.Add(new(RequestTypes.QueryChanges) { RequestId = 1,
            Data = new QueryChangesSubRequestData { IncludeStorageManifest = true, IncludeCellChanges = true } });
        async Task AssertDenied()
        {
            int reads = content.Reads;
            var denied = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents,
                request, new Dictionary<string, string>(), Other);
            Assert.Equal(CellBridgeAuthorization.AccessDeniedHResult, Assert.Single(denied.Response.SubResponses).Error!.ErrorCode);
            Assert.Null(denied.Response.DataElementPackage);
            Assert.Equal(reads, content.Reads);
        }
        await AssertDenied();
        await Grant(provider, state.ResourceId, Other.Identity.Subject, DocumentAccess.Read);
        var allowed = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents,
            request, new Dictionary<string, string>(), Other);
        Assert.False(Assert.Single(allowed.Response.SubResponses).Status);
        Assert.NotEmpty(allowed.Response.DataElementPackage!.DataElements);
        await Grant(provider, state.ResourceId, Other.Identity.Subject, DocumentAccess.None);
        await AssertDenied();
    }

    [Fact]
    public async Task ReadOnlySaveIsRejectedBeforeContentStaging()
    {
        var content = new PausingContentStore();
        var provider = StorageTestDocuments.Memory(content);
        var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/shared/readonly.docx", MinimalDocx.Create(), TestActor.Value))!;
        await Grant(provider, state.ResourceId, Other.Identity.Subject, DocumentAccess.Read);
        var before = await provider.Documents.FindAsync(new DocumentIdentity.Resource(state.ResourceId));
        int writes = content.Writes;
        var request = await StorageTestDocuments.SaveRequestAsync(service, state, MinimalDocx.Create("denied write"));
        var result = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents,
            request, new Dictionary<string, string>(), Other);
        Assert.Equal(CellBridgeAuthorization.AccessDeniedHResult, Assert.Single(result.Response.SubResponses).Error!.ErrorCode);
        Assert.Equal(writes, content.Writes);
        Assert.Equal(before, await provider.Documents.FindAsync(new DocumentIdentity.Resource(state.ResourceId)));
    }

    [Fact]
    public async Task RevocationDuringStagingPreventsPublication()
    {
        var content = new PausingContentStore();
        var provider = StorageTestDocuments.Memory(content);
        var service = new CellBridgeDocumentService(provider);
        var owner = new CellBridgeActor(new("tests:owner", "owner", "Owner"), true);
        var state = (await service.CreateAsync("/shared/race.docx", MinimalDocx.Create(), owner))!;
        await Grant(provider, state.ResourceId, TestActor.Value.Identity.Subject, DocumentAccess.Read | DocumentAccess.Write);
        var request = await StorageTestDocuments.SaveRequestAsync(service, state, MinimalDocx.Create("staged write"));
        content.Pause = true;
        var saving = service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents, request,
            new Dictionary<string, string>(), TestActor.Value).AsTask();
        await content.Staged.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Grant(provider, state.ResourceId, TestActor.Value.Identity.Subject, DocumentAccess.Read);
        var revoked = await provider.Documents.FindAsync(new DocumentIdentity.Resource(state.ResourceId));
        content.Resume.TrySetResult();
        var result = await saving;
        Assert.Equal(CellBridgeAuthorization.AccessDeniedHResult, Assert.Single(result.Response.SubResponses).Error!.ErrorCode);
        Assert.Equal(revoked, await provider.Documents.FindAsync(new DocumentIdentity.Resource(state.ResourceId)));
        Assert.Equal(state.Package, revoked!.Package);
        Assert.Equal(state.ContentVersion, revoked.ContentVersion);
        Assert.Equal(state.Security.ModifiedBy, revoked.Security.ModifiedBy);
        Assert.Equal(0, revoked.Counters.SaveReceipts);
    }

    [Fact]
    public async Task ReceiptIsBoundToWriterAndRecheckedAfterRevocation()
    {
        var provider = Provider();
        var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/shared/retry.docx", MinimalDocx.Create(), TestActor.Value))!;
        await Grant(provider, state.ResourceId, Other.Identity.Subject, DocumentAccess.Read | DocumentAccess.Write);
        var request = await StorageTestDocuments.SaveRequestAsync(service, state, MinimalDocx.Create("accepted write"));
        var accepted = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents,
            request, new Dictionary<string, string>(), TestActor.Value);
        Assert.False(Assert.Single(accepted.Response.SubResponses).Status);
        var identity = IndexedPartitionSavePreparer.Identify(state.LifecycleGeneration, 0, request.SubRequests[0], request.DataElementPackage!).Operation;
        Assert.Equal(TestActor.Value.Identity.Subject, (await provider.Documents.ReadReceiptAsync(state.ResourceId, identity))!.Receipt!.OwnerSubject);
        var denied = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents,
            request, new Dictionary<string, string>(), Other);
        Assert.Equal(CellBridgeAuthorization.AccessDeniedHResult, Assert.Single(denied.Response.SubResponses).Error!.ErrorCode);
        Assert.Equal(accepted.State, await provider.Documents.FindAsync(new DocumentIdentity.Resource(state.ResourceId)));
        // An unowned legacy receipt cannot authenticate a replay either.
        var unowned = new CellBridgeDocumentService(new(new UnownedReceipt(provider.Documents), provider.Content));
        denied = await unowned.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents,
            request, new Dictionary<string, string>(), TestActor.Value);
        Assert.Equal(CellBridgeAuthorization.AccessDeniedHResult, Assert.Single(denied.Response.SubResponses).Error!.ErrorCode);
        var current = (await provider.Documents.ReadControlAsync(state.ResourceId))!;
        await TestStorage.SetPermissionsAsync(provider, state.ResourceId, current.Head.Security with
            { Owner = Other.Identity.Subject, Grants = current.Head.Security.Grants.SetItem(TestActor.Value.Identity.Subject, DocumentAccess.Read) });
        var revoked = await provider.Documents.FindAsync(new DocumentIdentity.Resource(state.ResourceId));
        denied = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents,
            request, new Dictionary<string, string>(), TestActor.Value);
        Assert.Equal(CellBridgeAuthorization.AccessDeniedHResult, Assert.Single(denied.Response.SubResponses).Error!.ErrorCode);
        Assert.Equal(revoked, await provider.Documents.FindAsync(new DocumentIdentity.Resource(state.ResourceId)));
    }

    [Fact]
    public async Task RevocationReconcilesEditorKnowledgeAndLeasesAtomically()
    {
        var provider = Provider();
        var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/shared/editors.docx", MinimalDocx.Create(), TestActor.Value))!;
        await Grant(provider, state.ResourceId, Other.Identity.Subject, DocumentAccess.Read | DocumentAccess.Write);
        var client = Guid.NewGuid(); var schema = Guid.NewGuid().ToString();
        var current = (await provider.Documents.ReadControlAsync(state.ResourceId))!;
        var joined = await provider.Documents.ExecuteControlAsync(new DocumentControlCommand.Office(state.ResourceId,
            TestStorage.Admit(current.Head, Other), new() { Type = SubRequestType.Coauth, SubRequestDataAttributes =
            { ["CoauthRequestType"] = "JoinCoauthoring", ["ClientID"] = client.ToString(), ["SchemaLockID"] = schema, ["Timeout"] = "3600" } }));
        Assert.Null(joined.ErrorCode);
        var before = (await provider.Documents.ReadControlAsync(state.ResourceId))!;
        Assert.True(Assert.Single(before.Editors).AsEditor);
        Assert.Equal(client.ToString(), Assert.Single(before.Coordination.CoauthorClients));
        await Grant(provider, state.ResourceId, Other.Identity.Subject, DocumentAccess.Read);
        var after = (await provider.Documents.ReadControlAsync(state.ResourceId))!;
        Assert.False(Assert.Single(after.Editors).AsEditor);
        Assert.Equal(Other.Identity, Assert.Single(after.Editors).Owner);
        Assert.True(after.Partitions.Single(p => p.Kind == 2).Knowledge > before.Partitions.Single(p => p.Kind == 2).Knowledge);
        Assert.Empty(after.Coordination.SchemaOwners);
        Assert.Empty(after.Coordination.CoauthorClients);
        Assert.False(after.Coordination.CoauthorTransitionPending);
        Assert.Equal(before.Head.Package, after.Head.Package);
        var restored = JsonSerializer.Deserialize<DocumentControlRead>(JsonSerializer.Serialize(after))!;
        Assert.Equal(Other.Identity, Assert.Single(restored.Editors).Owner);
        await Grant(provider, state.ResourceId, Other.Identity.Subject, DocumentAccess.None);
        Assert.Empty((await provider.Documents.ReadControlAsync(state.ResourceId))!.Editors);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ActiveLockTokensDoNotAuthorizeAnotherSubject(bool schemaLock)
    {
        var document = new DocumentStore().Put("/shared/locks.docx", MinimalDocx.Create());
        var coordinator = FssHttpLockCoordinator.For(document, TestActor.Value.Identity);
        var id = Guid.NewGuid().ToString(); var client = Guid.NewGuid().ToString();
        var acquire = new FssHttpSubRequest { SubRequestDataAttributes =
        { [schemaLock ? "SchemaLockRequestType" : "ExclusiveLockRequestType"] = "GetLock",
          [schemaLock ? "SchemaLockID" : "ExclusiveLockID"] = id, ["ClientID"] = client, ["Timeout"] = "3600" } };
        Assert.Equal(LockOperationResult.Granted, schemaLock ? coordinator.ApplySchemaLock(acquire, new()) : coordinator.ApplyExclusiveLock(acquire, new()));
        var before = JsonSerializer.Serialize(coordinator.Capture());
        FssHttpLockCoordinator.For(document, Other.Identity);
        foreach (var includeClient in new[] { false, true })
        {
            var attributes = new Dictionary<string, string> { [schemaLock ? "SchemaLockID" : "ExclusiveLockID"] = id, ["BypassLockID"] = id };
            if (includeClient) attributes["ClientID"] = client;
            Assert.False(coordinator.ExecuteCellWrite(attributes, () => true, out _, out var error));
            Assert.Equal("FileUnauthorizedAccess", error);
        }
        acquire.SubRequestDataAttributes[schemaLock ? "SchemaLockRequestType" : "ExclusiveLockRequestType"] = "RefreshLock";
        Assert.Equal(LockOperationResult.AccessDenied, schemaLock ? coordinator.ApplySchemaLock(acquire, new()) : coordinator.ApplyExclusiveLock(acquire, new()));
        Assert.Equal(before, JsonSerializer.Serialize(coordinator.Capture()));
        // The authenticated owner may save without ClientID when Office omits it.
        FssHttpLockCoordinator.For(document, TestActor.Value.Identity);
        Assert.True(coordinator.ExecuteCellWrite(new Dictionary<string, string> { [schemaLock ? "SchemaLockID" : "ExclusiveLockID"] = id }, () => true, out _, out _));
    }

    [Fact]
    public async Task CatalogPaginationSkipsHiddenRowsBeforeOffsetAndLimit()
    {
        var provider = Provider();
        var service = new CellBridgeDocumentService(provider);
        for (int i = 0; i < 270; i++) await service.CreateAsync($"/shared/{i:D3}.bin", [1], TestActor.Value);
        var allowed = (await service.CreateAsync("/shared/z-allowed.bin", [2], TestActor.Value))!;
        await Grant(provider, allowed.ResourceId, Other.Identity.Subject, DocumentAccess.Read);
        var page = await AuthorizedDocumentCatalog.ReadAsync(service, Other, 0, 1);
        Assert.Equal(allowed.ResourceId, Assert.Single(page.Documents).ResourceId);
        Assert.Null(page.NextOffset);
        Assert.Empty((await AuthorizedDocumentCatalog.ReadAsync(service, Other, 1, 1)).Documents);
    }

    private static StorageProvider Provider() => StorageTestDocuments.Memory();
    private static async ValueTask<bool> Grant(StorageProvider provider, Guid id, string subject, DocumentAccess access)
    {
        var current = (await provider.Documents.ReadControlAsync(id))!;
        var owner = current.Head.Security.Owner!;
        var issuer = new CellBridgeActor(current.Head.Security.CreatedBy is { } author && author.Subject == owner
            ? author : new(owner, owner, owner));
        var result = await provider.Documents.ExecuteControlAsync(new DocumentControlCommand.SetPermissions(id, TestStorage.Admit(current.Head, issuer),
            current.Head.Security with { Grants = current.Head.Security.Grants.SetItem(subject, access) }, current.Head.AuthorityGeneration));
        Assert.True(result.Changed);
        return result.Changed;
    }

    private sealed class UnownedReceipt(IDocumentRepository inner) : RepositoryTestProxy(inner)
    {
        public override async ValueTask<SaveReceiptRead?> ReadReceiptAsync(Guid id, SaveOperationIdentity operation, CancellationToken ct = default)
        {
            var read = await Inner.ReadReceiptAsync(id, operation, ct);
            return read?.Receipt is { } receipt ? read with { Receipt = receipt with { OwnerSubject = null } } : read;
        }
    }

    private sealed class PausingContentStore : IContentStore
    {
        private readonly InMemoryContentStore _inner = new();
        public bool Durable => false;
        public bool Shared => false;
        public int Writes;
        public int Reads;
        public bool Pause;
        public TaskCompletionSource Staged { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<ContentHandle> WriteAsync(Stream source, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Writes);
            var result = await _inner.WriteAsync(source, cancellationToken);
            if (Pause) { Pause = false; Staged.TrySetResult(); await Resume.Task.WaitAsync(cancellationToken); }
            return result;
        }
        public ValueTask<Stream> OpenReadAsync(ContentHandle handle, CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref Reads); return _inner.OpenReadAsync(handle, cancellationToken); }
    }
}
