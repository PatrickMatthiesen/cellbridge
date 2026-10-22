using System.Text.Json;
using CellBridge.AspNetCore;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using CellBridge.Storage.PostgreSql;
using CellBridge.TestFixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace CellBridge.Storage.Tests;

[Collection("PostgreSQL")]
public sealed class EfPostgreSqlMigrationTests
{
    [Fact]
    public void EfModelMatchesItsVersionedSnapshot()
    {
        using var context = new CellBridgeDbContext(new DbContextOptionsBuilder<CellBridgeDbContext>()
            .UseNpgsql("Host=localhost;Database=model_inspection;Username=model_inspection").Options);
        Assert.False(context.Database.HasPendingModelChanges());
        Assert.Equal("20261022000100_BaselineSchema6",
            context.GetService<IMigrationsAssembly>().FindMigrationId("BaselineSchema6"));
    }

    [Fact]
    public void RetryingEfStrategiesAreRejectedBeforeDatabaseAccess()
    {
        var options = new DbContextOptionsBuilder<CellBridgeDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused", postgres => postgres.EnableRetryOnFailure()).Options;
        Assert.Throws<ArgumentException>(() => new PostgreSqlStorageMigrator(options));
        Assert.Throws<ArgumentException>(() => new PostgreSqlStateStore(options));
        Assert.Throws<ArgumentException>(() => new PostgreSqlContentStore(options));
        Assert.Throws<ArgumentException>(() => new StorageMaintenance(options));
    }

    [PostgreSqlFact]
    public Task FutureMigrationHistoryIsPreservedAndRejected() => WithEmptyDatabase(async source =>
    {
        var migrator = new PostgreSqlStorageMigrator(source);
        await migrator.MigrateAsync();
        await using (var future = source.CreateCommand("""
            INSERT INTO "__CellBridgeDocumentMigrationsHistory" VALUES ('20990101000000_Future','99.0.0')
            """))
            await future.ExecuteNonQueryAsync();
        await Assert.ThrowsAsync<StorageUnavailableException>(() => migrator.MigrateAsync());
        await using var count = source.CreateCommand("SELECT count(*) FROM \"__CellBridgeDocumentMigrationsHistory\"");
        Assert.Equal(2L, await count.ExecuteScalarAsync());
    });

    [PostgreSqlFact]
    public Task EmptyDatabaseRequiresExplicitInitialization() => WithEmptyDatabase(async source =>
    {
        var store = new PostgreSqlStateStore(source);
        _ = new StorageProvider(store, new PostgreSqlContentStore(source));
        await Assert.ThrowsAsync<StorageUnavailableException>(() => store.CheckHealthAsync().AsTask());
        await using (var absent = source.CreateCommand("SELECT to_regclass('cellbridge_documents') IS NULL"))
            Assert.Equal(true, await absent.ExecuteScalarAsync());

        await store.InitializeAsync();
        await store.CheckHealthAsync();
        await using (var history = source.CreateCommand("SELECT count(*) FROM \"__CellBridgeDocumentMigrationsHistory\""))
            Assert.True((long)(await history.ExecuteScalarAsync())! > 0);
        var service = new CellBridgeDocumentService(new(store, new PostgreSqlContentStore(source)));
        var created = (await service.CreateAsync("/empty-ef.docx", MinimalDocx.Create("EF"), TestActor.Value))!;
        await store.InitializeAsync();
        Assert.Equal(DocumentStateJson.Serialize(created),
            DocumentStateJson.Serialize((await store.FindByResourceIdAsync(created.ResourceId))!));
    });

    [PostgreSqlFact]
    public Task ContentReadObservesCancellationSuppliedAfterOpeningTheStream() =>
        BudgetAndQueryTests.WithDatabase(new(), async source =>
        {
            var content = new PostgreSqlContentStore(source);
            using var input = new MemoryStream(new byte[2 * 65536]);
            var handle = await content.WriteAsync(input);
            await using var stream = await content.OpenReadAsync(handle, CancellationToken.None);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                stream.ReadAsync(new byte[65536].AsMemory(), cancellation.Token).AsTask());
        });

    [PostgreSqlFact]
    public Task ConsumerConfiguredOptionsSupportSharedAccountingAndExplicitMigrations() => WithEmptyDatabase(async source =>
    {
        var options = new DbContextOptionsBuilder<CellBridgeDbContext>()
            .UseNpgsql(source, postgres => postgres.CommandTimeout(45)).EnableDetailedErrors().Options;
        var limits = new StorageLimits { MaxDocuments = 2 };
        await new PostgreSqlStorageMigrator(options, limits).MigrateAsync();
        var store = new PostgreSqlStateStore(options, limits);
        var content = new PostgreSqlContentStore(options, limits.MaxObjectBytes);
        var provider = new StorageProvider(store, content, limits);
        await provider.CheckHealthAsync();
        var bytes = MinimalDocx.Create("configured");
        var state = (await new CellBridgeDocumentService(provider).CreateAsync(
            "/configured-ef.docx", bytes, TestActor.Value))!;
        Assert.Equal(bytes, await content.ReadVerifiedAsync(state.Content));
        await using var context = new CellBridgeDbContext(options);
        Assert.Equal(45, context.Database.GetCommandTimeout());
    });

    [PostgreSqlFact]
    public Task SqlSchema6UpgradePreservesSnapshotsGraphsRepliesHistoryAndContent() => WithEmptyDatabase(async source =>
    {
        // The schema fixture is byte-for-byte from SQL baseline 9827c292. Seed it
        // with independent in-memory protocol execution and SQL, without using EF.
        await using (var stream = typeof(EfPostgreSqlMigrationTests).Assembly.GetManifestResourceStream(
            "CellBridge.Storage.Tests.Fixtures.PostgreSqlSchema6.sql")!)
        using (var reader = new StreamReader(stream))
        await using (var schema = source.CreateCommand(await reader.ReadToEndAsync()))
            await schema.ExecuteNonQueryAsync();
        await using (var recovery = source.CreateCommand("""
            CREATE TABLE cellbridge_recovery_schema(version integer PRIMARY KEY);
            INSERT INTO cellbridge_recovery_schema VALUES(1);
            CREATE TABLE cellbridge_recovery_receipts(operation_id uuid PRIMARY KEY,receipt_json text NOT NULL);
            CREATE TABLE consumer_business_data(id integer PRIMARY KEY, value text NOT NULL);
            INSERT INTO consumer_business_data VALUES(1,'keep consumer tables');
            """))
            await recovery.ExecuteNonQueryAsync();

        var memoryState = new InMemoryStateStore();
        var memoryContent = new InMemoryContentStore();
        var memoryService = new CellBridgeDocumentService(new(memoryState, memoryContent));
        var original = (await memoryService.CreateAsync("/sql-upgrade.docx", MinimalDocx.Create("original"), TestActor.Value))!;
        var savedBytes = MinimalDocx.Create("SQL accepted save");
        var request = RevisionHistoryTests.Request(original, savedBytes);
        var accepted = await memoryService.ExecuteAsync(original.ResourceId, DocumentPartitionKind.FileContents,
            request, new Dictionary<string, string>(), TestActor.Value);
        Assert.False(Assert.Single(accepted.AcceptedSaves).IsReplay);
        var saved = accepted.State;
        var retired = (await memoryService.CreateAsync("/retired-sql.docx", MinimalDocx.Create("retired"), TestActor.Value))!;
        await memoryState.TryDeleteAsync(retired.ResourceId, retired.LifecycleGeneration, retired.StateVersion);
        retired = (await memoryState.FindLifecycleAsync(retired.ResourceId))!;
        DocumentState[] snapshots = [original, saved, retired];
        var recoveryReceipt = new RecoveryReceipt(Guid.NewGuid(), new string('a', 64), 2, snapshots.Length);

        foreach (var handle in ContentStoreReader.UniqueHandles(snapshots.SelectMany(StorageReferences.Handles)))
        {
            await using (var metadata = source.CreateCommand("INSERT INTO cellbridge_objects VALUES($1,$2,$3)"))
            {
                metadata.Parameters.AddWithValue(handle.Key);
                metadata.Parameters.AddWithValue(handle.Length);
                metadata.Parameters.AddWithValue(handle.Sha256);
                await metadata.ExecuteNonQueryAsync();
            }
            var bytes = await memoryContent.ReadVerifiedAsync(handle);
            for (var offset = 0; offset < bytes.Length; offset += 65536)
            {
                await using var chunk = source.CreateCommand("INSERT INTO cellbridge_chunks VALUES($1,$2,$3)");
                chunk.Parameters.AddWithValue(handle.Key);
                chunk.Parameters.AddWithValue(offset / 65536);
                chunk.Parameters.AddWithValue(bytes.AsSpan(offset, Math.Min(65536, bytes.Length - offset)).ToArray());
                await chunk.ExecuteNonQueryAsync();
            }
        }
        foreach (var snapshot in snapshots)
        {
            await using var insert = source.CreateCommand("INSERT INTO cellbridge_states VALUES($1,$2,$3)");
            insert.Parameters.AddWithValue(snapshot.ResourceId);
            insert.Parameters.AddWithValue(snapshot.StateVersion);
            insert.Parameters.AddWithValue(DocumentStateJson.Serialize(snapshot));
            await insert.ExecuteNonQueryAsync();
        }
        foreach (var head in new[] { saved, retired })
        {
            await using (var insert = source.CreateCommand("INSERT INTO cellbridge_documents VALUES($1,$2,$3,$4)"))
            {
                insert.Parameters.AddWithValue(head.ResourceId);
                insert.Parameters.AddWithValue(head.PathKey);
                insert.Parameters.AddWithValue(head.StateVersion);
                insert.Parameters.AddWithValue(head.IsDeleted);
                await insert.ExecuteNonQueryAsync();
            }
            var read = DocumentReadModels.Metadata(head);
            await using (var insert = source.CreateCommand("INSERT INTO cellbridge_read_heads VALUES($1,$2,$3,$4,$5)"))
            {
                insert.Parameters.AddWithValue(head.ResourceId);
                insert.Parameters.AddWithValue(head.StateVersion);
                insert.Parameters.AddWithValue(head.LifecycleGeneration);
                insert.Parameters.AddWithValue(read.RevisionCount);
                insert.Parameters.AddWithValue(JsonSerializer.Serialize(read));
                await insert.ExecuteNonQueryAsync();
            }
            foreach (var summary in DocumentReadModels.Revisions(head))
            {
                await using var insert = source.CreateCommand("INSERT INTO cellbridge_revision_summaries VALUES($1,$2,$3,$4)");
                insert.Parameters.AddWithValue(head.ResourceId);
                insert.Parameters.AddWithValue(head.LifecycleGeneration);
                insert.Parameters.AddWithValue((decimal)summary.RevisionNumber);
                insert.Parameters.AddWithValue(JsonSerializer.Serialize(summary));
                await insert.ExecuteNonQueryAsync();
            }
        }
        await using (var insert = source.CreateCommand("INSERT INTO cellbridge_recovery_receipts VALUES($1,$2)"))
        {
            insert.Parameters.AddWithValue(recoveryReceipt.OperationId);
            insert.Parameters.AddWithValue(JsonSerializer.Serialize(recoveryReceipt));
            await insert.ExecuteNonQueryAsync();
        }
        await using (var ledger = source.CreateCommand("""
            UPDATE cellbridge_usage SET document_count=2,
              stored_bytes=(SELECT COALESCE(SUM(length),0) FROM cellbridge_objects)
                +(SELECT COALESCE(SUM(octet_length(state_json)),0) FROM cellbridge_states)
                +(SELECT COALESCE(SUM(octet_length(receipt_json)),0) FROM cellbridge_recovery_receipts)
            """))
            await ledger.ExecuteNonQueryAsync();
        var before = await Rows(source);

        var store = new PostgreSqlStateStore(source);
        await store.InitializeAsync();
        await store.InitializeAsync();
        Assert.Equal(before, await Rows(source));
        Assert.Equal(recoveryReceipt, await store.FindRecoveryReceiptAsync(recoveryReceipt.OperationId));
        Assert.Equal(DocumentStateJson.Serialize(saved), DocumentStateJson.Serialize((await store.FindByResourceIdAsync(saved.ResourceId))!));
        Assert.Null(await store.FindByResourceIdAsync(retired.ResourceId));
        Assert.Equal(DocumentStateJson.Serialize(retired), DocumentStateJson.Serialize((await store.FindLifecycleAsync(retired.ResourceId))!));
        var snapshotAfter = await store.CaptureRecoveryAsync();
        Assert.Equal(snapshots.Length, snapshotAfter.Snapshots.Length);
        Assert.Equal(snapshots.Select(DocumentStateJson.Serialize).Order(), snapshotAfter.Snapshots.Select(DocumentStateJson.Serialize).Order());

        var content = new PostgreSqlContentStore(source);
        foreach (var handle in ContentStoreReader.UniqueHandles(snapshots.SelectMany(StorageReferences.Handles)))
            Assert.Equal(await memoryContent.ReadVerifiedAsync(handle), await content.ReadVerifiedAsync(handle));
        var provider = new StorageProvider(store, content);
        var service = new CellBridgeDocumentService(provider);
        var replay = await service.ExecuteAsync(original.ResourceId, DocumentPartitionKind.FileContents,
            request, new Dictionary<string, string>(), TestActor.Value);
        Assert.True(Assert.Single(replay.AcceptedSaves).IsReplay);
        Assert.Equal(accepted.Response.ToByteArray(), replay.Response.ToByteArray());
        Assert.Equal(saved.StateVersion, replay.State.StateVersion);
        Assert.Equal(2, (await service.ListRevisionsAsync(saved.ResourceId, TestActor.Value)).Count);
        var graph = await DocumentPartition.RestoreFileGraphAsync(saved, content, provider.Limits);
        Assert.Equal(savedBytes, graph.Materialize());
        var next = await RevisionHistoryTests.Save(service, saved, "EF save after SQL upgrade");
        Assert.Equal(saved.ContentVersion + 1, next.ContentVersion);
        Assert.Equal(3, next.Revisions.Length);
        await using var consumer = source.CreateCommand("SELECT value FROM consumer_business_data WHERE id=1");
        Assert.Equal("keep consumer tables", await consumer.ExecuteScalarAsync());
    });

    private static async Task<string> Rows(NpgsqlDataSource source)
    {
        // A deterministic digest covers every pre-existing library data row.
        await using var command = source.CreateCommand("""
            SELECT jsonb_build_object(
              'documents',(SELECT jsonb_agg(to_jsonb(t) ORDER BY resource_id) FROM cellbridge_documents t),
              'states',(SELECT jsonb_agg(to_jsonb(t) ORDER BY resource_id,state_version) FROM cellbridge_states t),
              'objects',(SELECT jsonb_agg(to_jsonb(t) ORDER BY object_key) FROM cellbridge_objects t),
              'chunks',(SELECT jsonb_agg(to_jsonb(t) ORDER BY object_key,ordinal) FROM cellbridge_chunks t),
              'heads',(SELECT jsonb_agg(to_jsonb(t) ORDER BY resource_id) FROM cellbridge_read_heads t),
              'summaries',(SELECT jsonb_agg(to_jsonb(t) ORDER BY resource_id,lifecycle_generation,revision_number) FROM cellbridge_revision_summaries t),
              'receipts',(SELECT jsonb_agg(to_jsonb(t) ORDER BY operation_id) FROM cellbridge_recovery_receipts t),
              'usage',(SELECT jsonb_agg(to_jsonb(t)) FROM cellbridge_usage t))::text
            """);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static async Task WithEmptyDatabase(Func<NpgsqlDataSource, Task> test)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__cellbridge")!;
        await using var admin = NpgsqlDataSource.Create(connectionString);
        var database = "cellbridge_ef_upgrade_" + Guid.NewGuid().ToString("N");
        await using (var create = admin.CreateCommand($"CREATE DATABASE {database}")) await create.ExecuteNonQueryAsync();
        try
        {
            await using var source = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(connectionString) { Database = database }.ConnectionString);
            await test(source);
        }
        finally
        {
            await using var drop = admin.CreateCommand($"DROP DATABASE {database} WITH (FORCE)");
            await drop.ExecuteNonQueryAsync();
        }
    }
}
