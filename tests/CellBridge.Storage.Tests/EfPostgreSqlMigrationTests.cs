using System.Data;
using System.Data.Common;
using System.Text.Json;
using CellBridge.AspNetCore;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using CellBridge.Storage.PostgreSql;
using CellBridge.TestFixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
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
            .UseNpgsql("Host=localhost;Database=model_inspection;Username=model_inspection",
                postgres => postgres.MigrationsHistoryTable("__CellBridgeDocumentMigrationsHistory"))
            .Options);
        Assert.False(context.Database.HasPendingModelChanges());
        Assert.Equal("20261022000100_BaselineSchema6",
            context.GetService<IMigrationsAssembly>().FindMigrationId("BaselineSchema6"));
        Assert.Equal("20261028000100_UsageAllowances",
            context.GetService<IMigrationsAssembly>().FindMigrationId("UsageAllowances"));
        Assert.Equal("20261028000300_NormalizedDocumentRepository",
            context.GetService<IMigrationsAssembly>().FindMigrationId("NormalizedDocumentRepository"));
        Assert.Equal("20261028000200_RemoveSharedQuotas",
            context.GetService<IMigrationsAssembly>().FindMigrationId("RemoveSharedQuotas"));
        Assert.Equal("20261029000100_DocumentContentStaging",
            context.GetService<IMigrationsAssembly>().FindMigrationId("DocumentContentStaging"));
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
        Assert.Throws<ArgumentException>(() => new PostgreSqlFileSystemContentStore(options, Path.GetTempPath()));
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
        Assert.Equal(6L, await count.ExecuteScalarAsync());
    });

    [PostgreSqlFact]
    public Task EmptyDatabaseRequiresExplicitInitialization() => WithEmptyDatabase(async source =>
    {
        var store = new PostgreSqlStateStore(source);
        _ = new StorageProvider(store, new PostgreSqlContentStore(source));
        await Assert.ThrowsAsync<StorageUnavailableException>(() => ((IDocumentRepository)store).CheckHealthAsync().AsTask());
        await using (var absent = source.CreateCommand("SELECT to_regclass('cellbridge_documents') IS NULL"))
            Assert.Equal(true, await absent.ExecuteScalarAsync());

        await store.InitializeAsync();
        await ((IDocumentRepository)store).CheckHealthAsync();
        await using (var history = source.CreateCommand("SELECT count(*) FROM \"__CellBridgeDocumentMigrationsHistory\""))
            Assert.Equal(5L, await history.ExecuteScalarAsync());
        var service = new CellBridgeDocumentService(new(store, new PostgreSqlContentStore(source)));
        var created = (await service.CreateAsync("/empty-ef.docx", MinimalDocx.Create("EF"), TestActor.Value))!;
        await store.InitializeAsync();
        Assert.Equal(JsonSerializer.Serialize(created),
            JsonSerializer.Serialize(await ((IDocumentRepository)store).FindAsync(new DocumentIdentity.Resource(created.ResourceId))));
    });

    [PostgreSqlFact]
    public Task ContentReadObservesCancellationSuppliedAfterOpeningTheStream() =>
        WithInitializedDatabaseAsync(new(), async source =>
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
    public Task ConsumerConfiguredOptionsSupportAtomicPublicationAndExplicitMigrations() => WithEmptyDatabase(async source =>
    {
        var interceptor = new LeaseTrackingInterceptor();
        var options = new DbContextOptionsBuilder<CellBridgeDbContext>()
            .UseNpgsql(source, postgres => postgres.CommandTimeout(45)).EnableDetailedErrors()
            .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
            .AddInterceptors(interceptor).Options;
        Assert.Equal(QueryTrackingBehavior.NoTracking, options.FindExtension<CoreOptionsExtension>()!.QueryTrackingBehavior);
        var limits = new StorageLimits();
        await new PostgreSqlStorageMigrator(options, limits).MigrateAsync();
        var store = new PostgreSqlStateStore(options, limits);
        var content = new PostgreSqlContentStore(options, limits.MaxObjectBytes);
        var provider = new StorageProvider(store, content, limits);
        await provider.CheckHealthAsync();
        var service = new CellBridgeDocumentService(provider);
        var bytes = MinimalDocx.Create("configured");
        var state = (await service.CreateAsync(
            "/configured-ef.docx", bytes, TestActor.Value))!;
        Assert.Equal(bytes, await StorageTestDocuments.PackageAsync(provider, state.Package));
        Assert.Equal(JsonSerializer.Serialize(state),
            JsonSerializer.Serialize(await provider.Documents.FindAsync(new DocumentIdentity.Resource(state.ResourceId))));

        var savedBytes = MinimalDocx.Create("configured save");
        var result = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents,
            await StorageTestDocuments.SaveRequestAsync(service, state, savedBytes), new Dictionary<string, string>(), TestActor.Value);
        Assert.False(Assert.Single(result.Response.SubResponses).Status);
        Assert.True(result.State.StateVersion > state.StateVersion);
        var persisted = (await provider.Documents.FindAsync(new DocumentIdentity.Resource(state.ResourceId)))!;
        Assert.Equal(JsonSerializer.Serialize(result.State), JsonSerializer.Serialize(persisted));
        var graph = await StorageTestDocuments.GraphAsync(service, persisted);
        Assert.Equal(savedBytes, graph.Materialize());

        Assert.NotEmpty(interceptor.CommandTimeouts);
        Assert.All(interceptor.CommandTimeouts, timeout => Assert.Equal(45, timeout));
        await using var context = new CellBridgeDbContext(options);
        Assert.Equal(45, context.Database.GetCommandTimeout());
        Assert.Equal(QueryTrackingBehavior.NoTracking, context.ChangeTracker.QueryTrackingBehavior);
        Assert.Equal(QueryTrackingBehavior.NoTracking, options.FindExtension<CoreOptionsExtension>()!.QueryTrackingBehavior);
    });

    [PostgreSqlFact]
    public Task RuntimePoolReusesContextsAfterCancellationAndFailure() => WithEmptyDatabase(async source =>
    {
        await new PostgreSqlStorageMigrator(source).MigrateAsync();
        var interceptor = new LeaseTrackingInterceptor();
        var options = new DbContextOptionsBuilder<CellBridgeDbContext>()
            .UseNpgsql(source).AddInterceptors(interceptor).Options;
        var store = new PostgreSqlStateStore(options);
        var missing = Guid.NewGuid();

        interceptor.FailNext(new OperationCanceledException("Injected cancellation."));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ((IDocumentRepository)store).FindAsync(new DocumentIdentity.Resource(missing)).AsTask());
        var context = Assert.Single(interceptor.Contexts.Distinct());

        interceptor.Clear();
        Assert.Null(await ((IDocumentRepository)store).FindAsync(new DocumentIdentity.Resource(missing)));
        Assert.Same(context, Assert.Single(interceptor.Contexts.Distinct()));

        interceptor.Clear();
        interceptor.FailNext(new InvalidOperationException("Injected command failure."));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ((IDocumentRepository)store).FindAsync(new DocumentIdentity.Resource(missing)).AsTask());
        Assert.Same(context, Assert.Single(interceptor.Contexts.Distinct()));

        interceptor.Clear();
        Assert.Null(await ((IDocumentRepository)store).FindAsync(new DocumentIdentity.Resource(missing)));
        Assert.Same(context, Assert.Single(interceptor.Contexts.Distinct()));
    });

    [PostgreSqlFact]
    public Task ExplicitConnectionOptionsStayUnpooledAndKeepCallerOwnership() => WithEmptyDatabase(async source =>
    {
        await new PostgreSqlStorageMigrator(source).MigrateAsync();
        await using var connection = await source.OpenConnectionAsync();
        var interceptor = new LeaseTrackingInterceptor();
        var options = new DbContextOptionsBuilder<CellBridgeDbContext>()
            .UseNpgsql(connection).AddInterceptors(interceptor).Options;
        var store = new PostgreSqlStateStore(options);
        var content = new PostgreSqlContentStore(options);

        Assert.Null(await ((IDocumentRepository)store).FindAsync(new DocumentIdentity.Resource(Guid.NewGuid())));
        var first = Assert.Single(interceptor.Contexts.Distinct());
        interceptor.Clear();
        Assert.Null(await ((IDocumentRepository)store).FindAsync(new DocumentIdentity.Resource(Guid.NewGuid())));
        var second = Assert.Single(interceptor.Contexts.Distinct());

        Assert.NotSame(first, second);
        Assert.Equal(ConnectionState.Open, connection.State);
        var handle = await content.WriteAsync(new MemoryStream(new byte[] { 1, 2, 3 }, writable: false));
        Assert.Equal(new byte[] { 1, 2, 3 }, await content.ReadVerifiedAsync(handle));
        Assert.Equal(ConnectionState.Open, connection.State);
    });

    [PostgreSqlFact]
    public Task PartialStreamDisposalIsIdempotentAndReturnsFailedEnumeratorLease() =>
        WithInitializedDatabaseAsync(new(), async source =>
        {
            var interceptor = new LeaseTrackingInterceptor();
            var options = new DbContextOptionsBuilder<CellBridgeDbContext>()
                .UseNpgsql(source).AddInterceptors(interceptor).Options;
            var content = new PostgreSqlContentStore(options);
            var bytes = Enumerable.Range(0, 2 * 65536).Select(index => (byte)(index * 31)).ToArray();
            var handle = await content.WriteAsync(new MemoryStream(bytes, writable: false));

            foreach (var disposeSynchronously in new[] { false, true })
            {
                interceptor.Clear();
                var first = await content.OpenReadAsync(handle);
                Assert.Equal(17, await first.ReadAsync(new byte[17].AsMemory()));
                var leasedContext = Assert.Single(interceptor.Contexts.Distinct());
                var disposalFailure = interceptor.DelayNextReaderDisposeFailure();
                var firstDisposal = disposeSynchronously
                    ? Task.Run(first.Dispose)
                    : Task.Run(async () => await first.DisposeAsync());
                await disposalFailure.Started;
                var concurrentDisposal = disposeSynchronously
                    ? first.DisposeAsync().AsTask()
                    : Task.Run(first.Dispose);
                Assert.False(firstDisposal.IsCompleted);
                Assert.False(concurrentDisposal.IsCompleted);
                disposalFailure.Release();
                await Assert.ThrowsAsync<InvalidOperationException>(() => firstDisposal);
                await Assert.ThrowsAsync<InvalidOperationException>(() => concurrentDisposal);
                Assert.False(first.CanRead);
                Assert.Throws<ObjectDisposedException>(() => first.Read(new byte[1], 0, 1));
                await Assert.ThrowsAsync<ObjectDisposedException>(() =>
                    first.ReadAsync(new byte[1].AsMemory()).AsTask());

                interceptor.Clear();
                await using var second = await content.OpenReadAsync(handle);
                Assert.Same(leasedContext, Assert.Single(interceptor.Contexts.Distinct()));
                Assert.Throws<InvalidOperationException>(first.Dispose);
                await Assert.ThrowsAsync<InvalidOperationException>(() => first.DisposeAsync().AsTask());
                using var copy = new MemoryStream();
                await second.CopyToAsync(copy);
                Assert.Equal(bytes, copy.ToArray());
                Assert.All(interceptor.Contexts, context => Assert.Same(leasedContext, context));
            }
        });

    [PostgreSqlFact]
    public Task StreamDisposalWaitsForActiveReadBeforeReturningContextLease() =>
        WithInitializedDatabaseAsync(new(), async source =>
        {
            var interceptor = new LeaseTrackingInterceptor();
            var options = new DbContextOptionsBuilder<CellBridgeDbContext>()
                .UseNpgsql(source).AddInterceptors(interceptor).Options;
            var content = new PostgreSqlContentStore(options);
            var bytes = Enumerable.Range(0, 2 * 65536).Select(index => (byte)(index * 17)).ToArray();
            var handle = await content.WriteAsync(new MemoryStream(bytes, writable: false));

            foreach (var disposeSynchronously in new[] { false, true })
            {
                interceptor.Clear();
                var delay = interceptor.DelayNextChunkReader();
                var first = await content.OpenReadAsync(handle);
                var leasedContext = Assert.Single(interceptor.Contexts.Distinct());
                var read = first.ReadAsync(new byte[17].AsMemory()).AsTask();
                await delay.Started;

                var firstDisposal = disposeSynchronously
                    ? Task.Run(first.Dispose)
                    : first.DisposeAsync().AsTask();
                var concurrentDisposal = disposeSynchronously
                    ? first.DisposeAsync().AsTask()
                    : Task.Run(first.Dispose);
                await delay.CancellationObserved;
                Assert.False(firstDisposal.IsCompleted);
                Assert.False(concurrentDisposal.IsCompleted);
                await Assert.ThrowsAsync<ObjectDisposedException>(() =>
                    first.ReadAsync(new byte[1].AsMemory()).AsTask());

                await using var second = await content.OpenReadAsync(handle);
                var contextsWhileReadIsActive = interceptor.Contexts.Distinct().ToArray();
                Assert.Equal(2, contextsWhileReadIsActive.Length);
                Assert.Same(leasedContext, contextsWhileReadIsActive[0]);
                Assert.NotSame(leasedContext, contextsWhileReadIsActive[1]);

                delay.Release();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
                await Task.WhenAll(firstDisposal, concurrentDisposal);

                interceptor.Clear();
                await using var returnedLease = await content.OpenReadAsync(handle);
                Assert.Same(leasedContext, Assert.Single(interceptor.Contexts.Distinct()));
            }
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

        var memoryContent = new InMemoryContentStore();
        var original = await LegacyStorageFixture.CreateAsync(memoryContent, "/sql-upgrade.docx", MinimalDocx.Create("original"));
        var savedBytes = MinimalDocx.Create("SQL accepted save");
        var accepted = await LegacyStorageFixture.SaveAsync(original, memoryContent, savedBytes);
        var request = accepted.Request;
        var saved = accepted.State;
        var retired = await LegacyStorageFixture.CreateAsync(memoryContent, "/retired-sql.docx", MinimalDocx.Create("retired"));
        retired = retired with { IsDeleted = true, DeletedFromStateVersion = retired.StateVersion, StateVersion = retired.StateVersion + 1 };
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
        await using (var schema = source.CreateCommand("SELECT version FROM cellbridge_schema"))
            Assert.Equal(10, await schema.ExecuteScalarAsync());
        await using (var history = source.CreateCommand("SELECT count(*) FROM \"__CellBridgeDocumentMigrationsHistory\""))
            Assert.Equal(5L, await history.ExecuteScalarAsync());
        await using (var removed = source.CreateCommand("SELECT to_regclass('cellbridge_usage') IS NULL AND to_regclass('cellbridge_usage_buckets') IS NULL"))
            Assert.Equal(true, await removed.ExecuteScalarAsync());
        var repository = (IDocumentRepository)store;
        Assert.Equal(recoveryReceipt, await repository.FindRecoveryReceiptAsync(recoveryReceipt.OperationId));
        var migratedHead = (await repository.FindAsync(new DocumentIdentity.Resource(saved.ResourceId)))!;
        Assert.Equal(saved.StateVersion, migratedHead.StateVersion);
        Assert.Equal(saved.Content.Sha256, migratedHead.Package.Sha256);
        Assert.Null(await repository.FindAsync(new DocumentIdentity.Resource(retired.ResourceId)));
        var retiredHead = (await repository.FindAsync(new DocumentIdentity.Resource(retired.ResourceId, IncludeDeleted: true)))!;
        Assert.True(retiredHead.IsDeleted);
        Assert.Equal(retired.StateVersion, retiredHead.StateVersion);
        var snapshotAfter = await repository.CaptureRecoveryAsync();
        Assert.Equal(2, snapshotAfter.Snapshots.Length);
        var migrated = Assert.Single(snapshotAfter.Snapshots, item => item.Current.Head.ResourceId == saved.ResourceId);
        Assert.Equal(saved.Revisions.Length, migrated.ApplicationRevisions.Length);
        Assert.Equal(saved.Receipts.Select(receipt => receipt.Response), migrated.Receipts.Select(receipt => receipt.Response));
        foreach (var document in snapshotAfter.Snapshots)
            Assert.Equal(NormalizedDocumentRecovery.StoredBytes(document), document.Current.Head.Counters.StoredBytes);

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
        Assert.Equal(2, (await provider.Documents.ReadRevisionsAsync(saved.ResourceId, 0, 10))!.Revisions.Length);
        var graph = await DocumentPartition.RestoreFileGraphAsync(saved, content, provider.Limits);
        Assert.Equal(savedBytes, graph.Materialize());
        foreach (var revision in migrated.ApplicationRevisions)
            Assert.Equal(await memoryContent.ReadVerifiedAsync(saved.Revisions.Single(old => old.RevisionNumber == revision.RevisionNumber).Content),
                await StorageTestDocuments.PackageAsync(provider, revision.Package));
        var next = await service.ExecuteAsync(saved.ResourceId, DocumentPartitionKind.FileContents,
            await StorageTestDocuments.SaveRequestAsync(service, migratedHead, MinimalDocx.Create("EF save after SQL upgrade")),
            new Dictionary<string, string>(), TestActor.Value);
        Assert.False(Assert.Single(next.Response.SubResponses).Status);
        Assert.Equal(saved.ContentVersion + 1, next.State.ContentVersion);
        Assert.Equal(3, next.State.Counters.Revisions);
        await using var consumer = source.CreateCommand("SELECT value FROM consumer_business_data WHERE id=1");
        Assert.Equal("keep consumer tables", await consumer.ExecuteScalarAsync());
    });

    private static async Task<string> Rows(NpgsqlDataSource source)
    {
        // A deterministic digest covers every pre-existing library data row.
        await using var command = source.CreateCommand($$"""
            SELECT jsonb_build_object(
              'documents',(SELECT jsonb_agg(to_jsonb(t) ORDER BY resource_id) FROM cellbridge_documents t),
              'states',(SELECT jsonb_agg(to_jsonb(t) ORDER BY resource_id,state_version) FROM cellbridge_states t),
              'objects',(SELECT jsonb_agg(to_jsonb(t) ORDER BY object_key) FROM cellbridge_objects t),
              'chunks',(SELECT jsonb_agg(to_jsonb(t) ORDER BY object_key,ordinal) FROM cellbridge_chunks t),
              'heads',(SELECT jsonb_agg(to_jsonb(t) ORDER BY resource_id) FROM cellbridge_read_heads t),
              'summaries',(SELECT jsonb_agg(to_jsonb(t) ORDER BY resource_id,lifecycle_generation,revision_number) FROM cellbridge_revision_summaries t),
              'receipts',(SELECT jsonb_agg(to_jsonb(t) ORDER BY operation_id) FROM cellbridge_recovery_receipts t))::text
            """);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    internal static Task WithInitializedDatabaseAsync(StorageLimits limits, Func<NpgsqlDataSource, Task> test) =>
        WithEmptyDatabase(async source =>
        {
            await new PostgreSqlStorageMigrator(source, limits).MigrateAsync();
            await test(source);
        });

    internal static async Task WithEmptyDatabase(Func<NpgsqlDataSource, Task> test)
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

    private sealed class LeaseTrackingInterceptor : DbCommandInterceptor
    {
        private Exception? _nextFailure;
        private DelayedDisposeFailure? _nextReaderDisposeFailure;
        private DelayedReader? _nextChunkReaderDelay;
        public List<DbContext> Contexts { get; } = [];
        public List<int?> CommandTimeouts { get; } = [];

        public void Clear()
        {
            Contexts.Clear();
            CommandTimeouts.Clear();
        }
        public void FailNext(Exception exception) => _nextFailure = exception;
        public DelayedDisposeFailure DelayNextReaderDisposeFailure()
        {
            var failure = new DelayedDisposeFailure();
            if (Interlocked.CompareExchange(ref _nextReaderDisposeFailure, failure, null) is not null)
                throw new InvalidOperationException("A reader disposal failure is already pending.");
            return failure;
        }
        public DelayedReader DelayNextChunkReader()
        {
            var delay = new DelayedReader();
            if (Interlocked.CompareExchange(ref _nextChunkReaderDelay, delay, null) is not null)
                throw new InvalidOperationException("A chunk reader delay is already pending.");
            return delay;
        }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (eventData.Context is { } context)
            {
                Contexts.Add(context);
                CommandTimeouts.Add(context.Database.GetCommandTimeout());
            }
            var failure = Interlocked.Exchange(ref _nextFailure, null);
            if (failure is not null) throw failure;
            if (command.CommandText.Contains("cellbridge_chunks", StringComparison.OrdinalIgnoreCase) &&
                Interlocked.Exchange(ref _nextChunkReaderDelay, null) is { } delay)
                await delay.WaitAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }

        public override InterceptionResult DataReaderDisposing(DbCommand command,
            DataReaderDisposingEventData eventData, InterceptionResult result)
        {
            Interlocked.Exchange(ref _nextReaderDisposeFailure, null)?.WaitAndThrow();
            return result;
        }
    }

    private sealed class DelayedDisposeFailure
    {
        private readonly TaskCompletionSource _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => _started.Task;
        public void Release() => _release.TrySetResult();

        public void WaitAndThrow()
        {
            _started.TrySetResult();
            _release.Task.GetAwaiter().GetResult();
            throw new InvalidOperationException("Injected reader disposal failure.");
        }
    }

    private sealed class DelayedReader
    {
        private readonly TaskCompletionSource _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _cancellationObserved =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => _started.Task;
        public Task CancellationObserved => _cancellationObserved.Task;
        public void Release() => _release.TrySetResult();

        public async Task WaitAsync(CancellationToken cancellationToken)
        {
            _started.TrySetResult();
            try { await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                _cancellationObserved.TrySetResult();
                await _release.Task.ConfigureAwait(false);
                throw;
            }
        }
    }
}
