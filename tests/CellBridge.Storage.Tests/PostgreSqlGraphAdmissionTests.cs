using System.Collections.Immutable;
using System.Text.Json;
using CellBridge.AspNetCore;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.PostgreSql;
using CellBridge.TestFixtures;
using Npgsql;

namespace CellBridge.Storage.Tests;

[Collection("PostgreSQL")]
public sealed class PostgreSqlGraphAdmissionTests
{
    [PostgreSqlFact]
    public Task MembershipBudgetMigrationFailureRollsBackAndExactBudgetPreservesHistory() =>
        BudgetAndQueryTests.WithDatabase(new(), async source =>
        {
            var store = new PostgreSqlStateStore(source);
            var content = new PostgreSqlContentStore(source);
            var service = new CellBridgeDocumentService(new(store, content));
            var state = (await service.CreateAsync("/membership-migration.docx", MinimalDocx.Create("original"), TestActor.Value))!;
            for (var index = 0; index < 3; index++)
                state = await RevisionHistoryTests.Save(service, state, "changed revision " + index);
            var originalRecovery = await store.CaptureRecoveryAsync();
            var originalRows = await AuthoritativeRows(source);
            var originalBytes = new List<byte[]>();
            foreach (var revision in state.Revisions)
                originalBytes.Add(await content.ReadVerifiedAsync(revision.Content));
            var exactBytes = StorageLimits.GetDecodedGraphMembershipBytes(state);

            await using (var remove = source.CreateCommand("DROP TABLE cellbridge_read_heads,cellbridge_revision_summaries"))
                await remove.ExecuteNonQueryAsync();
            await using (var version = source.CreateCommand("DROP TABLE \"__CellBridgeDocumentMigrationsHistory\"; UPDATE cellbridge_schema SET version=5"))
                await version.ExecuteNonQueryAsync();

            var restricted = new PostgreSqlStateStore(source, new() { MaxDecodedGraphMembershipBytes = exactBytes - 1 });
            var exception = await Assert.ThrowsAsync<StorageQuotaExceededException>(() => restricted.InitializeAsync());
            Assert.Equal("decoded graph membership bytes", exception.Budget);
            Assert.Equal(exactBytes, exception.Used);
            Assert.Equal(originalRows, await AuthoritativeRows(source));
            await using (var check = source.CreateCommand("SELECT version,to_regclass('cellbridge_read_heads') IS NULL,to_regclass('cellbridge_revision_summaries') IS NULL FROM cellbridge_schema"))
            await using (var reader = await check.ExecuteReaderAsync())
            {
                Assert.True(await reader.ReadAsync());
                Assert.Equal(5, reader.GetInt32(0));
                Assert.True(reader.GetBoolean(1));
                Assert.True(reader.GetBoolean(2));
            }

            var admitted = new PostgreSqlStateStore(source, new() { MaxDecodedGraphMembershipBytes = exactBytes });
            await admitted.InitializeAsync();
            Assert.Equal(originalRows, await AuthoritativeRows(source));
            Assert.Equal(JsonSerializer.Serialize(originalRecovery), JsonSerializer.Serialize(await admitted.CaptureRecoveryAsync()));
            var migrated = (await admitted.FindByResourceIdAsync(state.ResourceId))!;
            Assert.Equal(state.Revisions.Length, migrated.Revisions.Length);
            var summaries = (await admitted.ReadRevisionSummariesAsync(state.ResourceId, 0, 100))!;
            Assert.Equal(state.Revisions.Reverse().Select(revision => revision.RevisionNumber),
                summaries.Revisions.Select(revision => revision.RevisionNumber));
            for (var index = 0; index < migrated.Revisions.Length; index++)
                Assert.Equal(originalBytes[index], await content.ReadVerifiedAsync(migrated.Revisions[index].Content));
        });

    [PostgreSqlFact]
    public Task RejectedCreatesAndTransitionsPreservePointersSnapshotsIndexesAndLedger() =>
        BudgetAndQueryTests.WithDatabase(new(), async source =>
        {
            var store = new PostgreSqlStateStore(source);
            var state = (await new CellBridgeDocumentService(new(store, new PostgreSqlContentStore(source)))
                .CreateAsync("/membership-publication.docx", MinimalDocx.Create(), TestActor.Value))!;
            var restricted = new PostgreSqlStateStore(source, new()
            {
                MaxDecodedGraphMembershipBytes = StorageLimits.GetDecodedGraphMembershipBytes(state),
            });
            var before = await PublicationRows(source);
            var partitionIndex = Array.FindIndex(state.Partitions.ToArray(), partition => partition.Elements.Length > 0);
            var partition = state.Partitions[partitionIndex];
            var duplicatePartitions = state.Partitions.SetItem(partitionIndex,
                partition with { Elements = [partition.Elements[0], partition.Elements[0]] });
            var replacementId = Guid.NewGuid();
            var replacement = state with
            {
                ResourceId = replacementId,
                Path = "/membership-refused.docx",
                PathKey = StorageIds.PathKey("/membership-refused.docx"),
                Revisions = state.Revisions.Select(revision => revision with { ResourceId = replacementId }).ToImmutableArray(),
            };

            await Assert.ThrowsAsync<StorageCorruptionException>(() =>
                restricted.TryCreateAsync(replacement with { Partitions = duplicatePartitions }).AsTask());
            Assert.Equal(before, await PublicationRows(source));
            var overBudget = replacement with
            {
                Revisions = replacement.Revisions.Add(replacement.Revisions[^1] with { RevisionNumber = 2 }),
            };
            await Assert.ThrowsAsync<StorageQuotaExceededException>(() => restricted.TryCreateAsync(overBudget).AsTask());
            Assert.Equal(before, await PublicationRows(source));
            Assert.Null(await store.FindByResourceIdAsync(replacementId));

            await Assert.ThrowsAsync<StorageCorruptionException>(() => restricted.TransitionAsync(state.ResourceId,
                (current, _) => new StateTransition<bool>(current with { Partitions = duplicatePartitions }, true)).AsTask());
            Assert.Equal(before, await PublicationRows(source));
            await Assert.ThrowsAsync<StorageQuotaExceededException>(() => restricted.TransitionAsync(state.ResourceId,
                (current, _) => new StateTransition<bool>(current with
                {
                    Revisions = current.Revisions.Add(current.Revisions[^1] with { RevisionNumber = 2 }),
                }, true)).AsTask());
            Assert.Equal(before, await PublicationRows(source));
        });

    private static async Task<string> AuthoritativeRows(NpgsqlDataSource source)
    {
        await using var command = source.CreateCommand("""
            SELECT jsonb_build_object(
                'documents',(SELECT jsonb_agg(to_jsonb(d) ORDER BY resource_id) FROM cellbridge_documents d),
                'snapshots',(SELECT jsonb_agg(to_jsonb(s) ORDER BY resource_id,state_version) FROM cellbridge_states s),
                'usage',(SELECT to_jsonb(u) FROM cellbridge_usage u))::text
            """);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<string> PublicationRows(NpgsqlDataSource source)
    {
        await using var command = source.CreateCommand("""
            SELECT jsonb_build_object(
                'documents',(SELECT jsonb_agg(to_jsonb(d) ORDER BY resource_id) FROM cellbridge_documents d),
                'snapshots',(SELECT jsonb_agg(to_jsonb(s) ORDER BY resource_id,state_version) FROM cellbridge_states s),
                'heads',(SELECT jsonb_agg(to_jsonb(h) ORDER BY resource_id) FROM cellbridge_read_heads h),
                'revisions',(SELECT jsonb_agg(to_jsonb(r) ORDER BY resource_id,lifecycle_generation,revision_number) FROM cellbridge_revision_summaries r),
                'usage',(SELECT to_jsonb(u) FROM cellbridge_usage u))::text
            """);
        return (string)(await command.ExecuteScalarAsync())!;
    }
}
