using CellBridge.AspNetCore;
using CellBridge.FssHttp;
using CellBridge.FssHttpB;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.TestFixtures;

namespace CellBridge.Storage.Tests;

[Collection("PostgreSQL")]
public sealed class UploadHintTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompleteHintPublishesAndRetriesAfterServiceAndProviderRecreation(bool postgreSql)
    {
        await using var testProvider = ProtocolExecutionTestProvider.Create(postgreSql);
        var provider = testProvider.Provider;
        var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/hint-" + Guid.NewGuid().ToString("N") + ".docx", MinimalDocx.Create(), TestActor.Value))!;
        var request = await Save(service, state, 0x20);
        request.SubRequests[0].TargetPartitionId = Guid.Empty;
        var saved = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.Metadata, request, new Dictionary<string, string>(), TestActor.Value);
        Assert.False(Assert.Single(saved.Response.SubResponses).Status);
        Assert.Equal(state.ContentVersion + 1, saved.State.ContentVersion);
        await using var recreated = ProtocolExecutionTestProvider.Create(postgreSql);
        var restarted = new CellBridgeDocumentService(postgreSql ? recreated.Provider : provider);
        var retry = await restarted.ExecuteAsync(state.ResourceId, DocumentPartitionKind.Metadata, request, new Dictionary<string, string>(), TestActor.Value);
        Assert.False(Assert.Single(retry.Response.SubResponses).Status);
        Assert.Equal(saved.State.StateVersion, retry.State.StateVersion);
        Assert.Equal(saved.Response.ToByteArray(), retry.Response.ToByteArray());
        Assert.Equal(1, retry.State.Counters.SaveReceipts);
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
    public async Task PartialFailureDoesNotCancelQueriesOrIndependentCompleteHintSave(bool postgreSql, int partial)
    {
        await using var testProvider = ProtocolExecutionTestProvider.Create(postgreSql);
        var provider = testProvider.Provider;
        var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/partial-" + Guid.NewGuid().ToString("N") + ".docx", MinimalDocx.Create(), TestActor.Value))!;
        var request = await Save(service, state, 0x20);
        var complete = request.SubRequests[0]; complete.RequestId = 3; complete.TargetPartitionId = Guid.Empty;
        request.SubRequests.Clear();
        request.SubRequests.Add(Query(1));
        request.SubRequests.Add(new(RequestTypes.PutChanges) { RequestId = 2, TargetPartitionId = Guid.Empty,
            Data = new PutChangesSubRequestData { Flags = (byte)partial, StorageIndex = ExGuid.Null } });
        request.SubRequests.Add(complete);
        request.SubRequests.Add(Query(4));
        var execution = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.Metadata, request,
            new Dictionary<string, string>(), TestActor.Value);
        var response = execution.Response;
        var version = execution.State.ContentVersion;
        Assert.Equal(new[] { false, true, false, false }, response.SubResponses.Select(s => s.Status));
        Assert.Equal((ulong)ProtocolErrorCode.RequestNotSupported, response.SubResponses[1].Error!.ErrorCode);
        Assert.Equal(state.ContentVersion + 1, version);
        var beforeIndex = Assert.IsType<QueryChangesSubResponseData>(response.SubResponses[0].Data).StorageIndexExtendedGuid;
        var afterIndex = Assert.IsType<QueryChangesSubResponseData>(response.SubResponses[3].Data).StorageIndexExtendedGuid;
        Assert.NotEqual(beforeIndex, afterIndex);
        Assert.Contains(response.DataElementPackage!.DataElements, e => e.DataElementExtendedGuid.Equals(beforeIndex));
        Assert.Contains(response.DataElementPackage.DataElements, e => e.DataElementExtendedGuid.Equals(afterIndex));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitFileSaveFromMetadataSoapCannotBypassLockOrAccess(bool postgreSql)
    {
        await using var testProvider = ProtocolExecutionTestProvider.Create(postgreSql);
        var provider = testProvider.Provider;
        var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/locked-" + Guid.NewGuid().ToString("N") + ".docx", MinimalDocx.Create(), TestActor.Value))!;
        var lockedHead = await provider.Documents.ExecuteControlAsync(new DocumentControlCommand.Office(state.ResourceId,
            StorageTestDocuments.Admission(state), new() { Type = SubRequestType.ExclusiveLock, SubRequestDataAttributes =
            { ["ExclusiveLockRequestType"] = "GetLock", ["ExclusiveLockID"] = "11111111-1111-1111-1111-111111111111", ["ClientID"] = "client", ["Timeout"] = "3600" } }));
        Assert.True(lockedHead.Changed);
        var request = await Save(service, state, 0x20); request.SubRequests[0].TargetPartitionId = Guid.Empty;
        var locked = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.Metadata, request, new Dictionary<string, string>(), TestActor.Value);
        Assert.NotNull(locked.LockError);
        Assert.Equal(state.ContentVersion, locked.State.ContentVersion);
        var denied = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.Metadata, request,
            new Dictionary<string, string> { ["ExclusiveLockID"] = "11111111-1111-1111-1111-111111111111", ["Timeout"] = "3600" }, new CellBridgeActor(new("other", "other", "Other")));
        Assert.Equal(CellBridgeAuthorization.AccessDeniedHResult, Assert.Single(denied.Response.SubResponses).Error!.ErrorCode);
        Assert.Equal(state.ContentVersion, denied.State.ContentVersion); Assert.Equal(0, denied.State.Counters.SaveReceipts);
        var allowed = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.Metadata, request,
            new Dictionary<string, string> { ["ExclusiveLockID"] = "11111111-1111-1111-1111-111111111111", ["Timeout"] = "3600" }, TestActor.Value);
        Assert.Null(allowed.LockError); Assert.False(Assert.Single(allowed.Response.SubResponses).Status);
        Assert.Equal(state.ContentVersion + 1, allowed.State.ContentVersion);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PermissionRevocationBetweenOperationsPreservesEarlierPayloadAndDeniesRemainingOperations(bool postgreSql)
    {
        await using var testProvider = ProtocolExecutionTestProvider.Create(postgreSql);
        var inner = testProvider.Provider.Documents;
        var content = testProvider.Provider.Content;
        var state = (await new CellBridgeDocumentService(new(inner, content)).CreateAsync("/revoked-batch-" + Guid.NewGuid().ToString("N") + ".docx", MinimalDocx.Create(), TestActor.Value))!;
        var revoking = new BeforeReadStateStore(inner, 5, async () =>
        {
            var current = (await inner.FindAsync(new DocumentIdentity.Resource(state.ResourceId)))!;
            Assert.True((await inner.ExecuteControlAsync(new DocumentControlCommand.SetPermissions(state.ResourceId,
                StorageTestDocuments.Admission(current), current.Security with { Owner = "new-owner" }, current.AuthorityGeneration))).Changed);
        });
        var request = new FsshttpbCellRequest { SubRequests =
        {
            Query(1),
            new(RequestTypes.PutChanges) { RequestId = 2, TargetPartitionId = Guid.Empty, Data = new PutChangesSubRequestData { Flags = 2 } },
            new(RequestTypes.AllocateExtendedGuidRange) { RequestId = 3, TargetPartitionId = StoredDocument.EditorsTablePartitionId,
                Data = new AllocateExtendedGuidRangeSubRequestData { RequestIdCount = 1 } },
            new(RequestTypes.QueryChanges) { RequestId = 4, TargetPartitionId = StoredDocument.MetadataPartitionId,
                Data = new QueryChangesSubRequestData() },
        } };
        var result = await new CellBridgeDocumentService(new(revoking, content)).ExecuteAsync(state.ResourceId,
            DocumentPartitionKind.Metadata, request, new Dictionary<string, string>(), TestActor.Value);
        Assert.False(result.Response.SubResponses[0].Status);
        Assert.All(result.Response.SubResponses.Skip(1), s => Assert.Equal(CellBridgeAuthorization.AccessDeniedHResult, s.Error!.ErrorCode));
        Assert.NotEmpty(result.Response.DataElementPackage!.DataElements);
        Assert.Equal(state.Package, result.State.Package); Assert.Equal(state.ContentVersion, result.State.ContentVersion);
        Assert.Equal(0, result.State.Counters.SaveReceipts);
    }

    private static async Task<FsshttpbCellRequest> Save(CellBridgeDocumentService service, DocumentHead state, byte extraFlags)
    {
        var request = StorageTests.Fixture("save-first");
        var put = Assert.IsType<PutChangesSubRequestData>(request.SubRequests[0].Data);
        StorageTests.Rebase(request, await StorageTestDocuments.GraphAsync(service, state));
        put.Flags |= extraFlags;
        return request;
    }
    private static FsshttpbCellSubRequest Query(ulong id) => new(RequestTypes.QueryChanges)
    { RequestId = id, TargetPartitionId = Guid.Empty, Data = new QueryChangesSubRequestData { IncludeStorageManifest = true, IncludeCellChanges = true } };
}
