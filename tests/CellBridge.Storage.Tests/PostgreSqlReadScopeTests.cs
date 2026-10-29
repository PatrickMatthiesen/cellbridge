using System.Data;
using System.Data.Common;
using CellBridge.AspNetCore;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.PostgreSql;
using CellBridge.TestFixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CellBridge.Storage.Tests;

[Collection("PostgreSQL")]
public sealed class PostgreSqlReadScopeTests
{
    [PostgreSqlFact]
    public Task ResourceLookupReadsOneCoherentHeadWithoutAnExplicitTransaction() =>
        BudgetAndQueryTests.WithDatabase(new(), async source =>
        {
            var content = new PostgreSqlContentStore(source);
            await CellBridge.Tests.ImmutableIndexedReadScenario.RunHeadReadAsync(new(new PostgreSqlStateStore(source), content), interceptors =>
                new PostgreSqlStateStore(new DbContextOptionsBuilder<CellBridgeDbContext>().UseNpgsql(source, pg => pg.CommandTimeout(42))
                    .AddInterceptors(interceptors).Options));
        });

    [PostgreSqlFact]
    public Task ExactImmutablePointReadsUseNoExplicitTransactionAndPreserveScopeTimeoutAndCancellation() =>
        BudgetAndQueryTests.WithDatabase(new(), async source =>
        {
            var content = new PostgreSqlContentStore(source);
            await CellBridge.Tests.ImmutableIndexedReadScenario.RunAsync(new(new PostgreSqlStateStore(source), content), interceptors =>
                new PostgreSqlStateStore(new DbContextOptionsBuilder<CellBridgeDbContext>().UseNpgsql(source, pg => pg.CommandTimeout(42))
                    .AddInterceptors(interceptors).Options));
        });

    [PostgreSqlFact]
    public Task SingleHeadAndImmutableReadsLeaveAnExplicitConnectionInItsOriginalOpenOrClosedState() =>
        BudgetAndQueryTests.WithDatabase(new(), async source =>
        {
            IDocumentRepository normal = new PostgreSqlStateStore(source); var content = new PostgreSqlContentStore(source);
            var head = (await new CellBridgeDocumentService(new(normal, content)).CreateAsync("/connection.docx", MinimalDocx.Create(), TestActor.Value))!;
            var element = (await normal.ReadPartitionAsync(head.ResourceId, new(0) { IncludeCatalog = true }))!.Elements[0];
            await using var connection = source.CreateConnection();
            IDocumentRepository store = new PostgreSqlStateStore(new DbContextOptionsBuilder<CellBridgeDbContext>().UseNpgsql(connection).Options);
            var scope = new IndexedGraphScope(head.ResourceId, head.LifecycleGeneration, 0);
            Assert.Equal(ConnectionState.Closed, connection.State);
            Assert.Single(await store.ReadElementsAsync(scope, [element.Id]));
            Assert.NotNull(await store.FindAsync(new DocumentIdentity.Resource(head.ResourceId)));
            Assert.Equal(ConnectionState.Closed, connection.State);
            await connection.OpenAsync();
            Assert.Single(await store.ReadElementsAsync(scope, [element.Id]));
            Assert.NotNull(await store.FindAsync(new DocumentIdentity.Resource(head.ResourceId)));
            Assert.Equal(ConnectionState.Open, connection.State);
            await using var command = connection.CreateCommand(); command.CommandText = "SELECT 1";
            Assert.Equal(1, await command.ExecuteScalarAsync());
        });

    [PostgreSqlFact]
    public Task TypedReadsUseCoherentRepeatableReadTransactionsWithoutLegacyAggregates() =>
        BudgetAndQueryTests.WithDatabase(new(), async source =>
        {
            var content = new PostgreSqlContentStore(source);
            var head = (await new CellBridgeDocumentService(new(new PostgreSqlStateStore(source), content)).CreateAsync("/read-scope.docx", MinimalDocx.Create(), TestActor.Value))!;
            var commands = new ReadCommands(); var transactions = new ReadTransactions();
            IDocumentRepository store = new PostgreSqlStateStore(new DbContextOptionsBuilder<CellBridgeDbContext>().UseNpgsql(source).AddInterceptors(commands, transactions).Options);
            Func<Task>[] reads =
            [
                async () => Assert.NotNull(await store.FindAsync(new DocumentIdentity.Path(head.PathKey))),
                async () => Assert.NotNull(await store.ReadControlAsync(head.ResourceId)),
                async () => Assert.NotNull(await store.ReadPartitionAsync(head.ResourceId, new(0) { IncludeBindings = true })),
                async () => Assert.NotNull(await store.PrepareSaveAsync(head.ResourceId, new(0, new(1, SaveOperationFamily.Protocol, 0, "read"), []))),
                async () => Assert.NotNull(await store.ReadRevisionAsync(head.ResourceId, 1)),
                async () => Assert.NotNull(await store.ReadRevisionsAsync(head.ResourceId, 0, 1)),
                async () => Assert.Single(await store.ListAsync(0, 1)),
                async () => Assert.Null(await store.FindRecoveryReceiptAsync(Guid.NewGuid())),
            ];
            foreach (var read in reads)
            {
                commands.Sql.Clear(); transactions.Started.Clear(); transactions.Committed = 0; await read();
                Assert.Equal(IsolationLevel.RepeatableRead, Assert.Single(transactions.Started)); Assert.Equal(1, transactions.Committed);
                Assert.DoesNotContain(commands.Sql, sql => sql.Contains("cellbridge_states", StringComparison.Ordinal) || sql.Contains("cellbridge_read_heads", StringComparison.Ordinal));
            }
        });
    [PostgreSqlFact]
    public Task ControlReadKeepsOneSnapshotWhenAnotherTransactionChangesAuthority() =>
        BudgetAndQueryTests.WithDatabase(new(), async source =>
        {
            IDocumentRepository normal = new PostgreSqlStateStore(source);
            var head = (await new CellBridgeDocumentService(new(normal, new PostgreSqlContentStore(source))).CreateAsync("/coherent-control.docx", MinimalDocx.Create(), TestActor.Value))!;
            var injection = new AfterHeadRead(async () => Assert.True((await normal.ExecuteControlAsync(new DocumentControlCommand.SetPermissions(head.ResourceId,
                StorageTestDocuments.Admission(head), head.Security with { Grants = head.Security.Grants.Add("other", DocumentAccess.Read) }, head.AuthorityGeneration))).Changed));
            IDocumentRepository guarded = new PostgreSqlStateStore(new DbContextOptionsBuilder<CellBridgeDbContext>().UseNpgsql(source).AddInterceptors(injection).Options);
            var snapshot = (await guarded.ReadControlAsync(head.ResourceId))!;
            Assert.True(injection.Injected); Assert.Equal(head.StateVersion, snapshot.Head.StateVersion); Assert.Equal(head.AuthorityGeneration, snapshot.Head.AuthorityGeneration);
            Assert.False(snapshot.Head.Security.Grants.ContainsKey("other"));
            Assert.True((await normal.ReadControlAsync(head.ResourceId))!.Head.Security.Grants.ContainsKey("other"));
        });
    [PostgreSqlFact]
    public Task HeadAndCatalogLookupsDoNotDecodeUnrelatedImmutablePayloadMetadata() =>
        BudgetAndQueryTests.WithDatabase(new(), async source =>
        {
            IDocumentRepository store = new PostgreSqlStateStore(source); var content = new PostgreSqlContentStore(source);
            var head = (await new CellBridgeDocumentService(new(store, content)).CreateAsync("/unrelated-graph.docx", MinimalDocx.Create(), TestActor.Value))!;
            var element = (await store.ReadPartitionAsync(head.ResourceId, new(0) { IncludeCatalog = true }))!.Elements[0];
            await using (var corrupt = source.CreateCommand("UPDATE cellbridge_repository_elements SET \"Json\"='not JSON' WHERE \"ResourceId\"=$1 AND \"Partition\"=0"))
            { corrupt.Parameters.AddWithValue(head.ResourceId); await corrupt.ExecuteNonQueryAsync(); }
            Assert.NotNull(await store.FindAsync(new DocumentIdentity.Resource(head.ResourceId)));
            Assert.Single(await store.ListAsync(0, 10)); Assert.NotNull(await store.ReadControlAsync(head.ResourceId));
            await Assert.ThrowsAsync<StorageCorruptionException>(() => store.ReadElementsAsync(new(head.ResourceId, head.LifecycleGeneration, 0), [element.Id]).AsTask());
        });
    private sealed class ReadCommands : DbCommandInterceptor
    {
        public List<string> Sql { get; } = [];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData data,
            InterceptionResult<DbDataReader> result, CancellationToken ct = default) { Sql.Add(command.CommandText); return ValueTask.FromResult(result); }
    }
    private sealed class ReadTransactions : DbTransactionInterceptor
    {
        public List<IsolationLevel> Started { get; } = []; public int Committed;
        public override ValueTask<DbTransaction> TransactionStartedAsync(DbConnection connection, TransactionEndEventData data, DbTransaction result, CancellationToken ct = default)
        { Started.Add(result.IsolationLevel); return ValueTask.FromResult(result); }
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData data, CancellationToken ct = default)
        { Committed++; return Task.CompletedTask; }
    }
    private sealed class AfterHeadRead(Func<Task> action) : DbCommandInterceptor
    {
        public bool Injected;
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData data, DbDataReader result, CancellationToken ct = default)
        {
            if (!Injected && command.CommandText.Contains("cellbridge_repository_heads", StringComparison.Ordinal))
            { Injected = true; try { await action(); } catch { await result.DisposeAsync(); throw; } }
            return result;
        }
    }
}
