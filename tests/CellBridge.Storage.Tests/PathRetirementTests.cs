using CellBridge.AspNetCore;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using CellBridge.Storage.PostgreSql;
using CellBridge.TestFixtures;
using Npgsql;

namespace CellBridge.Storage.Tests;

[Collection("PostgreSQL")]
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
        var renamed = (await provider.Documents.FindAsync(new DocumentIdentity.Resource(state.ResourceId)))!;
        var reserved = (await provider.Documents.ExportDocumentAsync(state.ResourceId))!.PathClaims;
        Assert.Equal(new[] { state.PathKey }, reserved.Where(path => path != renamed.PathKey));
        Assert.Null(await provider.Documents.FindAsync(new DocumentIdentity.Path(state.PathKey)));
        Assert.Null(await service.CreateAsync(state.Path, MinimalDocx.Create(), TestActor.Value));
        var other = (await service.CreateAsync(folder + "other.docx", MinimalDocx.Create(), TestActor.Value))!;
        var collision = await Assert.ThrowsAsync<DocumentOperationLockException>(() => service.RenameDocumentAsync(other.ResourceId, "a.docx", TestActor.Value).AsTask());
        Assert.Equal("FileAlreadyExists", collision.ErrorCode);
        // Creation cannot replace the existing identity or release its provider-owned claims.
        Assert.Null(await service.CreateAsync(renamed.ResourceId, folder + "forged.docx", MinimalDocx.Create(), TestActor.Value));
        Assert.Equal(reserved.ToArray(), (await provider.Documents.ExportDocumentAsync(state.ResourceId))!.PathClaims.ToArray());
        await service.RenameDocumentAsync(state.ResourceId, "a.docx", TestActor.Value);
        Assert.Equal(state.ResourceId, (await provider.Documents.FindAsync(new DocumentIdentity.Path(state.PathKey)))!.ResourceId);
        Assert.Null(await service.CreateAsync(folder + "b.docx", MinimalDocx.Create(), TestActor.Value));
        // Tombstones retain reservations even when a lifecycle provider moves its SQL name.
        var current = (await provider.Documents.FindAsync(new DocumentIdentity.Resource(state.ResourceId)))!;
        Assert.True((await provider.Documents.ChangeLifecycleAsync(new DocumentLifecycleCommand.Delete(
            current.ResourceId, current.LifecycleGeneration, current.StateVersion))).Applied);
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
        var before = (await provider.Documents.FindAsync(new DocumentIdentity.Resource(state.ResourceId)))!;
        await Assert.ThrowsAsync<StorageQuotaExceededException>(() => service.RenameDocumentAsync(state.ResourceId, "d.docx", TestActor.Value).AsTask());
        Assert.Equal(before, await provider.Documents.FindAsync(new DocumentIdentity.Resource(state.ResourceId)));
        Assert.Null(await provider.Documents.FindAsync(new DocumentIdentity.Path("/CAP/D.DOCX")));
        Assert.Equal(2, before.Counters.RetiredPaths);
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
            var atB = (await provider.Documents.FindAsync(new DocumentIdentity.Path((folder + "b.docx").ToUpperInvariant())))!;
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
        catch (DocumentOperationLockException e) when (e.ErrorCode == "FileAlreadyExists") { return false; }
    }

    private static StorageProvider Memory(StorageLimits? limits = null) =>
        StorageTestDocuments.Memory(limits: limits);
}
