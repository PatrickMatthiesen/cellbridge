using System.Text.Json;
using System.Runtime.CompilerServices;
using CellBridge.AspNetCore;
using CellBridge.FssHttp;
using CellBridge.FssHttpB;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using CellBridge.Storage.PostgreSql;
using CellBridge.TestFixtures;
using Npgsql;

namespace CellBridge.Storage.Tests;

[Collection("PostgreSQL")]
public sealed class WholeFileSaveTests
{
    private static readonly ConditionalWeakTable<StorageProvider, ManualClock> Clocks = new();
    private static StorageProvider Memory()
    {
        var content = new InMemoryContentStore(); var clock = new ManualClock();
        var provider = new StorageProvider(new InMemoryDocumentRepository(content, clock: clock), content);
        Clocks.Add(provider, clock); return provider;
    }
    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance() => now += TimeSpan.FromDays(1);
    }

    [Fact]
    public Task SaveReadBackHistoryAndReplay() => CheckHistory(Memory());

    [PostgreSqlFact]
    public async Task PostgreSqlIndependentProvidersRetainGraphHistoryAndReceipt()
    {
        var connection = Environment.GetEnvironmentVariable("ConnectionStrings__cellbridge")!;
        await using var first = NpgsqlDataSource.Create(connection);
        await using var second = NpgsqlDataSource.Create(connection);
        await CheckHistory(new(new PostgreSqlStateStore(first), new PostgreSqlContentStore(first)),
            new(new PostgreSqlStateStore(second), new PostgreSqlContentStore(second)));
    }

    private static async Task CheckHistory(StorageProvider provider, StorageProvider? peer = null)
    {
        peer ??= provider;
        var service = new CellBridgeDocumentService(provider);
        var before = (await service.CreateAsync("/whole-" + Guid.NewGuid().ToString("N") + ".docx",
            MinimalDocx.Create("original"), TestActor.Value))!;
        var initialRevision = (await provider.Documents.ReadRevisionAsync(before.ResourceId, 1))!.Revision!;
        var initialPartition = (await provider.Documents.ReadPartitionAsync(before.ResourceId, new(0) { IncludeCatalog = true }))!;
        var publisher = new ExternalRevisionPublisher(provider, new UnusedDestination());
        Assert.True(await publisher.BindAsync(before.ResourceId, before.LifecycleGeneration, before.StateVersion,
            Guid.NewGuid(), "external", "baseline"));
        Assert.True((await new SharedDocumentLocks(service).ApplyAsync(before.ResourceId,
            HostLockOperation.Acquire, "lock", TestActor.Value)).Success);
        var bytes = MinimalDocx.Create("whole replacement");
        var accepted = await Save(service, before.ResourceId, bytes, "first");
        Assert.False(accepted.IsReplay);
        Assert.Equal(before.ContentVersion + 1, accepted.Revision.ContentVersion);
        var saved = (await peer.Documents.FindAsync(new DocumentIdentity.Resource(before.ResourceId)))!;
        Assert.Equal(bytes, await StorageTestDocuments.PackageAsync(peer, saved.Package));
        Assert.Equal(bytes, (await StorageTestDocuments.GraphAsync(new(peer), saved)).Materialize());
        var originalCell = IndexedDocumentGraph.Restore(before.Package.Root.Cell);
        var selected = (await StorageTestDocuments.GraphAsync(new(peer), saved)).FileCell;
        Assert.Equal(originalCell, selected);
        var query = new FsshttpbCellRequest { SubRequests =
        { new(RequestTypes.QueryChanges) { RequestId = 1, Data = new QueryChangesSubRequestData { CellId = originalCell } } } };
        var queried = await new CellBridgeDocumentService(peer).ExecuteAsync(saved.ResourceId,
            DocumentPartitionKind.FileContents, query, new Dictionary<string, string>(), TestActor.Value);
        Assert.False(Assert.Single(queried.Response.SubResponses).Status);
        Assert.Equal(2, saved.Counters.Revisions);
        Assert.Equal(JsonSerializer.Serialize(initialRevision), JsonSerializer.Serialize((await peer.Documents.ReadRevisionAsync(saved.ResourceId, 1))!.Revision));
        var historical = (await peer.Documents.ExportDocumentAsync(saved.ResourceId))!;
        Assert.All(initialPartition.Elements, e => Assert.Contains(historical.Elements,
            x => x.Scope.TargetPartition == 0 && x.Element.Id == e.Id && x.Element.Payload == e.Payload));
        var savedPartition = (await peer.Documents.ReadPartitionAsync(saved.ResourceId, new(0)))!.Partition;
        Assert.Equal(initialPartition.Partition.Identity, savedPartition.Identity);
        Assert.True(savedPartition.Knowledge > initialPartition.Partition.Knowledge);
        Assert.Equal(1, saved.Counters.PendingDeliveries);
        Assert.Equal(1, saved.Counters.WholeFileSaveReceipts);
        var laterBytes = MinimalDocx.Create("later");
        var later = await Save(new(peer), before.ResourceId, laterBytes, "second");
        var replay = await Save(new(peer), before.ResourceId, bytes, "first");
        Assert.True(replay.IsReplay);
        Assert.Equal(JsonSerializer.Serialize(accepted.Revision), JsonSerializer.Serialize(replay.Revision));
        saved = (await peer.Documents.FindAsync(new DocumentIdentity.Resource(before.ResourceId)))!;
        Assert.Equal(later.Revision.ContentVersion, saved.ContentVersion);
        Assert.Equal(3, saved.Counters.Revisions);
        Assert.Equal(2, saved.Counters.PendingDeliveries);
        await Assert.ThrowsAsync<WholeFileSaveConflictException>(() => Save(service, before.ResourceId,
            MinimalDocx.Create("different retry"), "first"));
        var noOp = await Save(service, before.ResourceId, laterBytes, "no-op");
        Assert.Equal(later.Revision.ContentVersion, noOp.Revision.ContentVersion);
        Assert.Equal(3, (await provider.Documents.FindAsync(new DocumentIdentity.Resource(before.ResourceId)))!.Counters.Revisions);
        // Receipt replay does not require a currently active lease.
        Assert.True((await new SharedDocumentLocks(service).ApplyAsync(before.ResourceId,
            HostLockOperation.Release, "lock", TestActor.Value)).Success);
        Assert.True((await Save(service, before.ResourceId, bytes, "first")).IsReplay);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("LOCK")]
    [InlineData("other")]
    public async Task MissingOrMismatchedTokenCannotWriteNonemptyDocument(string? token)
    {
        var provider = Memory(); var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/locked.docx", MinimalDocx.Create(), TestActor.Value))!;
        await new SharedDocumentLocks(service).ApplyAsync(state.ResourceId, HostLockOperation.Acquire, "lock", TestActor.Value);
        await Assert.ThrowsAsync<DocumentOperationLockException>(() => Save(service, state.ResourceId,
            MinimalDocx.Create("replacement"), "key", token));
        Assert.Equal(state.Package, (await provider.Documents.FindAsync(new DocumentIdentity.Resource(state.ResourceId)))!.Package);
    }

    [Fact]
    public async Task EmptyUnlockedFileCanBeWrittenButExplicitExpiredTokenCannotDowngrade()
    {
        var provider = Memory(); var service = new CellBridgeDocumentService(provider);
        var empty = (await service.CreateAsync("/empty.docx", [], TestActor.Value))!;
        var result = await Save(service, empty.ResourceId, MinimalDocx.Create(), "empty", null);
        Assert.Equal(2u, result.Revision.ContentVersion);
        await new SharedDocumentLocks(service).ApplyAsync(empty.ResourceId, HostLockOperation.Acquire, "lock", TestActor.Value);
        Clocks.GetValue(provider, _ => throw new InvalidOperationException()).Advance();
        await Assert.ThrowsAsync<DocumentOperationLockException>(() => Save(service, empty.ResourceId,
            MinimalDocx.Create("expired"), "expired"));
    }

    [Theory]
    [InlineData("empty", false)]
    [InlineData("empty", true)]
    [InlineData("malformed", false)]
    [InlineData("malformed", true)]
    [InlineData("unsupported", false)]
    [InlineData("unsupported", true)]
    public async Task UnchangedInvalidInputCannotCommitANewReceipt(string kind, bool import)
    {
        var provider = Memory();
        var service = new CellBridgeDocumentService(provider);
        byte[] bytes = kind switch
        {
            "empty" => [],
            "malformed" => [1, 2, 3],
            _ => MinimalDocx.Create("unsupported extension"),
        };
        var path = kind == "unsupported" ? "/unchanged.txt" : "/unchanged.docx";
        var state = (import
            ? await service.ImportAsync(path, bytes, TestActor.Value.Identity, TestActor.Value)
            : await service.CreateAsync(path, bytes, TestActor.Value))!;
        Assert.True(await new ExternalRevisionPublisher(provider, new UnusedDestination()).BindAsync(
            state.ResourceId, state.LifecycleGeneration, state.StateVersion, Guid.NewGuid(), "external", "baseline"));
        if (bytes.Length > 0)
            Assert.True((await new SharedDocumentLocks(service).ApplyAsync(state.ResourceId,
                HostLockOperation.Acquire, "lock", TestActor.Value)).Success);
        var before = (await provider.Documents.FindAsync(new DocumentIdentity.Resource(state.ResourceId)))!;

        await Assert.ThrowsAsync<InvalidDataException>(() => Save(service, state.ResourceId, bytes, "new-no-op",
            bytes.Length == 0 ? null : "lock"));

        var after = (await provider.Documents.FindAsync(new DocumentIdentity.Resource(state.ResourceId)))!;
        Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(after));
        Assert.Equal(0, after.Counters.WholeFileSaveReceipts);
        Assert.Equal(0, after.Counters.PendingDeliveries);
    }

    [Fact]
    public async Task ValidNoOpRecordsOneReceiptAndReplaysAfterLockExpiry()
    {
        var provider = Memory();
        var service = new CellBridgeDocumentService(provider);
        var bytes = MinimalDocx.Create("valid unchanged package");
        var state = (await service.CreateAsync("/unchanged.docx", bytes, TestActor.Value))!;
        Assert.True(await new ExternalRevisionPublisher(provider, new UnusedDestination()).BindAsync(
            state.ResourceId, state.LifecycleGeneration, state.StateVersion, Guid.NewGuid(), "external", "baseline"));
        Assert.True((await new SharedDocumentLocks(service).ApplyAsync(state.ResourceId,
            HostLockOperation.Acquire, "lock", TestActor.Value)).Success);
        var before = (await provider.Documents.FindAsync(new DocumentIdentity.Resource(state.ResourceId)))!;

        var beforeSnapshot = (await provider.Documents.ExportDocumentAsync(state.ResourceId))!;
        var accepted = await Save(service, state.ResourceId, bytes, "no-op");

        Assert.False(accepted.IsReplay);
        var after = (await provider.Documents.FindAsync(new DocumentIdentity.Resource(state.ResourceId)))!;
        Assert.Equal(before.Package, after.Package);
        Assert.Equal(before.ContentVersion, after.ContentVersion);
        Assert.Equal(before.ModifiedUtc, after.ModifiedUtc);
        Assert.Equal(JsonSerializer.Serialize(beforeSnapshot.Partitions), JsonSerializer.Serialize((await provider.Documents.ExportDocumentAsync(state.ResourceId))!.Partitions));
        Assert.Equal(JsonSerializer.Serialize(beforeSnapshot.ApplicationRevisions), JsonSerializer.Serialize((await provider.Documents.ExportDocumentAsync(state.ResourceId))!.ApplicationRevisions));
        Assert.Equal(JsonSerializer.Serialize(beforeSnapshot.ApplicationRevisions[^1]), JsonSerializer.Serialize(accepted.Revision));
        Assert.Equal(1, after.Counters.WholeFileSaveReceipts);
        Assert.Equal(0, after.Counters.PendingDeliveries);

        Clocks.GetValue(provider, _ => throw new InvalidOperationException()).Advance();
        var expired = (await provider.Documents.FindAsync(new DocumentIdentity.Resource(state.ResourceId)))!;
        var replay = await Save(service, state.ResourceId, bytes, "no-op");
        Assert.True(replay.IsReplay);
        Assert.Equal(JsonSerializer.Serialize(accepted.Revision), JsonSerializer.Serialize(replay.Revision));
        // Receipt identity still takes precedence over validation of different retry bytes.
        await Assert.ThrowsAsync<WholeFileSaveConflictException>(() => Save(service, state.ResourceId, [], "no-op"));
        await Assert.ThrowsAsync<DocumentOperationLockException>(() => Save(service, state.ResourceId, bytes, "new-no-op"));
        Assert.Equal(JsonSerializer.Serialize(expired),
            JsonSerializer.Serialize(await provider.Documents.FindAsync(new DocumentIdentity.Resource(state.ResourceId))));
    }

    [Theory]
    [InlineData("refresh")]
    [InlineData("reacquire")]
    [InlineData("expire")]
    [InlineData("revoke")]
    [InlineData("compete")]
    public async Task ChangesDuringPreparationCannotOverwriteCurrentState(string change)
    {
        var provider = Memory(); var service = new CellBridgeDocumentService(provider);
        var original = (await service.CreateAsync("/race.docx", MinimalDocx.Create(), TestActor.Value))!;
        var locks = new SharedDocumentLocks(service);
        await locks.ApplyAsync(original.ResourceId, HostLockOperation.Acquire, "lock", TestActor.Value);
        var paused = new PausedContent(provider.Content);
        var racing = Save(new(new(provider.Documents, paused)), original.ResourceId, MinimalDocx.Create("stale"), "stale");
        await paused.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        if (change == "refresh") await locks.ApplyAsync(original.ResourceId, HostLockOperation.Refresh, "lock", TestActor.Value);
        if (change == "reacquire")
        {
            await locks.ApplyAsync(original.ResourceId, HostLockOperation.Release, "lock", TestActor.Value);
            await locks.ApplyAsync(original.ResourceId, HostLockOperation.Acquire, "lock", TestActor.Value);
        }
        if (change == "expire") Clocks.GetValue(provider, _ => throw new InvalidOperationException()).Advance();
        if (change == "revoke") await RevokeAsync(provider, original.ResourceId);
        if (change == "compete") await Save(service, original.ResourceId, MinimalDocx.Create("winner"), "winner");
        var expected = (await provider.Documents.FindAsync(new DocumentIdentity.Resource(original.ResourceId)))!;
        paused.Continue.SetResult();
        if (change == "revoke") await Assert.ThrowsAsync<UnauthorizedAccessException>(() => racing);
        else await Assert.ThrowsAnyAsync<InvalidOperationException>(() => racing);
        var actual = (await provider.Documents.FindAsync(new DocumentIdentity.Resource(original.ResourceId)))!;
        Assert.Equal(expected.Package, actual.Package);
        Assert.Equal(expected.ContentVersion, actual.ContentVersion);
        Assert.Equal(expected.Counters.Revisions, actual.Counters.Revisions);
    }

    [Fact]
    public async Task PermissionCeilingAndPolicyDenyNewWritesAndReceiptReplay()
    {
        var provider = Memory(); var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/permission.docx", MinimalDocx.Create(), TestActor.Value))!;
        await new SharedDocumentLocks(service).ApplyAsync(state.ResourceId, HostLockOperation.Acquire, "lock", TestActor.Value);
        var bytes = MinimalDocx.Create("accepted");
        await Save(service, state.ResourceId, bytes, "accepted");
        var limited = TestActor.Value with { AccessLimit = new(state.ResourceId, DocumentAccess.Read) };
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.SaveWholeFileAsync(state.ResourceId,
            new MemoryStream(bytes), "accepted", limited, "lock").AsTask());
        await RevokeAsync(provider, state.ResourceId);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Save(service, state.ResourceId, bytes, "accepted"));
    }

    [Fact]
    public async Task FssHttpLeaseExcludesWholeFileSave()
    {
        var provider = Memory(); var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/foreign.docx", [], TestActor.Value))!;
        Assert.True((await provider.Documents.ExecuteControlAsync(new DocumentControlCommand.Office(state.ResourceId,
            StorageTestDocuments.Admission(state), new() { Type = SubRequestType.ExclusiveLock, SubRequestDataAttributes =
            { ["ExclusiveLockRequestType"] = "GetLock", ["ExclusiveLockID"] = "foreign", ["ClientID"] = "client", ["Timeout"] = "3600" } }))).Changed);
        await Assert.ThrowsAsync<DocumentOperationLockException>(() => Save(service, state.ResourceId,
            MinimalDocx.Create(), "foreign", null));
        await Assert.ThrowsAsync<DocumentOperationLockException>(() => Save(service, state.ResourceId,
            MinimalDocx.Create(), "foreign", "foreign"));
    }

    [Fact]
    public async Task ReceiptBudgetRejectsNewSavesWithoutDroppingRetryProtection()
    {
        var provider = StorageTestDocuments.Memory(limits: new StorageLimits { MaxWholeFileSaveReceipts = 1 });
        var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/receipt-budget.docx", MinimalDocx.Create(), TestActor.Value))!;
        await new SharedDocumentLocks(service).ApplyAsync(state.ResourceId, HostLockOperation.Acquire, "lock", TestActor.Value);
        var bytes = MinimalDocx.Create("accepted");
        await Save(service, state.ResourceId, bytes, "accepted");
        var before = (await provider.Documents.FindAsync(new DocumentIdentity.Resource(state.ResourceId)))!;
        await Assert.ThrowsAsync<StorageQuotaExceededException>(() => Save(service, state.ResourceId,
            MinimalDocx.Create("rejected"), "new"));
        Assert.True((await Save(service, state.ResourceId, bytes, "accepted")).IsReplay);
        Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(await provider.Documents.FindAsync(new DocumentIdentity.Resource(state.ResourceId))));
    }

    [Fact]
    public async Task ProtocolSaveAfterWholeFileReplacementUsesItsNewGraphAndRejectsStaleIndex()
    {
        var provider = Memory(); var service = new CellBridgeDocumentService(provider);
        var before = (await service.CreateAsync("/protocol-after-whole.docx", MinimalDocx.Create(), TestActor.Value))!;
        var stale = await StorageTestDocuments.SaveRequestAsync(service, before, MinimalDocx.Create("stale protocol bytes"));
        var locks = new SharedDocumentLocks(service);
        await locks.ApplyAsync(before.ResourceId, HostLockOperation.Acquire, "lock", TestActor.Value);
        await Save(service, before.ResourceId, MinimalDocx.Create("whole-file save"), "whole");
        await locks.ApplyAsync(before.ResourceId, HostLockOperation.Release, "lock", TestActor.Value);
        var saved = (await provider.Documents.FindAsync(new DocumentIdentity.Resource(before.ResourceId)))!;
        var refused = await service.ExecuteAsync(before.ResourceId, DocumentPartitionKind.FileContents, stale,
            new Dictionary<string, string>(), TestActor.Value);
        Assert.True(Assert.Single(refused.Response.SubResponses).Status);
        Assert.Equal(saved.Package, refused.State.Package);
        var final = await RevisionHistoryTests.Save(service, saved, "new protocol bytes");
        Assert.Equal(3, final.Counters.Revisions);
        Assert.Equal(await StorageTestDocuments.PackageAsync(provider, final.Package),
            (await StorageTestDocuments.GraphAsync(service, final)).Materialize());
    }

    private static async Task RevokeAsync(StorageProvider provider, Guid id)
    {
        var current = (await provider.Documents.FindAsync(new DocumentIdentity.Resource(id)))!;
        Assert.True((await provider.Documents.ExecuteControlAsync(new DocumentControlCommand.SetPermissions(id,
            StorageTestDocuments.Admission(current), current.Security with { Owner = "revoked" }, current.AuthorityGeneration))).Changed);
    }

    private static async Task<WholeFileSaveResult> Save(CellBridgeDocumentService service, Guid id, byte[] bytes,
        string key, string? token = "lock")
    {
        using var source = new MemoryStream(bytes);
        return await service.SaveWholeFileAsync(id, source, key, TestActor.Value, token);
    }

    private sealed class UnusedDestination : IExternalRevisionDestination
    {
        public ValueTask<ExternalDeliveryResult> CompareExchangeAsync(ExternalDeliveryRequest request, Stream content,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("No delivery expected.");
    }

    private sealed class PausedContent(IContentStore inner) : IContentStore
    {
        private int _writes;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Durable => inner.Durable;
        public bool Shared => inner.Shared;
        public async ValueTask<ContentHandle> WriteAsync(Stream source, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _writes) == 1) { Entered.SetResult(); await Continue.Task.WaitAsync(cancellationToken); }
            return await inner.WriteAsync(source, cancellationToken);
        }
        public ValueTask<Stream> OpenReadAsync(ContentHandle handle, CancellationToken cancellationToken = default) => inner.OpenReadAsync(handle, cancellationToken);
    }
}
