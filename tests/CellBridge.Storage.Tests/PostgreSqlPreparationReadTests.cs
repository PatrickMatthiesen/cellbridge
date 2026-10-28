using CellBridge.AspNetCore;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.PostgreSql;
using CellBridge.TestFixtures;

namespace CellBridge.Storage.Tests;

[Collection("PostgreSQL")]
public sealed class PostgreSqlPreparationReadTests
{
    [PostgreSqlFact]
    public Task PreparationUsesCurrentSnapshotAndDatabaseClockAndRejectsMissingSnapshot() =>
        BudgetAndQueryTests.WithDatabase(new(), async source =>
        {
            var store = new PostgreSqlStateStore(source);
            var initial = (await new CellBridgeDocumentService(new(store, new PostgreSqlContentStore(source)))
                .CreateAsync("/preparation.docx", MinimalDocx.Create(), TestActor.Value))!;
            Assert.Null(await store.ReadPreparationAsync(Guid.NewGuid()));

            await using var beforeCommand = source.CreateCommand("SELECT clock_timestamp()");
            var before = (DateTime)(await beforeCommand.ExecuteScalarAsync())!;
            var view = (await store.ReadPreparationAsync(initial.ResourceId))!;
            await using var afterCommand = source.CreateCommand("SELECT clock_timestamp()");
            var after = (DateTime)(await afterCommand.ExecuteScalarAsync())!;
            Assert.Equal(initial.StateVersion, view.State.StateVersion);
            Assert.Equal(initial.Content, view.State.Content);
            Assert.Equal(DateTimeKind.Utc, view.UtcNow.Kind);
            Assert.InRange(view.UtcNow, before, after);

            await using (var remove = source.CreateCommand("DELETE FROM cellbridge_states WHERE resource_id=$1"))
            {
                remove.Parameters.AddWithValue(initial.ResourceId);
                await remove.ExecuteNonQueryAsync();
            }
            await Assert.ThrowsAsync<StorageCorruptionException>(() => store.ReadPreparationAsync(initial.ResourceId).AsTask());
        });
}
