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
public sealed class SqlServerMaintenanceTests(SqlServerFixture fixture)
{
    [Fact]
    public async Task DryRunAndApplyPreserveHistoryCanonicalGraphsAndReceipts()
    {
        var database=await SqlServerDocuments.CreateAsync(fixture); var provider=database.Provider();
        var originalBytes=MinimalDocx.Create();
        var state=(await new CellBridgeDocumentService(provider).CreateAsync("/retained.docx",originalBytes,SqlServerDocuments.Actor))!;
        var original=state.Package;
        state=await SqlServerDocuments.SaveAsync(provider,state,"retained revision");
        var snapshot=await provider.Documents.CaptureRecoveryAsync();
        Assert.Single(snapshot.Snapshots); Assert.Single(snapshot.Snapshots[0].Receipts);
        Assert.Equal(2,snapshot.Snapshots[0].ApplicationRevisions.Length);
        var expected=await ReadRoots(provider.Content,snapshot);
        var orphan=await provider.Content.WriteAsync(new MemoryStream([91,92,93]));
        var maintenance=new StorageMaintenance(database.Options(admin:true),database.Limits);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>maintenance.CollectOrphansAsync(apply:true));
        var dry=await maintenance.CollectOrphansAsync();
        Assert.False(dry.Applied); Assert.Equal(1,dry.OrphanObjects); Assert.Equal(3,dry.OrphanBytes);
        Assert.Equal(expected.Count,dry.ReferencedObjects); Assert.Equal(0,dry.Snapshots);
        Assert.Equal(new byte[] {91,92,93},await provider.Content.ReadVerifiedAsync(orphan));
        var restricted=new StorageMaintenance(database.Options());
        Assert.Equal(dry,await restricted.CollectOrphansAsync());
        await Assert.ThrowsAsync<StorageUnavailableException>(()=>restricted.CollectOrphansAsync(true,true));
        Assert.Equal(dry,await maintenance.CollectOrphansAsync());
        var applied=await maintenance.CollectOrphansAsync(true,true);
        Assert.Equal(dry with { StoredBytes=dry.StoredBytes-3,Applied=true },applied);
        await Assert.ThrowsAsync<StorageCorruptionException>(()=>provider.Content.OpenReadAsync(orphan).AsTask());
        Assert.Equal(SnapshotJson(snapshot),SnapshotJson(await database.Provider().Documents.CaptureRecoveryAsync()));
        foreach(var pair in expected) Assert.Equal(pair.Value,await database.Provider().Content.ReadVerifiedAsync(pair.Key));
        Assert.Equal(originalBytes,await SqlServerDocuments.PackageAsync(provider,original));
        Assert.Equal(0,(await maintenance.CollectOrphansAsync()).OrphanObjects);
    }

    [Fact]
    public async Task UnreferencedEmptyObjectIsReclaimed()
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
        await database.AssertInventoryAsync();
    }

    [Fact]
    public async Task CollectionPreservesDeletedRecreatedPendingDeliveryAndImportedRecoveryRoots()
    {
        var sourceDb=await SqlServerDocuments.CreateAsync(fixture); var source=sourceDb.Provider(); var store=source.Documents;
        var retired=(await new CellBridgeDocumentService(source).CreateAsync("/retired.docx",MinimalDocx.Create("history"),SqlServerDocuments.Actor))!;
        retired=await SqlServerDocuments.SaveAsync(source,retired,"last publication");
        retired=await SqlServerDocuments.DeleteAsync(store,retired.ResourceId);
        var replacement=await SqlServerDocuments.ReplacementAsync(source,retired);
        Assert.True((await store.ChangeLifecycleAsync(new DocumentLifecycleCommand.Recreate(retired.ResourceId,
            retired.LifecycleGeneration,retired.StateVersion,replacement))).Applied);
        var pending=(await new CellBridgeDocumentService(source).CreateAsync("/pending.docx",MinimalDocx.Create("pending baseline"),SqlServerDocuments.Actor))!;
        var publisher=new ExternalRevisionPublisher(source,new UnusedDestination());
        Assert.True(await publisher.BindAsync(pending.ResourceId,pending.LifecycleGeneration,pending.StateVersion,Guid.NewGuid(),"destination","revision-0"));
        pending=(await store.FindAsync(new DocumentIdentity.Resource(pending.ResourceId)))!;
        pending=await SqlServerDocuments.SaveAsync(source,pending,"first pending save");
        pending=await SqlServerDocuments.SaveAsync(source,pending,"second pending save");
        Assert.Equal(2,pending.Counters.PendingDeliveries);
        var snapshot=await store.CaptureRecoveryAsync();
        Assert.True(snapshot.Snapshots.Single(row=>row.Current.Head.ResourceId==retired.ResourceId).Current.Head.IsDeleted);
        var expected=await ReadRoots(source.Content,snapshot);
        var targetDb=await SqlServerDocuments.CreateAsync(fixture); var target=targetDb.Provider(); var recovery=target.Documents;
        await SqlServerNamespaceTests.CopyContent(source,target,snapshot);
        var receipt=new RecoveryReceipt(Guid.NewGuid(),new string('a',64),snapshot.Snapshots.Length,snapshot.Snapshots.Length);
        await recovery.ImportRecoveryAsync(snapshot,receipt);
        var before=SnapshotJson(await recovery.CaptureRecoveryAsync());
        var orphan=await target.Content.WriteAsync(new MemoryStream([99]));
        var report=await new StorageMaintenance(targetDb.Options(admin:true)).CollectOrphansAsync(true,true);
        Assert.Equal(1,report.OrphanObjects); Assert.Equal(1,report.OrphanBytes);
        Assert.Equal(before,SnapshotJson(await recovery.CaptureRecoveryAsync()));
        Assert.Equal(receipt,await recovery.FindRecoveryReceiptAsync(receipt.OperationId));
        Assert.Equal(receipt,await recovery.ImportRecoveryAsync(snapshot,receipt));
        Assert.Null(await recovery.FindAsync(new DocumentIdentity.Resource(retired.ResourceId)));
        Assert.Equal(replacement.Head.ResourceId,(await recovery.FindAsync(new DocumentIdentity.Path(retired.PathKey)))!.ResourceId);
        Assert.Equal(2,(await recovery.FindAsync(new DocumentIdentity.Resource(pending.ResourceId)))!.Counters.PendingDeliveries);
        foreach(var pair in expected) Assert.Equal(pair.Value,await target.Content.ReadVerifiedAsync(pair.Key));
        await Assert.ThrowsAsync<StorageCorruptionException>(()=>target.Content.OpenReadAsync(orphan).AsTask());
    }

    [Theory]
    [InlineData("history-json")]
    [InlineData("current-head")]
    [InlineData("reference")]
    [InlineData("reference-metadata")]
    [InlineData("reference-row")]
    public async Task CorruptInventoryRefusesCollectionBeforeDeletingAnything(string fault)
    {
        var database=await SqlServerDocuments.CreateAsync(fixture); var provider=database.Provider();
        var state=(await new CellBridgeDocumentService(provider).CreateAsync("/corrupt.docx",MinimalDocx.Create(),SqlServerDocuments.Actor))!;
        var snapshot=(await provider.Documents.ExportDocumentAsync(state.ResourceId))!;
        var handle=snapshot.Elements[0].Element.Payload;
        var orphan=await provider.Content.WriteAsync(new MemoryStream([17,18]));
        await using var admin=new SqlServerDocumentDbContext(database.Options(admin:true));
        switch(fault)
        {
            case "history-json": await admin.Database.ExecuteSqlRawAsync("UPDATE cellbridge_documents.repository_revisions SET Json=0xFF"); break;
            case "current-head": await admin.Database.ExecuteSqlRawAsync("UPDATE cellbridge_documents.repository_heads SET Json=0xFF"); break;
            case "reference":
                await admin.Chunks.Where(row=>row.ObjectKey==handle.Key).ExecuteDeleteAsync();
                await admin.Objects.Where(row=>row.ObjectKey==handle.Key).ExecuteDeleteAsync(); break;
            case "reference-metadata": await admin.Objects.Where(row=>row.ObjectKey==handle.Key).ExecuteUpdateAsync(set=>set.SetProperty(row=>row.Length,row=>row.Length+1)); break;
            case "reference-row": await admin.Database.ExecuteSqlAsync($"DELETE FROM cellbridge_documents.repository_references WHERE ObjectKey={handle.Key}"); break;
        }
        var keys=await admin.Objects.OrderBy(row=>row.ObjectKey).Select(row=>row.ObjectKey).ToArrayAsync();
        var maintenance=new StorageMaintenance(database.Options(admin:true));
        await Assert.ThrowsAsync<StorageCorruptionException>(()=>maintenance.CollectOrphansAsync());
        await Assert.ThrowsAsync<StorageCorruptionException>(()=>maintenance.CollectOrphansAsync(true,true));
        Assert.Equal(keys,await admin.Objects.OrderBy(row=>row.ObjectKey).Select(row=>row.ObjectKey).ToArrayAsync());
        Assert.Equal(new byte[] {17,18},await provider.Content.ReadVerifiedAsync(orphan));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedSweepRollsBackDeletedChunksAndObjects(bool cancel)
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
        await database.AssertInventoryAsync();
    }

    [Fact]
    public async Task ApplyingRequiresQuiescenceBeforeOpeningTheDatabase()
    {
        var database=await SqlServerDocuments.CreateAsync(fixture);
        var maintenance=new StorageMaintenance(database.Options(true,new RejectAnyCommand()));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>maintenance.CollectOrphansAsync(apply:true));
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
        await database.AssertInventoryAsync();
    }

    private static string SnapshotJson(ProviderSnapshot snapshot) => JsonSerializer.Serialize(snapshot with
        { Snapshots=snapshot.Snapshots.Select(row=>row with { Current=row.Current with {UtcNow=default} }).ToImmutableArray() });

    // Derive roots independently from the collector's maintained reference rows.
    private static IEnumerable<ContentHandle> Roots(NormalizedDocumentSnapshot snapshot) =>
        snapshot.Elements.Select(row=>row.Element.Payload).Concat(snapshot.Receipts.Where(row=>row.Response is not null).Select(row=>row.Response!));

    private static async Task<Dictionary<ContentHandle,byte[]>> ReadRoots(IContentStore content,ProviderSnapshot snapshot)
    {
        var result=new Dictionary<ContentHandle,byte[]>();
        foreach(var handle in snapshot.Snapshots.SelectMany(Roots).Distinct()) result.Add(handle,await content.ReadVerifiedAsync(handle));
        return result;
    }

    private sealed class RejectAnyCommand:DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,CommandEventData eventData,
            InterceptionResult<DbDataReader> result,CancellationToken cancellationToken=default) => throw new InvalidOperationException("Unexpected database command");
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
