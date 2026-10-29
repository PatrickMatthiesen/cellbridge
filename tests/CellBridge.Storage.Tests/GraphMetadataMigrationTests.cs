using System.Text.Json;
using CellBridge.AspNetCore;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using CellBridge.Storage.PostgreSql;
using CellBridge.TestFixtures;

namespace CellBridge.Storage.Tests;

[Collection("PostgreSQL")]
public sealed class GraphMetadataMigrationTests
{
    [PostgreSqlFact]
    public Task Schema4MigrationPreservesEverySnapshotHistoryAndReceipt() =>
        EfPostgreSqlMigrationTests.WithEmptyDatabase(async source =>
        {
            var memory = new InMemoryContentStore();
            var state = await LegacyStorageFixture.CreateAsync(memory, "/graph-migration.docx", MinimalDocx.Create("original"));
            var snapshots = new List<DocumentState> { state };
            for (var i = 0; i < 10; i++)
            {
                state = (await LegacyStorageFixture.SaveAsync(state, memory, MinimalDocx.Create("revision " + i))).State;
                snapshots.Add(state);
            }
            await LegacyPostgreSqlFixture.SeedAsync(source, memory, snapshots, 4, expandedJson: true);
            var store = new PostgreSqlStateStore(source);
            var repository = (IDocumentRepository)store;
            await Assert.ThrowsAsync<StorageUnavailableException>(() => repository.CheckHealthAsync().AsTask());
            await store.InitializeAsync();
            var provider = new StorageProvider(repository, new PostgreSqlContentStore(source));
            var migrated = (await repository.ExportDocumentAsync(state.ResourceId))!;
            Assert.Equal(state.Revisions.Length, migrated.ApplicationRevisions.Length);
            Assert.Equal(state.Receipts.OrderBy(receipt => receipt.OperationKey, StringComparer.Ordinal).ThenBy(receipt => receipt.Digest, StringComparer.Ordinal).Select(receipt => receipt.Response),
                migrated.Receipts.OrderBy(receipt => receipt.Operation.OperationKey, StringComparer.Ordinal).ThenBy(receipt => receipt.Digest, StringComparer.Ordinal).Select(receipt => receipt.Response));
            Assert.Equal(NormalizedDocumentRecovery.StoredBytes(migrated), migrated.Current.Head.Counters.StoredBytes);
            foreach (var revision in migrated.ApplicationRevisions)
            {
                var legacy = state.Revisions.Single(old => old.RevisionNumber == revision.RevisionNumber);
                Assert.Equal(await memory.ReadVerifiedAsync(legacy.Content), await StorageTestDocuments.PackageAsync(provider, revision.Package));
            }
            await using (var rows = source.CreateCommand("SELECT state_json FROM cellbridge_states ORDER BY state_version"))
            await using (var reader = await rows.ExecuteReaderAsync())
            {
                var count = 0;
                while (await reader.ReadAsync())
                {
                    var json = reader.GetString(0);
                    Assert.True(JsonDocument.Parse(json).RootElement.TryGetProperty("GraphMetadata", out _));
                    Assert.Equal(JsonSerializer.Serialize(snapshots[count++]), JsonSerializer.Serialize(DocumentStateJson.Deserialize(json)));
                }
                Assert.Equal(snapshots.Count, count);
            }
            var before = await repository.CaptureRecoveryAsync();
            await store.InitializeAsync();
            var after = await repository.CaptureRecoveryAsync();
            Assert.Equal(PortabilityTests.CanonicalSnapshots(before.Snapshots), PortabilityTests.CanonicalSnapshots(after.Snapshots));
            Assert.Equal(JsonSerializer.Serialize(before.Receipts), JsonSerializer.Serialize(after.Receipts));
        });

    [PostgreSqlFact]
    public Task FailedMigrationRollsBackVersionAndState() =>
        EfPostgreSqlMigrationTests.WithEmptyDatabase(async source =>
        {
            var memory = new InMemoryContentStore();
            var state = await LegacyStorageFixture.CreateAsync(memory, "/migration-rollback.docx", MinimalDocx.Create());
            var original = JsonSerializer.Serialize(state);
            await LegacyPostgreSqlFixture.SeedAsync(source, memory, [state], 4, expandedJson: true);
            var restricted = new PostgreSqlStateStore(source, new() { MaxDocumentBytes = 1 });
            await Assert.ThrowsAsync<StorageQuotaExceededException>(() => restricted.InitializeAsync());
            await using (var version = source.CreateCommand("SELECT version FROM cellbridge_schema"))
                Assert.Equal(4, await version.ExecuteScalarAsync());
            await using (var json = source.CreateCommand("SELECT state_json FROM cellbridge_states"))
                Assert.Equal(original, await json.ExecuteScalarAsync());
            await using (var normalized = source.CreateCommand("SELECT to_regclass('cellbridge_repository_heads') IS NULL"))
                Assert.Equal(true, await normalized.ExecuteScalarAsync());
            var content = new PostgreSqlContentStore(source);
            foreach (var handle in StorageReferences.Handles(state).DistinctBy(handle => handle.Key))
                Assert.Equal(await memory.ReadVerifiedAsync(handle), await content.ReadVerifiedAsync(handle));
        });
}
