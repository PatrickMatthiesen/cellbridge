using CellBridge.Storage.Abstractions;
using CellBridge.Storage.PostgreSql;
using CellBridge.Tests;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using Xunit.Abstractions;

namespace CellBridge.Storage.Tests;

[Collection("PostgreSQL")]
public sealed class PostgreSqlCurrentMetadataReadTests(ITestOutputHelper output)
{
    [PostgreSqlFact]
    public Task PreparationAndPublicationFenceReuseHeadersAndBatchOnlyAffectedCurrentMappings() =>
        BudgetAndQueryTests.WithDatabase(new(), async source =>
        {
            var content = new PostgreSqlContentStore(source);
            await CurrentMetadataReadScenario.RunAsync(new(new PostgreSqlStateStore(source, validationContent: content), content), interceptors =>
                new PostgreSqlStateStore(new DbContextOptionsBuilder<CellBridgeDbContext>().UseNpgsql(source).AddInterceptors(interceptors).Options,
                    validationContent: content), output.WriteLine);
        });

    [PostgreSqlFact]
    public Task CurrentLookupValidatesScopeAndRootIdentityAndOfflineRecoveryRejectsAMissingProjection() =>
        BudgetAndQueryTests.WithDatabase(new(), async source =>
        {
            var content = new PostgreSqlContentStore(source); IDocumentRepository repository = new PostgreSqlStateStore(source);
            var head = (await new CellBridge.AspNetCore.CellBridgeDocumentService(new(repository, content))
                .CreateAsync("/projection-integrity.docx", CellBridge.TestFixtures.MinimalDocx.Create(), TestActor.Value))!;
            var partition = (await repository.ReadPartitionAsync(head.ResourceId, new(0)))!.Partition;
            var scope = new IndexedGraphScope(head.ResourceId, head.LifecycleGeneration, 0);
            var mapping = (await repository.ReadMappingsAsync(scope, partition.PublicationSequence))[0];
            var request = new SavePreparationRequest(0, new(head.LifecycleGeneration, SaveOperationFamily.Protocol, 0, "integrity"), [mapping.Key]);
            await using (var forge = source.CreateCommand("UPDATE cellbridge_repository_mappings SET \"Json\"=$1 WHERE \"ResourceId\"=$2 AND \"Partition\"=0"))
            { forge.Parameters.AddWithValue(JsonSerializer.Serialize(mapping with { Scope = scope with { ResourceId = Guid.NewGuid() } })); forge.Parameters.AddWithValue(head.ResourceId); await forge.ExecuteNonQueryAsync(); }
            await Assert.ThrowsAsync<StorageCorruptionException>(() => repository.PrepareSaveAsync(head.ResourceId, request).AsTask());
            await using (var repair = source.CreateCommand("UPDATE cellbridge_repository_mappings m SET \"Json\"=i.\"Json\" FROM cellbridge_repository_mapping_intervals i WHERE m.\"ResourceId\"=$1 AND m.\"Partition\"=0 AND i.\"ResourceId\"=m.\"ResourceId\" AND i.\"Lifecycle\"=m.\"Lifecycle\" AND i.\"Partition\"=m.\"Partition\" AND i.\"MappingKey\"=m.\"MappingKey\" AND i.\"StartSequence\"=m.\"StartSequence\""))
            { repair.Parameters.AddWithValue(head.ResourceId); await repair.ExecuteNonQueryAsync(); }
            await using (var forge = source.CreateCommand("UPDATE cellbridge_repository_mapping_roots SET \"Json\"=$1 WHERE \"ResourceId\"=$2 AND \"Partition\"=0"))
            { forge.Parameters.AddWithValue(JsonSerializer.Serialize(partition with { StorageIndex = new(1, Guid.NewGuid()) })); forge.Parameters.AddWithValue(head.ResourceId); await forge.ExecuteNonQueryAsync(); }
            await Assert.ThrowsAsync<StorageCorruptionException>(() => repository.PrepareSaveAsync(head.ResourceId, request).AsTask());
            await using (var repair = source.CreateCommand("UPDATE cellbridge_repository_mapping_roots SET \"Json\"=$1 WHERE \"ResourceId\"=$2 AND \"Partition\"=0"))
            { repair.Parameters.AddWithValue(JsonSerializer.Serialize(partition)); repair.Parameters.AddWithValue(head.ResourceId); await repair.ExecuteNonQueryAsync(); }
            Assert.Single((await repository.PrepareSaveAsync(head.ResourceId, request))!.Partition.Mappings);
            await using (var remove = source.CreateCommand("DELETE FROM cellbridge_repository_mappings WHERE \"ResourceId\"=$1 AND \"Partition\"=0"))
            { remove.Parameters.AddWithValue(head.ResourceId); await remove.ExecuteNonQueryAsync(); }
            // Current rows are authoritative for hot reads; history remains intact
            // and complete offline export must reject their missing projection.
            Assert.Empty((await repository.PrepareSaveAsync(head.ResourceId, request))!.Partition.Mappings);
            Assert.NotNull(await repository.FindMappingAsync(scope, partition.StorageIndex!, mapping.Key));
            await Assert.ThrowsAsync<StorageCorruptionException>(() => repository.CaptureRecoveryAsync().AsTask());
        });
}
