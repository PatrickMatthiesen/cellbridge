using CellBridge.AspNetCore;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using CellBridge.Storage.PostgreSql;
using CellBridge.Web;
using Npgsql;

namespace CellBridge.Storage.Tests;

public sealed class PathRetirementTests
{
    [Fact]
    public Task MemoryRetiresNamesAndRejectsReservationMutation() => CheckReservations(Memory());

    [PostgreSqlFact]
    public async Task PostgreSqlRetiresNamesAndRejectsReservationMutation()
    {
        await using var source = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("ConnectionStrings__cellbridge")!);
        await CheckReservations(new(new PostgreSqlStateStore(source), new PostgreSqlContentStore(source)));
    }

    private static async Task CheckReservations(StorageProvider provider)
    {
        var service = new CellBridgeDocumentService(provider);
        var folder = "/" + Guid.NewGuid().ToString("N") + "/";
        var state = (await service.CreateAsync(folder + "a.docx", MinimalDocx.Create(), TestActor.Value))!;
        await service.RenameDocumentAsync(state.ResourceId, "b.docx", TestActor.Value);
        var renamed = (await provider.State.FindByResourceIdAsync(state.ResourceId))!;
        Assert.Equal(new[] { state.PathKey }, renamed.RetiredPathKeys);
        Assert.Null(await provider.State.FindByPathKeyAsync(state.PathKey));
        Assert.Null(await service.CreateAsync(state.Path, MinimalDocx.Create(), TestActor.Value));
        var other = (await service.CreateAsync(folder + "other.docx", MinimalDocx.Create(), TestActor.Value))!;
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RenameDocumentAsync(other.ResourceId, "a.docx", TestActor.Value).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.State.TransitionAsync(state.ResourceId,
            (current, _) => new StateTransition<bool>(current with { RetiredPathKeys = [] }, true)).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.State.TryCreateAsync(renamed with
            { ResourceId = Guid.NewGuid(), Path = folder + "forged.docx", PathKey = (folder + "forged.docx").ToUpperInvariant() }).AsTask());
        await service.RenameDocumentAsync(state.ResourceId, "a.docx", TestActor.Value);
        Assert.Equal(state.ResourceId, (await provider.State.FindByPathKeyAsync(state.PathKey))!.ResourceId);
        Assert.Null(await service.CreateAsync(folder + "b.docx", MinimalDocx.Create(), TestActor.Value));
        var current = (await provider.State.FindByResourceIdAsync(state.ResourceId))!;
        Assert.True(await ((IDocumentLifecycleStore)provider.State).TryDeleteAsync(state.ResourceId,
            current.LifecycleGeneration, current.StateVersion));
        Assert.Null(await service.CreateAsync(folder + "b.docx", MinimalDocx.Create(), TestActor.Value));
    }

    [Fact]
    public async Task RenameAtCapAllowsExistingReservationsAndRollsBackQuotaFailure()
    {
        var provider = Memory(new StorageLimits { MaxRetiredPathKeys = 2 });
        var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/cap/a.docx", MinimalDocx.Create(), TestActor.Value))!;
        await service.RenameDocumentAsync(state.ResourceId, "b.docx", TestActor.Value);
        await service.RenameDocumentAsync(state.ResourceId, "a.docx", TestActor.Value);
        await service.RenameDocumentAsync(state.ResourceId, "b.docx", TestActor.Value);
        await service.RenameDocumentAsync(state.ResourceId, "c.docx", TestActor.Value);
        var before = (await provider.State.FindByResourceIdAsync(state.ResourceId))!;
        await Assert.ThrowsAsync<StorageQuotaExceededException>(() => service.RenameDocumentAsync(state.ResourceId, "d.docx", TestActor.Value).AsTask());
        Assert.Equal(before, await provider.State.FindByResourceIdAsync(state.ResourceId));
        Assert.Null(await provider.State.FindByPathKeyAsync("/CAP/D.DOCX"));
        Assert.Equal(2, before.RetiredPathKeys.Length);
    }

    [Fact]
    public Task MemoryNamespaceRacesPreserveUniqueOwnership() => CheckRaces(Memory());

    [PostgreSqlFact]
    public async Task PostgreSqlNamespaceRacesPreserveUniqueOwnership()
    {
        await using var source = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("ConnectionStrings__cellbridge")!);
        await CheckRaces(new(new PostgreSqlStateStore(source), new PostgreSqlContentStore(source)));
    }

    private static async Task CheckRaces(StorageProvider provider)
    {
        var service = new CellBridgeDocumentService(provider);
        for (var round = 0; round < 4; round++)
        {
            var folder = "/" + Guid.NewGuid().ToString("N") + "/";
            var a = (await service.CreateAsync(folder + "a.docx", MinimalDocx.Create(), TestActor.Value))!;
            var rename = TryRename(service, a.ResourceId, "b.docx");
            var create = service.CreateAsync(folder + "b.docx", MinimalDocx.Create(), TestActor.Value).AsTask();
            await Task.WhenAll(rename, create).WaitAsync(TimeSpan.FromSeconds(15));
            Assert.NotEqual(await rename, await create is not null);
            var atB = (await provider.State.FindByPathKeyAsync((folder + "b.docx").ToUpperInvariant()))!;
            Assert.Equal(await rename ? a.ResourceId : (await create)!.ResourceId, atB.ResourceId);

            var c = (await service.CreateAsync(folder + "c.docx", MinimalDocx.Create(), TestActor.Value))!;
            var release = service.RenameDocumentAsync(c.ResourceId, "d.docx", TestActor.Value).AsTask();
            var reuse = service.CreateAsync(folder + "c.docx", MinimalDocx.Create(), TestActor.Value).AsTask();
            await Task.WhenAll((Task)release, reuse).WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Null(await reuse);
            Assert.Null(await service.CreateAsync(folder + "c.docx", MinimalDocx.Create(), TestActor.Value));

            var x = (await service.CreateAsync(folder + "x.docx", MinimalDocx.Create(), TestActor.Value))!;
            var y = (await service.CreateAsync(folder + "y.docx", MinimalDocx.Create(), TestActor.Value))!;
            var swaps = await Task.WhenAll(TryRename(service, x.ResourceId, "y.docx"), TryRename(service, y.ResourceId, "x.docx"))
                .WaitAsync(TimeSpan.FromSeconds(15));
            Assert.All(swaps, success => Assert.False(success));
        }
    }

    private static async Task<bool> TryRename(CellBridgeDocumentService service, Guid id, string name)
    {
        try { await service.RenameDocumentAsync(id, name, TestActor.Value); return true; }
        catch (InvalidOperationException) { return false; }
    }

    private static StorageProvider Memory(StorageLimits? limits = null) =>
        new(new InMemoryStateStore(), new InMemoryContentStore(), limits: limits);
}
