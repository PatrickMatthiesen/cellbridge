using System.Collections.Immutable;
using System.Data.Common;
using System.Text.Json;
using CellBridge.AspNetCore;
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
    public Task ImportPublishesNormalizedHeadsAndHistoryWithoutLegacyAggregateRows() =>
        BudgetAndQueryTests.WithDatabase(new(), async source =>
        {
            var content = new PostgreSqlContentStore(source);
            var snapshot = await PrepareAsync(content, 16);
            var commands = new ProjectionCommands();
            IDocumentRepository store = new PostgreSqlStateStore(new DbContextOptionsBuilder<CellBridgeDbContext>()
                .UseNpgsql(source).AddInterceptors(commands).Options, validationContent: content);
            var receipt = Receipt(snapshot);
            Assert.Equal(receipt, await store.ImportRecoveryAsync(snapshot, receipt));
            Assert.DoesNotContain(commands.Selects, sql => sql.Contains("cellbridge_states", StringComparison.Ordinal) ||
                sql.Contains("cellbridge_read_heads", StringComparison.Ordinal) || sql.Contains("cellbridge_revision_summaries", StringComparison.Ordinal));
            Assert.Equal(receipt, await store.FindRecoveryReceiptAsync(receipt.OperationId));
            var restored = await store.CaptureRecoveryAsync();
            Assert.Equal(PortabilityTests.CanonicalSnapshots(snapshot.Snapshots), PortabilityTests.CanonicalSnapshots(restored.Snapshots));
            await using (var rows = source.CreateCommand("SELECT (SELECT count(*) FROM cellbridge_states) + (SELECT count(*) FROM cellbridge_read_heads) + (SELECT count(*) FROM cellbridge_revision_summaries)"))
                Assert.Equal(0L, await rows.ExecuteScalarAsync());
            var provider = new StorageProvider(store, content);
            foreach (var current in snapshot.Snapshots)
            {
                var head = current.Current.Head;
                Assert.Equal(JsonSerializer.Serialize(head), JsonSerializer.Serialize(await store.FindAsync(new DocumentIdentity.Resource(head.ResourceId))));
                Assert.Equal(head.ResourceId, (await store.FindAsync(new DocumentIdentity.Path(head.PathKey)))!.ResourceId);
                var page = (await store.ReadRevisionsAsync(head.ResourceId, 0, 10))!;
                Assert.Equal(JsonSerializer.Serialize(current.ApplicationRevisions.Reverse()), JsonSerializer.Serialize(page.Revisions));
                Assert.Null(page.NextOffset);
                foreach (var revision in current.ApplicationRevisions)
                {
                    var selected = (await store.ReadRevisionAsync(head.ResourceId, revision.RevisionNumber))!;
                    Assert.Equal(JsonSerializer.Serialize(revision), JsonSerializer.Serialize(selected.Revision));
                    Assert.Equal(head.ResourceId, selected.Current.ResourceId);
                    Assert.Equal(revision.Package.Length, (await StorageTestDocuments.PackageAsync(provider, revision.Package)).Length);
                }
            }
        });
    [PostgreSqlFact]
    public Task InconsistentRevisionMetadataRollsBackTheEntireImport() =>
        BudgetAndQueryTests.WithDatabase(new(), async source =>
        {
            var snapshot = await PrepareAsync(new PostgreSqlContentStore(source), 2);
            var bad = snapshot.Snapshots[1];
            snapshot = snapshot with { Snapshots = snapshot.Snapshots.SetItem(1,
                bad with { ApplicationRevisions = [bad.ApplicationRevisions[0], bad.ApplicationRevisions[0]] }) };
            IDocumentRepository store = new PostgreSqlStateStore(source);
            var before = await InventoryAsync(source);
            await Assert.ThrowsAsync<StorageCorruptionException>(() => store.ImportRecoveryAsync(snapshot, Receipt(snapshot)).AsTask());
            Assert.Equal(before, await InventoryAsync(source));
            var after = await store.CaptureRecoveryAsync(); Assert.Empty(after.Snapshots); Assert.Empty(after.Receipts);
            await using var rows = source.CreateCommand("SELECT (SELECT count(*) FROM cellbridge_repository_heads) + (SELECT count(*) FROM cellbridge_repository_revisions)");
            Assert.Equal(0L, await rows.ExecuteScalarAsync());
        });
    private static async Task<string> InventoryAsync(NpgsqlDataSource source)
    {
        await using var command = source.CreateCommand("SELECT ROW((SELECT count(*) FROM cellbridge_objects),(SELECT count(*) FROM cellbridge_chunks),(SELECT count(*) FROM cellbridge_repository_heads),(SELECT count(*) FROM cellbridge_repository_revisions),(SELECT count(*) FROM cellbridge_recovery_receipts))::text");
        return (string)(await command.ExecuteScalarAsync())!;
    }
    private static RecoveryReceipt Receipt(ProviderSnapshot snapshot) => new(Guid.NewGuid(), new string('A', 64), snapshot.Snapshots.Length, snapshot.Snapshots.Length);
    private static async Task<ProviderSnapshot> PrepareAsync(IContentStore content, int count)
    {
        var memory = new InMemoryDocumentRepository(content); var service = new CellBridgeDocumentService(new(memory, content));
        for (var index = 0; index < count; index++)
        {
            var actor = new CellBridgeActor(new("recovery-owner-" + index, "owner-" + index, "Owner " + index), CanCreate: true);
            var head = (await service.CreateAsync("/bulk-recovery-" + index + ".docx", MinimalDocx.Create("before " + index), actor))!;
            var request = await StorageTestDocuments.SaveRequestAsync(service, head, MinimalDocx.Create("after " + index), actor);
            var saved = await service.ExecuteAsync(head.ResourceId, DocumentPartitionKind.FileContents, request, ImmutableDictionary<string, string>.Empty, actor);
            Assert.False(Assert.Single(saved.Response.SubResponses).Status);
        }
        return await memory.CaptureRecoveryAsync();
    }
    private sealed class ProjectionCommands : DbCommandInterceptor
    {
        public List<string> Selects { get; } = [];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken ct = default)
        { if (command.CommandText.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)) Selects.Add(command.CommandText); return ValueTask.FromResult(result); }
    }
}
