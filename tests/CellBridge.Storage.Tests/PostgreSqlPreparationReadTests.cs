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
        EfPostgreSqlMigrationTests.WithInitializedDatabaseAsync(new(), async source =>
        {
            IDocumentRepository store = new PostgreSqlStateStore(source);
            var initial = (await new CellBridgeDocumentService(new(store, new PostgreSqlContentStore(source)))
                .CreateAsync("/preparation.docx", MinimalDocx.Create(), TestActor.Value))!;
            Assert.Null(await store.ReadControlAsync(Guid.NewGuid()));

            await using var beforeCommand = source.CreateCommand("SELECT clock_timestamp()");
            var before = (DateTime)(await beforeCommand.ExecuteScalarAsync())!;
            var view = (await store.ReadControlAsync(initial.ResourceId))!;
            await using var afterCommand = source.CreateCommand("SELECT clock_timestamp()");
            var after = (DateTime)(await afterCommand.ExecuteScalarAsync())!;
            Assert.Equal(initial.StateVersion, view.Head.StateVersion);
            Assert.Equal(initial.Package, view.Head.Package);
            Assert.Equal(DateTimeKind.Utc, view.UtcNow.Kind);
            Assert.InRange(view.UtcNow, before, after);

            await using (var remove = source.CreateCommand("DELETE FROM cellbridge_repository_coordination WHERE \"ResourceId\"=$1"))
            {
                remove.Parameters.AddWithValue(initial.ResourceId);
                await remove.ExecuteNonQueryAsync();
            }
            await Assert.ThrowsAsync<StorageCorruptionException>(() => store.ReadControlAsync(initial.ResourceId).AsTask());
        });
}
