using CellBridge.AspNetCore;
using CellBridge.FssHttpB;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using CellBridge.Storage.PostgreSql;
using CellBridge.Web;
using Npgsql;

namespace CellBridge.Storage.Tests;

public sealed class UploadHintTests
{
    [Fact]
    public Task CompleteHintPublishesAndRetriesAfterServiceRecreation() =>
        CheckHint(new(new InMemoryStateStore(), new InMemoryContentStore()));

    [PostgreSqlFact]
    public async Task PostgreSqlCompleteHintPublishesAndRetriesAfterProviderRecreation()
    {
        await using var source = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("ConnectionStrings__cellbridge")!);
        await CheckHint(new(new PostgreSqlStateStore(source), new PostgreSqlContentStore(source)),
            () => new(new PostgreSqlStateStore(source), new PostgreSqlContentStore(source)));
    }

    private static async Task CheckHint(StorageProvider provider, Func<StorageProvider>? recreate = null)
    {
        var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/" + Guid.NewGuid().ToString("N") + ".docx", MinimalDocx.Create(), TestActor.Value))!;
        var request = Save(state, 0x20);
        request.SubRequests[0].TargetPartitionId = Guid.Empty;
        var saved = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.Metadata, request, new Dictionary<string, string>(), TestActor.Value);
        Assert.False(Assert.Single(saved.Response.SubResponses).Status);
        Assert.Equal(state.ContentVersion + 1, saved.State.ContentVersion);
        var restarted = new CellBridgeDocumentService(recreate?.Invoke() ?? provider);
        var retry = await restarted.ExecuteAsync(state.ResourceId, DocumentPartitionKind.Metadata, request, new Dictionary<string, string>(), TestActor.Value);
        Assert.False(Assert.Single(retry.Response.SubResponses).Status);
        Assert.Equal(saved.State.StateVersion, retry.State.StateVersion);
        Assert.Equal(saved.Response.ToByteArray(), retry.Response.ToByteArray());
        Assert.Single(retry.State.Receipts);
        Assert.IsType<PutChangesSubRequestData>(request.SubRequests[0].Data).Flags ^= 0x20;
        var withoutHint = await restarted.ExecuteAsync(state.ResourceId, DocumentPartitionKind.Metadata, request, new Dictionary<string, string>(), TestActor.Value);
        Assert.False(Assert.Single(withoutHint.Response.SubResponses).Status);
        Assert.Equal(saved.State.StateVersion, withoutHint.State.StateVersion);
        Assert.Equal(saved.Response.ToByteArray(), withoutHint.Response.ToByteArray());
    }

    [Theory]
    [InlineData(false, 0x02)]
    [InlineData(false, 0x04)]
    [InlineData(true, 0x02)]
    [InlineData(true, 0x04)]
    public async Task PartialFailureDoesNotCancelQueriesOrIndependentCompleteHintSave(bool buffered, int partial)
    {
        var provider = new StorageProvider(new InMemoryStateStore(), new InMemoryContentStore());
        var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/partial.docx", MinimalDocx.Create(), TestActor.Value))!;
        var request = Save(state, 0x20);
        var complete = request.SubRequests[0]; complete.RequestId = 3; complete.TargetPartitionId = Guid.Empty;
        request.SubRequests.Clear();
        request.SubRequests.Add(Query(1));
        request.SubRequests.Add(new(RequestTypes.PutChanges) { RequestId = 2, TargetPartitionId = Guid.Empty,
            Data = new PutChangesSubRequestData { Flags = (byte)partial, StorageIndex = ExGuid.Null } });
        request.SubRequests.Add(complete);
        request.SubRequests.Add(Query(4));
        FsshttpbResponse response;
        uint version;
        if (buffered)
        {
            var document = await StoredDocument.RestoreAsync(state, provider.Content);
            response = CellBinaryRequestExecutor.Execute(document, document.MetadataPartition, request, DocumentAccess.Read | DocumentAccess.Write);
            version = document.ContentVersion;
        }
        else
        {
            var execution = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.Metadata, request, new Dictionary<string, string>(), TestActor.Value);
            response = execution.Response; version = execution.State.ContentVersion;
        }
        Assert.Equal(new[] { false, true, false, false }, response.SubResponses.Select(s => s.Status));
        Assert.Equal((ulong)ProtocolErrorCode.RequestNotSupported, response.SubResponses[1].Error!.ErrorCode);
        Assert.Equal(state.ContentVersion + 1, version);
        var beforeIndex = Assert.IsType<QueryChangesSubResponseData>(response.SubResponses[0].Data).StorageIndexExtendedGuid;
        var afterIndex = Assert.IsType<QueryChangesSubResponseData>(response.SubResponses[3].Data).StorageIndexExtendedGuid;
        Assert.NotEqual(beforeIndex, afterIndex);
        Assert.Contains(response.DataElementPackage!.DataElements, e => e.DataElementExtendedGuid.Equals(beforeIndex));
        Assert.Contains(response.DataElementPackage.DataElements, e => e.DataElementExtendedGuid.Equals(afterIndex));
    }

    [Fact]
    public async Task ExplicitFileSaveFromMetadataSoapCannotBypassLockOrAccess()
    {
        var provider = new StorageProvider(new InMemoryStateStore(), new InMemoryContentStore());
        var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/locked.docx", MinimalDocx.Create(), TestActor.Value))!;
        await provider.State.TransitionAsync(state.ResourceId, (current, now) => new StateTransition<bool>(current with
        { Coordination = new(null, [], new("exclusive", "client", now.AddHours(1), 0, null, TestActor.Value.Identity.Subject), 1) }, true));
        var request = Save(state, 0x20); request.SubRequests[0].TargetPartitionId = Guid.Empty;
        var locked = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.Metadata, request, new Dictionary<string, string>(), TestActor.Value);
        Assert.NotNull(locked.LockError);
        Assert.Equal(state.ContentVersion, locked.State.ContentVersion);
        var denied = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.Metadata, request,
            new Dictionary<string, string> { ["ExclusiveLockID"] = "exclusive" }, new CellBridgeActor(new("other", "other", "Other")));
        Assert.Equal(CellBridgeAuthorization.AccessDeniedHResult, Assert.Single(denied.Response.SubResponses).Error!.ErrorCode);
        Assert.Equal(state.ContentVersion, denied.State.ContentVersion); Assert.Empty(denied.State.Receipts);
        var allowed = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.Metadata, request,
            new Dictionary<string, string> { ["ExclusiveLockID"] = "exclusive" }, TestActor.Value);
        Assert.Null(allowed.LockError); Assert.False(Assert.Single(allowed.Response.SubResponses).Status);
        Assert.Equal(state.ContentVersion + 1, allowed.State.ContentVersion);
    }

    [Fact]
    public async Task PermissionRevocationBetweenOperationsPreservesEarlierPayloadAndDeniesRemainingOperations()
    {
        var inner = new InMemoryStateStore(); var content = new InMemoryContentStore();
        var state = (await new CellBridgeDocumentService(new(inner, content)).CreateAsync("/revoked-batch.docx", MinimalDocx.Create(), TestActor.Value))!;
        var revoking = new BeforeReadStateStore(inner, 3, () => inner.TransitionAsync(state.ResourceId, (current, now) =>
            new StateTransition<bool>(current with { Security = current.Security with { Owner = "new-owner" } }, true)).AsTask());
        var request = new FsshttpbCellRequest { SubRequests =
        {
            Query(1),
            new(RequestTypes.PutChanges) { RequestId = 2, TargetPartitionId = Guid.Empty, Data = new PutChangesSubRequestData { Flags = 2 } },
            new(RequestTypes.AllocateExtendedGuidRange) { RequestId = 3, TargetPartitionId = StoredDocument.EditorsTablePartitionId,
                Data = new AllocateExtendedGuidRangeSubRequestData { RequestIdCount = 1 } },
            new(RequestTypes.QueryChanges) { RequestId = 4, TargetPartitionId = StoredDocument.MetadataPartitionId },
        } };
        var result = await new CellBridgeDocumentService(new(revoking, content)).ExecuteAsync(state.ResourceId,
            DocumentPartitionKind.Metadata, request, new Dictionary<string, string>(), TestActor.Value);
        Assert.False(result.Response.SubResponses[0].Status);
        Assert.All(result.Response.SubResponses.Skip(1), s => Assert.Equal(CellBridgeAuthorization.AccessDeniedHResult, s.Error!.ErrorCode));
        Assert.NotEmpty(result.Response.DataElementPackage!.DataElements);
        Assert.Equal(state.Content, result.State.Content); Assert.Equal(state.ContentVersion, result.State.ContentVersion);
        Assert.Empty(result.State.Receipts);
    }

    private static FsshttpbCellRequest Save(DocumentState state, byte extraFlags)
    {
        var request = StorageTests.Fixture("save-first");
        var put = Assert.IsType<PutChangesSubRequestData>(request.SubRequests[0].Data);
        put.ExpectedStorageIndex = StorageIds.Restore(state.Partitions[0].StorageIndex!);
        put.Flags |= extraFlags;
        return request;
    }
    private static FsshttpbCellSubRequest Query(ulong id) => new(RequestTypes.QueryChanges)
    { RequestId = id, TargetPartitionId = Guid.Empty, Data = new QueryChangesSubRequestData { IncludeStorageManifest = true, IncludeCellChanges = true } };
}
