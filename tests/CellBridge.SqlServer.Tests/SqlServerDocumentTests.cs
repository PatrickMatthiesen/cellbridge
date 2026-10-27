using System.Collections.Immutable;
using System.Data.Common;
using System.Text.Json;
using CellBridge.AspNetCore;
using CellBridge.FssHttpB;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.TestInfrastructure;
using CellBridge.Storage.SqlServer;
using CellBridge.TestFixtures;
using CellBridge.Tests;
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
        await database.AssertAccountingAsync();
        await using var db = new SqlServerDocumentDbContext(database.Options());
        await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlRawAsync("CREATE TABLE cellbridge_documents.forbidden(id int)"));
        await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlRawAsync("UPDATE cellbridge_documents.usage SET MaxDocuments=1"));
        Assert.Equal(0, await db.Database.SqlQueryRaw<int>("SELECT IS_SRVROLEMEMBER('sysadmin') AS Value").SingleAsync());
        Assert.Equal(0, await db.Database.SqlQueryRaw<int>("SELECT HAS_PERMS_BY_NAME(DB_NAME(),'DATABASE','CONTROL') AS Value").SingleAsync());
    }

    [Fact]
    public async Task CanonicalGraphSaveReplayRestoreAndSelectiveReadsAcrossInstances()
    {
        var database = await SqlServerDocuments.CreateAsync(fixture);
        var provider = database.Provider();
        var service = new CellBridgeDocumentService(provider);
        var initial = (await service.CreateAsync("/graph.docx", MinimalDocx.Create("initial"), SqlServerDocuments.Actor))!;
        var bytes = MinimalDocx.Create("inherited file data, not the largest blob");
        var graph = new GraphFixture(bytes, blob: true);
        var request = new FsshttpbCellRequest { DataElementPackage = new(), SubRequests =
        { new(RequestTypes.PutChanges) { RequestId = 1, Data = new PutChangesSubRequestData
            { StorageIndex = graph.Index, ExpectedStorageIndex = StorageIds.Restore(initial.Partitions[0].StorageIndex!) } } } };
        request.DataElementPackage.DataElements.AddRange(graph.Elements);
        var saved = await service.ExecuteAsync(initial.ResourceId, DocumentPartitionKind.FileContents, request, new Dictionary<string, string>(), SqlServerDocuments.Actor);
        Assert.False(Assert.Single(saved.Response.SubResponses).Status);
        Assert.False(Assert.Single(saved.AcceptedSaves).IsReplay);
        var fresh = database.Provider();
        var state = (await fresh.State.FindByResourceIdAsync(initial.ResourceId))!;
        Assert.Equal(bytes, await fresh.Content.ReadVerifiedAsync(state.Content));
        Assert.Equal(bytes, (await DocumentPartition.RestoreFileGraphAsync(state, fresh.Content, fresh.Limits)).Materialize());
        var replay = await new CellBridgeDocumentService(fresh).ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents, request,
            new Dictionary<string, string>(), SqlServerDocuments.Actor);
        Assert.True(Assert.Single(replay.AcceptedSaves).IsReplay);
        Assert.Equal(saved.Response.ToByteArray(), replay.Response.ToByteArray());
        Assert.Equal(state.StateVersion, replay.State.StateVersion);
        var observer = new ObserveReads();
        var reads = new SqlServerStateStore(database.Options(false, observer));
        Assert.Equal(state.StateVersion, (await reads.FindReadMetadataByResourceIdAsync(state.ResourceId))!.StateVersion);
        Assert.Equal(state.ResourceId, (await reads.FindReadMetadataByPathKeyAsync(state.PathKey))!.ResourceId);
        Assert.Single(await reads.ListAsync(0, 100));
        var page = (await reads.ReadRevisionSummariesAsync(state.ResourceId, 0, 1))!;
        Assert.Single(page.Revisions); Assert.Equal(1, page.NextOffset);
        Assert.Equal(2UL, page.Revisions[0].RevisionNumber);
        Assert.Single((await reads.ReadRevisionSummariesAsync(state.ResourceId, 1, 1))!.Revisions);
        Assert.Null((await reads.ReadRevisionSummaryAsync(state.ResourceId, 999))!.Revision);
        Assert.DoesNotContain(observer.Commands, sql => sql.Contains("[snapshots]", StringComparison.Ordinal) ||
            sql.Contains("[chunks]", StringComparison.Ordinal) || sql.Contains("[objects]", StringComparison.Ordinal));
        var restored = await new CellBridgeDocumentService(fresh).RestoreRevisionAsync(state.ResourceId, 1, 2, "restore-original", SqlServerDocuments.Actor);
        Assert.False(restored.IsReplay);
        var restoredAgain = await new CellBridgeDocumentService(database.Provider()).RestoreRevisionAsync(state.ResourceId, 1, 2, "restore-original", SqlServerDocuments.Actor);
        Assert.True(restoredAgain.IsReplay);
        Assert.Equal(initial.Content, restored.Revision.Content);
        var current = (await fresh.State.FindByResourceIdAsync(state.ResourceId))!;
        Assert.Equal(await fresh.Content.ReadVerifiedAsync(initial.Content), (await DocumentPartition.RestoreFileGraphAsync(current, fresh.Content, fresh.Limits)).Materialize());
        await using var admin = new SqlServerDocumentDbContext(database.Options(admin: true));
        await admin.Metadata.Where(row => row.ResourceId == state.ResourceId).ExecuteDeleteAsync();
        await Assert.ThrowsAsync<StorageCorruptionException>(async () => await reads.FindReadMetadataByResourceIdAsync(state.ResourceId));
        Assert.Equal(current.StateVersion, (await fresh.State.FindByResourceIdAsync(state.ResourceId))!.StateVersion);
        await database.AssertAccountingAsync();
    }

    [Fact]
    public async Task CompetingHostsAndAuthoritativeTimeAfterCoordination()
    {
        var database = await SqlServerDocuments.CreateAsync(fixture);
        var first = database.Provider(); var second = database.Provider();
        var initial = (await new CellBridgeDocumentService(first).CreateAsync("/competing.docx", MinimalDocx.Create(), SqlServerDocuments.Actor))!;
        var proposals = new[] { Proposal(initial, MinimalDocx.Create("one")), Proposal(initial, MinimalDocx.Create("two")) };
        var results = await Task.WhenAll(proposals.Select((proposal, index) => new CellBridgeDocumentService(index == 0 ? first : second)
            .ExecuteAsync(initial.ResourceId, DocumentPartitionKind.FileContents, proposal, new Dictionary<string, string>(), SqlServerDocuments.Actor).AsTask()));
        var winner = Assert.Single(results, result => !result.AcceptedSaves.IsEmpty);
        var loser = Assert.Single(results, result => result.AcceptedSaves.IsEmpty);
        Assert.Equal((ulong)CellErrorCode.CoherencyFailure, Assert.Single(loser.Response.SubResponses).Error!.ErrorCode);
        Assert.Single(winner.State.Receipts);
        var id = initial.ResourceId;
        await using var gate = new SqlServerDocumentDbContext(database.Options(admin: true));
        await using var transaction = await gate.Database.BeginTransactionAsync();
        await gate.Database.ExecuteSqlAsync($"UPDATE cellbridge_documents.documents SET StateVersion=StateVersion WHERE ResourceId={id}");
        var attempted = new ObserveDocumentRow(id);
        var invoked = 0;
        var waiting = database.Provider(attempted).State.TransitionAsync(id, (state, now) =>
        { Interlocked.Increment(ref invoked); return new StateTransition<DateTime>(null, now); }).AsTask();
        DateTime release;
        try
        {
            await attempted.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(waiting.IsCompleted);
            Assert.Equal(0, Volatile.Read(ref invoked));
            release = DateTime.SpecifyKind(await gate.Database.SqlQueryRaw<DateTime>("SELECT SYSUTCDATETIME() AS Value").SingleAsync(), DateTimeKind.Utc);
        }
        finally { await transaction.CommitAsync(); }
        var observed = await waiting.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(DateTimeKind.Utc, observed.Kind);
        Assert.True(observed >= release);
        Assert.Equal(1, invoked);
        await database.AssertAccountingAsync();
    }

    [Fact]
    public async Task AcceptedCancellationAndLostCommitReplyReplayWithoutDeletingData()
    {
        var database = await SqlServerDocuments.CreateAsync(fixture);
        var normal = database.Provider();
        var initial = (await new CellBridgeDocumentService(normal).CreateAsync("/cancel.docx", MinimalDocx.Create(), SqlServerDocuments.Actor))!;
        using var cancellation = new CancellationTokenSource();
        var fault = new CommitObservation { AfterCommit = () => cancellation.Cancel() };
        var proposal = Proposal(initial, MinimalDocx.Create("accepted before cancellation"));
        proposal.SubRequests.Add(new(RequestTypes.QueryChanges) { RequestId = 2, Data = new QueryChangesSubRequestData { IncludeStorageManifest = true, IncludeCellChanges = true } });
        var partial = await new CellBridgeDocumentService(database.Provider(fault)).ExecuteAsync(initial.ResourceId,
            DocumentPartitionKind.FileContents, proposal, new Dictionary<string, string>(), SqlServerDocuments.Actor, cancellation.Token);
        Assert.True(partial.IsCancelled);
        Assert.Single(partial.AcceptedSaves);
        Assert.Single(partial.Response.SubResponses);
        var current = (await normal.State.FindByResourceIdAsync(initial.ResourceId))!;
        Assert.Single(current.Receipts);
        Assert.Equal(initial.ContentVersion + 1, current.ContentVersion);
        proposal.SubRequests.RemoveAt(1);
        var replay = await new CellBridgeDocumentService(database.Provider()).ExecuteAsync(initial.ResourceId, DocumentPartitionKind.FileContents,
            proposal, new Dictionary<string, string>(), SqlServerDocuments.Actor);
        Assert.True(Assert.Single(replay.AcceptedSaves).IsReplay);
        Assert.Equal(await normal.Content.ReadVerifiedAsync(current.Receipts[0].Response!), replay.Response.ToByteArray());

        var uncertain = Proposal(current, MinimalDocx.Create("commit reply lost"));
        var lost = new CommitObservation { AfterCommit = () => throw new IOException("Injected lost commit reply") };
        await Assert.ThrowsAsync<IOException>(async () => await new CellBridgeDocumentService(database.Provider(lost)).ExecuteAsync(current.ResourceId,
            DocumentPartitionKind.FileContents, uncertain, new Dictionary<string, string>(), SqlServerDocuments.Actor));
        var accepted = (await normal.State.FindByResourceIdAsync(current.ResourceId))!;
        Assert.Equal(current.ContentVersion + 1, accepted.ContentVersion);
        var retry = await new CellBridgeDocumentService(database.Provider()).ExecuteAsync(current.ResourceId, DocumentPartitionKind.FileContents,
            uncertain, new Dictionary<string, string>(), SqlServerDocuments.Actor);
        Assert.True(Assert.Single(retry.AcceptedSaves).IsReplay);
        Assert.Equal(accepted.StateVersion, retry.State.StateVersion);
        Assert.Equal(await normal.Content.ReadVerifiedAsync(accepted.Content), (await DocumentPartition.RestoreFileGraphAsync(accepted, normal.Content, normal.Limits)).Materialize());
        await database.AssertAccountingAsync();
    }

    internal static FsshttpbCellRequest Proposal(DocumentState state, byte[] bytes)
    {
        var id = DocumentStorageIdentity.Create();
        var identity = new StorageManifestBuilder.StableIdentity(id.StorageManifestGuid, id.CellManifestGuid, id.RevisionManifestGuid,
            id.ObjectGroupGuid, id.ObjectDataBlobGuid, id.ObjectGuid, id.RevisionId, id.CellId, id.SerialGuid);
        var generated = FileContentPartitionBuilder.BuildQueryChangesResponse(1, bytes, identity, state.ContentVersion + 1);
        var index = ((QueryChangesSubResponseData)generated.SubResponses[0].Data!).StorageIndexExtendedGuid;
        return new() { DataElementPackage = generated.DataElementPackage, SubRequests = { new(RequestTypes.PutChanges)
        { RequestId = 1, Data = new PutChangesSubRequestData { StorageIndex = index, ExpectedStorageIndex = StorageIds.Restore(state.Partitions.Single(p => p.Kind == 0).StorageIndex!) } } } };
    }

    [Fact]
    public async Task PreparedWritersReportOneConflictWithoutReplayingCallbacksAndPreserveSnapshotRetention()
    {
        var database = await SqlServerDocuments.CreateAsync(fixture, new() { MaxRetainedStateSnapshots = 1 });
        var initial = (await new CellBridgeDocumentService(database.Provider()).CreateAsync(
            "/token-collision.docx", MinimalDocx.Create(), SqlServerDocuments.Actor))!;
        var barrier = new PreparedWritersBarrier();
        var callbacks = new int[2];
        var errors = await Task.WhenAll(Enumerable.Range(0, 2).Select(index => Record.ExceptionAsync(async () =>
        {
            var observed = await database.Provider(barrier).State.TransitionAsync(initial.ResourceId, (current, now) =>
            {
                Interlocked.Increment(ref callbacks[index]);
                Assert.Equal(0, current.StateVersion);
                return new StateTransition<long>(current with { ModifiedUtc = now }, current.StateVersion);
            });
            Assert.Equal(0, observed);
        })));
        Assert.Single(errors, error => error is null);
        Assert.IsType<StorageConflictException>(Assert.Single(errors, error => error is not null));
        Assert.Equal(new[] { 1, 1 }, callbacks);
        var fresh = database.Provider();
        Assert.Equal(1, (await fresh.State.FindByResourceIdAsync(initial.ResourceId))!.StateVersion);
        await using var db = new SqlServerDocumentDbContext(database.Options(admin: true));
        Assert.Single(await db.Snapshots.ToListAsync());
        Assert.Equal(1, (await db.Metadata.SingleAsync()).StateVersion);
        await database.AssertAccountingAsync();
    }

    [Fact]
    public async Task SuppressedPointerUpdateAndFailedPublicationNeverReplayCallbacksOrPublishPartialState()
    {
        var database = await SqlServerDocuments.CreateAsync(fixture, new() { MaxRetainedStateSnapshots = 1 });
        var initial = (await new CellBridgeDocumentService(database.Provider()).CreateAsync(
            "/token-rollback.docx", MinimalDocx.Create(), SqlServerDocuments.Actor))!;
        var callbacks = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => database.Provider(new FailSave(suppressPointer: true)).State
            .TransitionAsync(initial.ResourceId, (current, _) =>
            { callbacks++; return new StateTransition<bool>(current, true); }).AsTask());
        Assert.Equal(1, callbacks);
        await Assert.ThrowsAsync<InvalidOperationException>(() => database.Provider(new FailSave(suppressPointer: false)).State
            .TransitionAsync(initial.ResourceId, (current, now) =>
            { callbacks++; return new StateTransition<bool>(current with { ModifiedUtc = now }, true); }).AsTask());
        Assert.Equal(2, callbacks);
        Assert.Equal(0, (await database.Provider().State.FindByResourceIdAsync(initial.ResourceId))!.StateVersion);
        await using var db = new SqlServerDocumentDbContext(database.Options(admin: true));
        Assert.Equal(0, (await db.Snapshots.SingleAsync()).StateVersion);
        Assert.Equal(0, (await db.Metadata.SingleAsync()).StateVersion);
        await database.AssertAccountingAsync();
    }

    private sealed class ObserveDocumentRow(Guid id) : DbCommandInterceptor
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("[cellbridge_documents].[documents]", StringComparison.Ordinal) &&
                command.Parameters.Cast<DbParameter>().Any(parameter => Equals(parameter.Value, id)))
                Entered.TrySetResult();
            return ValueTask.FromResult(result);
        }
    }

    private static bool IsPublication(DbContext db) => db.ChangeTracker.Entries().Any(entry =>
        entry.Metadata.GetTableName() == "documents" && entry.State == EntityState.Modified &&
        entry.Property("StateVersion").IsModified && !Equals(entry.Property("StateVersion").CurrentValue,
            entry.Property("StateVersion").OriginalValue));

    private sealed class PreparedWritersBarrier : SaveChangesInterceptor
    {
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int prepared;
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (IsPublication(eventData.Context!))
            {
                if (Interlocked.Increment(ref prepared) == 2) release.TrySetResult();
                await release.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            }
            return result;
        }
    }

    private sealed class FailSave(bool suppressPointer) : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (suppressPointer && IsPublication(eventData.Context!)) return ValueTask.FromResult(InterceptionResult<int>.SuppressWithResult(1));
            if (!suppressPointer && eventData.Context!.ChangeTracker.Entries().Any(entry =>
                entry.Metadata.GetTableName() == "snapshots" && entry.State == EntityState.Added))
                throw new InvalidOperationException("Injected publication failure after pruning.");
            return ValueTask.FromResult(result);
        }
    }

    private sealed class CommitObservation : DbTransactionInterceptor
    {
        public Action? AfterCommit { get; set; }
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries().Any(entry => entry.Metadata.GetTableName() == "snapshots"))
            { var action = AfterCommit; AfterCommit = null; action?.Invoke(); }
            return Task.CompletedTask;
        }
    }

    private sealed class ObserveReads : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        { Commands.Add(command.CommandText); return base.ReaderExecutingAsync(command, eventData, result, cancellationToken); }
    }
}
