using System.Data;
using System.Data.Common;
using CellBridge.AspNetCore;
using CellBridge.Storage;
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
    public Task OrdinaryReadsIssueOneStatementWithoutTransactionOrConfigurationCommands() =>
        BudgetAndQueryTests.WithDatabase(new(), async source =>
        {
            var state = (await new CellBridgeDocumentService(new(new PostgreSqlStateStore(source), new PostgreSqlContentStore(source)))
                .CreateAsync("/read-scope.docx", MinimalDocx.Create(), TestActor.Value))!;
            var commands = new ReadCommands();
            var transactions = new ReadTransactions();
            var store = new PostgreSqlStateStore(new DbContextOptionsBuilder<CellBridgeDbContext>()
                .UseNpgsql(source).AddInterceptors(commands, transactions).Options);
            Func<Task>[] reads =
            [
                async () => Assert.NotNull(await store.FindByResourceIdAsync(state.ResourceId)),
                async () => Assert.NotNull(await store.FindByPathKeyAsync(state.PathKey)),
                async () => Assert.NotNull(await store.FindLifecycleAsync(state.ResourceId)),
                async () => Assert.NotNull(await store.FindReadMetadataByResourceIdAsync(state.ResourceId)),
                async () => Assert.NotNull(await store.FindReadMetadataByPathKeyAsync(state.PathKey)),
                async () => Assert.NotNull(await store.ReadRevisionSummaryAsync(state.ResourceId, 1)),
                async () => Assert.NotNull(await store.ReadRevisionSummariesAsync(state.ResourceId, 0, 1)),
                async () => Assert.Single(await store.ListAsync(0, 1)),
                async () => Assert.Null(await store.FindRecoveryReceiptAsync(Guid.NewGuid())),
            ];
            foreach (var read in reads)
            {
                commands.Sql.Clear();
                await read();
                Assert.Single(commands.Sql);
                Assert.DoesNotContain("SET ", commands.Sql[0], StringComparison.OrdinalIgnoreCase);
            }
            Assert.Empty(transactions.Started);
            Assert.Equal(0, transactions.Committed);
        });

    [PostgreSqlFact]
    public Task HealthKeepsOneSnapshotAcrossConfigurationAndNamespaceInventory() =>
        BudgetAndQueryTests.WithDatabase(new(), async source =>
        {
            var normal = new PostgreSqlStateStore(source);
            var state = (await new CellBridgeDocumentService(new(normal, new PostgreSqlContentStore(source)))
                .CreateAsync("/health-snapshot.docx", MinimalDocx.Create(), TestActor.Value))!;
            var injection = new AfterConfiguration(async () =>
            {
                await using var corrupt = source.CreateCommand("UPDATE cellbridge_documents SET path_key='/WRONG.DOCX' WHERE resource_id=$1");
                corrupt.Parameters.AddWithValue(state.ResourceId);
                await corrupt.ExecuteNonQueryAsync();
            });
            var transactions = new ReadTransactions();
            var guarded = new PostgreSqlStateStore(new DbContextOptionsBuilder<CellBridgeDbContext>()
                .UseNpgsql(source).AddInterceptors(injection, transactions).Options);
            try
            {
                await guarded.CheckHealthAsync();
                Assert.True(injection.Injected);
                Assert.Equal(IsolationLevel.RepeatableRead, Assert.Single(transactions.Started));
                Assert.Equal(1, transactions.Committed);
                await Assert.ThrowsAsync<StorageCorruptionException>(() => normal.CheckHealthAsync().AsTask());
            }
            finally
            {
                await using var repair = source.CreateCommand("UPDATE cellbridge_documents SET path_key=$2 WHERE resource_id=$1");
                repair.Parameters.AddWithValue(state.ResourceId);
                repair.Parameters.AddWithValue(state.PathKey);
                await repair.ExecuteNonQueryAsync();
            }
        });

    [Theory]
    [InlineData("GraphMetadata")]
    [InlineData("Revisions")]
    public Task NamespaceChecksDoNotDecodeUnrelatedCurrentOrHistoricalGraphs(string property) =>
        BudgetAndQueryTests.WithDatabase(new(), async source =>
        {
            var store = new PostgreSqlStateStore(source);
            var content = new PostgreSqlContentStore(source);
            var state = (await new CellBridgeDocumentService(new(store, content))
                .CreateAsync("/namespace-graphs.docx", MinimalDocx.Create(), TestActor.Value))!;
            await using (var corrupt = source.CreateCommand("UPDATE cellbridge_states SET state_json=jsonb_set(state_json::jsonb,ARRAY[$2],'{\"malformed\":true}'::jsonb)::text WHERE resource_id=$1"))
            {
                corrupt.Parameters.AddWithValue(state.ResourceId);
                corrupt.Parameters.AddWithValue(property);
                await corrupt.ExecuteNonQueryAsync();
            }
            await Assert.ThrowsAsync<StorageCorruptionException>(() => store.FindByResourceIdAsync(state.ResourceId).AsTask());
            await store.CheckHealthAsync();
            Assert.Null(await store.FindByPathKeyAsync("/MISSING.DOCX"));
            var candidate = await new DocumentStore().Put("/admitted.docx", MinimalDocx.Create()).CaptureAsync(content);
            Assert.True(await store.TryCreateAsync(candidate));
            await using var wrongPointer = source.CreateCommand("UPDATE cellbridge_documents SET path_key='/WRONG.DOCX' WHERE resource_id=$1");
            wrongPointer.Parameters.AddWithValue(state.ResourceId);
            await wrongPointer.ExecuteNonQueryAsync();
            await Assert.ThrowsAsync<StorageCorruptionException>(() => store.CheckHealthAsync().AsTask());
        });

    private sealed class ReadCommands : DbCommandInterceptor
    {
        public List<string> Sql { get; } = [];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        { Sql.Add(command.CommandText); return ValueTask.FromResult(result); }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        { Sql.Add(command.CommandText); return ValueTask.FromResult(result); }
    }

    private sealed class ReadTransactions : DbTransactionInterceptor
    {
        public List<IsolationLevel> Started { get; } = [];
        public int Committed { get; private set; }
        public override ValueTask<DbTransaction> TransactionStartedAsync(DbConnection connection, TransactionEndEventData eventData,
            DbTransaction result, CancellationToken cancellationToken = default)
        { Started.Add(result.IsolationLevel); return ValueTask.FromResult(result); }
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        { Committed++; return Task.CompletedTask; }
    }

    private sealed class AfterConfiguration(Func<Task> action) : DbCommandInterceptor
    {
        public bool Injected { get; private set; }
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (!Injected && command.CommandText.Contains("FROM cellbridge_schema", StringComparison.Ordinal))
            { Injected = true; await action(); }
            return result;
        }
    }
}
