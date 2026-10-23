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
    public Task StateWriteRepairsMissingStaleAndUnexpectedSummariesWithoutTrackingUnchangedRows() =>
        BudgetAndQueryTests.WithDatabase(new(), async source =>
        {
            var changes = new SummaryChangesInterceptor();
            var options = new DbContextOptionsBuilder<CellBridgeDbContext>()
                .UseNpgsql(source).AddInterceptors(changes).Options;
            var store = new PostgreSqlStateStore(options);
            var service = new CellBridgeDocumentService(new(store, new PostgreSqlContentStore(source)));
            var state = (await service.CreateAsync("/summary-repair.docx", MinimalDocx.Create(), TestActor.Value))!;
            state = await RevisionHistoryTests.Save(service, state, "second");
            state = await RevisionHistoryTests.Save(service, state, "third");
            var expected = JsonSerializer.Serialize((await store.ReadRevisionSummariesAsync(state.ResourceId, 0, 10))!.Revisions);
            await using (var corrupt = source.CreateCommand("""
                UPDATE cellbridge_revision_summaries SET summary_json='{}' WHERE revision_number=1;
                DELETE FROM cellbridge_revision_summaries WHERE revision_number=2;
                INSERT INTO cellbridge_revision_summaries(resource_id,lifecycle_generation,revision_number,summary_json)
                SELECT resource_id,lifecycle_generation,900,summary_json FROM cellbridge_revision_summaries WHERE revision_number=3;
                INSERT INTO cellbridge_revision_summaries(resource_id,lifecycle_generation,revision_number,summary_json)
                SELECT resource_id,lifecycle_generation+1,901,summary_json FROM cellbridge_revision_summaries WHERE revision_number=3;
                """))
                await corrupt.ExecuteNonQueryAsync();
            await Assert.ThrowsAsync<StorageCorruptionException>(() =>
                store.FindReadMetadataByResourceIdAsync(state.ResourceId).AsTask());

            changes.Rows.Clear();
            await store.TransitionAsync(state.ResourceId, (current, _) => new StateTransition<bool>(current, true));

            Assert.Equal(expected, JsonSerializer.Serialize((await store.ReadRevisionSummariesAsync(state.ResourceId, 0, 10))!.Revisions));
            Assert.Equal(4, changes.Rows.Count);
            Assert.Contains((1m, EntityState.Modified), changes.Rows);
            Assert.Contains((2m, EntityState.Added), changes.Rows);
            Assert.Contains((900m, EntityState.Deleted), changes.Rows);
            Assert.Contains((901m, EntityState.Deleted), changes.Rows);
            Assert.DoesNotContain(changes.Rows, row => row.Revision == 3m);
        });

    [PostgreSqlFact]
    public Task CachedStateSizeKeepsExactAccountingAcrossUnicodeStatesAndSnapshotRetention() =>
        BudgetAndQueryTests.WithDatabase(new() { MaxRetainedStateSnapshots = 2 }, async source =>
        {
            var limits = new StorageLimits { MaxRetainedStateSnapshots = 2 };
            var store = new PostgreSqlStateStore(source, limits);
            var service = new CellBridgeDocumentService(new(store, new PostgreSqlContentStore(source), limits));
            var state = (await service.CreateAsync("/資料-é-😀.docx", MinimalDocx.Create("résumé"), TestActor.Value))!;
            for (var index = 0; index < 4; index++)
                await store.TransitionAsync(state.ResourceId, (current, now) =>
                    new StateTransition<bool>(current with { ModifiedUtc = now }, true));

            await using var accounting = source.CreateCommand("""
                SELECT (SELECT count(*) FROM cellbridge_states) = 2,
                    stored_bytes = (SELECT COALESCE(SUM(length),0) FROM cellbridge_objects)
                        + (SELECT COALESCE(SUM(octet_length(state_json)),0) FROM cellbridge_states)
                FROM cellbridge_usage
                """);
            await using var reader = await accounting.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.True(reader.GetBoolean(0));
            Assert.True(reader.GetBoolean(1));
        });

    [PostgreSqlFact]
    public Task CompiledReadsHonorEachStoresModelTimeoutAndCommandInterceptors() =>
        BudgetAndQueryTests.WithDatabase(new(), async source =>
        {
            var originalStore = new PostgreSqlStateStore(source);
            var originalContent = new PostgreSqlContentStore(source);
            var state = (await new CellBridgeDocumentService(new(originalStore, originalContent))
                .CreateAsync("/compiled-model.docx", MinimalDocx.Create(), TestActor.Value))!;
            var bytes = Enumerable.Range(0, 2 * 65536 + 19).Select(index => (byte)(index * 31)).ToArray();
            var handle = await originalContent.WriteAsync(new MemoryStream(bytes, writable: false));
            // Bind the ordinary model first. A process-wide delegate would reject
            // or incorrectly reuse it for the customized store below.
            await originalStore.TransitionAsync(state.ResourceId, (current, _) => new StateTransition<bool>(null, true));
            Assert.Equal(bytes, await originalContent.ReadVerifiedAsync(handle));
            await using (var aliases = source.CreateCommand("""
                CREATE VIEW followup_cellbridge_states AS SELECT * FROM cellbridge_states;
                CREATE VIEW followup_cellbridge_objects AS SELECT * FROM cellbridge_objects;
                CREATE VIEW followup_cellbridge_chunks AS SELECT * FROM cellbridge_chunks;
                """))
                await aliases.ExecuteNonQueryAsync();

            var commands = new ReadCommandsInterceptor();
            var options = new DbContextOptionsBuilder<CellBridgeDbContext>()
                .UseNpgsql(source, postgres => postgres.CommandTimeout(45))
                .ReplaceService<IModelCustomizer, AlternateReadModelCustomizer>()
                .AddInterceptors(commands).Options;
            var store = new PostgreSqlStateStore(options);
            var content = new PostgreSqlContentStore(options);
            for (var index = 0; index < 2; index++)
            {
                Assert.Equal(state.StateVersion, await store.TransitionAsync(state.ResourceId,
                    (current, _) => new StateTransition<long>(null, current.StateVersion)));
                Assert.Equal(bytes, await content.ReadVerifiedAsync(handle));
                Assert.Equal(handle, await content.WriteAsync(new MemoryStream(bytes, writable: false)));
            }
            foreach (var table in new[] { "states", "objects", "chunks" })
                Assert.Contains(commands.Reads, read => read.Sql.Contains("followup_cellbridge_" + table, StringComparison.Ordinal));
            Assert.All(commands.Reads, read => Assert.Equal(45, read.Timeout));
            Assert.Equal(bytes, await originalContent.ReadVerifiedAsync(handle));
        });

    [PostgreSqlFact]
    public Task DifferentModelsWithinOneStoreUseFallbackAndKeepExplicitConnectionOwnership() =>
        BudgetAndQueryTests.WithDatabase(new(), async source =>
        {
            var originalStore = new PostgreSqlStateStore(source);
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
            var store = new PostgreSqlStateStore(options);
            var content = new PostgreSqlContentStore(options);
            for (var index = 0; index < 2; index++)
            {
                Assert.Equal(state.StateVersion, await store.TransitionAsync(state.ResourceId,
                    (current, _) => new StateTransition<long>(null, current.StateVersion)));
                Assert.Equal(bytes, await content.ReadVerifiedAsync(state.Content));
            }
            var stateModels = commands.Reads.Where(read => read.Sql.Contains("cellbridge_states", StringComparison.Ordinal))
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
            var store = new PostgreSqlStateStore(options);
            var content = new PostgreSqlContentStore(options);
            var bytes = MinimalDocx.Create("cancellation");
            var state = (await new CellBridgeDocumentService(new(store, content))
                .CreateAsync("/compiled-cancel.docx", bytes, TestActor.Value))!;
            foreach (var table in new[] { "cellbridge_states", "cellbridge_objects", "cellbridge_chunks" })
            {
                using var cancellation = new CancellationTokenSource();
                commands.CancelTable = table;
                commands.CancelSource = cancellation;
                if (table == "cellbridge_states")
                {
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.TransitionAsync<bool>(state.ResourceId,
                        (current, _) => throw new InvalidOperationException("Cancelled lookup reached transition."),
                        cancellation.Token).AsTask());
                }
                else
                {
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                    {
                        await using var stream = await content.OpenReadAsync(state.Content, cancellation.Token);
                        await stream.CopyToAsync(Stream.Null, cancellation.Token);
                    });
                }
                Assert.Null(commands.CancelTable);
                Assert.True(cancellation.IsCancellationRequested);
                Assert.Equal(state.StateVersion, await store.TransitionAsync(state.ResourceId,
                    (current, _) => new StateTransition<long>(null, current.StateVersion)));
                Assert.Equal(bytes, await content.ReadVerifiedAsync(state.Content));
            }
        });

    private sealed class SummaryChangesInterceptor : SaveChangesInterceptor
    {
        public List<(decimal Revision, EntityState State)> Rows { get; } = [];

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            foreach (var entry in eventData.Context!.ChangeTracker.Entries()
                .Where(entry => entry.Metadata.GetTableName() == "cellbridge_revision_summaries"))
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
                if (entity.GetTableName() is "cellbridge_states" or "cellbridge_objects" or "cellbridge_chunks")
                    entity.SetTableName("followup_" + entity.GetTableName());
        }
    }

    public sealed class ContextIdentityModelCacheKeyFactory : IModelCacheKeyFactory
    {
        public object Create(DbContext context, bool designTime) => (context.ContextId.InstanceId, designTime);
    }
}
