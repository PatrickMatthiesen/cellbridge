using System.Collections.Immutable;
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
public sealed class SharedHostLockTests
{
    [Fact]
    public Task CompetingProtocolsRefreshReplaceReleaseAndFencedCommit() => CheckLocks(StorageTestDocuments.Memory());

    [PostgreSqlFact]
    public async Task PostgreSqlTwoHostsShareLockAuthority()
    {
        await using var first = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("ConnectionStrings__cellbridge")!);
        await using var second = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("ConnectionStrings__cellbridge")!);
        await CheckLocks(new(new PostgreSqlStateStore(first), new PostgreSqlContentStore(first)),
            new(new PostgreSqlStateStore(second), new PostgreSqlContentStore(second)));
    }

    private static async Task CheckLocks(StorageProvider provider, StorageProvider? peer = null)
    {
        peer ??= provider;
        var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/locks-" + Guid.NewGuid().ToString("N") + ".docx", MinimalDocx.Create(), TestActor.Value))!;
        var host = new SharedDocumentLocks(service);
        var other = new SharedDocumentLocks(new(peer));
        var wopi = host.ApplyAsync(state.ResourceId, HostLockOperation.Acquire, "CaseSensitive", TestActor.Value).AsTask();
        var fss = ApplyFss(peer, state.ResourceId, "GetLock");
        await Task.WhenAll(wopi, fss);
        Assert.NotEqual((await wopi).Success, (await fss) == "Success");
        if (await fss == "Success")
        {
            Assert.False((await host.ApplyAsync(state.ResourceId, HostLockOperation.Acquire, "CaseSensitive", TestActor.Value)).Success);
            Assert.Equal("Success", await ApplyFss(peer, state.ResourceId, "ConvertToSchema"));
            Assert.False((await host.ApplyAsync(state.ResourceId, HostLockOperation.Acquire, "CaseSensitive", TestActor.Value)).Success);
            Assert.Equal("Success", await ApplySchema(peer, state.ResourceId, "ConvertToExclusive"));
            Assert.Equal("Success", await ApplyFss(peer, state.ResourceId, "ReleaseLock"));
        }
        var acquired = await host.ApplyAsync(state.ResourceId, HostLockOperation.Acquire, "CaseSensitive", TestActor.Value);
        Assert.True(acquired.Success);
        Assert.Equal("FileAlreadyLockedOnServer", await ApplyFss(peer, state.ResourceId, "GetLock"));
        Assert.False((await other.ApplyAsync(state.ResourceId, HostLockOperation.Acquire, "casesensitive", TestActor.Value)).Success);
        var current = (await peer.Documents.ReadControlAsync(state.ResourceId))!;
        var doc = StoredDocument.RestoreControl(current);
        var coordinator = FssHttpLockCoordinator.Restore(doc, current.Coordination, current.UtcNow, TestActor.Value.Identity);
        Assert.Equal(current.Coordination.HostLock, coordinator.Capture().HostLock);
        Assert.False(coordinator.ExecuteCellWrite(new Dictionary<string, string>(), () => true, out _, out var error));
        Assert.Equal("FileAlreadyLockedOnServer", error);
        var refresh = await other.ApplyAsync(state.ResourceId, HostLockOperation.Refresh, "CaseSensitive", TestActor.Value);
        Assert.True(refresh.Success);
        Assert.False(await host.TryCommitAsync(acquired.WriteToken!, RepositoryTestPublications.Receipt(current, TestActor.Value, acquired.WriteToken!.LockToken), TestActor.Value));
        var replaced = await host.ApplyAsync(state.ResourceId, HostLockOperation.Replace, "CaseSensitive", TestActor.Value, "replacement");
        Assert.True(replaced.Success);
        Assert.False((await other.ApplyAsync(state.ResourceId, HostLockOperation.Release, "CaseSensitive", TestActor.Value)).Success);
        current = (await peer.Documents.ReadControlAsync(state.ResourceId))!;
        var prepared = RepositoryTestPublications.Receipt(current, TestActor.Value, replaced.WriteToken!.LockToken);
        Assert.True(await other.TryCommitAsync(replaced.WriteToken!, prepared, TestActor.Value));
        Assert.Equal(current.Head.Counters.WholeFileSaveReceipts + 1,
            (await peer.Documents.FindAsync(new DocumentIdentity.Resource(state.ResourceId)))!.Counters.WholeFileSaveReceipts);
        Assert.True((await other.ApplyAsync(state.ResourceId, HostLockOperation.Release, "replacement", TestActor.Value)).Success);
        Assert.False(await host.TryCommitAsync(replaced.WriteToken!, RepositoryTestPublications.Receipt(current, TestActor.Value, replaced.WriteToken!.LockToken), TestActor.Value));
        Assert.Equal("Success", await ApplyFss(peer, state.ResourceId, "GetLock"));
        Assert.Equal("Success", await ApplyFss(peer, state.ResourceId, "ConvertToSchema"));
        Assert.Equal("Success", await ApplySchema(peer, state.ResourceId, "ReleaseLock"));
        Assert.True((await other.ApplyAsync(state.ResourceId, HostLockOperation.Acquire, "final", TestActor.Value)).Success);
    }

    [Fact]
    public async Task ProviderTimeExpiryAndPermissionRevocationFenceHostWrite()
    {
        var clock = new ManualClock(); var content = new InMemoryContentStore();
        var provider = new StorageProvider(new InMemoryDocumentRepository(content, clock: clock), content);
        var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/expired.docx", MinimalDocx.Create(), TestActor.Value))!;
        var locks = new SharedDocumentLocks(service);
        var acquired = await locks.ApplyAsync(state.ResourceId, HostLockOperation.Acquire, "a", TestActor.Value);
        var prepared = RepositoryTestPublications.Receipt((await provider.Documents.ReadControlAsync(state.ResourceId))!, TestActor.Value, "a");
        clock.Advance(TimeSpan.FromHours(1));
        Assert.False(await locks.TryCommitAsync(acquired.WriteToken!, prepared, TestActor.Value));
        Assert.False((await locks.ApplyAsync(state.ResourceId, HostLockOperation.Refresh, "a", TestActor.Value)).Success);
        var newOwner = await locks.ApplyAsync(state.ResourceId, HostLockOperation.Acquire, "b", TestActor.Value);
        Assert.True(newOwner.Success);
        prepared = RepositoryTestPublications.Receipt((await provider.Documents.ReadControlAsync(state.ResourceId))!, TestActor.Value, "b");
        await TestStorage.SetPermissionsAsync(provider, state.ResourceId, state.Security with { Owner = "different" });
        Assert.Null((await provider.Documents.ReadControlAsync(state.ResourceId))!.Coordination.HostLock);
        Assert.False(await locks.TryCommitAsync(newOwner.WriteToken!, prepared, TestActor.Value));

    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LockedFileSaveCannotDowngradeAfterExpiryOrReacquire(bool refresh)
    {
        var clock = new ManualClock();
        var content = new InMemoryContentStore();
        var stateStore = new InMemoryDocumentRepository(content, clock: clock);
        var provider = new StorageProvider(stateStore, content);
        var state = (await new CellBridgeDocumentService(provider).CreateAsync("/stale-save.docx", MinimalDocx.Create(), TestActor.Value))!;
        Assert.Equal("Success", await ApplyFss(provider, state.ResourceId, "GetLock"));
        var before = (await stateStore.FindAsync(new DocumentIdentity.Resource(state.ResourceId)))!;
        var request = await StorageTestDocuments.SaveRequestAsync(new(provider), before, MinimalDocx.Create("saved"));
        var paused = new PausedWrites(content);
        var saved = new CellBridgeDocumentService(new(stateStore, paused)).ExecuteAsync(state.ResourceId,
            DocumentPartitionKind.FileContents, request, CellLock(), TestActor.Value).AsTask();
        await paused.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        clock.Advance(TimeSpan.FromSeconds(1));
        if (refresh) Assert.Equal("Success", await ApplyFss(provider, state.ResourceId, "RefreshLock"));
        else
        {
            Assert.Equal("Success", await ApplyFss(provider, state.ResourceId, "ReleaseLock"));
            Assert.Equal("Success", await ApplyFss(provider, state.ResourceId, "GetLock"));
        }
        paused.Continue.SetResult();
        Assert.Equal("InvalidCoauthSession", (await saved).LockError);
        Assert.Equal(before.ContentVersion, (await stateStore.FindAsync(new DocumentIdentity.Resource(state.ResourceId)))!.ContentVersion);
        // An expired explicit lock is also rejected instead of becoming an unlocked write.
        clock.Advance(TimeSpan.FromHours(2));
        var expired = await new CellBridgeDocumentService(provider).ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents,
            request, CellLock(), TestActor.Value);
        Assert.Equal("InvalidCoauthSession", expired.LockError);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnclaimedSaveIsRejectedBeforeStagingUnderEitherLock(bool wopi)
    {
        var provider = StorageTestDocuments.Memory();
        var state = (await new CellBridgeDocumentService(provider).CreateAsync("/preflight.docx", MinimalDocx.Create(), TestActor.Value))!;
        if (wopi) Assert.True((await new SharedDocumentLocks(new(provider)).ApplyAsync(state.ResourceId, HostLockOperation.Acquire, "host", TestActor.Value)).Success);
        else Assert.Equal("Success", await ApplyFss(provider, state.ResourceId, "GetLock"));
        var request = await StorageTestDocuments.SaveRequestAsync(new(provider), state, MinimalDocx.Create("saved"));
        var paused = new PausedWrites(provider.Content);
        var result = await new CellBridgeDocumentService(new(provider.Documents, paused)).ExecuteAsync(state.ResourceId,
            DocumentPartitionKind.FileContents, request, new Dictionary<string, string>(), TestActor.Value);
        Assert.Equal("FileAlreadyLockedOnServer", result.LockError);
        Assert.False(paused.Entered.Task.IsCompleted);
        Assert.Equal(state.ContentVersion, result.State.ContentVersion);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SameTokenReacquisitionBeforePreflightCannotReplaceTheOriginalFence(bool refresh)
    {
        var clock = new ManualClock(); var content = new InMemoryContentStore();
        var provider = new StorageProvider(new InMemoryDocumentRepository(content, clock: clock), content);
        var state = (await new CellBridgeDocumentService(provider).CreateAsync("/first-fence.docx", MinimalDocx.Create(), TestActor.Value))!;
        Assert.Equal("Success", await ApplyFss(provider, state.ResourceId, "GetLock"));
        var request = await StorageTestDocuments.SaveRequestAsync(new(provider), state, MinimalDocx.Create("saved"));
        var reads = new PausedSaveRead(provider.Documents);
        var writes = new PausedWrites(provider.Content);
        var save = new CellBridgeDocumentService(new(reads, writes)).ExecuteAsync(state.ResourceId,
            DocumentPartitionKind.FileContents, request, CellLock(), TestActor.Value).AsTask();
        await reads.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        clock.Advance(TimeSpan.FromSeconds(1));
        if (refresh) Assert.Equal("Success", await ApplyFss(provider, state.ResourceId, "RefreshLock"));
        else
        {
            Assert.Equal("Success", await ApplyFss(provider, state.ResourceId, "ReleaseLock"));
            Assert.Equal("Success", await ApplyFss(provider, state.ResourceId, "GetLock"));
        }
        reads.Continue.SetResult();
        try { Assert.Equal("InvalidCoauthSession", (await save.WaitAsync(TimeSpan.FromSeconds(5))).LockError); }
        finally { writes.Continue.TrySetResult(); }
        Assert.False(writes.Entered.Task.IsCompleted);
        Assert.Equal(state.ContentVersion, (await provider.Documents.FindAsync(new DocumentIdentity.Resource(state.ResourceId)))!.ContentVersion);
    }

    [Fact]
    public async Task AnotherOwnersExpiryCannotReplaceThePreparedAuthorityAfterAnUnrelatedRetry()
    {
        var clock = new ManualClock(); var content = new InMemoryContentStore();
        var provider = new StorageProvider(new InMemoryDocumentRepository(content, clock: clock), content);
        var service = new CellBridgeDocumentService(provider);
        var head = (await service.CreateAsync("/retry-authority.docx", MinimalDocx.Create(), TestActor.Value))!;
        var other = new CellBridgeActor(new("other:writer", "other", "Other writer"));
        await TestStorage.SetPermissionsAsync(provider, head.ResourceId,
            head.Security with { Grants = head.Security.Grants.Add(other.Identity.Subject, DocumentAccess.Write) });
        var writerAttributes = LockInputValidationTests.Attributes(SubRequestType.SchemaLock, "GetLock");
        writerAttributes["Timeout"] = "120000";
        writerAttributes["SchemaLockID"] = Guid.NewGuid().ToString();
        writerAttributes.Remove("ExclusiveLockID");
        var otherAttributes = new Dictionary<string, string>(writerAttributes) { ["ClientID"] = Guid.NewGuid().ToString(), ["Timeout"] = "3600" };
        var processor = new CellBridgeRequestProcessor(service);
        foreach (var (attributes, actor) in new[] { (writerAttributes, TestActor.Value), (otherAttributes, other) })
            Assert.Equal("Success", (await processor.ExecuteAsync(CellStorageRequestParser.Parse(
                LockInputValidationTests.Soap(head.ResourceId, SubRequestType.SchemaLock, attributes)), "https://host.test", actor))
                .Response.Responses[0].SubResponses[0].ErrorCode);
        head = (await provider.Documents.FindAsync(new DocumentIdentity.Resource(head.ResourceId)))!;
        var before = (await provider.Documents.ReadControlAsync(head.ResourceId))!;
        Assert.Equal(2, before.Coordination.SchemaOwners.Length);
        var request = await StorageTestDocuments.SaveRequestAsync(service, head, MinimalDocx.Create("must not publish"));
        var retry = new ExpireOtherOwnerOnPublish(provider.Documents, clock, writerAttributes);
        var result = await new CellBridgeDocumentService(new(retry, content)).ExecuteAsync(head.ResourceId,
            DocumentPartitionKind.FileContents, request, writerAttributes, TestActor.Value);
        Assert.Equal("InvalidCoauthSession", result.LockError);
        Assert.Empty(result.AcceptedSaves);
        Assert.Equal(1, retry.Publications);
        var current = (await provider.Documents.ReadControlAsync(head.ResourceId))!;
        Assert.Equal(head.ContentVersion, current.Head.ContentVersion);
        Assert.Equal(head.StateVersion + 1, current.Head.StateVersion);
        Assert.Equal(head.CoordinationGeneration, current.Head.CoordinationGeneration);
        Assert.Equal(0, current.Head.Counters.SaveReceipts);
        Assert.Equal(head.Package, current.Head.Package);
    }

    private sealed class ExpireOtherOwnerOnPublish(IDocumentRepository inner, ManualClock clock,
        IReadOnlyDictionary<string, string> attributes) : RepositoryTestProxy(inner)
    {
        public int Publications { get; private set; }
        public override async ValueTask<PublicationResult> PublishAsync(PreparedDocumentPublication publication, CancellationToken ct = default)
        {
            if (++Publications == 1)
            {
                var head = (await Inner.FindAsync(new DocumentIdentity.Resource(publication.ResourceId), ct))!;
                Assert.True((await Inner.ChangePathAsync(new(head.ResourceId, TestStorage.Admit(head),
                    "/unrelated-move.docx", "/UNRELATED-MOVE.DOCX", attributes.ToImmutableDictionary()), ct)).Changed);
                clock.Advance(TimeSpan.FromHours(2));
            }
            return await Inner.PublishAsync(publication, ct);
        }
    }

    private sealed class PausedSaveRead(IDocumentRepository inner) : RepositoryTestProxy(inner)
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<SavePreparationRead?> PrepareSaveAsync(Guid id, SavePreparationRequest request, CancellationToken ct = default)
        {
            var state = await Inner.PrepareSaveAsync(id, request, ct);
            if (!Entered.Task.IsCompleted)
            {
                Entered.SetResult();
                await Continue.Task.WaitAsync(ct);
            }
            return state;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReceiptReplayRechecksAuthorityAfterResponseContentRead(bool delete)
    {
        var provider = StorageTestDocuments.Memory();
        var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/receipt-race.docx", MinimalDocx.Create(), TestActor.Value))!;
        var request = await StorageTestDocuments.SaveRequestAsync(new(provider), state, MinimalDocx.Create("saved"));
        var accepted = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents, request, new Dictionary<string, string>(), TestActor.Value);
        var identity = IndexedPartitionSavePreparer.Identify(state.LifecycleGeneration, 0, request.SubRequests[0], request.DataElementPackage!);
        var receipt = (await provider.Documents.ReadReceiptAsync(state.ResourceId, identity.Operation))!.Receipt!;
        var paused = new PausedReceiptRead(provider.Content, receipt.Response!);
        var replay = new CellBridgeDocumentService(new(provider.Documents, paused)).ExecuteAsync(state.ResourceId,
            DocumentPartitionKind.FileContents, request, new Dictionary<string, string>(), TestActor.Value).AsTask();
        await paused.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (delete) Assert.True((await provider.Documents.ChangeLifecycleAsync(new DocumentLifecycleCommand.Delete(state.ResourceId, 1, accepted.State.StateVersion))).Applied);
        else await TestStorage.SetPermissionsAsync(provider, state.ResourceId, state.Security with { Owner = "different" });
        paused.Continue.SetResult();
        if (delete) await Assert.ThrowsAsync<KeyNotFoundException>(() => replay);
        else
        {
            var result = await replay;
            Assert.True(Assert.Single(result.Response.SubResponses).Status);
            Assert.Empty(result.AcceptedSaves);
        }
    }

    private sealed class PausedReceiptRead(IContentStore inner, ContentHandle receipt) : IContentStore
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Durable => inner.Durable;
        public bool Shared => inner.Shared;
        public ValueTask<ContentHandle> WriteAsync(Stream source, CancellationToken cancellationToken = default) => inner.WriteAsync(source, cancellationToken);
        public async ValueTask<Stream> OpenReadAsync(ContentHandle handle, CancellationToken cancellationToken = default)
        {
            if (handle == receipt) { Entered.SetResult(); await Continue.Task.WaitAsync(cancellationToken); }
            return await inner.OpenReadAsync(handle, cancellationToken);
        }
    }

    private static Task<string?> ApplyFss(StorageProvider provider, Guid id, string operation) => Apply(provider, id, new()
    {
        Type = SubRequestType.ExclusiveLock,
        SubRequestToken = 1,
        SubRequestDataXml = "<SubRequestData />",
        SubRequestDataAttributes = { ["ExclusiveLockRequestType"] = operation, ["ExclusiveLockID"] = "11111111-1111-1111-1111-111111111111",
            ["SchemaLockID"] = "22222222-2222-2222-2222-222222222222", ["ClientID"] = "client", ["Timeout"] = "3600" },
    });
    private static Task<string?> ApplySchema(StorageProvider provider, Guid id, string operation)
    {
        var request = new FssHttpSubRequest
        {
            Type = SubRequestType.SchemaLock,
            SubRequestToken = 1,
            SubRequestDataXml = "<SubRequestData />",
            SubRequestDataAttributes = { ["SchemaLockRequestType"] = operation, ["SchemaLockID"] = "22222222-2222-2222-2222-222222222222", ["ClientID"] = "client",
                ["ExclusiveLockID"] = "11111111-1111-1111-1111-111111111111", ["Timeout"] = "3600" },
        };
        if (operation == "ConvertToExclusive")
            request.SubRequestDataAttributes["ReleaseLockOnConversionToExclusiveFailure"] = "false";
        return Apply(provider, id, request);
    }
    private static async Task<string?> Apply(StorageProvider provider, Guid id, FssHttpSubRequest request)
    {
        var response = await new CellBridgeRequestProcessor(new(provider)).ExecuteAsync(new() { Requests =
            { new FssHttpRequest { UseResourceId = true, ResourceId = id.ToString(), Url = "https://host.test/lock.docx",
                RequestToken = 1, SubRequests = { request } } } }, "https://host.test", TestActor.Value);
        return response.Response.Responses[0].SubResponses[0].ErrorCode;
    }
    private static Dictionary<string, string> CellLock() => new()
    {
        ["ExclusiveLockID"] = "11111111-1111-1111-1111-111111111111",
        ["Timeout"] = "3600",
    };
    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan elapsed) => _now += elapsed;
    }

    private sealed class PausedWrites(IContentStore inner) : IContentStore
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _count;
        public bool Durable => inner.Durable;
        public bool Shared => inner.Shared;
        public async ValueTask<ContentHandle> WriteAsync(Stream source, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _count) == 1) { Entered.SetResult(); await Continue.Task.WaitAsync(cancellationToken); }
            return await inner.WriteAsync(source, cancellationToken);
        }
        public ValueTask<Stream> OpenReadAsync(ContentHandle handle, CancellationToken cancellationToken = default) => inner.OpenReadAsync(handle, cancellationToken);
    }
}
