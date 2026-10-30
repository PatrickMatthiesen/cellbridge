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
public sealed class SqlServerNamespaceTests(SqlServerFixture fixture)
{
    [Fact]
    public async Task RepeatedRecreationPreservesVerifiedAncestorsAndRejectsConflictingRecoveryClaims()
    {
        var database=await SqlServerDocuments.CreateAsync(fixture); var provider=database.Provider(); var store=provider.Documents;
        var a=(await new CellBridgeDocumentService(provider).CreateAsync("/a.docx",MinimalDocx.Create(),SqlServerDocuments.Actor))!;
        var moved=await SqlServerDocuments.RenameAsync(store,a,"/b.docx"); Assert.True(moved.Changed); a=moved.Current;
        var returned=await SqlServerDocuments.RenameAsync(store,a,"/a.docx"); Assert.True(returned.Changed); a=returned.Current;
        a=await SqlServerDocuments.DeleteAsync(store,a.ResourceId);
        var b=await Recreate(a); b=await SqlServerDocuments.DeleteAsync(store,b.ResourceId);
        var beforeThird=await store.CaptureRecoveryAsync();
        var c=await Recreate(b); c=await SqlServerDocuments.DeleteAsync(store,c.ResourceId);
        var d=await Recreate(c);
        Assert.Equal(4,d.LifecycleGeneration);
        Assert.Equal(d.ResourceId,(await store.FindAsync(new DocumentIdentity.Path("/A.DOCX")))!.ResourceId);
        Assert.Equal(b.ResourceId,(await store.FindAsync(new DocumentIdentity.Resource(a.ResourceId,true)))!.ReplacedBy);
        Assert.Null(await new CellBridgeDocumentService(provider).CreateAsync("/b.docx",MinimalDocx.Create(),SqlServerDocuments.Actor));
        await database.AssertInventoryAsync();
        var unrelatedDb=await SqlServerDocuments.CreateAsync(fixture); var unrelated=unrelatedDb.Provider();
        var other=(await new CellBridgeDocumentService(unrelated).CreateAsync("/a.docx",MinimalDocx.Create(),SqlServerDocuments.Actor))!;
        other=(await SqlServerDocuments.RenameAsync(unrelated.Documents,other,"/unrelated.docx")).Current;
        await SqlServerDocuments.DeleteAsync(unrelated.Documents,other.ResourceId);
        var unrelatedSnapshot=await unrelated.Documents.CaptureRecoveryAsync();
        var combined=new ProviderSnapshot(beforeThird.Snapshots.AddRange(unrelatedSnapshot.Snapshots),[]);
        var targetDb=await SqlServerDocuments.CreateAsync(fixture); var target=targetDb.Provider();
        await CopyContent(provider,target,beforeThird); await CopyContent(unrelated,target,unrelatedSnapshot);
        await Assert.ThrowsAsync<StorageCorruptionException>(()=>target.Documents.ImportRecoveryAsync(combined,
            new(Guid.NewGuid(),new string('a',64),combined.Snapshots.Length,combined.Snapshots.Length)).AsTask());
        Assert.Empty(await target.Documents.ListAsync(0,100));
        async Task<DocumentHead> Recreate(DocumentHead retired)
        {
            var replacement=await SqlServerDocuments.ReplacementAsync(provider,retired);
            var command=new DocumentLifecycleCommand.Recreate(retired.ResourceId,retired.LifecycleGeneration,retired.StateVersion,replacement);
            Assert.True((await store.ChangeLifecycleAsync(command)).Applied);
            Assert.True((await store.ChangeLifecycleAsync(command)).Applied);
            return (await store.FindAsync(new DocumentIdentity.Resource(replacement.Head.ResourceId)))!;
        }
    }

    [Theory]
    [InlineData("head-hash")]
    [InlineData("head-key")]
    [InlineData("claim-missing")]
    public async Task NamespaceTamperingIsRejectedByOfflineValidationWithoutReadingContent(string fault)
    {
        var database=await SqlServerDocuments.CreateAsync(fixture); var provider=database.Provider();
        var original=(await new CellBridgeDocumentService(provider).CreateAsync("/a.docx",MinimalDocx.Create(),SqlServerDocuments.Actor))!;
        var other=(await new CellBridgeDocumentService(provider).CreateAsync("/other.docx",MinimalDocx.Create(),SqlServerDocuments.Actor))!;
        await using var admin=new SqlServerDocumentDbContext(database.Options(admin:true));
        var id=original.ResourceId;
        if(fault=="head-hash") await admin.Database.ExecuteSqlAsync($"UPDATE cellbridge_documents.repository_heads SET PathHash={new string('0',64)} WHERE ResourceId={id}");
        if(fault=="head-key") await admin.Database.ExecuteSqlAsync($"UPDATE cellbridge_documents.repository_heads SET PathKey={'/' + "WRONG.DOCX"} WHERE ResourceId={id}");
        if(fault=="claim-missing") await admin.Database.ExecuteSqlAsync($"DELETE FROM cellbridge_documents.repository_paths WHERE ResourceId={id}");
        var guarded=database.Provider(new RejectContentReads()).Documents;
        await Assert.ThrowsAsync<StorageCorruptionException>(()=>guarded.ExportDocumentAsync(id).AsTask());
        // Corruption checks on an affected document do not scan unrelated documents.
        Assert.Equal(other.PathKey,(await guarded.FindAsync(new DocumentIdentity.Resource(other.ResourceId)))!.PathKey);
        await guarded.CheckHealthAsync();
    }

    [Theory]
    [InlineData("repository_graph")]
    [InlineData("repository_revisions")]
    public async Task NamespaceAndMetadataReadsDoNotDecodeUnrelatedHistory(string table)
    {
        var database=await SqlServerDocuments.CreateAsync(fixture); var provider=database.Provider();
        var state=(await new CellBridgeDocumentService(provider).CreateAsync("/namespace.docx",MinimalDocx.Create(),SqlServerDocuments.Actor))!;
        await using var admin=new SqlServerDocumentDbContext(database.Options(admin:true));
        await admin.Database.ExecuteSqlRawAsync(table=="repository_graph" ? "UPDATE cellbridge_documents.repository_graph SET Json=0xFF" : "UPDATE cellbridge_documents.repository_revisions SET Json=0xFF");
        var guarded=database.Provider(new RejectContentReads()).Documents;
        Assert.Equal(state.PathKey,(await guarded.FindAsync(new DocumentIdentity.Resource(state.ResourceId)))!.PathKey);
        Assert.Null(await guarded.FindAsync(new DocumentIdentity.Path("/MISSING.DOCX")));
        Assert.NotNull(await new CellBridgeDocumentService(database.Provider()).CreateAsync("/admitted.docx",MinimalDocx.Create(),SqlServerDocuments.Actor));
        await Assert.ThrowsAsync<StorageCorruptionException>(()=>provider.Documents.ExportDocumentAsync(state.ResourceId).AsTask());
    }

    internal static async Task CopyContent(StorageProvider source,StorageProvider target,ProviderSnapshot snapshot)
    {
        foreach(var handle in ContentStoreReader.UniqueHandles(snapshot.Snapshots.SelectMany(row=>row.ContentReferences).Select(row=>row.Handle)))
        { await using var stream=await source.Content.OpenReadAsync(handle); Assert.Equal(handle,await target.Content.WriteAsync(stream)); }
    }
    private sealed class RejectContentReads:DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,CommandEventData eventData,
            InterceptionResult<DbDataReader> result,CancellationToken cancellationToken=default)
        { Assert.DoesNotContain("[objects]",command.CommandText,StringComparison.Ordinal); Assert.DoesNotContain("[chunks]",command.CommandText,StringComparison.Ordinal); return ValueTask.FromResult(result); }
    }
}
