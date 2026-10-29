using System.Collections.Immutable;
using System.Text.Json;
using CellBridge.AspNetCore;
using CellBridge.FssHttp;
using CellBridge.FssHttpB;
using CellBridge.Tests;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using CellBridge.TestFixtures;

namespace CellBridge.Storage.Tests;

public sealed class RestoreFencingTests
{
    [Fact]
    public async Task SharedHostLeaseSurvivesCaptureAndBlocksRestoreAndRename()
    {
        var provider = Memory(); var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/host-lock.docx", MinimalDocx.Create(), TestActor.Value))!;
        var lease = new HostLease("host-token", TestActor.Value.Identity.Subject, DateTime.UtcNow.AddMinutes(1));
        var leased = await provider.Documents.ExecuteControlAsync(new DocumentControlCommand.SetHostLease(state.ResourceId,
            StorageTestDocuments.Admission(state), lease, null));
        Assert.True(leased.Changed);
        state = leased.Current.Head;
        var document = StoredDocument.RestoreControl(leased.Current);
        Assert.Equal(lease, FssHttpLockCoordinator.Restore(document, leased.Current.Coordination).Capture().HostLock);
        var denied = await Assert.ThrowsAsync<DocumentOperationLockException>(() => service.RestoreRevisionAsync(
            state.ResourceId, 1, 1, "host-locked", TestActor.Value).AsTask());
        Assert.Equal("FileAlreadyLockedOnServer", denied.ErrorCode);
        await Assert.ThrowsAsync<DocumentOperationLockException>(() => service.RenameDocumentAsync(state.ResourceId,
            "renamed.docx", TestActor.Value).AsTask());
        Assert.Equal(state, await provider.Documents.FindAsync(new DocumentIdentity.Resource(state.ResourceId)));
    }

    [Fact]
    public async Task LostCommitReplyResolvesExactReceiptAndPinsExternalRevision()
    {
        var provider = Memory(); var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/lost-ack.docx", MinimalDocx.Create(), TestActor.Value))!;
        Assert.True((await provider.Documents.UpdateDeliveryAsync(new DocumentDeliveryCommand.Bind(state.ResourceId,
            state.LifecycleGeneration, state.StateVersion, new(Guid.NewGuid(), "external-id", "etag", 1)))).Applied);
        var lost = new CellBridgeDocumentService(new(new LostReply(provider.Documents), provider.Content));
        var result = await lost.RestoreRevisionAsync(state.ResourceId, 1, 1, "lost-reply", TestActor.Value);
        Assert.True(result.IsReplay);
        var current = (await provider.Documents.FindAsync(new DocumentIdentity.Resource(state.ResourceId)))!;
        Assert.Equal(2, current.Counters.Revisions); Assert.Equal(1, current.Counters.RestoreReceipts);
        var pending = (await provider.Documents.ReadNextDeliveryAsync(state.ResourceId))!.Revision;
        Assert.Equal(result.Revision.Package, pending.Package);
        Assert.Equal(pending.Package.Length, (await StorageTestDocuments.PackageAsync(provider, pending.Package)).LongLength);
        Assert.Equal(0, (await provider.Documents.ReadControlAsync(state.ResourceId))!.Coordination.Generation);
    }

    [Theory]
    [InlineData("permission")]
    [InlineData("metadata")]
    [InlineData("lock")]
    public async Task PreparationCannotOverrideNewAuthorityOrMetadata(string mutation)
    {
        var provider = Memory(); var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/fence.docx", MinimalDocx.Create(), TestActor.Value))!;
        var gate = new PausedRead(provider.Content);
        var preparing = new CellBridgeDocumentService(new(provider.Documents, gate)).RestoreRevisionAsync(state.ResourceId, 1, 1,
            "fenced", TestActor.Value).AsTask();
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        switch (mutation)
        {
            case "permission":
                Assert.True((await provider.Documents.ExecuteControlAsync(new DocumentControlCommand.SetPermissions(state.ResourceId,
                    StorageTestDocuments.Admission(state), state.Security with { Owner = "revoked" }, state.AuthorityGeneration))).Changed);
                break;
            case "metadata":
                var metadata = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.Metadata,
                    StorageTestDocuments.MetadataRequest(new GraphFixture([42])), new Dictionary<string, string>(), TestActor.Value);
                Assert.False(Assert.Single(metadata.Response.SubResponses).Status);
                break;
            default:
                await LockAsync(provider, state, "lock");
                break;
        }
        var changed = (await provider.Documents.ExportDocumentAsync(state.ResourceId))!;
        gate.Resume.TrySetResult();
        if (mutation == "permission") await Assert.ThrowsAsync<UnauthorizedAccessException>(() => preparing);
        else await Assert.ThrowsAnyAsync<InvalidOperationException>(() => preparing);
        var after = (await provider.Documents.FindAsync(new DocumentIdentity.Resource(state.ResourceId)))!;
        Assert.Equal(changed.Current.Head.ContentVersion, after.ContentVersion);
        Assert.Equal(changed.Current.Head.Counters.Revisions, after.Counters.Revisions); Assert.Equal(0, after.Counters.RestoreReceipts);
        var afterSnapshot = (await provider.Documents.ExportDocumentAsync(state.ResourceId))!;
        Assert.Equal(changed.Current.Head, afterSnapshot.Current.Head);
        Assert.Equal(JsonSerializer.Serialize(changed.Current.Partitions), JsonSerializer.Serialize(afterSnapshot.Current.Partitions));
    }

    [Fact]
    public async Task DeletionDuringPreparationCannotRestoreRetiredResource()
    {
        var provider = Memory(); var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/retired-restore.docx", MinimalDocx.Create(), TestActor.Value))!;
        var gate = new PausedRead(provider.Content);
        var preparing = new CellBridgeDocumentService(new(provider.Documents, gate)).RestoreRevisionAsync(
            state.ResourceId, 1, 1, "retired", TestActor.Value).AsTask();
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var lifecycle = provider.Documents;
        Assert.True((await lifecycle.ChangeLifecycleAsync(new DocumentLifecycleCommand.Delete(state.ResourceId, state.LifecycleGeneration, state.StateVersion))).Applied);
        gate.Resume.TrySetResult();
        await Assert.ThrowsAsync<KeyNotFoundException>(() => preparing);
        var retired = (await lifecycle.FindAsync(new DocumentIdentity.Resource(state.ResourceId, true)))!;
        Assert.True(retired.IsDeleted);
        Assert.Equal(state.ContentVersion, retired.ContentVersion);
        Assert.Equal(1, retired.Counters.Revisions);
        Assert.Equal(0, retired.Counters.RestoreReceipts);
    }

    [Fact]
    public async Task ConcurrentSaveAndRestoreHaveOneWinnerFromTheSameGraph()
    {
        var provider = Memory(); var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/save-race.docx", MinimalDocx.Create(), TestActor.Value))!;
        var save = service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents,
            await StorageTestDocuments.SaveRequestAsync(service, state, MinimalDocx.Create("concurrent")), new Dictionary<string, string>(), TestActor.Value).AsTask();
        var restore = service.RestoreRevisionAsync(state.ResourceId, 1, 1, "race", TestActor.Value).AsTask();
        CellExecution saved = await save;
        bool restoreSucceeded;
        try { await restore; restoreSucceeded = true; } catch (InvalidOperationException) { restoreSucceeded = false; }
        Assert.NotEqual(restoreSucceeded, !saved.Response.SubResponses[0].Status);
        var final = (await provider.Documents.FindAsync(new DocumentIdentity.Resource(state.ResourceId)))!;
        Assert.Equal(2, final.Counters.Revisions); Assert.Equal(2U, final.ContentVersion);
    }

    [Fact]
    public async Task ActiveLeaseExpiryDuringPreparationCannotTurnRestoreIntoAnUnlockedWrite()
    {
        var clock = new ManualClock(); var content = new InMemoryContentStore();
        var provider = new StorageProvider(new InMemoryDocumentRepository(content, clock: clock), content);
        var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/expiry.docx", MinimalDocx.Create(), TestActor.Value))!;
        const string lockId = "11111111-1111-1111-1111-111111111111";
        await LockAsync(provider, state, lockId, 60);
        var leaseExpires = (await provider.Documents.ReadControlAsync(state.ResourceId))!.Coordination.Exclusive!.ExpiresUtc;
        var gate = new PausedRead(provider.Content);
        var restoring = new CellBridgeDocumentService(new(provider.Documents, gate)).RestoreRevisionAsync(state.ResourceId, 1, 1,
            "expires", TestActor.Value, new Dictionary<string, string>
            { ["ExclusiveLockID"] = "11111111-1111-1111-1111-111111111111", ["Timeout"] = "3600" }).AsTask();
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        clock.Advance(TimeSpan.FromSeconds(61));
        gate.Resume.TrySetResult();
        await Assert.ThrowsAsync<DocumentOperationLockException>(() => restoring);
        Assert.Equal(1, (await provider.Documents.FindAsync(new DocumentIdentity.Resource(state.ResourceId)))!.Counters.Revisions);
    }

    [Fact]
    public async Task RestoreExpiresEditorsAndPublishesMatchingEditorTableKnowledge()
    {
        var clock = new ManualClock(); var content = new InMemoryContentStore();
        var provider = new StorageProvider(new InMemoryDocumentRepository(content, clock: clock), content);
        var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/editors-expiry.docx", MinimalDocx.Create(), TestActor.Value))!;
        var joined = await provider.Documents.ExecuteControlAsync(new DocumentControlCommand.Office(state.ResourceId,
            StorageTestDocuments.Admission(state), new() { Type = SubRequestType.EditorsTable, SubRequestDataAttributes =
            { ["EditorsTableRequestType"] = "JoinEditingSession", ["ClientID"] = Guid.NewGuid().ToString(), ["Timeout"] = "60", ["AsEditor"] = "true" } }));
        Assert.True(joined.Changed);
        clock.Advance(TimeSpan.FromHours(1) + TimeSpan.FromSeconds(1));
        await service.RestoreRevisionAsync(state.ResourceId, 1, 1, "editor-expiry", TestActor.Value);
        var restored = (await provider.Documents.ReadControlAsync(state.ResourceId))!;
        Assert.Empty(restored.Editors);
        var before = joined.Current.Partitions.Single(p => p.Kind == 2);
        var after = restored.Partitions.Single(p => p.Kind == 2);
        Assert.True(after.Knowledge > before.Knowledge);
        Assert.NotEqual(before.InlineContent, after.InlineContent);
    }

    private static async Task LockAsync(StorageProvider provider, DocumentHead head, string id, int timeout = 3600)
    {
        var result = await provider.Documents.ExecuteControlAsync(new DocumentControlCommand.Office(head.ResourceId,
            StorageTestDocuments.Admission(head), new() { Type = SubRequestType.ExclusiveLock, SubRequestDataAttributes =
            { ["ExclusiveLockRequestType"] = "GetLock", ["ExclusiveLockID"] = id, ["ClientID"] = "restore-client", ["Timeout"] = timeout.ToString() } }));
        Assert.Null(result.ErrorCode); Assert.True(result.Changed);
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan duration) => now += duration;
    }

    private static StorageProvider Memory() => StorageTestDocuments.Memory();
    private sealed class PausedRead(IContentStore inner) : IContentStore
    {
        private int _entered;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Durable => false; public bool Shared => false;
        public ValueTask<ContentHandle> WriteAsync(Stream stream, CancellationToken ct = default) => inner.WriteAsync(stream, ct);
        public async ValueTask<Stream> OpenReadAsync(ContentHandle handle, CancellationToken ct = default)
        {
            if (Interlocked.Exchange(ref _entered, 1) == 0) { Entered.TrySetResult(); await Resume.Task.WaitAsync(ct); }
            return await inner.OpenReadAsync(handle, ct);
        }
    }
    private sealed class LostReply(IDocumentRepository inner) : RepositoryTestProxy(inner)
    {
        public override async ValueTask<PublicationResult> PublishAsync(PreparedDocumentPublication publication, CancellationToken ct = default)
        {
            var result = await Inner.PublishAsync(publication, ct);
            if (result.Status == PublicationStatus.Accepted) throw new StorageUnavailableException("Injected lost reply.");
            return result;
        }
    }
}
