using System.Collections.Immutable;
using System.Data.Common;
using System.Text.Json;
using CellBridge.AspNetCore;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using CellBridge.Storage.PostgreSql;
using CellBridge.TestFixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace CellBridge.Storage.Tests;

[Collection("PostgreSQL")]
public sealed class RecoveryBulkProjectionTests
{
    [PostgreSqlFact]
    public Task ImportBuildsOnlyCurrentProjectionsWithoutReadingEachDocumentsEmptyIndexes() =>
        BudgetAndQueryTests.WithDatabase(new(), async source =>
        {
            var content = new PostgreSqlContentStore(source);
            var snapshot = await PrepareAsync(content, 16);
            var before = await UsageAsync(source);
            var commands = new ProjectionCommands();
            var detections = 0;
            var store = new PostgreSqlStateStore(new DbContextOptionsBuilder<CellBridgeDbContext>()
                .UseNpgsql(source).AddInterceptors(commands)
                .LogTo(_ => detections++, [CoreEventId.DetectChangesStarting]).Options);
            var receipt = Receipt(snapshot);

            Assert.Equal(receipt, await store.ImportRecoveryAsync(snapshot, receipt));
            Assert.Equal(1, detections);
            var after = await UsageAsync(source);
            Assert.Equal(before.Documents + snapshot.Heads.Length, after.Documents);
            Assert.Equal(before.Bytes + snapshot.Snapshots.Sum(DocumentStateJson.GetSerializedSize) +
                JsonSerializer.SerializeToUtf8Bytes(receipt).LongLength, after.Bytes);

            // The two namespace-wide emptiness probes are required. Import must not
            // add a head or history SELECT for every document it publishes.
            foreach (var table in new[] { "cellbridge_read_heads", "cellbridge_revision_summaries" })
            {
                var query = Assert.Single(commands.Selects, sql => sql.Contains(table, StringComparison.Ordinal));
                Assert.Contains("EXISTS", query, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("WHERE", query, StringComparison.OrdinalIgnoreCase);
            }
            Assert.Equal(receipt, await store.FindRecoveryReceiptAsync(receipt.OperationId));
            var expectedHeads = snapshot.Heads.ToDictionary(head => head.ResourceId, head => head.StateVersion);
            var restored = await store.CaptureRecoveryAsync();
            Assert.Equal(snapshot.Snapshots.Length, restored.Snapshots.Length);
            Assert.Equal(expectedHeads.OrderBy(pair => pair.Key),
                restored.Heads.ToDictionary(head => head.ResourceId, head => head.StateVersion).OrderBy(pair => pair.Key));

            foreach (var current in snapshot.Snapshots.Where(state => state.StateVersion == expectedHeads[state.ResourceId]))
            {
                var metadata = (await store.FindReadMetadataByResourceIdAsync(current.ResourceId))!;
                Assert.Equal(JsonSerializer.Serialize(DocumentReadModels.Metadata(current)), JsonSerializer.Serialize(metadata));
                Assert.Equal(metadata.ResourceId, (await store.FindReadMetadataByPathKeyAsync(current.PathKey))!.ResourceId);
                var page = (await store.ReadRevisionSummariesAsync(current.ResourceId, 0, 10))!;
                Assert.Equal(JsonSerializer.Serialize(DocumentReadModels.Revisions(current).Reverse()),
                    JsonSerializer.Serialize(page.Revisions));
                Assert.Null(page.NextOffset);
                foreach (var revision in current.Revisions)
                {
                    var selected = (await store.ReadRevisionSummaryAsync(current.ResourceId, revision.RevisionNumber))!;
                    Assert.Equal(DocumentReadModels.Summary(revision), selected.Revision);
                    Assert.Equal(current.ResourceId, selected.Current.ResourceId);
                }
                var persisted = (await store.FindByResourceIdAsync(current.ResourceId))!;
                var graph = await DocumentPartition.RestoreFileGraphAsync(persisted, content, new());
                Assert.Equal(await content.ReadVerifiedAsync(current.Content), graph.Materialize());
            }
        });

    [PostgreSqlFact]
    public Task InconsistentRevisionProjectionRollsBackTheEntireImport() =>
        BudgetAndQueryTests.WithDatabase(new(), async source =>
        {
            var snapshot = await PrepareAsync(new PostgreSqlContentStore(source), 2);
            var bad = snapshot.Snapshots.Last(state => state.Revisions.Length == 2);
            var invalid = bad with { Revisions = [bad.Revisions[0], bad.Revisions[0]] };
            snapshot = snapshot with
            {
                Snapshots = snapshot.Snapshots.Select(state => state.ResourceId == bad.ResourceId &&
                    state.StateVersion == bad.StateVersion ? invalid : state).ToImmutableArray(),
            };
            var store = new PostgreSqlStateStore(source);
            var before = await UsageAsync(source);

            await Assert.ThrowsAsync<StorageCorruptionException>(() => store.ImportRecoveryAsync(snapshot, Receipt(snapshot)).AsTask());
            Assert.Equal(before, await UsageAsync(source));

            var after = await store.CaptureRecoveryAsync();
            Assert.Empty(after.Heads);
            Assert.Empty(after.Snapshots);
            Assert.Empty(after.Receipts);
            Assert.Empty(await store.ListAsync(0, 10));
            await using var rows = source.CreateCommand("SELECT (SELECT count(*) FROM cellbridge_read_heads) + (SELECT count(*) FROM cellbridge_revision_summaries)");
            Assert.Equal(0L, await rows.ExecuteScalarAsync());
        });

    [PostgreSqlFact]
    public Task QuotaFailureDoesNotPublishTrackedBudgetChanges() =>
        BudgetAndQueryTests.WithDatabase(new(), async source =>
        {
            var snapshot = await PrepareAsync(new PostgreSqlContentStore(source), 2);
            var before = await UsageAsync(source);
            // Allow the document-count adjustment, but reject the following state bytes.
            await using (var quota = source.CreateCommand("UPDATE cellbridge_usage SET max_stored_bytes=stored_bytes+1"))
                await quota.ExecuteNonQueryAsync();
            var store = new PostgreSqlStateStore(source);

            await Assert.ThrowsAsync<StorageQuotaExceededException>(() => store.ImportRecoveryAsync(snapshot, Receipt(snapshot)).AsTask());

            Assert.Equal(before, await UsageAsync(source));
            var after = await store.CaptureRecoveryAsync();
            Assert.Empty(after.Heads);
            Assert.Empty(after.Snapshots);
            Assert.Empty(after.Receipts);
        });

    private static async Task<(long Bytes, long Documents)> UsageAsync(NpgsqlDataSource source)
    {
        await using var command = source.CreateCommand("SELECT stored_bytes,document_count FROM cellbridge_usage WHERE singleton=true");
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetInt64(0), reader.GetInt64(1));
    }

    private static RecoveryReceipt Receipt(ProviderSnapshot snapshot) =>
        new(Guid.NewGuid(), new string('A', 64), snapshot.Heads.Length, snapshot.Snapshots.Length);

    private static async Task<ProviderSnapshot> PrepareAsync(IContentStore content, int count)
    {
        // Only the state store is temporary. Immutable content is already durable
        // in the destination content store before recovery publishes its heads.
        var memory = new InMemoryStateStore();
        var service = new CellBridgeDocumentService(new(memory, content));
        var initial = new List<DocumentState>();
        for (var index = 0; index < count; index++)
        {
            var actor = new CellBridgeActor(new("recovery-owner-" + index, "owner-" + index, "Owner " + index), CanCreate: true);
            var state = (await service.CreateAsync("/bulk-recovery-" + index + ".docx", MinimalDocx.Create("before " + index), actor))!;
            initial.Add(state);
            var request = RevisionHistoryTests.Request(state, MinimalDocx.Create("after " + index));
            var saved = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents,
                request, new Dictionary<string, string>(), actor);
            Assert.False(saved.Response.Status);
            Assert.False(Assert.Single(saved.Response.SubResponses).Status);
        }
        var snapshot = await memory.CaptureRecoveryAsync();
        // Retained older snapshots must not overwrite the projections of current heads.
        return snapshot with { Snapshots = snapshot.Snapshots.Concat(initial).ToImmutableArray() };
    }

    private sealed class ProjectionCommands : DbCommandInterceptor
    {
        public List<string> Selects { get; } = [];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
                Selects.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }
}
