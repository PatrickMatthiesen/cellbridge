using System.Text.Json;
using System.Text.Json.Nodes;
using CellBridge.AspNetCore;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.PostgreSql;
using CellBridge.Web;

namespace CellBridge.Storage.Tests;

public sealed class StorageInitializationTests
{
    [PostgreSqlFact]
    public Task Version1IsRejectedWithoutChangingDocuments() => RejectSchemaAsync(1);

    [PostgreSqlFact]
    public Task Version2IsRejectedWithoutChangingDocuments() => RejectSchemaAsync(2);

    [PostgreSqlFact]
    public Task FutureVersionIsRejectedWithoutChangingDocuments() => RejectSchemaAsync(4);

    private static Task RejectSchemaAsync(int schemaVersion) =>
        BudgetAndQueryTests.WithDatabase(new(), async source =>
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
            await using var snapshot = source.CreateCommand("SELECT state_json FROM cellbridge_states WHERE resource_id=$1");
            snapshot.Parameters.AddWithValue(document.ResourceId);
            Assert.Equal(JsonSerializer.Serialize(document), await snapshot.ExecuteScalarAsync());
        });

    [PostgreSqlFact]
    public Task RepeatedInitializationPreservesCurrentOwnershipAndContent() =>
        BudgetAndQueryTests.WithDatabase(new(), async source =>
        {
            var states = new PostgreSqlStateStore(source);
            var provider = new StorageProvider(states, new PostgreSqlContentStore(source));
            var document = (await new CellBridgeDocumentService(provider).CreateAsync(
                "/shared/current.docx", MinimalDocx.Create(), TestActor.Value))!;
            await states.InitializeAsync();
            var after = await states.FindByResourceIdAsync(document.ResourceId);
            Assert.Equal(JsonSerializer.Serialize(document), JsonSerializer.Serialize(after));
            await using var version = source.CreateCommand("SELECT version FROM cellbridge_schema");
            Assert.Equal(3, await version.ExecuteScalarAsync());
        });

    [PostgreSqlFact]
    public Task MissingOrNullMappingMetadataIsRejectedOnPersistedRead() =>
        BudgetAndQueryTests.WithDatabase(new(), async source =>
        {
            var states = new PostgreSqlStateStore(source);
            var provider = new StorageProvider(states, new PostgreSqlContentStore(source));
            var document = (await new CellBridgeDocumentService(provider).CreateAsync(
                "/shared/incomplete.docx", MinimalDocx.Create(), TestActor.Value))!;
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
                await Assert.ThrowsAsync<StorageCorruptionException>(() => states.FindByResourceIdAsync(document.ResourceId).AsTask());
            }
        });
}
