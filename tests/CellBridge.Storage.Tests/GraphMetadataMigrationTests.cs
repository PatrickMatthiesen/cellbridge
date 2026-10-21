using System.Text.Json;
using CellBridge.AspNetCore;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.PostgreSql;
using CellBridge.TestFixtures;

namespace CellBridge.Storage.Tests;

public sealed class GraphMetadataMigrationTests
{
    [PostgreSqlFact]
    public Task Schema4MigrationPreservesEverySnapshotHistoryReceiptAndUsage() =>
        BudgetAndQueryTests.WithDatabase(new(), async source =>
        {
            var store = new PostgreSqlStateStore(source);
            var content = new PostgreSqlContentStore(source);
            var provider = new StorageProvider(store, content);
            var service = new CellBridgeDocumentService(provider);
            var state = (await service.CreateAsync("/graph-migration.docx", MinimalDocx.Create("original"), TestActor.Value))!;
            for (var i = 0; i < 10; i++) state = await RevisionHistoryTests.Save(service, state, "revision " + i);
            var snapshot = await store.CaptureRecoveryAsync();
            foreach (var old in snapshot.Snapshots)
            {
                await using var expand = source.CreateCommand("UPDATE cellbridge_states SET state_json=$3 WHERE resource_id=$1 AND state_version=$2");
                expand.Parameters.AddWithValue(old.ResourceId);
                expand.Parameters.AddWithValue(old.StateVersion);
                expand.Parameters.AddWithValue(JsonSerializer.Serialize(old));
                await expand.ExecuteNonQueryAsync();
            }
            await using (var oldVersion = source.CreateCommand("UPDATE cellbridge_schema SET version=4"))
                await oldVersion.ExecuteNonQueryAsync();
            await Assert.ThrowsAsync<StorageUnavailableException>(() => store.TransitionAsync(state.ResourceId,
                (current, _) => new StateTransition<bool>(current, true)).AsTask());
            await store.InitializeAsync();
            var after = await store.CaptureRecoveryAsync();
            Assert.Equal(JsonSerializer.Serialize(snapshot), JsonSerializer.Serialize(after));
            foreach (var revision in state.Revisions)
            {
                var graph = await DocumentPartition.RestoreGraphAsync(revision.Partitions.Single(p => p.Kind == 0), content, provider.Limits);
                Assert.Equal(await content.ReadVerifiedAsync(revision.Content),
                    CellBridge.FssHttpB.PartitionGraphSnapshot.Create(graph.Elements, graph.StorageIndex).Materialize());
            }
            await using (var ledger = source.CreateCommand("SELECT stored_bytes=(SELECT COALESCE(SUM(length),0) FROM cellbridge_objects)+(SELECT COALESCE(SUM(octet_length(state_json)),0) FROM cellbridge_states)+(SELECT COALESCE(SUM(octet_length(receipt_json)),0) FROM cellbridge_recovery_receipts) FROM cellbridge_usage"))
                Assert.Equal(true, await ledger.ExecuteScalarAsync());
            await using (var compact = source.CreateCommand("SELECT bool_and(state_json::jsonb ? 'GraphMetadata') FROM cellbridge_states"))
                Assert.Equal(true, await compact.ExecuteScalarAsync());
        });

    [PostgreSqlFact]
    public Task FailedMigrationRollsBackVersionStateAndUsage() =>
        BudgetAndQueryTests.WithDatabase(new(), async source =>
        {
            var store = new PostgreSqlStateStore(source);
            var state = (await new CellBridgeDocumentService(new(store, new PostgreSqlContentStore(source)))
                .CreateAsync("/migration-rollback.docx", MinimalDocx.Create(), TestActor.Value))!;
            var original = JsonSerializer.Serialize(state);
            await using (var version = source.CreateCommand("UPDATE cellbridge_schema SET version=4"))
                await version.ExecuteNonQueryAsync();
            await using (var setup = source.CreateCommand("UPDATE cellbridge_states SET state_json=$1"))
            {
                setup.Parameters.AddWithValue(original);
                await setup.ExecuteNonQueryAsync();
            }
            long usage;
            await using (var read = source.CreateCommand("SELECT stored_bytes FROM cellbridge_usage"))
                usage = (long)(await read.ExecuteScalarAsync())!;
            var restricted = new PostgreSqlStateStore(source, new() { MaxStoredBytes = 1 });
            await Assert.ThrowsAsync<StorageQuotaExceededException>(() => restricted.InitializeAsync());
            await using (var version = source.CreateCommand("SELECT version FROM cellbridge_schema"))
                Assert.Equal(4, await version.ExecuteScalarAsync());
            await using (var json = source.CreateCommand("SELECT state_json FROM cellbridge_states"))
                Assert.Equal(original, await json.ExecuteScalarAsync());
            await using (var read = source.CreateCommand("SELECT stored_bytes FROM cellbridge_usage"))
                Assert.Equal(usage, await read.ExecuteScalarAsync());
        });
}
