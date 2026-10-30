using System.Data.Common;
using System.Text.Json;
using CellBridge.AspNetCore;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.PostgreSql;
using CellBridge.TestFixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace CellBridge.Storage.Tests;

[Collection("PostgreSQL")]
public sealed class PostgreSqlAllocationFollowupTests
{
    [PostgreSqlFact]
    public Task ControlMutationsDoNotTrackOrRewriteImmutableApplicationHistory() =>
        BudgetAndQueryTests.WithDatabase(new(), async source =>
        {
            var changes = new SummaryChangesInterceptor();
            var options = new DbContextOptionsBuilder<CellBridgeDbContext>().UseNpgsql(source).AddInterceptors(changes).Options;
            IDocumentRepository store = new PostgreSqlStateStore(options);
            var service = new CellBridgeDocumentService(new(store, new PostgreSqlContentStore(source)));
            var head = (await service.CreateAsync("/history-control.docx", MinimalDocx.Create(), TestActor.Value))!;
            var expected = JsonSerializer.Serialize((await store.ReadRevisionsAsync(head.ResourceId, 0, 10))!.Revisions);
            changes.Rows.Clear();
            Assert.True((await new SharedDocumentLocks(service).ApplyAsync(head.ResourceId, HostLockOperation.Acquire, "host", TestActor.Value)).Success);
            Assert.Empty(changes.Rows);
            Assert.Equal(expected, JsonSerializer.Serialize((await store.ReadRevisionsAsync(head.ResourceId, 0, 10))!.Revisions));
        });

    [PostgreSqlFact]
    public Task UnicodeControlUpdatesKeepOneSmallHeadAndNoLegacyAggregateSnapshots() =>
        BudgetAndQueryTests.WithDatabase(new(), async source =>
        {
            IDocumentRepository store = new PostgreSqlStateStore(source);
            var service = new CellBridgeDocumentService(new(store, new PostgreSqlContentStore(source)));
            var head = (await service.CreateAsync("/資料-é-😀.docx", MinimalDocx.Create("résumé"), TestActor.Value))!;
            var locks = new SharedDocumentLocks(service);
            foreach (var operation in new[] { HostLockOperation.Acquire, HostLockOperation.Refresh, HostLockOperation.Refresh, HostLockOperation.Release })
                Assert.True((await locks.ApplyAsync(head.ResourceId, operation, "host", TestActor.Value)).Success);
            Assert.Equal(head.Path, (await store.FindAsync(new DocumentIdentity.Resource(head.ResourceId)))!.Path);
            Assert.Single((await store.ReadRevisionsAsync(head.ResourceId, 0, 10))!.Revisions);
            await using var rows = source.CreateCommand("SELECT (SELECT count(*) FROM cellbridge_states)=0 AND (SELECT count(*) FROM cellbridge_repository_heads)=1");
            Assert.Equal(true, await rows.ExecuteScalarAsync());
        });

    [PostgreSqlFact]
    public Task CompiledReadsHonorEachStoresModelTimeoutAndCommandInterceptors() =>
        BudgetAndQueryTests.WithDatabase(new(), async source =>
        {
            IDocumentRepository originalStore = new PostgreSqlStateStore(source);
            var originalContent = new PostgreSqlContentStore(source);
            var state = (await new CellBridgeDocumentService(new(originalStore, originalContent))
                .CreateAsync("/compiled-model.docx", MinimalDocx.Create(), TestActor.Value))!;
            var bytes = Enumerable.Range(0, 2 * 65536 + 19).Select(index => (byte)(index * 31)).ToArray();
            var handle = await originalContent.WriteAsync(new MemoryStream(bytes, writable: false));
            // Bind the ordinary model first. A process-wide delegate would reject
            // or incorrectly reuse it for the customized store below.
            Assert.NotNull(await originalStore.FindAsync(new DocumentIdentity.Resource(state.ResourceId)));
            Assert.Equal(bytes, await originalContent.ReadVerifiedAsync(handle));
            await using (var aliases = source.CreateCommand("""
                CREATE VIEW followup_cellbridge_repository_heads AS SELECT * FROM cellbridge_repository_heads;
                CREATE VIEW followup_cellbridge_objects AS SELECT * FROM cellbridge_objects;
                CREATE VIEW followup_cellbridge_chunks AS SELECT * FROM cellbridge_chunks;
                """))
                await aliases.ExecuteNonQueryAsync();

            var commands = new ReadCommandsInterceptor();
            var options = new DbContextOptionsBuilder<CellBridgeDbContext>()
                .UseNpgsql(source, postgres => postgres.CommandTimeout(45))
                .ReplaceService<IModelCustomizer, AlternateReadModelCustomizer>()
                .AddInterceptors(commands).Options;
            IDocumentRepository store = new PostgreSqlStateStore(options);
            var content = new PostgreSqlContentStore(options);
            for (var index = 0; index < 2; index++)
            {
                Assert.Equal(state.StateVersion, (await store.FindAsync(new DocumentIdentity.Resource(state.ResourceId)))!.StateVersion);
                Assert.Equal(bytes, await content.ReadVerifiedAsync(handle));
                Assert.Equal(handle, await content.WriteAsync(new MemoryStream(bytes, writable: false)));
            }
            foreach (var table in new[] { "repository_heads", "objects", "chunks" })
                Assert.Contains(commands.Reads, read => read.Sql.Contains("followup_cellbridge_" + table, StringComparison.Ordinal));
            Assert.All(commands.Reads, read => Assert.Equal(45, read.Timeout));
            Assert.Equal(bytes, await originalContent.ReadVerifiedAsync(handle));
        });

    [PostgreSqlFact]
    public Task DifferentModelsWithinOneStoreUseFallbackAndKeepExplicitConnectionOwnership() =>
        BudgetAndQueryTests.WithDatabase(new(), async source =>
        {
            IDocumentRepository originalStore = new PostgreSqlStateStore(source);
            var originalContent = new PostgreSqlContentStore(source);
            var bytes = MinimalDocx.Create("fallback");
            var state = (await new CellBridgeDocumentService(new(originalStore, originalContent))
                .CreateAsync("/model-fallback.docx", bytes, TestActor.Value))!;
            await using var connection = await source.OpenConnectionAsync();
            var commands = new ReadCommandsInterceptor();
            var options = new DbContextOptionsBuilder<CellBridgeDbContext>()
                .UseNpgsql(connection)
                .ReplaceService<IModelCacheKeyFactory, ContextIdentityModelCacheKeyFactory>()
                .AddInterceptors(commands).Options;
            IDocumentRepository store = new PostgreSqlStateStore(options);
            var content = new PostgreSqlContentStore(options);
            var physical = (await originalStore.ReadPartitionAsync(state.ResourceId, new(0) { IncludeCatalog = true }))!.Elements[0].Payload;
            var physicalBytes = await originalContent.ReadVerifiedAsync(physical);
            for (var index = 0; index < 2; index++)
            {
                Assert.Equal(state.StateVersion, (await store.FindAsync(new DocumentIdentity.Resource(state.ResourceId)))!.StateVersion);
                Assert.Equal(physicalBytes, await content.ReadVerifiedAsync(physical));
            }
            var stateModels = commands.Reads.Where(read => read.Sql.Contains("cellbridge_repository_heads", StringComparison.Ordinal))
                .Select(read => read.Model).Distinct().ToArray();
            var objectModels = commands.Reads.Where(read => read.Sql.Contains("cellbridge_objects", StringComparison.Ordinal))
                .Select(read => read.Model).Distinct().ToArray();
            Assert.Equal(2, stateModels.Length);
            Assert.Equal(2, objectModels.Length);
            Assert.Equal(System.Data.ConnectionState.Open, connection.State);
        });

    [PostgreSqlFact]
    public Task CompiledStateMetadataAndChunkReadsPropagateCancellationAndRemainReusable() =>
        BudgetAndQueryTests.WithDatabase(new(), async source =>
        {
            var commands = new ReadCommandsInterceptor();
            var options = new DbContextOptionsBuilder<CellBridgeDbContext>()
                .UseNpgsql(source).AddInterceptors(commands).Options;
            IDocumentRepository store = new PostgreSqlStateStore(options);
            var content = new PostgreSqlContentStore(options);
            var bytes = MinimalDocx.Create("cancellation");
            var state = (await new CellBridgeDocumentService(new(store, content))
                .CreateAsync("/compiled-cancel.docx", bytes, TestActor.Value))!;
            var physical = (await store.ReadPartitionAsync(state.ResourceId, new(0) { IncludeCatalog = true }))!.Elements[0].Payload;
            var physicalBytes = await content.ReadVerifiedAsync(physical);
            foreach (var table in new[] { "cellbridge_repository_heads", "cellbridge_objects", "cellbridge_chunks" })
            {
                using var cancellation = new CancellationTokenSource();
                commands.CancelTable = table;
                commands.CancelSource = cancellation;
                if (table == "cellbridge_repository_heads")
                {
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.FindAsync(new DocumentIdentity.Resource(state.ResourceId), cancellation.Token).AsTask());
                }
                else
                {
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                    {
                        await using var stream = await content.OpenReadAsync(physical, cancellation.Token);
                        await stream.CopyToAsync(Stream.Null, cancellation.Token);
                    });
                }
                Assert.Null(commands.CancelTable);
                Assert.True(cancellation.IsCancellationRequested);
                Assert.Equal(state.StateVersion, (await store.FindAsync(new DocumentIdentity.Resource(state.ResourceId)))!.StateVersion);
                Assert.Equal(physicalBytes, await content.ReadVerifiedAsync(physical));
            }
        });

    private sealed class SummaryChangesInterceptor : SaveChangesInterceptor
    {
        public List<(decimal Revision, EntityState State)> Rows { get; } = [];

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            foreach (var entry in eventData.Context!.ChangeTracker.Entries()
                .Where(entry => entry.Metadata.GetTableName() == "cellbridge_repository_revisions"))
                Rows.Add(((decimal)entry.Property("RevisionNumber").CurrentValue!, entry.State));
            return ValueTask.FromResult(result);
        }
    }

    private sealed class ReadCommandsInterceptor : DbCommandInterceptor
    {
        public List<(string Sql, int Timeout, IModel Model)> Reads { get; } = [];
        public string? CancelTable { get; set; }
        public CancellationTokenSource? CancelSource { get; set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Reads.Add((command.CommandText, command.CommandTimeout, eventData.Context!.Model));
            if (CancelTable is { } table && command.CommandText.Contains(table, StringComparison.Ordinal))
            {
                CancelTable = null;
                Assert.True(cancellationToken.CanBeCanceled);
                CancelSource!.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            }
            return ValueTask.FromResult(result);
        }
    }

    public sealed class AlternateReadModelCustomizer(ModelCustomizerDependencies dependencies) : ModelCustomizer(dependencies)
    {
        public override void Customize(ModelBuilder modelBuilder, DbContext context)
        {
            base.Customize(modelBuilder, context);
            foreach (var entity in modelBuilder.Model.GetEntityTypes())
                if (entity.GetTableName() is "cellbridge_repository_heads" or "cellbridge_objects" or "cellbridge_chunks")
                    entity.SetTableName("followup_" + entity.GetTableName());
        }
    }

    public sealed class ContextIdentityModelCacheKeyFactory : IModelCacheKeyFactory
    {
        public object Create(DbContext context, bool designTime) => (context.ContextId.InstanceId, designTime);
    }
}
