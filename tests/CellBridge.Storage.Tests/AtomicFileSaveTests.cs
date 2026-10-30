using System.Data.Common;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using CellBridge.AspNetCore;
using CellBridge.FssHttpB;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using CellBridge.Storage.PostgreSql;
using CellBridge.TestFixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CellBridge.Storage.Tests;

[Collection("PostgreSQL")]
public sealed class AtomicFileSaveDatabaseTests
{
    [PostgreSqlFact]
    public Task SmallSavePublishesGraphReceiptRevisionAndHeadInOneDurableMetadataTransaction() =>
        EfPostgreSqlMigrationTests.WithInitializedDatabaseAsync(new(), async source =>
        {
            var audit = new TransactionAudit();
            var options = new DbContextOptionsBuilder<CellBridgeDbContext>().UseNpgsql(source)
                .AddInterceptors(new AuditCommands(audit), new AuditTransactions(audit)).Options;
            var stateStore = new PostgreSqlStateStore(options);
            var content = new PostgreSqlContentStore(options);
            var provider = new StorageProvider(stateStore, content);
            var service = new CellBridgeDocumentService(provider);
            var state = (await service.CreateAsync("/atomic-small.docx", MinimalDocx.Create("before"), TestActor.Value))!;
            var current = await StorageTestDocuments.GraphAsync(service, state);
            var expected = MinimalDocx.Create("saved atomically");
            var request = SyntheticWorkload.Save(current, expected, "full", 1, new Random(42));
            var originalKeys = new List<string>();
            await using (var originalObjects = source.CreateCommand("SELECT object_key FROM cellbridge_objects"))
            await using (var rows = await originalObjects.ExecuteReaderAsync())
                while (await rows.ReadAsync()) originalKeys.Add(rows.GetString(0));
            audit.Clear();
            var saved = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents,
                request, new Dictionary<string, string>(), TestActor.Value);
            Assert.False(Assert.Single(saved.Response.SubResponses).Status);
            var publication = Assert.Single(audit.Committed, transaction => audit.Writes.GetValueOrDefault(transaction)?
                .Any(sql => sql.Contains("cellbridge_repository_receipts", StringComparison.Ordinal)) == true);
            var writes = audit.Writes[publication];
            Assert.Contains(writes, sql => sql.Contains("cellbridge_repository_heads", StringComparison.Ordinal));
            Assert.Contains(writes, sql => sql.Contains("cellbridge_repository_revisions", StringComparison.Ordinal));
            Assert.Contains(writes, sql => sql.Contains("cellbridge_repository_receipts", StringComparison.Ordinal));
            Assert.Contains(writes, sql => sql.Contains("cellbridge_repository_bindings", StringComparison.Ordinal));
            Assert.DoesNotContain(writes, sql => sql.Contains("cellbridge_states", StringComparison.Ordinal));
            Assert.Equal(0, audit.WritesOutsideTransactions);
            Assert.Equal(publication, Assert.Single(audit.Committed, audit.HasWrites));
            // New physical objects and their chunks commit together with the
            // head, revision, receipt and graph records in this same transaction.
            await using (var command = source.CreateCommand("""
                SELECT 'head',xmin::text FROM cellbridge_repository_heads WHERE "ResourceId"=$1
                UNION ALL SELECT 'revision',xmin::text FROM cellbridge_repository_revisions WHERE "ResourceId"=$1 AND "RevisionNumber"=2
                UNION ALL SELECT 'receipt',xmin::text FROM cellbridge_repository_receipts WHERE "ResourceId"=$1
                UNION ALL SELECT 'delta-' || "Partition"::text,xmin::text FROM cellbridge_repository_deltas WHERE "ResourceId"=$1 AND "Sequence"=2
                UNION ALL SELECT 'root-' || "Partition"::text,xmin::text FROM cellbridge_repository_mapping_roots WHERE "ResourceId"=$1 AND "Sequence"=2
                UNION ALL SELECT 'physical-object',xmin::text FROM cellbridge_objects WHERE NOT (object_key=ANY($2))
                UNION ALL SELECT 'physical-chunk',xmin::text FROM cellbridge_chunks WHERE NOT (object_key=ANY($2))
                """))
            {
                command.Parameters.AddWithValue(saved.State.ResourceId);
                command.Parameters.AddWithValue(originalKeys.ToArray());
                var rows = new List<(string Kind, string Transaction)>();
                await using var persisted = await command.ExecuteReaderAsync();
                while (await persisted.ReadAsync()) rows.Add((persisted.GetString(0), persisted.GetString(1)));
                Assert.Equal(new[] { "delta-0", "delta-1", "head", "receipt", "revision", "root-0" }, rows.Where(row => !row.Kind.StartsWith("physical-", StringComparison.Ordinal)).Select(row => row.Kind).Order(StringComparer.Ordinal));
                Assert.Contains(rows, row => row.Kind == "physical-object");
                Assert.Contains(rows, row => row.Kind == "physical-chunk");
                Assert.Single(rows.Select(row => row.Transaction).Distinct());
            }
            Assert.Equal(state.ContentVersion + 1, saved.State.ContentVersion);
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(expected)), saved.State.Package.Sha256);
            Assert.Equal(1, saved.State.Counters.SaveReceipts);
            var restored = await StorageTestDocuments.GraphAsync(service, saved.State);
            Assert.Equal(expected, await StorageTestDocuments.PackageAsync(provider, saved.State.Package));
            Assert.Equal(expected, restored.Materialize());
            Assert.True(restored.AnalyzeRetention().Complete);
            var snapshot = (await provider.Documents.ExportDocumentAsync(state.ResourceId))!;
            Assert.Equal(NormalizedDocumentRecovery.StoredBytes(snapshot), saved.State.Counters.StoredBytes);
            var receiptBytes = await content.ReadVerifiedAsync(Assert.Single(snapshot.Receipts).Response!);
            var reader = new BinaryReaderEx(receiptBytes);
            var receipt = FsshttpbResponse.Deserialize(reader);
            Assert.Equal(0, reader.Remaining);
            Assert.Equal(saved.Response.ToByteArray(), receipt.ToByteArray());

            audit.Clear();
            var replay = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents,
                request, new Dictionary<string, string>(), TestActor.Value);
            Assert.False(Assert.Single(replay.Response.SubResponses).Status);
            Assert.Equal(saved.State.StateVersion, replay.State.StateVersion);
            Assert.Equal(saved.State.ContentVersion, replay.State.ContentVersion);
            Assert.Equal(saved.Response.ToByteArray(), replay.Response.ToByteArray());
            Assert.All(audit.Committed, transaction => Assert.False(audit.HasWrites(transaction)));
            Assert.Equal(0, audit.WritesOutsideTransactions);
        });

    [PostgreSqlFact]
    public Task InvalidZipIsRejectedBeforeAnyDurableContentOrStateWrites() =>
        EfPostgreSqlMigrationTests.WithInitializedDatabaseAsync(new(), async source =>
        {
            var audit = new TransactionAudit();
            var options = new DbContextOptionsBuilder<CellBridgeDbContext>().UseNpgsql(source)
                .AddInterceptors(new AuditCommands(audit), new AuditTransactions(audit)).Options;
            var provider = new StorageProvider(new PostgreSqlStateStore(options), new PostgreSqlContentStore(options));
            var service = new CellBridgeDocumentService(provider);
            var state = (await service.CreateAsync("/atomic-invalid.docx", MinimalDocx.Create(), TestActor.Value))!;
            var current = await StorageTestDocuments.GraphAsync(service, state);
            var request = SyntheticWorkload.Save(current, [1, 2, 3], "full", 1, new Random(42));
            async Task<string> InventoryAsync()
            {
                await using var command = source.CreateCommand("""
                    SELECT jsonb_build_object('objects',(SELECT count(*) FROM cellbridge_objects),
                        'chunks',(SELECT count(*) FROM cellbridge_chunks),
                        'revisions',(SELECT count(*) FROM cellbridge_repository_revisions),'receipts',(SELECT count(*) FROM cellbridge_repository_receipts))::text
                    """);
                return (string)(await command.ExecuteScalarAsync())!;
            }
            var inventory = await InventoryAsync();
            audit.Clear();
            var saved = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents,
                request, new Dictionary<string, string>(), TestActor.Value);
            Assert.Equal((ulong)CellErrorCode.InvalidObject, Assert.Single(saved.Response.SubResponses).Error!.ErrorCode);
            Assert.Equal(inventory, await InventoryAsync());
            Assert.All(audit.Committed, transaction => Assert.False(audit.HasWrites(transaction)));
            Assert.Equal(0, audit.WritesOutsideTransactions);
            var unchanged = (await provider.Documents.FindAsync(new DocumentIdentity.Resource(state.ResourceId)))!;
            Assert.Equal(state.Package, unchanged.Package);
            Assert.Equal(state.ContentVersion, unchanged.ContentVersion);
            Assert.Equal(state.StateVersion, unchanged.StateVersion);
            Assert.Equal(0, unchanged.Counters.SaveReceipts);
        });

    private sealed class TransactionAudit
    {
        public List<Guid> Committed { get; } = [];
        public Dictionary<Guid, List<string>> Writes { get; } = [];
        public List<(string Sql, bool InTransaction)> ClockedReads { get; } = [];
        public int WritesOutsideTransactions { get; private set; }
        public bool HasWrites(Guid transaction) => Writes.ContainsKey(transaction);
        public void Clear() { Committed.Clear(); Writes.Clear(); ClockedReads.Clear(); WritesOutsideTransactions = 0; }
        public void Record(DbCommand command, Guid? transactionId)
        {
            if (command.CommandText.Contains("clock_timestamp()", StringComparison.Ordinal) &&
                command.CommandText.Contains("cellbridge_documents", StringComparison.Ordinal) &&
                command.CommandText.Contains("cellbridge_states", StringComparison.Ordinal))
                ClockedReads.Add((command.CommandText, command.Transaction is not null));
            if (!Regex.IsMatch(command.CommandText, @"(?im)^\s*(INSERT|UPDATE|DELETE|MERGE)\b")) return;
            if (command.Transaction is null || transactionId is not { } transaction) { WritesOutsideTransactions++; return; }
            if (!Writes.TryGetValue(transaction, out var commands)) Writes.Add(transaction, commands = []);
            commands.Add(command.CommandText);
        }
    }

    private sealed class AuditCommands(TransactionAudit audit) : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        { audit.Record(command, eventData.Context?.Database.CurrentTransaction?.TransactionId); return ValueTask.FromResult(result); }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        { audit.Record(command, eventData.Context?.Database.CurrentTransaction?.TransactionId); return ValueTask.FromResult(result); }
    }

    private sealed class AuditTransactions(TransactionAudit audit) : DbTransactionInterceptor
    {
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        { audit.Committed.Add(eventData.TransactionId); return Task.CompletedTask; }
    }
}

public sealed class StagedFileSaveTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WrappedContentAndLargeSavesPublishGraphAndReplayWithoutMoreContentWrites(bool large)
    {
        var inner = new InMemoryContentStore();
        var writes = new CountingContent(inner);
        var provider = StorageTestDocuments.Memory(writes);
        var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/fallback.docx", MinimalDocx.Create(), TestActor.Value))!;
        var current = await StorageTestDocuments.GraphAsync(service, state);
        var expected = large ? new IncrementalOfficeWorkload(10 * 1024).CreateContent(1) : MinimalDocx.Create("fallback save");
        var request = SyntheticWorkload.Save(current, expected, "full", 1, new Random(42));
        writes.Writes = 0;
        var saved = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents,
            request, new Dictionary<string, string>(), TestActor.Value);
        Assert.False(Assert.Single(saved.Response.SubResponses).Status);
        Assert.True(writes.Writes >= 3);
        Assert.Equal(state.ContentVersion + 1, saved.State.ContentVersion);
        Assert.Equal(1, saved.State.Counters.SaveReceipts);
        Assert.Equal(expected, await StorageTestDocuments.PackageAsync(provider, saved.State.Package));
        Assert.Equal(expected, (await StorageTestDocuments.GraphAsync(service, saved.State)).Materialize());
        var previousWrites = writes.Writes;
        var replay = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents,
            request, new Dictionary<string, string>(), TestActor.Value);
        Assert.False(Assert.Single(replay.Response.SubResponses).Status);
        Assert.Equal(saved.State.StateVersion, replay.State.StateVersion);
        Assert.Equal(saved.State.ContentVersion, replay.State.ContentVersion);
        Assert.Equal(saved.Response.ToByteArray(), replay.Response.ToByteArray());
        Assert.Equal(previousWrites, writes.Writes);
    }

    private sealed class CountingContent(IContentStore inner) : IContentStore
    {
        public int Writes;
        public bool Durable => inner.Durable;
        public bool Shared => inner.Shared;
        public ValueTask<ContentHandle> WriteAsync(Stream source, CancellationToken cancellationToken = default)
        { Writes++; return inner.WriteAsync(source, cancellationToken); }
        public ValueTask<Stream> OpenReadAsync(ContentHandle handle, CancellationToken cancellationToken = default) => inner.OpenReadAsync(handle, cancellationToken);
    }

}
