using System.Collections.Immutable;
using CellBridge.AspNetCore;
using CellBridge.FssHttp;
using CellBridge.FssHttpB;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using CellBridge.Storage.PostgreSql;
using CellBridge.Web;
using Npgsql;

namespace CellBridge.Storage.Tests;

public sealed class SharedHostLockTests
{
    [Fact]
    public Task CompetingProtocolsRefreshReplaceReleaseAndFencedCommit() => CheckLocks(new(new InMemoryStateStore(), new InMemoryContentStore()));

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
        var current = (await peer.State.FindByResourceIdAsync(state.ResourceId))!;
        var doc = StoredDocument.RestoreMetadata(current, DateTime.UtcNow);
        var coordinator = FssHttpLockCoordinator.Restore(doc, current.Coordination, DateTime.UtcNow, TestActor.Value.Identity);
        Assert.Equal(current.Coordination.HostLock, coordinator.Capture().HostLock);
        Assert.False(coordinator.ExecuteCellWrite(new Dictionary<string, string>(), () => true, out _, out var error));
        Assert.Equal("FileAlreadyLockedOnServer", error);
        var refresh = await other.ApplyAsync(state.ResourceId, HostLockOperation.Refresh, "CaseSensitive", TestActor.Value);
        Assert.True(refresh.Success);
        Assert.False(await host.TryCommitAsync(acquired.WriteToken!, current.StateVersion, TestActor.Value, (c, _) => c, Guid.NewGuid()));
        var replaced = await host.ApplyAsync(state.ResourceId, HostLockOperation.Replace, "CaseSensitive", TestActor.Value, "replacement");
        Assert.True(replaced.Success);
        Assert.False((await other.ApplyAsync(state.ResourceId, HostLockOperation.Release, "CaseSensitive", TestActor.Value)).Success);
        current = (await peer.State.FindByResourceIdAsync(state.ResourceId))!;
        Assert.True(await other.TryCommitAsync(replaced.WriteToken!, current.StateVersion, TestActor.Value,
            (c, _) => c with { ContentVersion = c.ContentVersion + 1 }, Guid.NewGuid()));
        Assert.True((await other.ApplyAsync(state.ResourceId, HostLockOperation.Release, "replacement", TestActor.Value)).Success);
        Assert.False(await host.TryCommitAsync(replaced.WriteToken!, current.StateVersion, TestActor.Value, (c, _) => c, Guid.NewGuid()));
        Assert.Equal("Success", await ApplyFss(peer, state.ResourceId, "GetLock"));
        Assert.Equal("Success", await ApplyFss(peer, state.ResourceId, "ConvertToSchema"));
        Assert.Equal("Success", await ApplySchema(peer, state.ResourceId, "ReleaseLock"));
        Assert.True((await other.ApplyAsync(state.ResourceId, HostLockOperation.Acquire, "final", TestActor.Value)).Success);
    }

    [Fact]
    public async Task ProviderTimeExpiryAndPermissionRevocationFenceHostWrite()
    {
        var provider = new StorageProvider(new InMemoryStateStore(), new InMemoryContentStore());
        var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/expired.docx", MinimalDocx.Create(), TestActor.Value))!;
        var locks = new SharedDocumentLocks(service);
        var acquired = await locks.ApplyAsync(state.ResourceId, HostLockOperation.Acquire, "a", TestActor.Value);
        await provider.State.TransitionAsync(state.ResourceId, (c, now) => new StateTransition<bool>(c with
        { Coordination = c.Coordination with { HostLock = c.Coordination.HostLock! with { ExpiresUtc = now.AddSeconds(-1) } } }, true));
        state = (await provider.State.FindByResourceIdAsync(state.ResourceId))!;
        Assert.False(await locks.TryCommitAsync(acquired.WriteToken!, state.StateVersion, TestActor.Value, (c, _) => c, Guid.NewGuid()));
        Assert.False((await locks.ApplyAsync(state.ResourceId, HostLockOperation.Refresh, "a", TestActor.Value)).Success);
        var newOwner = await locks.ApplyAsync(state.ResourceId, HostLockOperation.Acquire, "b", TestActor.Value);
        Assert.True(newOwner.Success);
        await provider.State.TransitionAsync(state.ResourceId, (c, now) => new StateTransition<bool>(
            DocumentPermissionUpdates.Apply(c, now, c.Security with { Owner = "different" }), true));
        state = (await provider.State.FindByResourceIdAsync(state.ResourceId))!;
        Assert.Null(state.Coordination.HostLock);
        Assert.False(await locks.TryCommitAsync(newOwner.WriteToken!, state.StateVersion, TestActor.Value, (c, _) => c, Guid.NewGuid()));
    }

    [Fact]
    public async Task LockedFileSaveCannotDowngradeAfterExpiryOrReacquire()
    {
        var stateStore = new InMemoryStateStore();
        var content = new InMemoryContentStore();
        var provider = new StorageProvider(stateStore, content);
        var state = (await new CellBridgeDocumentService(provider).CreateAsync("/stale-save.docx", MinimalDocx.Create(), TestActor.Value))!;
        Assert.Equal("Success", await ApplyFss(provider, state.ResourceId, "GetLock"));
        var before = (await stateStore.FindByResourceIdAsync(state.ResourceId))!;
        var request = StorageTests.Fixture("save-first");
        ((PutChangesSubRequestData)request.SubRequests[0].Data!).ExpectedStorageIndex =
            (await StoredDocument.RestoreAsync(before, content)).FilePartition.FileGraph.StorageIndex;
        var paused = new PausedWrites(content);
        var saved = new CellBridgeDocumentService(new(stateStore, paused)).ExecuteAsync(state.ResourceId,
            DocumentPartitionKind.FileContents, request, CellLock(), TestActor.Value).AsTask();
        await paused.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("Success", await ApplyFss(provider, state.ResourceId, "ReleaseLock"));
        Assert.Equal("Success", await ApplyFss(provider, state.ResourceId, "GetLock"));
        paused.Continue.SetResult();
        Assert.Equal("InvalidCoauthSession", (await saved).LockError);
        Assert.Equal(before.ContentVersion, (await stateStore.FindByResourceIdAsync(state.ResourceId))!.ContentVersion);
        // An expired explicit lock is also rejected instead of becoming an unlocked write.
        await stateStore.TransitionAsync(state.ResourceId, (c, now) => new StateTransition<bool>(c with
        { Coordination = c.Coordination with { Exclusive = c.Coordination.Exclusive! with { ExpiresUtc = now.AddSeconds(-1) } } }, true));
        var expired = await new CellBridgeDocumentService(provider).ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents,
            request, CellLock(), TestActor.Value);
        Assert.Equal("InvalidCoauthSession", expired.LockError);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnclaimedSaveIsRejectedBeforeStagingUnderEitherLock(bool wopi)
    {
        var provider = new StorageProvider(new InMemoryStateStore(), new InMemoryContentStore());
        var state = (await new CellBridgeDocumentService(provider).CreateAsync("/preflight.docx", MinimalDocx.Create(), TestActor.Value))!;
        if (wopi) Assert.True((await new SharedDocumentLocks(new(provider)).ApplyAsync(state.ResourceId, HostLockOperation.Acquire, "host", TestActor.Value)).Success);
        else Assert.Equal("Success", await ApplyFss(provider, state.ResourceId, "GetLock"));
        var request = StorageTests.Fixture("save-first");
        ((PutChangesSubRequestData)request.SubRequests[0].Data!).ExpectedStorageIndex =
            (await StoredDocument.RestoreAsync(state, provider.Content)).FilePartition.FileGraph.StorageIndex;
        var paused = new PausedWrites(provider.Content);
        var result = await new CellBridgeDocumentService(new(provider.State, paused)).ExecuteAsync(state.ResourceId,
            DocumentPartitionKind.FileContents, request, new Dictionary<string, string>(), TestActor.Value);
        Assert.Equal("FileAlreadyLockedOnServer", result.LockError);
        Assert.False(paused.Entered.Task.IsCompleted);
        Assert.Equal(state.ContentVersion, result.State.ContentVersion);
    }

    [Fact]
    public async Task SameTokenReacquisitionBeforePreflightCannotReplaceTheOriginalFence()
    {
        var provider = new StorageProvider(new InMemoryStateStore(), new InMemoryContentStore());
        var state = (await new CellBridgeDocumentService(provider).CreateAsync("/first-fence.docx", MinimalDocx.Create(), TestActor.Value))!;
        Assert.Equal("Success", await ApplyFss(provider, state.ResourceId, "GetLock"));
        var request = StorageTests.Fixture("save-first");
        ((PutChangesSubRequestData)request.SubRequests[0].Data!).ExpectedStorageIndex =
            (await StoredDocument.RestoreAsync(state, provider.Content)).FilePartition.FileGraph.StorageIndex;
        var reads = new PausedSaveRead(provider.State);
        var writes = new PausedWrites(provider.Content);
        var save = new CellBridgeDocumentService(new(reads, writes)).ExecuteAsync(state.ResourceId,
            DocumentPartitionKind.FileContents, request, CellLock(), TestActor.Value).AsTask();
        await reads.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("Success", await ApplyFss(provider, state.ResourceId, "ReleaseLock"));
        Assert.Equal("Success", await ApplyFss(provider, state.ResourceId, "GetLock"));
        reads.Continue.SetResult();
        Assert.Equal("InvalidCoauthSession", (await save).LockError);
        Assert.False(writes.Entered.Task.IsCompleted);
        Assert.Equal(state.ContentVersion, (await provider.State.FindByResourceIdAsync(state.ResourceId))!.ContentVersion);
    }

    private sealed class PausedSaveRead(IDocumentStateStore inner) : IDocumentStateStore
    {
        private int _reads;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Durable => inner.Durable;
        public bool Shared => inner.Shared;
        public async ValueTask<DocumentState?> FindByResourceIdAsync(Guid id, CancellationToken cancellationToken = default)
        {
            var state = await inner.FindByResourceIdAsync(id, cancellationToken);
            if (Interlocked.Increment(ref _reads) == 2)
            {
                Entered.SetResult();
                await Continue.Task.WaitAsync(cancellationToken);
            }
            return state;
        }
        public ValueTask<DocumentState?> FindByPathKeyAsync(string key, CancellationToken cancellationToken = default) => inner.FindByPathKeyAsync(key, cancellationToken);
        public ValueTask<IReadOnlyList<DocumentSummary>> ListAsync(int offset, int limit, CancellationToken cancellationToken = default) => inner.ListAsync(offset, limit, cancellationToken);
        public ValueTask<bool> TryCreateAsync(DocumentState state, CancellationToken cancellationToken = default) => inner.TryCreateAsync(state, cancellationToken);
        public ValueTask<T> TransitionAsync<T>(Guid id, Func<DocumentState, DateTime, StateTransition<T>> transition, CancellationToken cancellationToken = default) => inner.TransitionAsync(id, transition, cancellationToken);
        public ValueTask CheckHealthAsync(CancellationToken cancellationToken = default) => inner.CheckHealthAsync(cancellationToken);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReceiptReplayRechecksAuthorityAfterResponseContentRead(bool delete)
    {
        var provider = new StorageProvider(new InMemoryStateStore(), new InMemoryContentStore());
        var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/receipt-race.docx", MinimalDocx.Create(), TestActor.Value))!;
        var request = StorageTests.Fixture("save-first");
        ((PutChangesSubRequestData)request.SubRequests[0].Data!).ExpectedStorageIndex =
            (await StoredDocument.RestoreAsync(state, provider.Content)).FilePartition.FileGraph.StorageIndex;
        var accepted = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents, request, new Dictionary<string, string>(), TestActor.Value);
        var paused = new PausedReceiptRead(provider.Content, accepted.State.Receipts[0].Response!);
        var replay = new CellBridgeDocumentService(new(provider.State, paused)).ExecuteAsync(state.ResourceId,
            DocumentPartitionKind.FileContents, request, new Dictionary<string, string>(), TestActor.Value).AsTask();
        await paused.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (delete) Assert.True(await ((IDocumentLifecycleStore)provider.State).TryDeleteAsync(state.ResourceId, 1, accepted.State.StateVersion));
        else await provider.State.TransitionAsync(state.ResourceId, (c, now) => new StateTransition<bool>(
            DocumentPermissionUpdates.Apply(c, now, c.Security with { Owner = "different" }), true));
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
    private static Task<string?> ApplySchema(StorageProvider provider, Guid id, string operation) => Apply(provider, id, new()
    {
        Type = SubRequestType.SchemaLock,
        SubRequestToken = 1,
        SubRequestDataXml = "<SubRequestData />",
        SubRequestDataAttributes = { ["SchemaLockRequestType"] = operation, ["SchemaLockID"] = "22222222-2222-2222-2222-222222222222", ["ClientID"] = "client",
            ["ExclusiveLockID"] = "11111111-1111-1111-1111-111111111111", ["Timeout"] = "3600" },
    });
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
