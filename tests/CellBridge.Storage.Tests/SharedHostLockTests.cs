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
            DocumentPartitionKind.FileContents, request, new Dictionary<string, string> { ["ExclusiveLockID"] = "fss" }, TestActor.Value).AsTask();
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
            request, new Dictionary<string, string> { ["ExclusiveLockID"] = "fss" }, TestActor.Value);
        Assert.Equal("InvalidCoauthSession", expired.LockError);
    }

    private static Task<string?> ApplyFss(StorageProvider provider, Guid id, string operation) => Apply(provider, id, new()
    {
        Type = SubRequestType.ExclusiveLock,
        SubRequestDataAttributes = { ["ExclusiveLockRequestType"] = operation, ["ExclusiveLockID"] = "fss",
            ["SchemaLockID"] = "schema", ["ClientID"] = "client", ["Timeout"] = "3600" },
    });
    private static Task<string?> ApplySchema(StorageProvider provider, Guid id, string operation) => Apply(provider, id, new()
    {
        Type = SubRequestType.SchemaLock,
        SubRequestDataAttributes = { ["SchemaLockRequestType"] = operation, ["SchemaLockID"] = "schema", ["ClientID"] = "client",
            ["ExclusiveLockID"] = "fss", ["Timeout"] = "3600" },
    });
    private static async Task<string?> Apply(StorageProvider provider, Guid id, FssHttpSubRequest request)
    {
        var response = await new CellBridgeRequestProcessor(new(provider)).ExecuteAsync(new() { Requests =
            { new FssHttpRequest { UseResourceId = true, ResourceId = id.ToString(), SubRequests = { request } } } }, "https://host.test", TestActor.Value);
        return response.Response.Responses[0].SubResponses[0].ErrorCode;
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
