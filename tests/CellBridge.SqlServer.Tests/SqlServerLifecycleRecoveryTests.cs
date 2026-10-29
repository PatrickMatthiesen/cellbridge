using System.Data.Common;
using System.Text.Json;
using CellBridge.AspNetCore;
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
        var database=await SqlServerDocuments.CreateAsync(fixture);
        var provider=database.Provider(); var service=new CellBridgeDocumentService(provider); var store=provider.Documents;
        var original=(await service.CreateAsync("/rename.docx",MinimalDocx.Create(),SqlServerDocuments.Actor))!;
        var other=(await service.CreateAsync("/other.docx",MinimalDocx.Create(),SqlServerDocuments.Actor))!;
        const string renamed="/漢字/e\u0301.docx";
        var moved=await SqlServerDocuments.RenameAsync(store,original,renamed);
        Assert.True(moved.Changed); var current=moved.Current;
        Assert.Null(await store.FindAsync(new DocumentIdentity.Path(original.PathKey)));
        Assert.Null(await store.FindAsync(new DocumentIdentity.Path(current.PathKey+" ")));
        Assert.Null(await store.FindAsync(new DocumentIdentity.Resource(Guid.NewGuid())));
        Assert.Equal(current.ResourceId,(await store.FindAsync(new DocumentIdentity.Path(current.PathKey)))!.ResourceId);
        Assert.Null(await service.CreateAsync(original.Path,MinimalDocx.Create(),SqlServerDocuments.Actor));
        Assert.False((await SqlServerDocuments.RenameAsync(store,current,other.Path)).Changed);
        Assert.False((await store.ChangeLifecycleAsync(new DocumentLifecycleCommand.Delete(current.ResourceId,current.LifecycleGeneration,current.StateVersion+1))).Applied);
        var retired=await SqlServerDocuments.DeleteAsync(store,current.ResourceId);
        Assert.True((await store.ChangeLifecycleAsync(new DocumentLifecycleCommand.Delete(current.ResourceId,current.LifecycleGeneration,current.StateVersion))).Applied);
        Assert.Null(await store.FindAsync(new DocumentIdentity.Resource(current.ResourceId)));
        var replacement=await SqlServerDocuments.ReplacementAsync(provider,retired);
        var recreate=new DocumentLifecycleCommand.Recreate(retired.ResourceId,retired.LifecycleGeneration,retired.StateVersion,replacement);
        Assert.True((await store.ChangeLifecycleAsync(recreate)).Applied);
        Assert.True((await store.ChangeLifecycleAsync(recreate)).Applied);
        Assert.False((await store.ChangeLifecycleAsync(recreate with { Replacement=await SqlServerDocuments.ReplacementAsync(provider,retired) })).Applied);
        Assert.Equal(replacement.Head.ResourceId,(await store.FindAsync(new DocumentIdentity.Path(retired.PathKey)))!.ResourceId);
        Assert.Equal(replacement.Head.ResourceId,(await store.FindAsync(new DocumentIdentity.Resource(retired.ResourceId,true)))!.ReplacedBy);
        Assert.False((await SqlServerDocuments.RenameAsync(store,(await store.FindAsync(new DocumentIdentity.Resource(replacement.Head.ResourceId)))!,original.Path)).Changed);
        await database.AssertInventoryAsync();
    }

    [Fact]
    public async Task HistoryLimitAndFailedPublicationKeepEveryNormalizedIndex()
    {
        var database=await SqlServerDocuments.CreateAsync(fixture,new() { MaxHistoryRevisions=3 });
        var provider=database.Provider(); var service=new CellBridgeDocumentService(provider);
        var state=(await service.CreateAsync("/limits.docx",MinimalDocx.Create(),SqlServerDocuments.Actor))!;
        for(var i=0;i<2;i++) state=await SqlServerDocuments.SaveAsync(provider,state,"revision "+i);
        Assert.Equal(3,(await provider.Documents.ReadRevisionsAsync(state.ResourceId,0,100))!.Revisions.Length);
        var before=SqlServerDocuments.SnapshotJson(await provider.Documents.CaptureRecoveryAsync());
        await SqlServerDocuments.RejectSaveAsync(provider,state,"rejected fourth revision");
        Assert.Equal(before,SqlServerDocuments.SnapshotJson(await provider.Documents.CaptureRecoveryAsync()));
        var bounded=database with { Limits=database.Limits with { MaxGraphElements=state.Counters.GraphElements } };
        await SqlServerDocuments.RejectSaveAsync(bounded.Provider(),state,"rejected graph growth");
        Assert.Equal(before,SqlServerDocuments.SnapshotJson(await provider.Documents.CaptureRecoveryAsync()));
        await database.AssertInventoryAsync();
    }

    [Fact]
    public async Task IndependentHostsCreateDocumentsAndDurabilityConfigurationIsExplicit()
    {
        var database=await SqlServerDocuments.CreateAsync(fixture); var providers=new[] {database.Provider(),database.Provider()};
        var created=await Task.WhenAll(Enumerable.Range(0,6).Select(async i=>await new CellBridgeDocumentService(providers[i%2])
            .CreateAsync("/independent-"+i+".docx",MinimalDocx.Create(),SqlServerDocuments.Actor)));
        Assert.All(created,Assert.NotNull);
        Assert.Equal(6,(await providers[0].Documents.ListAsync(0,100)).Count);
        await database.AssertInventoryAsync();
        await using var admin=new SqlServerDocumentDbContext(database.Options(admin:true));
        await admin.Database.ExecuteSqlRawAsync("ALTER DATABASE CURRENT SET DELAYED_DURABILITY=FORCED");
        try { await Assert.ThrowsAsync<StorageUnavailableException>(()=>providers[0].Documents.CheckHealthAsync().AsTask()); }
        finally { await admin.Database.ExecuteSqlRawAsync("ALTER DATABASE CURRENT SET DELAYED_DURABILITY=DISABLED"); }
        await providers[0].Documents.CheckHealthAsync();
    }

    [Fact]
    public async Task RecoveryPublishesNormalizedRecordsAndReceiptAtomicallyAndReplays()
    {
        var source=await SqlServerDocuments.CreateAsync(fixture); var original=source.Provider();
        var state=(await new CellBridgeDocumentService(original).CreateAsync("/recover.docx",MinimalDocx.Create(),SqlServerDocuments.Actor))!;
        state=await SqlServerDocuments.SaveAsync(original,state,"saved source");
        var snapshot=await original.Documents.CaptureRecoveryAsync();
        var target=await SqlServerDocuments.CreateAsync(fixture); var destination=target.Provider();
        await SqlServerNamespaceTests.CopyContent(original,destination,snapshot);
        var receipt=new RecoveryReceipt(Guid.NewGuid(),new string('a',64),snapshot.Heads.Length,snapshot.Snapshots.Length);
        await Assert.ThrowsAnyAsync<Exception>(()=>target.Provider(new FailPublication()).Documents.ImportRecoveryAsync(snapshot,receipt).AsTask());
        Assert.Empty(await destination.Documents.ListAsync(0,100));
        Assert.Null(await destination.Documents.FindRecoveryReceiptAsync(receipt.OperationId));
        var recovered=destination.Documents;
        Assert.Equal(receipt,await recovered.ImportRecoveryAsync(snapshot,receipt));
        Assert.Equal(receipt,await target.Provider().Documents.ImportRecoveryAsync(snapshot,receipt));
        await Assert.ThrowsAsync<RecoveryConflictException>(()=>recovered.ImportRecoveryAsync(snapshot,receipt with {Fingerprint=new string('b',64)}).AsTask());
        var current=(await recovered.FindAsync(new DocumentIdentity.Resource(state.ResourceId)))!;
        Assert.Equal(JsonSerializer.Serialize(state),JsonSerializer.Serialize(current));
        Assert.Equal(await SqlServerDocuments.PackageAsync(original,state.Package),await SqlServerDocuments.PackageAsync(destination,current.Package));
        Assert.Equal(state.Counters.Revisions,(await recovered.ReadRevisionsAsync(state.ResourceId,0,100))!.Revisions.Length);
        Assert.Single((await recovered.CaptureRecoveryAsync()).Receipts);
        await Assert.ThrowsAsync<RecoveryConflictException>(()=>recovered.ImportRecoveryAsync(snapshot,receipt with {OperationId=Guid.NewGuid()}).AsTask());
        await target.AssertInventoryAsync();
    }
    private sealed class FailPublication:DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,CommandEventData eventData,
            InterceptionResult<DbDataReader> result,CancellationToken cancellationToken=default)
        { if(command.CommandText.Contains("[repository_heads]",StringComparison.Ordinal)&&command.CommandText.Contains("INSERT",StringComparison.Ordinal))
            throw new IOException("Injected normalized recovery failure"); return ValueTask.FromResult(result); }
    }
}
