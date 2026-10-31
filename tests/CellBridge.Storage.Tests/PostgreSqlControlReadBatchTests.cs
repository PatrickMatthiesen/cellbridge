using CellBridge.AspNetCore;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.PostgreSql;
using CellBridge.TestFixtures;
using CellBridge.Tests;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CellBridge.Storage.Tests;

[Collection("PostgreSQL")]
public sealed class PostgreSqlControlReadBatchTests
{
    [PostgreSqlFact]
    public Task CoherentBatchPreservesControlRowsReusesPartitionsAndGuardedClock() => BudgetAndQueryTests.WithDatabase(new(), async source =>
    {
        var content = new PostgreSqlContentStore(source);
        await ControlReadBatchScenario.RunAsync(new(new PostgreSqlStateStore(source), content), interceptors =>
            new PostgreSqlStateStore(new DbContextOptionsBuilder<CellBridgeDbContext>().UseNpgsql(source).AddInterceptors(interceptors).Options),
            combinedControlSnapshot: true);
    });

    [PostgreSqlFact]
    public Task ControlSnapshotValidatesDeletedHeadsAndPreservesMissingSemantics() => BudgetAndQueryTests.WithDatabase(new(), async source =>
    {
        var content = new PostgreSqlContentStore(source);
        var provider = new StorageProvider(new PostgreSqlStateStore(source), content);
        var actor = new CellBridgeActor(new("control:owner", "owner", "Owner"), CanCreate: true);
        var created = (await new CellBridgeDocumentService(provider).CreateAsync("/control-snapshot.docx", MinimalDocx.Create(), actor))!;
        Assert.NotNull(await provider.Documents.ReadControlAsync(created.ResourceId));
        Assert.Null(await provider.Documents.ReadControlAsync(Guid.NewGuid()));

        Assert.True((await provider.Documents.ChangeLifecycleAsync(new DocumentLifecycleCommand.Delete(
            created.ResourceId, created.LifecycleGeneration, created.StateVersion))).Applied);
        Assert.Null(await provider.Documents.ReadControlAsync(created.ResourceId));

        await using (var corrupt = source.CreateCommand("UPDATE cellbridge_repository_heads SET \"PathHash\"='corrupt' WHERE \"ResourceId\"=@id"))
        {
            corrupt.Parameters.AddWithValue("id", created.ResourceId);
            Assert.Equal(1, await corrupt.ExecuteNonQueryAsync());
        }
        await Assert.ThrowsAsync<StorageCorruptionException>(() => provider.Documents.ReadControlAsync(created.ResourceId).AsTask());
    });
}
