using System.Collections.Immutable;
using System.Text.Json;
using CellBridge.AspNetCore;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.PostgreSql;
using CellBridge.TestFixtures;
using Npgsql;

namespace CellBridge.Storage.Tests;

[Collection("PostgreSQL")]
public sealed class PostgreSqlSelectiveReadTests
{
    [PostgreSqlFact]
    public Task SelectiveReadsPreserveUnsignedRevisionNumbersAndNeverDecodeFullSnapshots() =>
        BudgetAndQueryTests.WithDatabase(new(), async source =>
        {
            var store = new PostgreSqlStateStore(source);
            var service = new CellBridgeDocumentService(new(store, new PostgreSqlContentStore(source)));
            var state = (await service.CreateAsync("/selective.docx", MinimalDocx.Create("current"), TestActor.Value))!;
            var original = Assert.Single(state.Revisions);
            var firstNumber = (ulong)long.MaxValue + 7;
            await store.TransitionAsync(state.ResourceId, (current, _) => new StateTransition<bool>(current with
            {
                Revisions = [original with { RevisionNumber = firstNumber }, original with { RevisionNumber = ulong.MaxValue }],
            }, true));
            var current = (await store.FindByResourceIdAsync(state.ResourceId))!;

            // If any selective call touches authoritative JSON, it now fails. This
            // is an isolated test database; no repair or fallback is involved.
            await using (var corrupt = source.CreateCommand("UPDATE cellbridge_states SET state_json='not JSON' WHERE resource_id=$1"))
            {
                corrupt.Parameters.AddWithValue(state.ResourceId);
                await corrupt.ExecuteNonQueryAsync();
            }
            await Assert.ThrowsAsync<StorageCorruptionException>(() => store.FindByResourceIdAsync(state.ResourceId).AsTask());

            var metadata = (await store.FindReadMetadataByResourceIdAsync(state.ResourceId))!;
            AssertMetadata(current, metadata);
            Assert.Equal(JsonSerializer.Serialize(metadata), JsonSerializer.Serialize(await store.FindReadMetadataByPathKeyAsync(current.PathKey)));
            var page = (await store.ReadRevisionSummariesAsync(state.ResourceId, 0, 1))!;
            Assert.Equal(ulong.MaxValue, Assert.Single(page.Revisions).RevisionNumber);
            Assert.Equal(1, page.NextOffset);
            var next = (await store.ReadRevisionSummariesAsync(state.ResourceId, page.NextOffset!.Value, 1))!;
            Assert.Equal(firstNumber, Assert.Single(next.Revisions).RevisionNumber);
            Assert.Null(next.NextOffset);
            Assert.Empty((await store.ReadRevisionSummariesAsync(state.ResourceId, 100, 5))!.Revisions);
            Assert.Equal(ulong.MaxValue, (await store.ReadRevisionSummaryAsync(state.ResourceId, ulong.MaxValue))!.Revision!.RevisionNumber);
            Assert.Null((await store.ReadRevisionSummaryAsync(state.ResourceId, 1))!.Revision);
            var summary = Assert.Single(await store.ListAsync(0, 10));
            Assert.Equal(current.ResourceId, summary.ResourceId);
            Assert.Equal(current.Path, summary.Path);
        });

    [PostgreSqlFact]
    public Task MissingStaleMalformedAndIncompleteReadProjectionsFailExplicitly() =>
        BudgetAndQueryTests.WithDatabase(new(), async source =>
        {
            var store = new PostgreSqlStateStore(source);
            var state = (await new CellBridgeDocumentService(new(store, new PostgreSqlContentStore(source)))
                .CreateAsync("/projection-corruption.docx", MinimalDocx.Create(), TestActor.Value))!;
            await using (var stale = source.CreateCommand("UPDATE cellbridge_read_heads SET state_version=state_version+1"))
                await stale.ExecuteNonQueryAsync();
            await Assert.ThrowsAsync<StorageCorruptionException>(() => store.FindReadMetadataByResourceIdAsync(state.ResourceId).AsTask());
            await Assert.ThrowsAsync<StorageCorruptionException>(() => store.ReadRevisionSummariesAsync(state.ResourceId, 0, 1).AsTask());
            await Assert.ThrowsAsync<StorageCorruptionException>(() => store.ListAsync(0, 10).AsTask());
            await using (var repair = source.CreateCommand("UPDATE cellbridge_read_heads SET state_version=state_version-1"))
                await repair.ExecuteNonQueryAsync();

            await using (var malformed = source.CreateCommand("UPDATE cellbridge_revision_summaries SET summary_json='{}'"))
                await malformed.ExecuteNonQueryAsync();
            await Assert.ThrowsAsync<StorageCorruptionException>(() => store.ReadRevisionSummaryAsync(state.ResourceId, 1).AsTask());
            await using (var missing = source.CreateCommand("DELETE FROM cellbridge_revision_summaries"))
                await missing.ExecuteNonQueryAsync();
            await Assert.ThrowsAsync<StorageCorruptionException>(() => store.FindReadMetadataByPathKeyAsync(state.PathKey).AsTask());
            await using (var missing = source.CreateCommand("DELETE FROM cellbridge_read_heads"))
                await missing.ExecuteNonQueryAsync();
            await Assert.ThrowsAsync<StorageCorruptionException>(() => store.ReadRevisionSummaryAsync(state.ResourceId, 1).AsTask());
            Assert.Null(await store.FindReadMetadataByResourceIdAsync(Guid.NewGuid()));
        });

    [PostgreSqlFact]
    public Task Schema5MigrationBuildsReadIndexesWithoutRewritingSnapshotsOrChargingTheirBytes() =>
        BudgetAndQueryTests.WithDatabase(new(), async source =>
        {
            var store = new PostgreSqlStateStore(source);
            var service = new CellBridgeDocumentService(new(store, new PostgreSqlContentStore(source)));
            var state = (await service.CreateAsync("/projection-migration.docx", MinimalDocx.Create(), TestActor.Value))!;
            for (var index = 0; index < 4; index++) state = await RevisionHistoryTests.Save(service, state, "revision " + index);
            var before = await SnapshotJson(source);
            long usage;
            await using (var ledger = source.CreateCommand("SELECT stored_bytes FROM cellbridge_usage"))
                usage = (long)(await ledger.ExecuteScalarAsync())!;
            await using (var remove = source.CreateCommand("DROP TABLE cellbridge_read_heads,cellbridge_revision_summaries"))
                await remove.ExecuteNonQueryAsync();
            await using (var version = source.CreateCommand("DROP TABLE \"__CellBridgeDocumentMigrationsHistory\"; UPDATE cellbridge_schema SET version=5"))
                await version.ExecuteNonQueryAsync();
            await Assert.ThrowsAsync<StorageUnavailableException>(() => store.CheckHealthAsync().AsTask());
            await store.InitializeAsync();
            Assert.Equal(before, await SnapshotJson(source));
            AssertMetadata(state, await store.FindReadMetadataByResourceIdAsync(state.ResourceId));
            var page = (await store.ReadRevisionSummariesAsync(state.ResourceId, 0, 100))!;
            Assert.Equal(state.Revisions.Select(revision => revision.RevisionNumber).Reverse(),
                page.Revisions.Select(revision => revision.RevisionNumber));
            await using (var ledger = source.CreateCommand("SELECT stored_bytes FROM cellbridge_usage"))
                Assert.Equal(usage, await ledger.ExecuteScalarAsync());
            await using (var physical = source.CreateCommand("SELECT pg_total_relation_size('cellbridge_read_heads')+pg_total_relation_size('cellbridge_revision_summaries')"))
                Assert.True((long)(await physical.ExecuteScalarAsync())! > 0);
        });

    [PostgreSqlFact]
    public Task RenameDeleteAndRecreateKeepProjectionIdentityAndLegacyHistoryFenced() =>
        BudgetAndQueryTests.WithDatabase(new(), async source =>
        {
            var store = new PostgreSqlStateStore(source);
            var content = new PostgreSqlContentStore(source);
            var state = (await new CellBridgeDocumentService(new(store, content))
                .CreateAsync("/projection-before.docx", MinimalDocx.Create("before"), TestActor.Value))!;
            await store.RenameAsync(state.ResourceId, (current, _) => new StateTransition<bool>(current with
            {
                Path = "/projection-after.docx", PathKey = StorageIds.PathKey("/projection-after.docx"),
            }, true));
            var renamed = (await store.FindByResourceIdAsync(state.ResourceId))!;
            Assert.Null(await store.FindReadMetadataByPathKeyAsync(state.PathKey));
            var renamedRead = (await store.ReadRevisionSummaryAsync(state.ResourceId, 1))!;
            Assert.Equal(renamed.Path, renamedRead.Current.Path);
            Assert.Equal(state.Content, renamedRead.Revision!.Content);
            Assert.Equal(renamed.ResourceId, (await store.FindReadMetadataByPathKeyAsync(renamed.PathKey))!.ResourceId);
            Assert.True(await store.TryDeleteAsync(renamed.ResourceId, renamed.LifecycleGeneration, renamed.StateVersion));
            Assert.Null(await store.FindReadMetadataByResourceIdAsync(state.ResourceId));
            Assert.Null(await store.ReadRevisionSummariesAsync(state.ResourceId, 0, 10));
            Assert.Null(await store.ReadRevisionSummaryAsync(state.ResourceId, 1));
            Assert.Empty(await store.ListAsync(0, 10));

            var retired = (await store.FindLifecycleAsync(state.ResourceId))!;
            var replacement = await new DocumentStore().Put(renamed.Path, MinimalDocx.Create("replacement")).CaptureAsync(content);
            replacement = replacement with { LifecycleGeneration = retired.LifecycleGeneration + 1, Security = state.Security };
            Assert.Empty(replacement.Revisions); // Exercise the supported legacy initialization projection.
            Assert.True(await store.TryRecreateAsync(retired.ResourceId, retired.LifecycleGeneration, retired.StateVersion, replacement));
            var replacementRead = (await store.FindReadMetadataByPathKeyAsync(replacement.PathKey))!;
            Assert.Equal(replacement.ResourceId, replacementRead.ResourceId);
            Assert.Equal(2, replacementRead.LifecycleGeneration);
            Assert.Equal(1, replacementRead.RevisionCount);
            var history = (await store.ReadRevisionSummariesAsync(replacement.ResourceId, 0, 10))!;
            var revision = Assert.Single(history.Revisions);
            Assert.Equal(replacement.ResourceId, revision.ResourceId);
            Assert.Equal(replacement.Content, revision.Content);
            Assert.Null(await store.ReadRevisionSummaryAsync(retired.ResourceId, revision.RevisionNumber));
        });

    [PostgreSqlFact]
    public Task RemovedCurrentRevisionDoesNotReturnFromRetainedSnapshotsMigrationOrEitherImportOrder() =>
        BudgetAndQueryTests.WithDatabase(new(), async source =>
        {
            var store = new PostgreSqlStateStore(source);
            var content = new PostgreSqlContentStore(source);
            var service = new CellBridgeDocumentService(new(store, content));
            var state = (await service.CreateAsync("/projection-current-membership.docx", MinimalDocx.Create("original"), TestActor.Value))!;
            state = await RevisionHistoryTests.Save(service, state, "older retained revision");
            var removed = state.Revisions[^1];
            var removedBytes = await content.ReadVerifiedAsync(removed.Content);
            state = await RevisionHistoryTests.Save(service, state, "current revision");

            // This synthetic store-level change exercises exact current membership.
            // It is not a production history-retention or pruning policy. All old
            // snapshots, graph identities and content remain available to recovery.
            await store.TransitionAsync(state.ResourceId, (current, _) => new StateTransition<bool>(current with
            {
                Revisions = current.Revisions.Where(revision => revision.RevisionNumber != removed.RevisionNumber).ToImmutableArray(),
            }, true));
            await AssertCurrentMembership(store);
            var before = await SnapshotJson(source);
            await using (var remove = source.CreateCommand("DROP TABLE cellbridge_read_heads,cellbridge_revision_summaries"))
                await remove.ExecuteNonQueryAsync();
            await using (var version = source.CreateCommand("DROP TABLE \"__CellBridgeDocumentMigrationsHistory\"; UPDATE cellbridge_schema SET version=5"))
                await version.ExecuteNonQueryAsync();
            await store.InitializeAsync();
            Assert.Equal(before, await SnapshotJson(source));
            await AssertCurrentMembership(store);

            var snapshot = await store.CaptureRecoveryAsync();
            Assert.Contains(snapshot.Snapshots, old => old.StateVersion < snapshot.Heads.Single().StateVersion &&
                old.Revisions.Any(revision => revision.RevisionNumber == removed.RevisionNumber));
            Assert.Equal(removedBytes, await content.ReadVerifiedAsync(removed.Content));
            foreach (var reverse in new[] { false, true })
            {
                await BudgetAndQueryTests.WithDatabase(new(), async destination =>
                {
                    var target = new PostgreSqlStateStore(destination);
                    var targetContent = new PostgreSqlContentStore(destination);
                    foreach (var handle in ContentStoreReader.UniqueHandles(snapshot.Snapshots.SelectMany(StorageReferences.Handles)))
                    {
                        await using var bytes = await content.OpenReadAsync(handle);
                        Assert.Equal(handle, await targetContent.WriteAsync(bytes));
                    }
                    var ordered = snapshot with { Snapshots = reverse
                        ? snapshot.Snapshots.OrderByDescending(old => old.StateVersion).ToImmutableArray()
                        : snapshot.Snapshots.OrderBy(old => old.StateVersion).ToImmutableArray() };
                    await target.ImportRecoveryAsync(ordered, new(Guid.NewGuid(), new string('b', 64), snapshot.Heads.Length, snapshot.Snapshots.Length));
                    await AssertCurrentMembership(target);
                    Assert.Equal(JsonSerializer.Serialize(await store.FindReadMetadataByResourceIdAsync(state.ResourceId)),
                        JsonSerializer.Serialize(await target.FindReadMetadataByResourceIdAsync(state.ResourceId)));
                    Assert.Equal(JsonSerializer.Serialize((await store.ReadRevisionSummariesAsync(state.ResourceId, 0, 100))!.Revisions),
                        JsonSerializer.Serialize((await target.ReadRevisionSummariesAsync(state.ResourceId, 0, 100))!.Revisions));
                    var restored = await target.CaptureRecoveryAsync();
                    Assert.Contains(restored.Snapshots, old => old.StateVersion < restored.Heads.Single().StateVersion &&
                        old.Revisions.Any(revision => revision.RevisionNumber == removed.RevisionNumber));
                    Assert.Equal(removedBytes, await targetContent.ReadVerifiedAsync(removed.Content));
                });
            }

            async Task AssertCurrentMembership(PostgreSqlStateStore currentStore)
            {
                var selected = (await currentStore.ReadRevisionSummaryAsync(state.ResourceId, removed.RevisionNumber))!;
                Assert.Null(selected.Revision);
                Assert.Equal(2, selected.Current.RevisionCount);
                var page = (await currentStore.ReadRevisionSummariesAsync(state.ResourceId, 0, 10))!;
                Assert.Equal(new[] { state.Revisions[^1].RevisionNumber, state.Revisions[0].RevisionNumber },
                    page.Revisions.Select(revision => revision.RevisionNumber));
                Assert.DoesNotContain(page.Revisions, revision => revision.RevisionNumber == removed.RevisionNumber);
                Assert.Null(page.NextOffset);
            }
        });

    [PostgreSqlFact]
    public Task FailedSchema5ProjectionMigrationRollsBackTablesVersionAndJson() =>
        BudgetAndQueryTests.WithDatabase(new(), async source =>
        {
            var store = new PostgreSqlStateStore(source);
            var state = (await new CellBridgeDocumentService(new(store, new PostgreSqlContentStore(source)))
                .CreateAsync("/projection-rollback.docx", MinimalDocx.Create(), TestActor.Value))!;
            await using (var remove = source.CreateCommand("DROP TABLE cellbridge_read_heads,cellbridge_revision_summaries"))
                await remove.ExecuteNonQueryAsync();
            await using (var version = source.CreateCommand("DROP TABLE \"__CellBridgeDocumentMigrationsHistory\"; UPDATE cellbridge_schema SET version=5"))
                await version.ExecuteNonQueryAsync();
            await using (var corrupt = source.CreateCommand("UPDATE cellbridge_states SET state_json='not JSON'"))
                await corrupt.ExecuteNonQueryAsync();
            await Assert.ThrowsAsync<StorageCorruptionException>(() => store.InitializeAsync());
            await using (var check = source.CreateCommand("SELECT version,to_regclass('cellbridge_read_heads') IS NULL,to_regclass('cellbridge_revision_summaries') IS NULL FROM cellbridge_schema"))
            await using (var reader = await check.ExecuteReaderAsync())
            {
                Assert.True(await reader.ReadAsync());
                Assert.Equal(5, reader.GetInt32(0));
                Assert.True(reader.GetBoolean(1));
                Assert.True(reader.GetBoolean(2));
            }
            await using (var check = source.CreateCommand("SELECT state_json FROM cellbridge_states"))
                Assert.Equal("not JSON", await check.ExecuteScalarAsync());
        });

    private static void AssertMetadata(DocumentState expected, DocumentReadMetadata? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected.ResourceId, actual.ResourceId);
        Assert.Equal(expected.Path, actual.Path);
        Assert.Equal(expected.PathKey, actual.PathKey);
        Assert.Equal(expected.CreatedUtc, actual.CreatedUtc);
        Assert.Equal(expected.ModifiedUtc, actual.ModifiedUtc);
        Assert.Equal(expected.ContentVersion, actual.ContentVersion);
        Assert.Equal(expected.StateVersion, actual.StateVersion);
        Assert.Equal(expected.LifecycleGeneration, actual.LifecycleGeneration);
        Assert.Equal(expected.IsDeleted, actual.IsDeleted);
        Assert.Equal(expected.Content, actual.Content);
        Assert.Equal(JsonSerializer.Serialize(expected.Security), JsonSerializer.Serialize(actual.Security));
        Assert.Equal(expected.Revisions[^1].RevisionNumber, actual.LatestRevisionNumber);
        Assert.Equal(expected.Revisions.Length, actual.RevisionCount);
        Assert.Equal(expected.Editors.Select(editor => editor.ExpiresUtc), actual.EditorExpiries);
        Assert.Equal(expected.Publication?.Pending.Length ?? 0, actual.PendingDeliveryCount);
        Assert.Equal(expected.Publication?.BlockedReason, actual.DeliveryBlock);
    }

    private static async Task<string[]> SnapshotJson(NpgsqlDataSource source)
    {
        var result = new List<string>();
        await using var command = source.CreateCommand("SELECT state_json FROM cellbridge_states ORDER BY resource_id,state_version");
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) result.Add(reader.GetString(0));
        return result.ToArray();
    }
}
