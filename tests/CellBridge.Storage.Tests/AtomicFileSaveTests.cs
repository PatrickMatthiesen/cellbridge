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
    public Task SmallSavePublishesContentGraphReceiptAndStateInOneDurableWriteTransaction() =>
        BudgetAndQueryTests.WithDatabase(new(), async source =>
        {
            var audit = new TransactionAudit();
            var options = new DbContextOptionsBuilder<CellBridgeDbContext>().UseNpgsql(source)
                .AddInterceptors(new AuditCommands(audit), new AuditTransactions(audit)).Options;
            var stateStore = new PostgreSqlStateStore(options);
            var content = new PostgreSqlContentStore(options);
            Assert.True(stateStore.SupportsContentStore(content));
            var provider = new StorageProvider(stateStore, content);
            var service = new CellBridgeDocumentService(provider);
            var state = (await service.CreateAsync("/atomic-small.docx", MinimalDocx.Create("before"), TestActor.Value))!;
            var current = await StoredDocument.RestoreAsync(state, content);
            var expected = MinimalDocx.Create("saved atomically");
            var request = SyntheticWorkload.Save(current, expected, "full", 1, new Random(42));
            audit.Clear();
            var saved = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents,
                request, new Dictionary<string, string>(), TestActor.Value);
            Assert.False(Assert.Single(saved.Response.SubResponses).Status);
            Assert.Single(audit.Committed);
            var preparation = Assert.Single(audit.ClockedReads, read => !read.InTransaction);
            Assert.Contains("LEFT JOIN", preparation.Sql, StringComparison.OrdinalIgnoreCase);
            Assert.Matches(@"(?s)\bON\b.*resource_id.*resource_id.*state_version.*state_version", preparation.Sql);
            var publication = Assert.Single(audit.Committed, audit.HasWrites);
            var writes = audit.Writes[publication];
            Assert.Contains(writes, sql => sql.Contains("cellbridge_chunks", StringComparison.Ordinal));
            Assert.Contains(writes, sql => sql.Contains("cellbridge_states", StringComparison.Ordinal));
            Assert.Contains(writes, sql => sql.Contains("cellbridge_documents", StringComparison.Ordinal));
            Assert.Equal(0, audit.WritesOutsideTransactions);
            // Object metadata uses direct Npgsql commands and bypasses EF command interceptors.
            // PostgreSQL records the creating transaction on every row, so independently check
            // that new metadata, chunks and the published snapshot all have the same xmin.
            var previousKeys = StorageReferences.Handles(state).Select(handle => handle.Key).ToHashSet(StringComparer.Ordinal);
            var added = ContentStoreReader.UniqueHandles(StorageReferences.Handles(saved.State))
                .Where(handle => !previousKeys.Contains(handle.Key)).ToArray();
            Assert.NotEmpty(added);
            await using (var command = source.CreateCommand("""
                SELECT 'object',object_key,xmin::text FROM cellbridge_objects WHERE object_key=ANY($1)
                UNION ALL SELECT 'chunk',object_key,xmin::text FROM cellbridge_chunks WHERE object_key=ANY($1)
                UNION ALL SELECT 'snapshot','',xmin::text FROM cellbridge_states WHERE resource_id=$2 AND state_version=$3
                """))
            {
                command.Parameters.AddWithValue(added.Select(handle => handle.Key).ToArray());
                command.Parameters.AddWithValue(saved.State.ResourceId);
                command.Parameters.AddWithValue(saved.State.StateVersion);
                var rows = new List<(string Kind, string Key, string Transaction)>();
                await using var persisted = await command.ExecuteReaderAsync();
                while (await persisted.ReadAsync()) rows.Add((persisted.GetString(0), persisted.GetString(1), persisted.GetString(2)));
                Assert.Single(rows, row => row.Kind == "snapshot");
                Assert.Equal(added.Select(handle => handle.Key).Order(), rows.Where(row => row.Kind == "object").Select(row => row.Key).Order());
                Assert.Equal(added.Where(handle => handle.Length != 0).Select(handle => handle.Key).Order(),
                    rows.Where(row => row.Kind == "chunk").Select(row => row.Key).Distinct().Order());
                Assert.Single(rows.Select(row => row.Transaction).Distinct());
            }
            Assert.Equal(state.ContentVersion + 1, saved.State.ContentVersion);
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(expected)), saved.State.Content.Sha256);
            Assert.Single(saved.State.Receipts);
            var restored = await StoredDocument.RestoreAsync(saved.State, content);
            Assert.Equal(expected, restored.Content);
            Assert.Equal(expected, restored.FilePartition.FileGraph.Materialize());
            Assert.True(restored.FilePartition.FileGraph.AnalyzeRetention().Complete);
            var receiptBytes = await content.ReadVerifiedAsync(Assert.Single(saved.State.Receipts).Response!);
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
        BudgetAndQueryTests.WithDatabase(new(), async source =>
        {
            var audit = new TransactionAudit();
            var options = new DbContextOptionsBuilder<CellBridgeDbContext>().UseNpgsql(source)
                .AddInterceptors(new AuditCommands(audit), new AuditTransactions(audit)).Options;
            var provider = new StorageProvider(new PostgreSqlStateStore(options), new PostgreSqlContentStore(options));
            var service = new CellBridgeDocumentService(provider);
            var state = (await service.CreateAsync("/atomic-invalid.docx", MinimalDocx.Create(), TestActor.Value))!;
            var current = await StoredDocument.RestoreAsync(state, provider.Content);
            var request = SyntheticWorkload.Save(current, [1, 2, 3], "full", 1, new Random(42));
            async Task<string> InventoryAsync()
            {
                await using var command = source.CreateCommand("""
                    SELECT jsonb_build_object('objects',(SELECT count(*) FROM cellbridge_objects),
                        'chunks',(SELECT count(*) FROM cellbridge_chunks),
                        'snapshots',(SELECT count(*) FROM cellbridge_states),
                        'usage',(SELECT to_jsonb(u) FROM cellbridge_usage u))::text
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
            var unchanged = (await provider.State.FindByResourceIdAsync(state.ResourceId))!;
            Assert.Equal(state.Content, unchanged.Content);
            Assert.Equal(state.ContentVersion, unchanged.ContentVersion);
            Assert.Equal(state.StateVersion, unchanged.StateVersion);
            Assert.Empty(unchanged.Receipts);
        });

    private sealed class TransactionAudit
    {
        public List<DbTransaction> Committed { get; } = [];
        public Dictionary<DbTransaction, List<string>> Writes { get; } = [];
        public List<(string Sql, bool InTransaction)> ClockedReads { get; } = [];
        public int WritesOutsideTransactions { get; private set; }
        public bool HasWrites(DbTransaction transaction) => Writes.ContainsKey(transaction);
        public void Clear() { Committed.Clear(); Writes.Clear(); ClockedReads.Clear(); WritesOutsideTransactions = 0; }
        public void Record(DbCommand command)
        {
            if (command.CommandText.Contains("clock_timestamp()", StringComparison.Ordinal) &&
                command.CommandText.Contains("cellbridge_documents", StringComparison.Ordinal) &&
                command.CommandText.Contains("cellbridge_states", StringComparison.Ordinal))
                ClockedReads.Add((command.CommandText, command.Transaction is not null));
            if (!Regex.IsMatch(command.CommandText, @"(?im)^\s*(INSERT|UPDATE|DELETE|MERGE)\b")) return;
            if (command.Transaction is not { } transaction) { WritesOutsideTransactions++; return; }
            if (!Writes.TryGetValue(transaction, out var commands)) Writes.Add(transaction, commands = []);
            commands.Add(command.CommandText);
        }
    }

    private sealed class AuditCommands(TransactionAudit audit) : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        { audit.Record(command); return ValueTask.FromResult(result); }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        { audit.Record(command); return ValueTask.FromResult(result); }
    }

    private sealed class AuditTransactions(TransactionAudit audit) : DbTransactionInterceptor
    {
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        { audit.Committed.Add(transaction); return Task.CompletedTask; }
    }
}

public sealed class AtomicFileSaveFallbackTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnsupportedContentPairsAndLargeSavesKeepNormalPublicationAndReplayBehavior(bool large)
    {
        var inner = new InMemoryContentStore();
        var writes = new CountingContent(inner);
        // For the large case, advertise atomic support but fail if core incorrectly selects it.
        // The small case deliberately supplies a different content-store instance.
        var stateStore = new FallbackOnlyState(new InMemoryStateStore(), large ? writes : inner);
        var provider = new StorageProvider(stateStore, writes);
        var service = new CellBridgeDocumentService(provider);
        var state = (await service.CreateAsync("/fallback.docx", MinimalDocx.Create(), TestActor.Value))!;
        var current = await StoredDocument.RestoreAsync(state, writes);
        var expected = large ? new IncrementalOfficeWorkload(10 * 1024).CreateContent(1) : MinimalDocx.Create("fallback save");
        var request = SyntheticWorkload.Save(current, expected, "full", 1, new Random(42));
        writes.Writes = 0;
        var saved = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents,
            request, new Dictionary<string, string>(), TestActor.Value);
        Assert.False(Assert.Single(saved.Response.SubResponses).Status);
        Assert.Equal(0, stateStore.AtomicCalls);
        Assert.True(writes.Writes >= 3);
        Assert.Equal(state.ContentVersion + 1, saved.State.ContentVersion);
        Assert.Single(saved.State.Receipts);
        var restored = await StoredDocument.RestoreAsync(saved.State, writes);
        Assert.Equal(expected, restored.Content);
        Assert.Equal(expected, restored.FilePartition.FileGraph.Materialize());
        var previousWrites = writes.Writes;
        var replay = await service.ExecuteAsync(state.ResourceId, DocumentPartitionKind.FileContents,
            request, new Dictionary<string, string>(), TestActor.Value);
        Assert.False(Assert.Single(replay.Response.SubResponses).Status);
        Assert.Equal(saved.State.StateVersion, replay.State.StateVersion);
        Assert.Equal(saved.State.ContentVersion, replay.State.ContentVersion);
        Assert.Equal(saved.Response.ToByteArray(), replay.Response.ToByteArray());
        Assert.Equal(previousWrites, writes.Writes);
        Assert.Equal(0, stateStore.AtomicCalls);
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

    private sealed class FallbackOnlyState(IDocumentStateStore inner, IContentStore supported) : IDocumentStateStore, IAtomicDocumentSaveStore
    {
        public int AtomicCalls;
        public bool Durable => inner.Durable;
        public bool Shared => inner.Shared;
        public bool SupportsContentStore(IContentStore content) => ReferenceEquals(content, supported);
        public ValueTask<T> TransitionWithContentAsync<T>(Guid resourceId, IContentStore content, IReadOnlyList<ReadOnlyMemory<byte>> pending,
            Func<DocumentState, DateTime, StateTransition<T>> transition, CancellationToken cancellationToken = default)
        { AtomicCalls++; throw new InvalidOperationException("This save must use the normal content publication path."); }
        public ValueTask<DocumentState?> FindByResourceIdAsync(Guid id, CancellationToken ct = default) => inner.FindByResourceIdAsync(id, ct);
        public ValueTask<DocumentState?> FindByPathKeyAsync(string key, CancellationToken ct = default) => inner.FindByPathKeyAsync(key, ct);
        public ValueTask<IReadOnlyList<DocumentSummary>> ListAsync(int offset, int limit, CancellationToken ct = default) => inner.ListAsync(offset, limit, ct);
        public ValueTask<bool> TryCreateAsync(DocumentState state, CancellationToken ct = default) => inner.TryCreateAsync(state, ct);
        public ValueTask CheckHealthAsync(CancellationToken ct = default) => inner.CheckHealthAsync(ct);
        public ValueTask<T> TransitionAsync<T>(Guid id, Func<DocumentState, DateTime, StateTransition<T>> transition, CancellationToken ct = default) => inner.TransitionAsync(id, transition, ct);
    }
}
