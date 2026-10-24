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
public sealed class SqlServerMaintenanceTests(SqlServerFixture fixture)
{
    [Fact]
    public async Task DryRunAndApplyPreserveRetainedSnapshotsHistoryGraphsAndReceiptsWithExactAccounting()
    {
        var database = await SqlServerDocuments.CreateAsync(fixture, new() { MaxRetainedStateSnapshots = 2 });
        var provider = database.Provider();
        var state = (await new CellBridgeDocumentService(provider).CreateAsync("/retained.docx", MinimalDocx.Create(), SqlServerDocuments.Actor))!;
        var original = state.Content;
        state = await Save(provider, state, "retained revision");
        var retainedResponse = Assert.Single(state.Receipts).Response!;
        await provider.State.TransitionAsync(state.ResourceId, (row, _) => new StateTransition<bool>(row with { Receipts = [] }, true));
        var snapshot = await ((IProviderRecoveryStore)provider.State).CaptureRecoveryAsync();
        Assert.Equal(2, snapshot.Snapshots.Length);
        Assert.DoesNotContain(retainedResponse, Roots(snapshot.Snapshots.OrderBy(row => row.StateVersion).Last()));
        var expected = await ReadRoots(provider.Content, snapshot);
        Assert.Contains(original, expected.Keys);
        Assert.Contains(retainedResponse, expected.Keys);
        var orphan = await provider.Content.WriteAsync(new MemoryStream([91, 92, 93]));
        var emptyContent = await provider.Content.WriteAsync(new MemoryStream());
        Assert.Contains(emptyContent, expected.Keys);
        var maintenance = new StorageMaintenance(database.Options(admin: true), database.Limits);
        await Assert.ThrowsAsync<InvalidOperationException>(() => maintenance.CollectOrphansAsync(apply: true));
        var dry = await maintenance.CollectOrphansAsync();
        Assert.False(dry.Applied);
        Assert.Equal(1, dry.OrphanObjects);
        Assert.Equal(3, dry.OrphanBytes);
        Assert.Equal(expected.Count, dry.ReferencedObjects);
        Assert.Equal(2, dry.Snapshots);
        Assert.Equal(new byte[] { 91, 92, 93 }, await provider.Content.ReadVerifiedAsync(orphan));
        Assert.Empty(await provider.Content.ReadVerifiedAsync(emptyContent));

        var restricted = new StorageMaintenance(database.Options());
        Assert.Equal(dry, await restricted.CollectOrphansAsync());
        await Assert.ThrowsAsync<StorageUnavailableException>(() => restricted.CollectOrphansAsync(apply: true, quiescent: true));
        Assert.Equal(dry, await maintenance.CollectOrphansAsync());
        var applied = await maintenance.CollectOrphansAsync(apply: true, quiescent: true);
        Assert.True(applied.Applied);
        Assert.Equal(dry.StoredBytes - 3, applied.StoredBytes);
        Assert.Equal(dry with { StoredBytes = applied.StoredBytes, Applied = true }, applied);
        await Assert.ThrowsAsync<StorageCorruptionException>(async () => await provider.Content.OpenReadAsync(orphan));
        Assert.Empty(await provider.Content.ReadVerifiedAsync(emptyContent));
        Assert.Equal(JsonSerializer.Serialize(snapshot), JsonSerializer.Serialize(await ((IProviderRecoveryStore)database.Provider().State).CaptureRecoveryAsync()));
        foreach (var pair in expected) Assert.Equal(pair.Value, await database.Provider().Content.ReadVerifiedAsync(pair.Key));
        var empty = await maintenance.CollectOrphansAsync();
        Assert.Equal(0, empty.OrphanObjects);
        Assert.Equal(applied.StoredBytes, empty.StoredBytes);
        await database.AssertAccountingAsync();
    }

    [Fact]
    public async Task UnreferencedEmptyObjectIsReclaimedWithNoByteLedgerDelta()
    {
        var database = await SqlServerDocuments.CreateAsync(fixture);
        var content = database.Provider().Content;
        var empty = await content.WriteAsync(new MemoryStream());
        var maintenance = new StorageMaintenance(database.Options(admin: true));
        var dry = await maintenance.CollectOrphansAsync();
        Assert.Equal(1, dry.OrphanObjects);
        Assert.Equal(0, dry.OrphanBytes);
        var applied = await maintenance.CollectOrphansAsync(true, true);
        Assert.Equal(dry with { Applied = true }, applied);
        await Assert.ThrowsAsync<StorageCorruptionException>(async () => await content.OpenReadAsync(empty));
        Assert.Equal(0, (await maintenance.CollectOrphansAsync()).OrphanObjects);
        await database.AssertAccountingAsync();
    }

    [Fact]
    public async Task CollectionPreservesDeletedRecreatedPendingDeliveryAndImportedRecoveryRoots()
    {
        var sourceDb = await SqlServerDocuments.CreateAsync(fixture, new() { MaxRetainedStateSnapshots = 1 });
        var source = sourceDb.Provider(); var store = (SqlServerStateStore)source.State;
        var retired = (await new CellBridgeDocumentService(source).CreateAsync("/retired.docx", MinimalDocx.Create("history"), SqlServerDocuments.Actor))!;
        retired = await Save(source, retired, "last publication");
        Assert.True(await store.TryDeleteAsync(retired.ResourceId, retired.LifecycleGeneration, retired.StateVersion));
        retired = (await store.FindLifecycleAsync(retired.ResourceId))!;
        var replacement = await new DocumentStore().Put(retired.Path, MinimalDocx.Create("new incarnation")).CaptureAsync(source.Content);
        replacement = RevisionHistory.Initialize(replacement with { LifecycleGeneration = retired.LifecycleGeneration + 1, Security = retired.Security });
        Assert.True(await store.TryRecreateAsync(retired.ResourceId, retired.LifecycleGeneration, retired.StateVersion, replacement));
        var pending = (await new CellBridgeDocumentService(source).CreateAsync("/pending.docx", MinimalDocx.Create("pending baseline"), SqlServerDocuments.Actor))!;
        var publisher = new ExternalRevisionPublisher(source, new UnusedDestination());
        Assert.True(await publisher.BindAsync(pending.ResourceId, pending.LifecycleGeneration, pending.StateVersion, Guid.NewGuid(), "test-destination", "revision-0"));
        pending = (await source.State.FindByResourceIdAsync(pending.ResourceId))!;
        pending = await Save(source, pending, "first pending save");
        pending = await Save(source, pending, "second pending save");
        Assert.Equal(2, pending.Publication!.Pending.Length);
        var snapshot = await store.CaptureRecoveryAsync();
        Assert.All(snapshot.Snapshots.Where(row => row.ResourceId == retired.ResourceId), row => Assert.True(row.IsDeleted));
        var expected = await ReadRoots(source.Content, snapshot);
        var targetDb = await SqlServerDocuments.CreateAsync(fixture, sourceDb.Limits);
        var target = targetDb.Provider(); var recovery = (SqlServerStateStore)target.State;
        await SqlServerNamespaceTests.CopyContent(source, target, snapshot);
        var receipt = new RecoveryReceipt(Guid.NewGuid(), new string('a', 64), snapshot.Heads.Length, snapshot.Snapshots.Length);
        await recovery.ImportRecoveryAsync(snapshot, receipt);
        var before = JsonSerializer.Serialize(await recovery.CaptureRecoveryAsync());
        var orphan = await target.Content.WriteAsync(new MemoryStream([99]));
        var report = await new StorageMaintenance(targetDb.Options(admin: true)).CollectOrphansAsync(apply: true, quiescent: true);
        Assert.Equal(1, report.OrphanObjects);
        Assert.Equal(1, report.OrphanBytes);
        Assert.Equal(before, JsonSerializer.Serialize(await recovery.CaptureRecoveryAsync()));
        Assert.Equal(receipt, await recovery.FindRecoveryReceiptAsync(receipt.OperationId));
        Assert.Equal(receipt, await recovery.ImportRecoveryAsync(snapshot, receipt));
        Assert.Null(await target.State.FindByResourceIdAsync(retired.ResourceId));
        Assert.Equal(replacement.ResourceId, (await target.State.FindByPathKeyAsync(retired.PathKey))!.ResourceId);
        Assert.Equal(2, (await target.State.FindByResourceIdAsync(pending.ResourceId))!.Publication!.Pending.Length);
        foreach (var pair in expected) Assert.Equal(pair.Value, await targetDb.Provider().Content.ReadVerifiedAsync(pair.Key));
        await Assert.ThrowsAsync<StorageCorruptionException>(async () => await target.Content.OpenReadAsync(orphan));
        await targetDb.AssertAccountingAsync();
    }

    [Theory]
    [InlineData("retained-json")]
    [InlineData("current-snapshot")]
    [InlineData("reference")]
    [InlineData("reference-metadata")]
    [InlineData("ledger")]
    public async Task CorruptInventoryRefusesCollectionBeforeDeletingAnything(string fault)
    {
        var database = await SqlServerDocuments.CreateAsync(fixture);
        var provider = database.Provider();
        var state = (await new CellBridgeDocumentService(provider).CreateAsync("/corrupt.docx", MinimalDocx.Create(), SqlServerDocuments.Actor))!;
        await provider.State.TransitionAsync(state.ResourceId, (row, now) => new StateTransition<bool>(row with { ModifiedUtc = now }, true));
        var orphan = await provider.Content.WriteAsync(new MemoryStream([17, 18]));
        await using var admin = new SqlServerDocumentDbContext(database.Options(admin: true));
        switch (fault)
        {
            case "retained-json": await admin.Database.ExecuteSqlRawAsync("UPDATE cellbridge_documents.snapshots SET Data=0xFF WHERE StateVersion=0"); break;
            case "current-snapshot": await admin.Snapshots.Where(row => row.StateVersion == 1).ExecuteDeleteAsync(); break;
            case "reference":
                await admin.Chunks.Where(row => row.ObjectKey == state.Content.Key).ExecuteDeleteAsync();
                await admin.Objects.Where(row => row.ObjectKey == state.Content.Key).ExecuteDeleteAsync();
                break;
            case "reference-metadata": await admin.Objects.Where(row => row.ObjectKey == state.Content.Key)
                .ExecuteUpdateAsync(set => set.SetProperty(row => row.Length, row => row.Length + 1)); break;
            case "ledger": await admin.Usage.ExecuteUpdateAsync(set => set.SetProperty(row => row.StoredBytes, row => row.StoredBytes + 1)); break;
        }
        var keys = await admin.Objects.OrderBy(row => row.ObjectKey).Select(row => row.ObjectKey).ToArrayAsync();
        var usage = (await admin.Usage.AsNoTracking().SingleAsync()).StoredBytes;
        var maintenance = new StorageMaintenance(database.Options(admin: true));
        await Assert.ThrowsAsync<StorageCorruptionException>(() => maintenance.CollectOrphansAsync());
        await Assert.ThrowsAsync<StorageCorruptionException>(() => maintenance.CollectOrphansAsync(apply: true, quiescent: true));
        Assert.Equal(keys, await admin.Objects.OrderBy(row => row.ObjectKey).Select(row => row.ObjectKey).ToArrayAsync());
        Assert.Equal(usage, (await admin.Usage.AsNoTracking().SingleAsync()).StoredBytes);
        Assert.Equal(new byte[] { 17, 18 }, await provider.Content.ReadVerifiedAsync(orphan));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedSweepRollsBackDeletedChunksAndLedger(bool cancel)
    {
        var database = await SqlServerDocuments.CreateAsync(fixture);
        var provider = database.Provider();
        var bytes = Enumerable.Repeat((byte)73, 65536 + 19).ToArray();
        var orphan = await provider.Content.WriteAsync(new MemoryStream(bytes));
        var before = await new StorageMaintenance(database.Options(admin: true)).CollectOrphansAsync();
        using var cancellation = new CancellationTokenSource();
        var fault = new BeforeObjectDelete(() =>
        {
            if (!cancel) throw new IOException("Injected collection failure after deleting chunks.");
            cancellation.Cancel(); cancellation.Token.ThrowIfCancellationRequested();
        });
        var maintenance = new StorageMaintenance(database.Options(true, fault));
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => maintenance.CollectOrphansAsync(true, true, cancellation.Token));
        else await Assert.ThrowsAsync<IOException>(() => maintenance.CollectOrphansAsync(true, true));
        Assert.True(fault.Triggered);
        Assert.Equal(bytes, await provider.Content.ReadVerifiedAsync(orphan));
        Assert.Equal(before, await new StorageMaintenance(database.Options(admin: true)).CollectOrphansAsync());
        await database.AssertAccountingAsync();
    }

    [Fact]
    public async Task CollectionSerializesWriterAdmissionStatePublicationAndRecoveryAndReleasesQuota()
    {
        var database = await SqlServerDocuments.CreateAsync(fixture);
        var provider = database.Provider();
        var state = (await new CellBridgeDocumentService(provider).CreateAsync("/quota.docx", MinimalDocx.Create(), SqlServerDocuments.Actor))!;
        await provider.Content.WriteAsync(new MemoryStream(Enumerable.Repeat((byte)77, 200).ToArray()));
        await using (var admin = new SqlServerDocumentDbContext(database.Options(admin: true)))
            await admin.Usage.ExecuteUpdateAsync(set => set.SetProperty(row => row.MaxStoredBytes, row => row.StoredBytes + 50));
        var pause = new PauseInventory();
        var collecting = new StorageMaintenance(database.Options(true, pause)).CollectOrphansAsync(true, true);
        await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var observed = new ObservePublishers(5);
        var writers = Enumerable.Range(1, 3).Select(async value =>
        {
            try
            {
                await database.Provider(observed).Content.WriteAsync(new MemoryStream(Enumerable.Repeat((byte)value, 100).ToArray()));
                return true;
            }
            catch (StorageQuotaExceededException) { return false; }
        }).ToArray();
        var invoked = 0;
        var mutation = database.Provider(observed).State.TransitionAsync(state.ResourceId, (row, _) =>
        { Interlocked.Increment(ref invoked); return new StateTransition<bool>(null, true); }).AsTask();
        var capture = ((IProviderRecoveryStore)database.Provider(observed).State).CaptureRecoveryAsync().AsTask();
        try
        {
            await observed.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.All(writers, task => Assert.False(task.IsCompleted));
            Assert.False(mutation.IsCompleted);
            Assert.False(capture.IsCompleted);
            Assert.Equal(0, Volatile.Read(ref invoked));
        }
        finally
        {
            pause.Release.TrySetResult();
            await collecting;
            await Task.WhenAll(writers);
            await mutation;
            await capture;
        }
        var report = await collecting;
        Assert.Equal(200, report.OrphanBytes);
        Assert.Equal(2, (await Task.WhenAll(writers)).Count(value => value));
        Assert.True(await mutation);
        Assert.Single((await capture).Heads);
        Assert.Equal(1, invoked);
        await database.AssertAccountingAsync();
    }

    [Fact]
    public async Task CancellationAfterCollectionCommitReturnsCompletedReport()
    {
        var database = await SqlServerDocuments.CreateAsync(fixture);
        await database.Provider().Content.WriteAsync(new MemoryStream([1, 4, 9]));
        using var cancellation = new CancellationTokenSource();
        var maintenance = new StorageMaintenance(database.Options(true, new CancelCommitted(cancellation)));
        var report = await maintenance.CollectOrphansAsync(true, true, cancellation.Token);
        Assert.True(cancellation.IsCancellationRequested);
        Assert.True(report.Applied);
        Assert.Equal(3, report.OrphanBytes);
        Assert.Equal(0, report.StoredBytes);
        await database.AssertAccountingAsync();
    }

    private static async Task<DocumentState> Save(StorageProvider provider, DocumentState state, string text) =>
        (await new CellBridgeDocumentService(provider).ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents,
            SqlServerDocumentTests.Proposal(state, MinimalDocx.Create(text)), new Dictionary<string, string>(), SqlServerDocuments.Actor)).State;

    // Independent root enumeration checks the collector's use of StorageReferences.
    private static IEnumerable<ContentHandle> Roots(DocumentState state) => new[] { state.Content }
        .Concat(state.Partitions.SelectMany(p => new[] { p.Content }.Concat(p.Elements.Select(e => e.Payload))))
        .Concat(state.Revisions.SelectMany(r => new[] { r.Content }.Concat(r.Partitions.SelectMany(p => new[] { p.Content }.Concat(p.Elements.Select(e => e.Payload))))))
        .Concat(state.Receipts.Where(r => r.Response is not null).Select(r => r.Response!))
        .Concat(state.Publication?.Pending.Select(r => r.Content) ?? []);

    private static async Task<Dictionary<ContentHandle, byte[]>> ReadRoots(IContentStore content, ProviderSnapshot snapshot)
    {
        var result = new Dictionary<ContentHandle, byte[]>();
        foreach (var handle in snapshot.Snapshots.SelectMany(Roots).Distinct()) result.Add(handle, await content.ReadVerifiedAsync(handle));
        return result;
    }

    private sealed class BeforeObjectDelete(Action fail) : DbCommandInterceptor
    {
        public bool Triggered { get; private set; }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("DELETE", StringComparison.Ordinal) && command.CommandText.Contains("[objects]", StringComparison.Ordinal))
            { Triggered = true; fail(); }
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private sealed class PauseInventory : DbCommandInterceptor
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("[objects]", StringComparison.Ordinal))
            { Entered.TrySetResult(); await Release.Task.WaitAsync(cancellationToken); }
            return result;
        }
    }

    private sealed class ObservePublishers(int expected) : DbCommandInterceptor
    {
        private int entered;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("sp_getapplock", StringComparison.Ordinal) &&
                command.Parameters.Cast<DbParameter>().Any(parameter => Equals(parameter.Value, SqlServerDocumentDatabase.PublicationGate)) &&
                Interlocked.Increment(ref entered) == expected) Entered.TrySetResult();
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private sealed class CancelCommitted(CancellationTokenSource cancellation) : DbTransactionInterceptor
    {
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        { cancellation.Cancel(); return Task.CompletedTask; }
    }

    private sealed class UnusedDestination : IExternalRevisionDestination
    {
        public ValueTask<ExternalDeliveryResult> CompareExchangeAsync(ExternalDeliveryRequest request, Stream verifiedContent,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("This test must not publish externally.");
    }
}
