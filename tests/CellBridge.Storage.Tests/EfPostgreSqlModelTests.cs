using System.Data.Common;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Npgsql;

namespace CellBridge.Storage.Tests;

[Collection("PostgreSQL")]
public sealed class EfPostgreSqlModelTests
{
    [PostgreSqlFact]
    public Task FreshSchemaMatchesTheRelationalModelNamesActionsAndDefaults() => WithEmptyDatabase(async source =>
    {
        await new PostgreSqlStorageMigrator(source).MigrateAsync();
        await using var context = new CellBridgeDbContext(
            new DbContextOptionsBuilder<CellBridgeDbContext>()
                .UseNpgsql(source, postgres =>
                    postgres.MigrationsHistoryTable("__CellBridgeDocumentMigrationsHistory"))
                .Options);
        var entities = context.GetService<IDesignTimeModel>().Model.GetEntityTypes()
            .Where(entity => entity.GetTableName() is { } table && table.StartsWith("cellbridge_", StringComparison.Ordinal))
            .ToArray();
        var modelConstraints = entities.SelectMany(entity =>
        {
            var table = entity.GetTableName()!;
            var primaryKey = entity.FindPrimaryKey()
                ?? throw new InvalidOperationException($"{table} has no primary key in the EF model.");
            return new[] { new SchemaConstraint(table, primaryKey.GetName()!, "p", "") }
                .Concat(entity.GetCheckConstraints().Select(check =>
                    new SchemaConstraint(table, check.Name!, "c", "")))
                .Concat(entity.GetForeignKeys().Select(foreignKey =>
                    new SchemaConstraint(table, foreignKey.GetConstraintName()!, "f",
                        foreignKey.DeleteBehavior == DeleteBehavior.NoAction ? "a" : foreignKey.DeleteBehavior.ToString())));
        }).OrderBy(constraint => constraint.Table).ThenBy(constraint => constraint.Name).ToArray();

        var tables = entities.Select(entity => entity.GetTableName()!).Distinct().ToArray();
        var databaseConstraints = new List<SchemaConstraint>();
        await using (var command = source.CreateCommand("""
            SELECT relation.relname, constraint_row.conname, constraint_row.contype::text,
                CASE WHEN constraint_row.contype = 'f' THEN constraint_row.confdeltype::text ELSE '' END
            FROM pg_constraint constraint_row
            JOIN pg_class relation ON relation.oid = constraint_row.conrelid
            JOIN pg_namespace namespace_row ON namespace_row.oid = relation.relnamespace
            WHERE namespace_row.nspname = current_schema() AND relation.relname = ANY($1)
                AND constraint_row.contype IN ('p', 'c', 'f')
            ORDER BY relation.relname, constraint_row.conname
            """))
        {
            command.Parameters.AddWithValue(tables);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                databaseConstraints.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                    reader.GetString(3)));
        }
        Assert.Equal(modelConstraints, databaseConstraints);

        var modelIndexes = entities.SelectMany(entity => entity.GetIndexes())
            .Select(index => index.GetDatabaseName()).Order().ToArray();
        await using (var command = source.CreateCommand("""
            SELECT index_row.relname
            FROM pg_index definition
            JOIN pg_class index_row ON index_row.oid = definition.indexrelid
            JOIN pg_class table_row ON table_row.oid = definition.indrelid
            JOIN pg_namespace namespace_row ON namespace_row.oid = table_row.relnamespace
            WHERE namespace_row.nspname = current_schema() AND table_row.relname = ANY($1)
                AND NOT definition.indisprimary
            ORDER BY index_row.relname
            """))
        {
            command.Parameters.AddWithValue(tables);
            var databaseIndexes = new List<string>();
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) databaseIndexes.Add(reader.GetString(0));
            Assert.Equal(modelIndexes, databaseIndexes);
        }

        var document = entities.Single(entity => entity.GetTableName() == "cellbridge_documents");
        Assert.Equal(false, document.FindProperty("IsDeleted")!.GetDefaultValue());
        await using var defaultCommand = source.CreateCommand("""
            SELECT column_default FROM information_schema.columns
            WHERE table_schema = current_schema() AND table_name = 'cellbridge_documents'
                AND column_name = 'is_deleted'
            """);
        Assert.Equal("false", await defaultCommand.ExecuteScalarAsync());
    });

    [PostgreSqlFact]
    public Task FreshInitializationRollsBackEverythingWhenCommitIsCancelled() => WithEmptyDatabase(async source =>
    {
        using var cancellation = new CancellationTokenSource();
        var options = new DbContextOptionsBuilder<CellBridgeDbContext>()
            .UseNpgsql(source)
            .AddInterceptors(new CancelCommitInterceptor(cancellation))
            .Options;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new PostgreSqlStorageMigrator(options).MigrateAsync(cancellation.Token));

        await using var command = source.CreateCommand("""
            SELECT to_regclass('cellbridge_schema') IS NULL
                AND to_regclass('cellbridge_usage') IS NULL
                AND to_regclass('"__CellBridgeDocumentMigrationsHistory"') IS NULL
            """);
        Assert.Equal(true, await command.ExecuteScalarAsync());
    });

    [PostgreSqlFact]
    public Task CancellationAcrossAContentBatchBoundaryRollsBackChunksAndAccounting() =>
        WithEmptyDatabase(async source =>
        {
            await new PostgreSqlStorageMigrator(source).MigrateAsync();
            const int chunkSize = 65536;
            var bytes = new byte[17 * chunkSize];
            for (var index = 0; index < bytes.Length; index++) bytes[index] = (byte)(index * 31);
            using var cancellation = new CancellationTokenSource();
            var interceptor = new CancelAfterFirstSaveInterceptor(cancellation);
            var options = new DbContextOptionsBuilder<CellBridgeDbContext>()
                .UseNpgsql(source)
                .AddInterceptors(interceptor)
                .Options;
            var interrupted = new PostgreSqlContentStore(options, bytes.LongLength);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => interrupted.WriteAsync(
                new MemoryStream(bytes, writable: false), cancellation.Token).AsTask());

            Assert.True(interceptor.Triggered);
            await using (var empty = source.CreateCommand("""
                SELECT (SELECT count(*) FROM cellbridge_objects),
                    (SELECT count(*) FROM cellbridge_chunks), stored_bytes
                FROM cellbridge_usage
                """))
            await using (var reader = await empty.ExecuteReaderAsync())
            {
                Assert.True(await reader.ReadAsync());
                Assert.Equal(0, reader.GetInt64(0));
                Assert.Equal(0, reader.GetInt64(1));
                Assert.Equal(0, reader.GetInt64(2));
            }

            var content = new PostgreSqlContentStore(source, bytes.LongLength);
            var handle = await content.WriteAsync(new MemoryStream(bytes, writable: false));
            Assert.Equal(bytes, await content.ReadVerifiedAsync(handle));
            var oversized = new byte[bytes.Length + 1];
            bytes.CopyTo(oversized, 0);
            oversized[^1] = 1;
            await Assert.ThrowsAsync<StorageQuotaExceededException>(() => content.WriteAsync(
                new MemoryStream(oversized, writable: false)).AsTask());
            Assert.Equal(bytes, await content.ReadVerifiedAsync(handle));
            var emptyHandle = await content.WriteAsync(Stream.Null);
            Assert.Empty(await content.ReadVerifiedAsync(emptyHandle));
            await using var usage = source.CreateCommand("SELECT stored_bytes FROM cellbridge_usage");
            Assert.Equal(bytes.LongLength, (long)(await usage.ExecuteScalarAsync())!);
        });

    [PostgreSqlFact]
    public async Task LegacyInitializationRejectsWrongOrMultipleRecoveryVersionsAndRollsBack()
    {
        foreach (var versions in new[] { new[] { 2 }, new[] { 1, 2 } })
        {
            await WithEmptyDatabase(async source =>
            {
                await LoadLegacySchemaAsync(source);
                await using (var seed = source.CreateCommand($"""
                    UPDATE cellbridge_schema SET version=5;
                    CREATE TABLE cellbridge_recovery_schema(version integer PRIMARY KEY);
                    INSERT INTO cellbridge_recovery_schema VALUES {string.Join(',', versions.Select(version => $"({version})"))};
                    CREATE TABLE cellbridge_recovery_receipts(operation_id uuid PRIMARY KEY,receipt_json text NOT NULL);
                    """))
                    await seed.ExecuteNonQueryAsync();

                var error = await Assert.ThrowsAsync<StorageUnavailableException>(() =>
                    new PostgreSqlStorageMigrator(source).MigrateAsync());
                Assert.Equal("Unsupported recovery schema; version 1 is required.", error.Message);
                await using var check = source.CreateCommand("""
                    SELECT (SELECT array_agg(version ORDER BY version) FROM cellbridge_recovery_schema),
                        (SELECT array_agg(version) FROM cellbridge_schema),
                        to_regclass('"__CellBridgeDocumentMigrationsHistory"') IS NULL
                    """);
                await using var reader = await check.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.Equal(versions, reader.GetFieldValue<int[]>(0));
                Assert.Equal(new[] { 5 }, reader.GetFieldValue<int[]>(1));
                Assert.True(reader.GetBoolean(2));
            });
        }
    }

    private static async Task LoadLegacySchemaAsync(NpgsqlDataSource source)
    {
        await using var stream = typeof(EfPostgreSqlModelTests).Assembly.GetManifestResourceStream(
            "CellBridge.Storage.Tests.Fixtures.PostgreSqlSchema6.sql")!;
        using var reader = new StreamReader(stream);
        await using var schema = source.CreateCommand(await reader.ReadToEndAsync());
        await schema.ExecuteNonQueryAsync();
    }

    private static async Task WithEmptyDatabase(Func<NpgsqlDataSource, Task> test)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__cellbridge")!;
        await using var admin = NpgsqlDataSource.Create(connectionString);
        var database = "cellbridge_ef_model_" + Guid.NewGuid().ToString("N");
        await using (var create = admin.CreateCommand($"CREATE DATABASE {database}"))
            await create.ExecuteNonQueryAsync();
        try
        {
            await using var source = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(connectionString)
            {
                Database = database,
            }.ConnectionString);
            await test(source);
        }
        finally
        {
            await using var drop = admin.CreateCommand($"DROP DATABASE {database} WITH (FORCE)");
            await drop.ExecuteNonQueryAsync();
        }
    }

    private sealed record SchemaConstraint(string Table, string Name, string Type, string DeleteAction);

    private sealed class CancelCommitInterceptor(CancellationTokenSource cancellation) : DbTransactionInterceptor
    {
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result,
            CancellationToken cancellationToken = default)
        {
            cancellation.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(result);
        }
    }

    private sealed class CancelAfterFirstSaveInterceptor(CancellationTokenSource cancellation) : SaveChangesInterceptor
    {
        private int _triggered;
        public bool Triggered => Volatile.Read(ref _triggered) != 0;

        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _triggered, 1) == 0) cancellation.Cancel();
            return ValueTask.FromResult(result);
        }
    }
}
