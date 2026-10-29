using System.Text.Json;
using System.Text.Json.Nodes;
using CellBridge.AspNetCore;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.PostgreSql;
using CellBridge.TestFixtures;

namespace CellBridge.Storage.Tests;

[Collection("PostgreSQL")]
public sealed class StorageInitializationTests
{
    [PostgreSqlFact]
    public Task Version1IsRejectedWithoutChangingDocuments() => RejectSchemaAsync(1);

    [PostgreSqlFact]
    public Task Version2IsRejectedWithoutChangingDocuments() => RejectSchemaAsync(2);

    [PostgreSqlFact]
    public Task FutureVersionIsRejectedWithoutChangingDocuments() => RejectSchemaAsync(99);

    private static Task RejectSchemaAsync(int schemaVersion) =>
        EfPostgreSqlMigrationTests.WithInitializedDatabaseAsync(new(), async source =>
        {
            var states = new PostgreSqlStateStore(source);
            var provider = new StorageProvider(states, new PostgreSqlContentStore(source));
            var document = (await new CellBridgeDocumentService(provider).CreateAsync(
                "/shared/existing.docx", MinimalDocx.Create(), TestActor.Value))!;
            await using (var change = source.CreateCommand("UPDATE cellbridge_schema SET version=$1"))
            {
                change.Parameters.AddWithValue(schemaVersion);
                await change.ExecuteNonQueryAsync();
            }
            await Assert.ThrowsAsync<StorageUnavailableException>(() => states.InitializeAsync());
            await using (var version = source.CreateCommand("SELECT version FROM cellbridge_schema"))
                Assert.Equal(schemaVersion, await version.ExecuteScalarAsync());
            await using var snapshot = source.CreateCommand("SELECT \"Json\" FROM cellbridge_repository_heads WHERE \"ResourceId\"=$1");
            snapshot.Parameters.AddWithValue(document.ResourceId);
            Assert.Equal(JsonSerializer.Serialize(document), await snapshot.ExecuteScalarAsync());
        });

    [PostgreSqlFact]
    public Task RepeatedInitializationPreservesCurrentOwnershipAndContent() =>
        EfPostgreSqlMigrationTests.WithInitializedDatabaseAsync(new(), async source =>
        {
            var states = new PostgreSqlStateStore(source);
            var provider = new StorageProvider(states, new PostgreSqlContentStore(source));
            var document = (await new CellBridgeDocumentService(provider).CreateAsync(
                "/shared/current.docx", MinimalDocx.Create(), TestActor.Value))!;
            await states.InitializeAsync();
            var after = await provider.Documents.FindAsync(new DocumentIdentity.Resource(document.ResourceId));
            Assert.Equal(JsonSerializer.Serialize(document), JsonSerializer.Serialize(after));
            await using var version = source.CreateCommand("SELECT version FROM cellbridge_schema");
            Assert.Equal(10, await version.ExecuteScalarAsync());
        });

    [PostgreSqlFact]
    public Task Schema3MigratesInPlaceAndCurrentWritersFailClosedUntilMigration() =>
        EfPostgreSqlMigrationTests.WithEmptyDatabase(async source =>
        {
            var memory = new CellBridge.Storage.InMemory.InMemoryContentStore();
            var document = await LegacyStorageFixture.CreateAsync(memory, "/migration.docx", MinimalDocx.Create());
            await LegacyPostgreSqlFixture.SeedAsync(source, memory, [document], 3, expandedJson: true);
            await using (var oldSchema = source.CreateCommand("DROP INDEX cellbridge_live_paths; ALTER TABLE cellbridge_documents DROP COLUMN is_deleted; ALTER TABLE cellbridge_documents ADD CONSTRAINT cellbridge_documents_path_key_key UNIQUE(path_key)"))
                await oldSchema.ExecuteNonQueryAsync();
            var store = new PostgreSqlStateStore(source);
            var repository = (IDocumentRepository)store;
            await Assert.ThrowsAsync<StorageUnavailableException>(() => repository.CheckHealthAsync().AsTask());
            await store.InitializeAsync();
            var head = (await repository.FindAsync(new DocumentIdentity.Resource(document.ResourceId)))!;
            Assert.Equal(document.Security, head.Security);
            Assert.Equal(document.Content.Sha256, head.Package.Sha256);
            var deleted = await repository.ChangeLifecycleAsync(new DocumentLifecycleCommand.Delete(head.ResourceId,
                head.LifecycleGeneration, head.StateVersion));
            Assert.True(deleted.Applied);
            Assert.Null(await repository.FindAsync(new DocumentIdentity.Resource(document.ResourceId)));
        });

    [PostgreSqlFact]
    public Task MissingOrNullLegacyMappingMetadataIsRejectedBeforeMigrationPublishes() =>
        EfPostgreSqlMigrationTests.WithEmptyDatabase(async source =>
        {
            var memory = new CellBridge.Storage.InMemory.InMemoryContentStore();
            var document = await LegacyStorageFixture.CreateAsync(memory, "/shared/incomplete.docx", MinimalDocx.Create());
            await LegacyPostgreSqlFixture.SeedAsync(source, memory, [document], 4, expandedJson: true);
            foreach (var missing in new[] { true, false })
            {
                var snapshot = JsonNode.Parse(JsonSerializer.Serialize(document))!;
                var partition = snapshot["Partitions"]!.AsArray().Single(p => p!["Kind"]!.GetValue<int>() == 0)!;
                foreach (var element in partition["Elements"]!.AsArray())
                {
                    if (missing) element!.AsObject().Remove("MappingSerials");
                    else element!["MappingSerials"] = null;
                }
                await using var update = source.CreateCommand("UPDATE cellbridge_states SET state_json=$1 WHERE resource_id=$2");
                update.Parameters.AddWithValue(snapshot.ToJsonString());
                update.Parameters.AddWithValue(document.ResourceId);
                await update.ExecuteNonQueryAsync();
                await Assert.ThrowsAsync<StorageCorruptionException>(() => new PostgreSqlStorageMigrator(source).MigrateAsync());
                await using var version = source.CreateCommand("SELECT version FROM cellbridge_schema");
                Assert.Equal(4, await version.ExecuteScalarAsync());
            }
        });
}
