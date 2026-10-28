using System.Collections.Immutable;
using System.Data.Common;
using System.Text.Json;
using CellBridge.AspNetCore;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.SqlServer;
using CellBridge.TestFixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CellBridge.SqlServer.Tests;

[Collection("SQL Server")]
public sealed class SqlServerLifecycleRecoveryTests(SqlServerFixture fixture)
{
    [Fact]
    public async Task RenameDeletionRecreationAndPermanentExactPathReservations()
    {
        var database = await SqlServerDocuments.CreateAsync(fixture);
        var provider = database.Provider(); var service = new CellBridgeDocumentService(provider);
        var original = (await service.CreateAsync("/rename.docx", MinimalDocx.Create(), SqlServerDocuments.Actor))!;
        var other = (await service.CreateAsync("/other.docx", MinimalDocx.Create(), SqlServerDocuments.Actor))!;
        var state = (SqlServerStateStore)provider.State;
        const string renamed = "/漢字/e\u0301.docx";
        await state.RenameAsync(original.ResourceId, (current, _) => new StateTransition<bool>(current with { Path = renamed, PathKey = renamed.ToUpperInvariant() }, true));
        var current = (await state.FindByResourceIdAsync(original.ResourceId))!;
        Assert.Null(await state.FindByPathKeyAsync(original.PathKey));
        Assert.Null(await state.FindByPathKeyAsync(current.PathKey + " "));
        Assert.Null(await state.FindByResourceIdAsync(Guid.NewGuid()));
        Assert.Equal(current.ResourceId, (await state.FindByPathKeyAsync(current.PathKey))!.ResourceId);
        Assert.False(await state.TryCreateAsync(original with { ResourceId = Guid.NewGuid() }));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await state.RenameAsync(current.ResourceId,
            (row, _) => new StateTransition<bool>(row with { Path = other.Path, PathKey = other.PathKey }, true)));
        Assert.False(await state.TryDeleteAsync(current.ResourceId, current.LifecycleGeneration, current.StateVersion + 1));
        Assert.True(await state.TryDeleteAsync(current.ResourceId, current.LifecycleGeneration, current.StateVersion));
        Assert.True(await state.TryDeleteAsync(current.ResourceId, current.LifecycleGeneration, current.StateVersion));
        Assert.Null(await state.FindByResourceIdAsync(current.ResourceId));
        var retired = (await state.FindLifecycleAsync(current.ResourceId))!;
        var replacement = retired with { ResourceId = Guid.NewGuid(), LifecycleGeneration = retired.LifecycleGeneration + 1,
            StateVersion = 0, IsDeleted = false, DeletedFromStateVersion = null, ReplacedBy = null, Revisions = [],
            Editors = [], Coordination = CoordinationState.Empty, Receipts = [], RetiredPathKeys = [],
            RestoreReceipts = [], WholeFileSaveReceipts = [], Publication = null };
        Assert.True(await state.TryRecreateAsync(retired.ResourceId, retired.LifecycleGeneration, retired.StateVersion, replacement));
        Assert.True(await state.TryRecreateAsync(retired.ResourceId, retired.LifecycleGeneration, retired.StateVersion, replacement));
        Assert.False(await state.TryRecreateAsync(retired.ResourceId, retired.LifecycleGeneration, retired.StateVersion,
            replacement with { ResourceId = Guid.NewGuid() }));
        Assert.Equal(replacement.ResourceId, (await state.FindByPathKeyAsync(retired.PathKey))!.ResourceId);
        Assert.Equal(replacement.ResourceId, (await state.FindLifecycleAsync(retired.ResourceId))!.ReplacedBy);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await state.RenameAsync(replacement.ResourceId,
            (row, _) => new StateTransition<bool>(row with { Path = original.Path, PathKey = original.PathKey }, true)));
        Assert.Equal(await provider.Content.ReadVerifiedAsync(current.Content), await provider.Content.ReadVerifiedAsync(replacement.Content));
        await using var admin = new SqlServerDocumentDbContext(database.Options(admin: true));
        await admin.Reservations.Where(row => row.ResourceId == retired.ResourceId).ExecuteDeleteAsync();
        await Assert.ThrowsAsync<StorageCorruptionException>(async () => await state.TryCreateAsync(original with { ResourceId = Guid.NewGuid() }));
        await database.AssertAccountingAsync();
    }

    [Fact]
    public async Task SnapshotRetentionLimitsAndFailedPublicationKeepEveryIndexAndExactLedger()
    {
        var database = await SqlServerDocuments.CreateAsync(fixture, new() { MaxRetainedStateSnapshots = 2, MaxHistoryRevisions = 3 });
        var provider = database.Provider(); var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/limits.docx", MinimalDocx.Create(), SqlServerDocuments.Actor))!;
        for (var index = 0; index < 2; index++)
            state = (await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents,
                SqlServerDocumentTests.Proposal(state, MinimalDocx.Create("revision " + index)), new Dictionary<string, string>(), SqlServerDocuments.Actor)).State;
        await using var admin = new SqlServerDocumentDbContext(database.Options(admin: true));
        Assert.Equal(2, await admin.Snapshots.CountAsync());
        Assert.Equal(3, await admin.Revisions.CountAsync());
        var before = JsonSerializer.Serialize(await ((IProviderRecoveryStore)provider.State).CaptureRecoveryAsync());
        var usage = await admin.Usage.SingleAsync();
        var bytes = usage.ChargedBytes - await admin.UsageBuckets.SumAsync(row => row.AvailableBytes);
        await Assert.ThrowsAsync<StorageQuotaExceededException>(async () => await provider.State.TransitionAsync(state.ResourceId,
            (row, _) => new StateTransition<bool>(row with { Revisions = row.Revisions.Add(row.Revisions[^1] with { RevisionNumber = 4 }) }, true)));
        var restricted = new SqlServerStateStore(database.Options(), database.Limits with
            { MaxDecodedGraphMembershipBytes = StorageLimits.GetDecodedGraphMembershipBytes(state) });
        await Assert.ThrowsAsync<StorageQuotaExceededException>(async () => await restricted.TransitionAsync(state.ResourceId,
            (row, _) => new StateTransition<bool>(row with { Partitions = row.Partitions.SetItem(0, row.Partitions[0] with
                { Elements = row.Partitions[0].Elements.Add(row.Partitions[0].Elements[0]) }) }, true)));
        usage.MaxStoredBytes = bytes;
        await admin.SaveChangesAsync();
        await Assert.ThrowsAsync<StorageQuotaExceededException>(async () => await provider.State.TransitionAsync(state.ResourceId,
            (row, _) => new StateTransition<bool>(row with { Coordination = row.Coordination with { SchemaId = new string('x', 8192) } }, true)));
        Assert.Equal(before, JsonSerializer.Serialize(await ((IProviderRecoveryStore)provider.State).CaptureRecoveryAsync()));
        await database.AssertAccountingAsync();
        Assert.Equal(bytes, (await admin.Usage.AsNoTracking().SingleAsync()).ChargedBytes -
            await admin.UsageBuckets.SumAsync(row => row.AvailableBytes));
        usage.MaxStoredBytes = database.Limits.MaxStoredBytes;
        await admin.SaveChangesAsync();
        await provider.State.TransitionAsync(state.ResourceId, (row, _) => new StateTransition<bool>(row with { ModifiedUtc = row.ModifiedUtc.AddSeconds(1) }, true));
        Assert.Equal(2, await admin.Snapshots.CountAsync());
        await database.AssertAccountingAsync();
    }

    [Fact]
    public async Task SharedDocumentQuotaAdmitsOneHostAndDurabilityConfigurationIsExplicit()
    {
        var database = await SqlServerDocuments.CreateAsync(fixture, new() { MaxDocuments = 1 });
        var providers = new[] { database.Provider(), database.Provider() };
        var created = await Task.WhenAll(Enumerable.Range(0, 6).Select(async index =>
        {
            try { return await new CellBridgeDocumentService(providers[index % 2]).CreateAsync("/quota-" + index + ".docx", MinimalDocx.Create(), SqlServerDocuments.Actor) is not null; }
            catch (StorageQuotaExceededException) { return false; }
        }));
        Assert.Single(created, accepted => accepted);
        Assert.Single(await providers[0].State.ListAsync(0, 100));
        await database.AssertAccountingAsync();
        await using var admin = new SqlServerDocumentDbContext(database.Options(admin: true));
        await admin.Database.ExecuteSqlRawAsync("ALTER DATABASE CURRENT SET DELAYED_DURABILITY=FORCED");
        try { await Assert.ThrowsAsync<StorageUnavailableException>(async () => await providers[0].State.CheckHealthAsync()); }
        finally { await admin.Database.ExecuteSqlRawAsync("ALTER DATABASE CURRENT SET DELAYED_DURABILITY=DISABLED"); }
        await providers[0].State.CheckHealthAsync();
    }

    [Fact]
    public async Task RecoveryPublishesAllSnapshotsIndexesAndReceiptAtomicallyAndReplays()
    {
        var source = await SqlServerDocuments.CreateAsync(fixture, new() { MaxRetainedStateSnapshots = 3 });
        var original = source.Provider(); var service = new CellBridgeDocumentService(original);
        var state = (await service.CreateAsync("/recover.docx", MinimalDocx.Create(), SqlServerDocuments.Actor))!;
        state = (await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents,
            SqlServerDocumentTests.Proposal(state, MinimalDocx.Create("saved source")), new Dictionary<string, string>(), SqlServerDocuments.Actor)).State;
        var snapshot = await ((IProviderRecoveryStore)original.State).CaptureRecoveryAsync();
        var target = await SqlServerDocuments.CreateAsync(fixture);
        var destination = target.Provider();
        foreach (var handle in ContentStoreReader.UniqueHandles(snapshot.Snapshots.SelectMany(StorageReferences.Handles)))
        {
            await using var stream = await original.Content.OpenReadAsync(handle);
            Assert.Equal(handle, await destination.Content.WriteAsync(stream));
        }
        var receipt = new RecoveryReceipt(Guid.NewGuid(), new string('a', 64), snapshot.Heads.Length, snapshot.Snapshots.Length);
        var interrupted = (IProviderRecoveryStore)target.Provider(new FailSnapshots()).State;
        var failure = await Assert.ThrowsAsync<DbUpdateException>(async () => await interrupted.ImportRecoveryAsync(snapshot, receipt));
        Assert.IsType<IOException>(failure.InnerException);
        Assert.Empty(await destination.State.ListAsync(0, 100));
        Assert.Null(await ((IProviderRecoveryStore)destination.State).FindRecoveryReceiptAsync(receipt.OperationId));
        await target.AssertAccountingAsync();
        var recovered = (IProviderRecoveryStore)destination.State;
        Assert.Equal(receipt, await recovered.ImportRecoveryAsync(snapshot, receipt));
        Assert.Equal(receipt, await ((IProviderRecoveryStore)target.Provider().State).ImportRecoveryAsync(snapshot, receipt));
        await Assert.ThrowsAsync<RecoveryConflictException>(async () => await recovered.ImportRecoveryAsync(snapshot, receipt with { Fingerprint = new string('b', 64) }));
        var current = (await destination.State.FindByResourceIdAsync(state.ResourceId))!;
        Assert.Equal(DocumentStateJson.Serialize(state), DocumentStateJson.Serialize(current));
        Assert.Equal(await original.Content.ReadVerifiedAsync(state.Content), await destination.Content.ReadVerifiedAsync(current.Content));
        Assert.Equal(state.Revisions.Length, (await ((IDocumentReadStore)destination.State).ReadRevisionSummariesAsync(state.ResourceId, 0, 100))!.Revisions.Length);
        Assert.Single((await recovered.CaptureRecoveryAsync()).Receipts);
        await target.AssertAccountingAsync();
        await Assert.ThrowsAsync<RecoveryConflictException>(async () => await recovered.ImportRecoveryAsync(snapshot, receipt with { OperationId = Guid.NewGuid() }));
        Assert.Equal(current.ResourceId, (await destination.State.FindByPathKeyAsync(current.PathKey))!.ResourceId);
    }

    private sealed class FailSnapshots : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("[snapshots]", StringComparison.Ordinal) && command.CommandText.Contains("INSERT", StringComparison.Ordinal))
                throw new IOException("Injected snapshot publication failure.");
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
