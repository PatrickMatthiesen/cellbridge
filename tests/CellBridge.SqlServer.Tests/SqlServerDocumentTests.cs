using System.Text.Json;
using System.Data.Common;
using CellBridge.AspNetCore;
using CellBridge.Storage;
using CellBridge.FssHttp;
using CellBridge.FssHttpB;
using CellBridge.Storage.Abstractions;
using CellBridge.TestInfrastructure;
using CellBridge.Storage.SqlServer;
using CellBridge.TestFixtures;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CellBridge.SqlServer.Tests;

[Collection("SQL Server")]
public sealed class SqlServerDocumentTests(SqlServerFixture fixture)
{
    [Fact]
    public async Task ProviderConformanceAtomicTransitionsAndRestrictedRuntime()
    {
        var database = await SqlServerDocuments.CreateAsync(fixture);
        var provider = database.Provider();
        provider.Require(durable: true, multipleInstances: true);
        await ProviderConformance.VerifyAsync(provider);
        await database.AssertInventoryAsync();
        await using var db = new SqlServerDocumentDbContext(database.Options());
        await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlRawAsync("CREATE TABLE cellbridge_documents.forbidden(id int)"));
        Assert.Equal(0, await db.Database.SqlQueryRaw<int>("SELECT IS_SRVROLEMEMBER('sysadmin') AS Value").SingleAsync());
        Assert.Equal(0, await db.Database.SqlQueryRaw<int>("SELECT HAS_PERMS_BY_NAME(DB_NAME(),'DATABASE','CONTROL') AS Value").SingleAsync());
    }

    [Fact]
    public async Task CanonicalGraphSaveReplayRestoreAndSelectiveReadsAcrossInstances()
    {
        var database = await SqlServerDocuments.CreateAsync(fixture);
        var provider = database.Provider();
        var service = new CellBridgeDocumentService(provider);
        var initialBytes = MinimalDocx.Create("initial");
        var initial = (await service.CreateAsync("/graph.docx", initialBytes, SqlServerDocuments.Actor))!;
        var bytes = MinimalDocx.Create("inherited file data, not the largest blob");
        var request = await SqlServerDocuments.ProposalAsync(provider, initial, bytes);
        var saved = await service.ExecuteAsync(initial.ResourceId, DocumentPartitionKind.FileContents, request, new Dictionary<string,string>(), SqlServerDocuments.Actor);
        Assert.False(Assert.Single(saved.Response.SubResponses).Status);
        Assert.False(Assert.Single(saved.AcceptedSaves).IsReplay);
        var fresh = database.Provider();
        var state = (await fresh.Documents.FindAsync(new DocumentIdentity.Resource(initial.ResourceId)))!;
        Assert.Equal(bytes, await SqlServerDocuments.PackageAsync(fresh, state.Package));
        var replay = await new CellBridgeDocumentService(fresh).ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents,
            request, new Dictionary<string,string>(), SqlServerDocuments.Actor);
        Assert.True(Assert.Single(replay.AcceptedSaves).IsReplay);
        Assert.Equal(saved.Response.ToByteArray(), replay.Response.ToByteArray());
        Assert.Equal(state.StateVersion, replay.State.StateVersion);
        var observer = new ObserveReads();
        var reads = database.Provider(observer).Documents;
        Assert.Equal(state.StateVersion, (await reads.FindAsync(new DocumentIdentity.Resource(state.ResourceId)))!.StateVersion);
        Assert.Equal(state.ResourceId, (await reads.FindAsync(new DocumentIdentity.Path(state.PathKey)))!.ResourceId);
        Assert.Single(await reads.ListAsync(0, 100));
        var page = (await reads.ReadRevisionsAsync(state.ResourceId, 0, 1))!;
        Assert.Single(page.Revisions); Assert.Equal(1, page.NextOffset);
        Assert.Equal(2UL, page.Revisions[0].RevisionNumber);
        Assert.Single((await reads.ReadRevisionsAsync(state.ResourceId, 1, 1))!.Revisions);
        Assert.Null((await reads.ReadRevisionAsync(state.ResourceId, 999))!.Revision);
        Assert.DoesNotContain(observer.Commands, sql => sql.Contains("[snapshots]", StringComparison.Ordinal) ||
            sql.Contains("[chunks]", StringComparison.Ordinal) || sql.Contains("[objects]", StringComparison.Ordinal));
        var restored = await new CellBridgeDocumentService(fresh).RestoreRevisionAsync(state.ResourceId, 1, 2, "restore-original", SqlServerDocuments.Actor);
        Assert.False(restored.IsReplay);
        Assert.True((await new CellBridgeDocumentService(database.Provider()).RestoreRevisionAsync(state.ResourceId,
            1, 2, "restore-original", SqlServerDocuments.Actor)).IsReplay);
        Assert.Equal(initial.Package.Sha256, restored.Revision.Package.Sha256);
        var current = (await fresh.Documents.FindAsync(new DocumentIdentity.Resource(state.ResourceId)))!;
        Assert.Equal(initialBytes, await SqlServerDocuments.PackageAsync(fresh, current.Package));
        await database.AssertInventoryAsync();
    }

    [Fact]
    public async Task CompetingHostsAndAuthoritativeTimeAfterCoordination()
    {
        var database = await SqlServerDocuments.CreateAsync(fixture);
        var first = database.Provider(); var second = database.Provider();
        var initial = (await new CellBridgeDocumentService(first).CreateAsync("/competing.docx", MinimalDocx.Create(), SqlServerDocuments.Actor))!;
        var proposals = new[] { await SqlServerDocuments.ProposalAsync(first, initial, MinimalDocx.Create("one")),
            await SqlServerDocuments.ProposalAsync(second, initial, MinimalDocx.Create("two")) };
        var results = await Task.WhenAll(proposals.Select((proposal,index) => new CellBridgeDocumentService(index == 0 ? first : second)
            .ExecuteAsync(initial.ResourceId, DocumentPartitionKind.FileContents, proposal, new Dictionary<string,string>(), SqlServerDocuments.Actor).AsTask()));
        var winner = Assert.Single(results, result => !result.AcceptedSaves.IsEmpty);
        var loser = Assert.Single(results, result => result.AcceptedSaves.IsEmpty);
        Assert.Equal((ulong)CellErrorCode.CoherencyFailure, Assert.Single(loser.Response.SubResponses).Error!.ErrorCode);
        Assert.Equal(1, winner.State.Counters.SaveReceipts);
        var id = initial.ResourceId;
        await using var gate = new SqlServerDocumentDbContext(database.Options(admin:true));
        await using var transaction = await gate.Database.BeginTransactionAsync();
        await gate.Database.ExecuteSqlAsync($"SELECT ResourceId FROM cellbridge_documents.repository_heads WITH(UPDLOCK,ROWLOCK) WHERE ResourceId={id}");
        var attempted = new ObserveDocumentLock();
        var waiting = database.Provider(attempted).Documents.ExecuteControlAsync(new DocumentControlCommand.Office(id,
            SqlServerDocuments.Admit(winner.State), new() { Type = SubRequestType.ServerTime })).AsTask();
        DateTime release;
        try
        {
            await attempted.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(waiting.IsCompleted);
            release = DateTime.SpecifyKind(await gate.Database.SqlQueryRaw<DateTime>("SELECT SYSUTCDATETIME() AS Value").SingleAsync(), DateTimeKind.Utc);
        }
        finally { await transaction.CommitAsync(); }
        var observed = await waiting.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(DateTimeKind.Utc, observed.Current.UtcNow.Kind);
        Assert.True(observed.Current.UtcNow >= release);
        await database.AssertInventoryAsync();
    }

    [Fact]
    public async Task AcceptedCancellationAndLostCommitReplyReplayWithoutDeletingData()
    {
        var database = await SqlServerDocuments.CreateAsync(fixture);
        var normal = database.Provider();
        var initial = (await new CellBridgeDocumentService(normal).CreateAsync("/cancel.docx", MinimalDocx.Create(), SqlServerDocuments.Actor))!;
        using var cancellation = new CancellationTokenSource();
        var fault = new CommitObservation { AfterCommit = () => cancellation.Cancel() };
        var acceptedBytes = MinimalDocx.Create("accepted before cancellation");
        var proposal = await SqlServerDocuments.ProposalAsync(normal, initial, acceptedBytes);
        proposal.SubRequests.Add(new(RequestTypes.QueryChanges) { RequestId = 2, Data = new QueryChangesSubRequestData { IncludeStorageManifest = true, IncludeCellChanges = true } });
        var partial = await new CellBridgeDocumentService(database.Provider(fault)).ExecuteAsync(initial.ResourceId,
            DocumentPartitionKind.FileContents, proposal, new Dictionary<string,string>(), SqlServerDocuments.Actor, cancellation.Token);
        Assert.True(partial.IsCancelled); Assert.Single(partial.AcceptedSaves); Assert.Single(partial.Response.SubResponses);
        var current = (await normal.Documents.FindAsync(new DocumentIdentity.Resource(initial.ResourceId)))!;
        Assert.Equal(1, current.Counters.SaveReceipts); Assert.Equal(initial.ContentVersion+1, current.ContentVersion);
        Assert.Equal(acceptedBytes, await SqlServerDocuments.PackageAsync(normal, current.Package));
        proposal.SubRequests.RemoveAt(1);
        var replay = await new CellBridgeDocumentService(database.Provider()).ExecuteAsync(initial.ResourceId, DocumentPartitionKind.FileContents,
            proposal, new Dictionary<string,string>(), SqlServerDocuments.Actor);
        Assert.True(Assert.Single(replay.AcceptedSaves).IsReplay);
        Assert.True(partial.Response.Status); Assert.Equal(995UL, partial.Response.Error!.ErrorCode);
        Assert.False(replay.Response.Status); Assert.Null(replay.Response.Error);
        Assert.Equal(JsonSerializer.Serialize(Assert.Single(partial.Response.SubResponses)),
            JsonSerializer.Serialize(Assert.Single(replay.Response.SubResponses)));
        Assert.Equal(JsonSerializer.Serialize(partial.Response.DataElementPackage), JsonSerializer.Serialize(replay.Response.DataElementPackage));
        var uncertainBytes = MinimalDocx.Create("commit reply lost");
        var uncertain = await SqlServerDocuments.ProposalAsync(normal, current, uncertainBytes);
        var lost = new CommitObservation { AfterCommit = () => throw new IOException("Injected lost commit reply") };
        // The provider resolves an uncertain commit using its durable operation receipt.
        var resolved = await new CellBridgeDocumentService(database.Provider(lost)).ExecuteAsync(current.ResourceId,
            DocumentPartitionKind.FileContents, uncertain, new Dictionary<string,string>(), SqlServerDocuments.Actor);
        Assert.Single(resolved.AcceptedSaves);
        var accepted = (await normal.Documents.FindAsync(new DocumentIdentity.Resource(current.ResourceId)))!;
        Assert.Equal(current.ContentVersion+1, accepted.ContentVersion);
        var retry = await new CellBridgeDocumentService(database.Provider()).ExecuteAsync(current.ResourceId, DocumentPartitionKind.FileContents,
            uncertain, new Dictionary<string,string>(), SqlServerDocuments.Actor);
        Assert.True(Assert.Single(retry.AcceptedSaves).IsReplay); Assert.Equal(accepted.StateVersion,retry.State.StateVersion);
        Assert.Equal(uncertainBytes, await SqlServerDocuments.PackageAsync(normal,accepted.Package));
        await database.AssertInventoryAsync();
    }

    private sealed class ObserveDocumentLock : DbCommandInterceptor
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,CommandEventData eventData,
            InterceptionResult<DbDataReader> result,CancellationToken cancellationToken=default)
        { if(command.CommandText.Contains("UPDLOCK",StringComparison.Ordinal)) Entered.TrySetResult(); return ValueTask.FromResult(result); }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,CommandEventData eventData,
            InterceptionResult<int> result,CancellationToken cancellationToken=default)
        { if(command.CommandText.Contains("UPDLOCK",StringComparison.Ordinal)) Entered.TrySetResult(); return ValueTask.FromResult(result); }
    }
    private sealed class CommitObservation : DbTransactionInterceptor
    {
        public Action? AfterCommit { get; set; }
        public override Task TransactionCommittedAsync(DbTransaction transaction,TransactionEndEventData eventData,CancellationToken cancellationToken=default)
        {
            if(eventData.Context!.ChangeTracker.Entries().Any(entry => entry.Metadata.GetTableName()=="repository_receipts"))
            { var action=AfterCommit; AfterCommit=null; action?.Invoke(); }
            return Task.CompletedTask;
        }
    }
    private sealed class ObserveReads : DbCommandInterceptor
    {
        public List<string> Commands { get; }=[];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,CommandEventData eventData,
            InterceptionResult<DbDataReader> result,CancellationToken cancellationToken=default)
        { Commands.Add(command.CommandText); return ValueTask.FromResult(result); }
    }
}
