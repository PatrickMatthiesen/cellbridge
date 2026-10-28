using System.Data.Common;
using System.Security.Cryptography;
using CellBridge.AspNetCore;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.PostgreSql;
using CellBridge.TestFixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;

namespace CellBridge.Storage.Tests;

[Collection("PostgreSQL")]
public sealed class PostgreSqlChunkArrayWriteTests
{
    [PostgreSqlFact]
    public Task ArrayBatchesKeepStableSqlTypedParametersTimeoutAndExactContent() =>
        BudgetAndQueryTests.WithDatabase(new(), async source =>
        {
            var writes = new ArrayWrites();
            var content = new PostgreSqlContentStore(new DbContextOptionsBuilder<CellBridgeDbContext>()
                .UseNpgsql(source, postgres => postgres.CommandTimeout(45)).AddInterceptors(writes).Options);
            var bytes = RandomNumberGenerator.GetBytes(16 * 65536 + 19);
            var handle = await content.WriteAsync(new MemoryStream(bytes));
            Assert.Equal(new[] { 16, 1 }, writes.Rows);
            Assert.Equal(bytes, await content.ReadVerifiedAsync(handle));
            var sql = Assert.Single(writes.Sql.Distinct());

            writes.Rows.Clear();
            var first = RandomNumberGenerator.GetBytes(4 * 65536 + 23);
            var second = RandomNumberGenerator.GetBytes(11 * 65536 + 7);
            var handles = await content.WriteManyAsync(new ReadOnlyMemory<byte>[] { second, first, second, ReadOnlyMemory<byte>.Empty });
            Assert.Equal(new[] { 16, 1 }, writes.Rows);
            Assert.Equal(sql, Assert.Single(writes.Sql.Distinct()));
            Assert.Equal(handles[0], handles[2]);
            Assert.Equal(second, await content.ReadVerifiedAsync(handles[0]));
            Assert.Equal(first, await content.ReadVerifiedAsync(handles[1]));
            Assert.Empty(await content.ReadVerifiedAsync(handles[3]));
            Assert.All(writes.Timeouts, timeout => Assert.Equal(45, timeout));
            writes.Rows.Clear();
            Assert.Equal(handles, await content.WriteManyAsync(new ReadOnlyMemory<byte>[] { second, first, second, ReadOnlyMemory<byte>.Empty }));
            Assert.Empty(writes.Rows);
        });

    [PostgreSqlFact]
    public Task CancellationAfterAnArrayBatchRollsBackStandaloneBatchAndAtomicWrites() =>
        BudgetAndQueryTests.WithDatabase(new(), async source =>
        {
            var plainStates = new PostgreSqlStateStore(source);
            var plainContent = new PostgreSqlContentStore(source);
            var seedBytes = MinimalDocx.Create();
            var initial = (await new CellBridgeDocumentService(new(plainStates, plainContent))
                .CreateAsync("/array-cancel.docx", seedBytes, TestActor.Value))!;
            var before = await InventoryAsync(source);
            for (var mode = 0; mode < 3; mode++)
            {
                using var cancellation = new CancellationTokenSource();
                var writes = new ArrayWrites { AfterBatch = cancellation.Cancel };
                var options = new DbContextOptionsBuilder<CellBridgeDbContext>()
                    .UseNpgsql(source).AddInterceptors(writes).Options;
                var content = new PostgreSqlContentStore(options);
                var bytes = RandomNumberGenerator.GetBytes(17 * 65536);
                var callbacks = 0;
                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                {
                    if (mode == 0)
                        await content.WriteAsync(new MemoryStream(bytes), cancellation.Token);
                    else if (mode == 1)
                        await content.WriteManyAsync(new ReadOnlyMemory<byte>[] { bytes, new byte[] { 1 } }, cancellation.Token);
                    else
                        await new PostgreSqlStateStore(options).TransitionWithContentAsync(initial.ResourceId, content,
                            new ReadOnlyMemory<byte>[] { bytes }, (current, _) =>
                            {
                                callbacks++;
                                return new StateTransition<bool>(current, true);
                            }, cancellation.Token);
                });
                Assert.Equal(new[] { 16 }, writes.Rows);
                Assert.Equal(0, callbacks);
                Assert.Equal(before, await InventoryAsync(source));
                Assert.Equal(initial.StateVersion, (await plainStates.FindByResourceIdAsync(initial.ResourceId))!.StateVersion);
                Assert.Equal(seedBytes, await plainContent.ReadVerifiedAsync(initial.Content));
            }
        });

    [PostgreSqlFact]
    public Task QuotaRejectionRollsBackBothCompletedArrayBatches() =>
        BudgetAndQueryTests.WithDatabase(new() { MaxStoredBytes = 17 * 65536 - 1 }, async source =>
        {
            var writes = new ArrayWrites();
            var content = new PostgreSqlContentStore(new DbContextOptionsBuilder<CellBridgeDbContext>()
                .UseNpgsql(source).AddInterceptors(writes).Options);
            var bytes = new byte[17 * 65536];
            var before = await InventoryAsync(source);
            await Assert.ThrowsAsync<StorageQuotaExceededException>(() => content.WriteAsync(new MemoryStream(bytes)).AsTask());
            Assert.Equal(new[] { 16, 1 }, writes.Rows);
            Assert.Equal(before, await InventoryAsync(source));
            writes.Rows.Clear();
            await Assert.ThrowsAsync<StorageQuotaExceededException>(() => content.WriteManyAsync(new ReadOnlyMemory<byte>[] { bytes }).AsTask());
            Assert.Equal(new[] { 16, 1 }, writes.Rows);
            Assert.Equal(before, await InventoryAsync(source));
        });

    [PostgreSqlFact]
    public Task SaveChangesHooksStillObserveAndCanRejectChunkEntries() =>
        BudgetAndQueryTests.WithDatabase(new(), async source =>
        {
            var writes = new ArrayWrites();
            var hook = new RejectChunks();
            var options = new DbContextOptionsBuilder<CellBridgeDbContext>()
                .UseNpgsql(source).AddInterceptors(writes, hook).Options;
            var before = await InventoryAsync(source);
            await Assert.ThrowsAsync<InvalidOperationException>(() => new PostgreSqlContentStore(options)
                .WriteAsync(new MemoryStream(new byte[65537])).AsTask());
            Assert.Equal(2, hook.Chunks);
            Assert.Empty(writes.Rows);
            Assert.Equal(before, await InventoryAsync(source));

            // Hooks registered in EF's service provider also keep tracked writes.
            using var services = new ServiceCollection().AddEntityFrameworkNpgsql()
                .AddSingleton<IInterceptor>(hook).BuildServiceProvider();
            hook.Chunks = 0;
            options = new DbContextOptionsBuilder<CellBridgeDbContext>()
                .UseNpgsql(source).UseInternalServiceProvider(services).Options;
            await Assert.ThrowsAsync<InvalidOperationException>(() => new PostgreSqlContentStore(options)
                .WriteAsync(new MemoryStream(new byte[65537])).AsTask());
            Assert.Equal(2, hook.Chunks);
            Assert.Equal(before, await InventoryAsync(source));
        });

    [PostgreSqlFact]
    public Task NewContentWritesHonorCustomChunkTableAndColumnMappings() =>
        BudgetAndQueryTests.WithDatabase(new(), async source =>
        {
            await using (var alias = source.CreateCommand("""
                CREATE VIEW array_write_chunks AS
                SELECT object_key, ordinal AS chunk_number, bytes FROM cellbridge_chunks
                """))
                await alias.ExecuteNonQueryAsync();
            var writes = new ArrayWrites();
            var content = new PostgreSqlContentStore(new DbContextOptionsBuilder<CellBridgeDbContext>()
                .UseNpgsql(source, postgres => postgres.CommandTimeout(45))
                .ReplaceService<IModelCustomizer, AliasChunksCustomizer>().AddInterceptors(writes).Options);
            var bytes = RandomNumberGenerator.GetBytes(65536 + 17);
            var handle = await content.WriteAsync(new MemoryStream(bytes));
            Assert.Equal(bytes, await content.ReadVerifiedAsync(handle));
            Assert.Empty(writes.Rows);
            Assert.Contains(writes.OtherChunkInserts, sql => sql.Contains("chunk_number", StringComparison.Ordinal));
            Assert.All(writes.Timeouts, timeout => Assert.Equal(45, timeout));
        });

    [PostgreSqlFact]
    public Task SuppliedModelsKeepTrackedChunkWrites() =>
        BudgetAndQueryTests.WithDatabase(new(), async source =>
        {
            using var modelContext = new CellBridgeDbContext(new DbContextOptionsBuilder<CellBridgeDbContext>().UseNpgsql(source).Options);
            var writes = new ArrayWrites();
            var content = new PostgreSqlContentStore(new DbContextOptionsBuilder<CellBridgeDbContext>()
                .UseNpgsql(source).UseModel(modelContext.Model).AddInterceptors(writes).Options);
            var bytes = RandomNumberGenerator.GetBytes(65537);
            var handle = await content.WriteAsync(new MemoryStream(bytes));
            Assert.Equal(bytes, await content.ReadVerifiedAsync(handle));
            Assert.Empty(writes.Rows);
            Assert.NotEmpty(writes.OtherChunkInserts);
        });

    private static async Task<string> InventoryAsync(NpgsqlDataSource source)
    {
        await using var command = source.CreateCommand("""
            SELECT ROW((SELECT count(*) FROM cellbridge_objects),
                (SELECT count(*) FROM cellbridge_chunks), (SELECT count(*) FROM cellbridge_states),
                (SELECT count(*) FROM cellbridge_read_heads), (SELECT count(*) FROM cellbridge_revision_summaries),
                stored_bytes, (SELECT sum(available_bytes) FROM cellbridge_usage_buckets))::text
            FROM cellbridge_usage
            """);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private sealed class ArrayWrites : DbCommandInterceptor
    {
        public List<int> Rows { get; } = [];
        public List<string> Sql { get; } = [];
        public List<int> Timeouts { get; } = [];
        public List<string> OtherChunkInserts { get; } = [];
        public Action? AfterBatch { get; init; }

        public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            int result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("INSERT INTO cellbridge_chunks", StringComparison.Ordinal)
                && command.CommandText.Contains("unnest(", StringComparison.Ordinal))
            {
                Assert.Equal(3, command.Parameters.Count);
                Assert.Equal(NpgsqlDbType.Array | NpgsqlDbType.Text, ((NpgsqlParameter)command.Parameters[0]).NpgsqlDbType);
                Assert.Equal(NpgsqlDbType.Array | NpgsqlDbType.Integer, ((NpgsqlParameter)command.Parameters[1]).NpgsqlDbType);
                Assert.Equal(NpgsqlDbType.Array | NpgsqlDbType.Bytea, ((NpgsqlParameter)command.Parameters[2]).NpgsqlDbType);
                var keys = Assert.IsType<string[]>(command.Parameters[0].Value);
                Assert.Equal(keys.Length, Assert.IsType<int[]>(command.Parameters[1].Value).Length);
                Assert.Equal(keys.Length, Assert.IsType<byte[][]>(command.Parameters[2].Value).Length);
                Assert.Equal(keys.Length, result);
                Rows.Add(result);
                Sql.Add(command.CommandText);
                Timeouts.Add(command.CommandTimeout);
                AfterBatch?.Invoke();
            }
            else RecordTrackedInsert(command);
            return ValueTask.FromResult(result);
        }

        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            RecordTrackedInsert(command);
            return ValueTask.FromResult(result);
        }

        private void RecordTrackedInsert(DbCommand command)
        {
            if (command.CommandText.Contains("INSERT INTO", StringComparison.Ordinal)
                && (command.CommandText.Contains("cellbridge_chunks", StringComparison.Ordinal)
                    || command.CommandText.Contains("array_write_chunks", StringComparison.Ordinal)))
            {
                OtherChunkInserts.Add(command.CommandText);
                Timeouts.Add(command.CommandTimeout);
            }
        }
    }

    private sealed class RejectChunks : SaveChangesInterceptor
    {
        public int Chunks;
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Chunks = eventData.Context!.ChangeTracker.Entries().Count(entry =>
                entry.Metadata.GetTableName() == "cellbridge_chunks" && entry.State == EntityState.Added);
            if (Chunks != 0) throw new InvalidOperationException("Injected consumer chunk rejection.");
            return ValueTask.FromResult(result);
        }
    }

    public sealed class AliasChunksCustomizer(ModelCustomizerDependencies dependencies) : ModelCustomizer(dependencies)
    {
        public override void Customize(ModelBuilder modelBuilder, DbContext context)
        {
            base.Customize(modelBuilder, context);
            var chunks = modelBuilder.Model.GetEntityTypes().Single(entity => entity.GetTableName() == "cellbridge_chunks");
            chunks.SetTableName("array_write_chunks");
            chunks.FindProperty("Ordinal")!.SetColumnName("chunk_number");
        }
    }
}
