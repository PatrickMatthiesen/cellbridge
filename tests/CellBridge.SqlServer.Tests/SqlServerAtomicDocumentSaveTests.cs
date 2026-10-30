using System.Data.Common;
using System.Security.Cryptography;
using System.Text.Json;
using CellBridge.AspNetCore;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.SqlServer;
using CellBridge.TestFixtures;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CellBridge.SqlServer.Tests;

[Collection("SQL Server")]
public sealed class SqlServerAtomicDocumentSaveTests(SqlServerFixture fixture)
{
    [Fact]
    public async Task HeadHistoryReceiptAndReferencesUseOnePublicationCommitAndQuotaRejectionRollsThemBack()
    {
        var database = await SqlServerDocuments.CreateAsync(fixture,new() { MaxSaveReceipts=1 });
        var normal=database.Provider();
        var initial=(await new CellBridgeDocumentService(normal).CreateAsync("/atomic.docx",MinimalDocx.Create(),SqlServerDocuments.Actor))!;
        var commits=new CountPublicationCommits();
        var measured=database.Provider(commits);
        var acceptedBytes=MinimalDocx.Create("accepted publication");
        var saved=await SqlServerDocuments.SaveAsync(measured,initial,acceptedBytes);
        Assert.Equal(1,commits.Commits);
        Assert.Equal(initial.StateVersion+1,saved.StateVersion);
        Assert.Equal(1,saved.Counters.SaveReceipts);
        Assert.Equal(2,saved.Counters.Revisions);
        var accepted=SqlServerDocuments.SnapshotJson(await normal.Documents.ExportDocumentAsync(saved.ResourceId));
        await SqlServerDocuments.RejectSaveAsync(measured,saved,"rejected publication");
        Assert.Equal(1,commits.Commits);
        Assert.Equal(accepted,SqlServerDocuments.SnapshotJson(await normal.Documents.ExportDocumentAsync(saved.ResourceId)));
        Assert.Equal(saved.StateVersion,(await normal.Documents.FindAsync(new DocumentIdentity.Resource(saved.ResourceId)))!.StateVersion);
        // The known receipt limit rejects before staging, so it creates no orphan payloads.
        var maintenance=new StorageMaintenance(database.Options(admin:true));
        Assert.Equal(0,(await maintenance.CollectOrphansAsync()).OrphanObjects);
        Assert.DoesNotContain((await normal.Documents.ExportDocumentAsync(saved.ResourceId))!.ContentReferences, reference => reference.IsPending);
        await maintenance.CollectOrphansAsync(true,true);
        Assert.Equal(acceptedBytes,await SqlServerDocuments.PackageAsync(normal,saved.Package));
    }

    [Fact]
    public async Task ExistingCommonContentDoesNotSerializeIndependentPublications()
    {
        var database=await SqlServerDocuments.CreateAsync(fixture);
        var normal=database.Provider(); var service=new CellBridgeDocumentService(normal);
        var first=(await service.CreateAsync("/first.docx",MinimalDocx.Create(),SqlServerDocuments.Actor))!;
        var second=(await service.CreateAsync("/second.docx",MinimalDocx.Create(),SqlServerDocuments.Actor))!;
        var common=RandomNumberGenerator.GetBytes(4096);
        var shared=await normal.Content.WriteAsync(new MemoryStream(common));
        var pause=new PausePublication();
        var firstSave=SqlServerDocuments.SaveAsync(database.Provider(pause),first,"one");
        Task<DocumentHead>? secondSave=null;
        try
        {
            await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            // Verify and reuse the same immutable object while the first head is guarded.
            Assert.Equal(shared,await database.Provider().Content.WriteAsync(new MemoryStream(common)));
            secondSave=SqlServerDocuments.SaveAsync(database.Provider(),second,"two");
            Assert.Equal(second.ContentVersion+1,(await secondSave.WaitAsync(TimeSpan.FromSeconds(10))).ContentVersion);
            Assert.False(firstSave.IsCompleted);
        }
        finally
        {
            pause.Release.TrySetResult();
            await firstSave.WaitAsync(TimeSpan.FromSeconds(20));
            if(secondSave is not null) await secondSave;
        }
        Assert.Equal(common,await normal.Content.ReadVerifiedAsync(shared));
        await database.AssertInventoryAsync();
    }

    [Fact]
    public async Task CancellationDuringChunkStagingRollsBackPhysicalBatchAndLeavesDocumentUnchanged()
    {
        var database=await SqlServerDocuments.CreateAsync(fixture);
        var normal=database.Provider();
        var initial=(await new CellBridgeDocumentService(normal).CreateAsync("/cancel.docx",MinimalDocx.Create(),SqlServerDocuments.Actor))!;
        var before=await InventoryAsync(database.Admin);
        var snapshot=SqlServerDocuments.SnapshotJson(await normal.Documents.ExportDocumentAsync(initial.ResourceId));
        using var cancellation=new CancellationTokenSource();
        var cancel=new CancelChunkBatch(cancellation);
        var content=database.Provider(cancel).Content;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>content.WriteAsync(
            new MemoryStream(RandomNumberGenerator.GetBytes(17*65536)),cancellation.Token).AsTask());
        Assert.True(cancel.Triggered);
        Assert.Equal(before,await InventoryAsync(database.Admin));
        Assert.Equal(snapshot,SqlServerDocuments.SnapshotJson(await normal.Documents.ExportDocumentAsync(initial.ResourceId)));
    }

    private static async Task<(long Objects,long Chunks)> InventoryAsync(string connectionString)
    {
        await using var connection=new SqlConnection(connectionString); await connection.OpenAsync();
        await using var command=new SqlCommand("SELECT (SELECT count_big(*) FROM cellbridge_documents.objects),(SELECT count_big(*) FROM cellbridge_documents.chunks)",connection);
        await using var reader=await command.ExecuteReaderAsync(); Assert.True(await reader.ReadAsync());
        return(reader.GetInt64(0),reader.GetInt64(1));
    }
    private sealed class CountPublicationCommits:DbTransactionInterceptor
    {
        public int Commits { get;private set; }
        public override Task TransactionCommittedAsync(DbTransaction transaction,TransactionEndEventData eventData,CancellationToken cancellationToken=default)
        { if(eventData.Context!.ChangeTracker.Entries().Any(e=>e.Metadata.GetTableName()=="repository_receipts")) Commits++; return Task.CompletedTask; }
    }
    private sealed class PausePublication:DbCommandInterceptor
    {
        public TaskCompletionSource Entered {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,CommandEventData eventData,
            InterceptionResult<DbDataReader> result,CancellationToken cancellationToken=default)
        {
            if(command.CommandText.Contains("UPDATE",StringComparison.Ordinal)&&command.CommandText.Contains("[repository_heads]",StringComparison.Ordinal))
            { Entered.TrySetResult(); await Release.Task.WaitAsync(cancellationToken); }
            return result;
        }
    }
    private sealed class CancelChunkBatch(CancellationTokenSource cancellation):SaveChangesInterceptor
    {
        public bool Triggered {get;private set;}
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData,int result,CancellationToken cancellationToken=default)
        { Triggered=true; cancellation.Cancel(); return ValueTask.FromResult(result); }
    }
}
