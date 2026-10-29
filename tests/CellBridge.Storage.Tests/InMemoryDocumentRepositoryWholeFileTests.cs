using System.Collections.Concurrent;
using System.Security.Cryptography;
using CellBridge.AspNetCore;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using CellBridge.TestFixtures;

public sealed partial class InMemoryDocumentRepositoryTests
{
    [Fact]
    public async Task WholeFileSavePreservesTheCellExactBytesHistoryAndAcceptedReplay()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var content = new RecordingContent(new InMemoryContentStore());
        var repository = new InMemoryDocumentRepository(content, clock: clock);
        var provider = new StorageProvider(repository, content);
        var service = new CellBridgeDocumentService(provider);
        var actor = new CellBridgeActor(Actor, CanCreate: true);
        var original = MinimalDocx.Create("original");
        var created = (await service.CreateAsync("/whole-file.docx", original, actor))!;
        Assert.True((await new SharedDocumentLocks(service).ApplyAsync(created.ResourceId, HostLockOperation.Acquire, "host", actor)).Success);
        var bound = (await repository.ReadControlAsync(created.ResourceId))!.Head;
        Assert.True((await repository.UpdateDeliveryAsync(new DocumentDeliveryCommand.Bind(created.ResourceId, bound.LifecycleGeneration,
            bound.StateVersion, new(Guid.NewGuid(), "external", "baseline", 1)))).Applied);
        content.Writes.Clear();
        var replacement = MinimalDocx.Create("replacement");
        var accepted = await Save(replacement, "first");
        Assert.False(accepted.IsReplay);
        Assert.Equal(created.ContentVersion + 1, accepted.Revision.ContentVersion);
        Assert.Equal(created.Package.Root.Cell, accepted.Revision.Package.Root.Cell);
        Assert.Equal(replacement, await ReadPackage(accepted.Revision.Package));
        Assert.Equal(original, await ReadPackage(created.Package));
        var digest = Convert.ToHexStringLower(SHA256.HashData(replacement));
        Assert.DoesNotContain(content.Writes, handle => handle.Length == replacement.Length && handle.Sha256 == digest);
        Assert.Equal(1, (await repository.ReadControlAsync(created.ResourceId))!.Head.Counters.PendingDeliveries);

        var laterBytes = MinimalDocx.Create("later");
        var later = await Save(laterBytes, "second");
        var replay = await Save(replacement, "first");
        Assert.True(replay.IsReplay);
        Assert.Equal(accepted.Revision, replay.Revision);
        Assert.Equal(later.Revision.ContentVersion, (await repository.ReadControlAsync(created.ResourceId))!.Head.ContentVersion);
        await Assert.ThrowsAsync<WholeFileSaveConflictException>(async () => await Save(MinimalDocx.Create("different"), "first"));
        var noOp = await Save(laterBytes, "no-op");
        Assert.Equal(later.Revision.RevisionNumber, noOp.Revision.RevisionNumber);
        Assert.Equal(3, (await repository.ReadControlAsync(created.ResourceId))!.Head.Counters.Revisions);
        clock.Advance(TimeSpan.FromMinutes(31));
        Assert.True((await Save(replacement, "first")).IsReplay);
        Assert.Equal(3, (await repository.ReadControlAsync(created.ResourceId))!.Head.Counters.WholeFileSaveReceipts);

        async Task<WholeFileSaveResult> Save(byte[] bytes, string operation)
        {
            using var source = new MemoryStream(bytes);
            return await service.SaveWholeFileAsync(created.ResourceId, source, operation, actor, "host");
        }
        async Task<byte[]> ReadPackage(DocumentPackage package)
        {
            await using var stream = await provider.Packages.OpenReadAsync(package);
            using var output = new MemoryStream();
            await stream.CopyToAsync(output);
            return output.ToArray();
        }
    }

    [Fact]
    public async Task WholeFileSaveRechecksAnExpiredHostLeaseAfterReadingInput()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var content = new InMemoryContentStore();
        var repository = new InMemoryDocumentRepository(content, clock: clock);
        var service = new CellBridgeDocumentService(new(repository, content));
        var actor = new CellBridgeActor(Actor, CanCreate: true);
        var created = (await service.CreateAsync("/expire-while-reading.docx", MinimalDocx.Create("before"), actor))!;
        Assert.True((await new SharedDocumentLocks(service).ApplyAsync(created.ResourceId, HostLockOperation.Acquire, "host", actor)).Success);
        var before = (await repository.ReadControlAsync(created.ResourceId))!.Head;
        using var source = new BlockedReadStream(MinimalDocx.Create("after"));
        var pending = service.SaveWholeFileAsync(created.ResourceId, source, "late", actor, "host").AsTask();
        try
        {
            await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            clock.Advance(TimeSpan.FromMinutes(31));
        }
        finally { source.Continue.TrySetResult(); }
        var error = await Assert.ThrowsAsync<DocumentOperationLockException>(async () => await pending);
        Assert.Equal("InvalidCoauthSession", error.ErrorCode);
        Assert.Equal(before, (await repository.ReadControlAsync(created.ResourceId))!.Head);
    }

    [Fact]
    public async Task HostLocksRejectAnotherActorAndTypedCommitsFenceAReplacedLease()
    {
        var content = new InMemoryContentStore();
        var repository = new InMemoryDocumentRepository(content);
        var service = new CellBridgeDocumentService(new(repository, content));
        var actor = new CellBridgeActor(Actor, CanCreate: true);
        var created = (await service.CreateAsync("/typed-host.docx", MinimalDocx.Create("host"), actor))!;
        var locks = new SharedDocumentLocks(service);
        var acquired = await locks.ApplyAsync(created.ResourceId, HostLockOperation.Acquire, "host", actor);
        Assert.True(acquired.Success);
        var other = new CellBridgeActor(new("other", "other", "Other"));
        // The repository admission boundary accepts a trusted custom evaluator in this test.
        var custom = new CellBridgeDocumentService(new(repository, content), accessEvaluator: new GrantAll());
        Assert.False((await new SharedDocumentLocks(custom).ApplyAsync(created.ResourceId, HostLockOperation.Acquire, "host", other)).Success);
        var control = (await repository.ReadControlAsync(created.ResourceId))!;
        var publication = new PreparedDocumentPublication(created.ResourceId, Admission(control.Head),
            new(1, control.Head.AuthorityGeneration, control.Coordination.Generation, control.Head.StateVersion, control.Head.ContentVersion, []),
            new(new(1, SaveOperationFamily.WholeFile, 0, "typed"), "typed-digest", Actor.Subject,
                control.Head.ContentVersion, control.Head.LatestRevisionNumber, null, null), []) { HostLockToken = "host" };
        Assert.True((await locks.ApplyAsync(created.ResourceId, HostLockOperation.Replace, "host", actor, "replacement")).Success);
        Assert.False(await locks.TryCommitAsync(acquired.WriteToken!, publication, actor));
        Assert.Equal(0, (await repository.ReadControlAsync(created.ResourceId))!.Head.Counters.WholeFileSaveReceipts);
    }

    private sealed class RecordingContent(IContentStore inner) : IContentStore
    {
        public ConcurrentBag<ContentHandle> Writes { get; } = [];
        public bool Durable => false;
        public bool Shared => false;
        public async ValueTask<ContentHandle> WriteAsync(Stream source, CancellationToken cancellationToken = default)
        {
            var handle = await inner.WriteAsync(source, cancellationToken);
            Writes.Add(handle);
            return handle;
        }
        public ValueTask<Stream> OpenReadAsync(ContentHandle handle, CancellationToken cancellationToken = default) => inner.OpenReadAsync(handle, cancellationToken);
    }

    private sealed class GrantAll : ICellBridgeAccessEvaluator
    {
        public DocumentAccess Evaluate(CellBridgeActor actor, DocumentAuthorizationView state) => DocumentAccess.Read | DocumentAccess.Write;
    }

    private sealed class BlockedReadStream(byte[] bytes) : MemoryStream(bytes)
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Continue.Task.WaitAsync(cancellationToken);
            return await base.ReadAsync(buffer, cancellationToken);
        }
    }
}
