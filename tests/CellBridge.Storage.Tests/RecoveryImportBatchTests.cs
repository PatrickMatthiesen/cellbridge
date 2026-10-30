using System.Collections.Immutable;
using System.Data.Common;
using System.Text;
using CellBridge.AspNetCore;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using CellBridge.Storage.PostgreSql;
using CellBridge.TestFixtures;
using CellBridge.TestInfrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;

namespace CellBridge.Storage.Tests;

[Collection("PostgreSQL")]
public sealed class RecoveryImportBatchTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ImportClonesReadsEachOriginalPayloadOnceAndBoundsTrackedRows(bool filesystem) =>
        BudgetAndQueryTests.WithDatabase(new(), async source =>
        {
            var root = Path.Combine(Path.GetTempPath(), "cellbridge-import-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                IContentStore content = filesystem ? new PostgreSqlFileSystemContentStore(source, root) : new PostgreSqlContentStore(source);
                var snapshot = await PrepareClonesAsync(content, 48);
                var reads = new PayloadReads(content);
                var batches = new BatchAudit(); var commands = new ImportCommands();
                IDocumentRepository store = new PostgreSqlStateStore(new DbContextOptionsBuilder<CellBridgeDbContext>()
                    .UseNpgsql(source).AddInterceptors(batches, commands).Options, validationContent: reads);
                var receipt = Receipt(snapshot);
                Assert.Equal(receipt, await store.ImportRecoveryAsync(snapshot, receipt));
                Assert.True(batches.Sizes.Count > 1);
                Assert.All(batches.Sizes, size =>
                {
                    Assert.InRange(size.Rows, 1, 1024);
                    Assert.True(size.TextBytes <= 1024 * 1024 || size.Rows == 1);
                });
                var unique = ContentStoreReader.UniqueHandles(snapshot.Snapshots.SelectMany(s => s.ContentReferences).Select(r => r.Handle));
                Assert.Equal(unique.Count, reads.Counts.Count);
                Assert.All(reads.Counts.Values, count => Assert.Equal(1, count));
                Assert.DoesNotContain(commands.Selects, sql => new[] { "repository_graph", "repository_revisions", "repository_receipts", "repository_paths" }
                    .Any(table => sql.Contains(table, StringComparison.Ordinal)));
                var restored = await store.CaptureRecoveryAsync();
                Assert.Equal(PortabilityTests.CanonicalSnapshots(snapshot.Snapshots), PortabilityTests.CanonicalSnapshots(restored.Snapshots));
                Assert.Equal(receipt, Assert.Single(restored.Receipts));
                // The accepted operation resolves before source reads or further batches.
                var saves = batches.Sizes.Count;
                Assert.Equal(receipt, await store.ImportRecoveryAsync(snapshot, receipt));
                Assert.Equal(saves, batches.Sizes.Count);
                Assert.All(reads.Counts.Values, count => Assert.Equal(1, count));
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task FailureAfterAnIntermediateFlushRollsBackEveryDocumentAndTheLedger(bool cancel) =>
        BudgetAndQueryTests.WithDatabase(new(), async source =>
        {
            var content = new PostgreSqlContentStore(source);
            var snapshot = await PrepareClonesAsync(content, 48);
            using var cancellation = new CancellationTokenSource();
            var batches = new BatchAudit(cancel ? cancellation : null, fail: !cancel);
            IDocumentRepository store = new PostgreSqlStateStore(new DbContextOptionsBuilder<CellBridgeDbContext>()
                .UseNpgsql(source).AddInterceptors(batches).Options, validationContent: content);
            var receipt = Receipt(snapshot);
            if (cancel)
            {
                var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ImportRecoveryAsync(snapshot, receipt, cancellation.Token).AsTask());
                Assert.Equal(cancellation.Token, error.CancellationToken);
            }
            else
                Assert.Equal("Injected failure after a completed recovery batch.",
                    (await Assert.ThrowsAsync<InvalidOperationException>(() => store.ImportRecoveryAsync(snapshot, receipt).AsTask())).Message);
            Assert.Single(batches.Sizes);
            Assert.True(batches.HeadsBeforeFailure > 0);
            Assert.Equal(0, batches.LedgerBeforeFailure);
            var rolledBack = await store.CaptureRecoveryAsync();
            Assert.Empty(rolledBack.Snapshots); Assert.Empty(rolledBack.Receipts);
            // The same operation can be admitted again after rollback.
            IDocumentRepository retry = new PostgreSqlStateStore(source, validationContent: content);
            Assert.Equal(receipt, await retry.ImportRecoveryAsync(snapshot, receipt));
            Assert.Equal(receipt, await retry.ImportRecoveryAsync(snapshot, receipt));
            Assert.Equal(snapshot.Snapshots.Length, (await retry.CaptureRecoveryAsync()).Snapshots.Length);
        });

    [PostgreSqlFact]
    public Task LostCommitReplyResolvesThroughTheSingleAtomicReceiptWithoutReadingPayloadsAgain() =>
        BudgetAndQueryTests.WithDatabase(new(), async source =>
        {
            var content = new PostgreSqlContentStore(source); var snapshot = await PrepareClonesAsync(content, 2);
            var reads = new PayloadReads(content); var commands = new ImportCommands(); var commit = new LoseCommitReply(commands);
            IDocumentRepository store = new PostgreSqlStateStore(new DbContextOptionsBuilder<CellBridgeDbContext>()
                .UseNpgsql(source).AddInterceptors(commands, commit).Options, validationContent: reads);
            var receipt = Receipt(snapshot);
            var error = await Assert.ThrowsAsync<StorageUnavailableException>(() => store.ImportRecoveryAsync(snapshot, receipt).AsTask());
            Assert.IsType<IOException>(error.InnerException);
            Assert.True(commit.Triggered);
            Assert.Equal(receipt, await store.FindRecoveryReceiptAsync(receipt.OperationId));
            Assert.Equal(receipt, await store.ImportRecoveryAsync(snapshot, receipt));
            Assert.All(reads.Counts.Values, count => Assert.Equal(1, count));
            var restored = await store.CaptureRecoveryAsync();
            Assert.Equal(PortabilityTests.CanonicalSnapshots(snapshot.Snapshots), PortabilityTests.CanonicalSnapshots(restored.Snapshots));
            Assert.Equal(receipt, Assert.Single(restored.Receipts));
            await Assert.ThrowsAsync<RecoveryConflictException>(() => store.ImportRecoveryAsync(snapshot,
                receipt with { Fingerprint = new string('B', 64) }).AsTask());
        });

    private static RecoveryReceipt Receipt(ProviderSnapshot snapshot) => new(Guid.NewGuid(), new string('A', 64), snapshot.Snapshots.Length, snapshot.Snapshots.Length);

    private static async Task<ProviderSnapshot> PrepareClonesAsync(IContentStore content, int count)
    {
        IDocumentRepository memory = new InMemoryDocumentRepository(content);
        var service = new CellBridgeDocumentService(new(memory, content));
        var head = (await service.CreateAsync("/template.docx", MinimalDocx.Create("before"), TestActor.Value))!;
        var request = await StorageTestDocuments.SaveRequestAsync(service, head, MinimalDocx.Create("after"), TestActor.Value);
        var saved = await service.ExecuteAsync(head.ResourceId, DocumentPartitionKind.FileContents, request,
            ImmutableDictionary<string, string>.Empty, TestActor.Value);
        Assert.False(Assert.Single(saved.Response.SubResponses).Status);
        var template = (await memory.ExportDocumentAsync(head.ResourceId))!;
        var capture = new CaptureImport(new InMemoryDocumentRepository(content));
        await DocumentLoadSeeder.SeedAsync(new(capture, content), template,
            Enumerable.Range(0, count).Select(index => ($"/clone-{index:D4}.docx", TestActor.Value.Identity!)).ToArray());
        return capture.Snapshot!;
    }

    private sealed class CaptureImport(IDocumentRepository inner) : RepositoryTestProxy(inner)
    {
        public ProviderSnapshot? Snapshot { get; private set; }
        public override ValueTask<RecoveryReceipt> ImportRecoveryAsync(ProviderSnapshot snapshot, RecoveryReceipt receipt, CancellationToken ct = default)
        { Snapshot = snapshot; return ValueTask.FromResult(receipt); }
    }

    private sealed class PayloadReads(IContentStore inner) : IContentStore
    {
        public Dictionary<ContentHandle, int> Counts { get; } = [];
        public bool Durable => inner.Durable;
        public bool Shared => inner.Shared;
        public ValueTask<ContentHandle> WriteAsync(Stream stream, CancellationToken ct = default) => throw new InvalidOperationException("Unexpected write.");
        public ValueTask<Stream> OpenReadAsync(ContentHandle handle, CancellationToken ct = default)
        { Counts[handle] = Counts.GetValueOrDefault(handle) + 1; return inner.OpenReadAsync(handle, ct); }
    }

    private sealed class BatchAudit(CancellationTokenSource? cancel = null, bool fail = false) : SaveChangesInterceptor
    {
        public List<(int Rows, long TextBytes)> Sizes { get; } = [];
        public long HeadsBeforeFailure { get; private set; }
        public long LedgerBeforeFailure { get; private set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
        {
            var entries = eventData.Context!.ChangeTracker.Entries().ToArray();
            Sizes.Add((entries.Length, entries.SelectMany(entry => entry.Properties).Where(p => p.CurrentValue is string)
                .Sum(p => (long)Encoding.UTF8.GetByteCount((string)p.CurrentValue!))));
            return ValueTask.FromResult(result);
        }
        public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken ct = default)
        {
            if (Sizes.Count == 1 && (cancel is not null || fail))
            {
                var db = eventData.Context!;
                await using var command = db.Database.GetDbConnection().CreateCommand();
                command.Transaction = db.Database.CurrentTransaction!.GetDbTransaction();
                command.CommandText = "SELECT count(*) FROM cellbridge_repository_heads";
                HeadsBeforeFailure = (long)(await command.ExecuteScalarAsync(ct))!;
                command.CommandText = "SELECT count(*) FROM cellbridge_recovery_receipts";
                LedgerBeforeFailure = (long)(await command.ExecuteScalarAsync(ct))!;
                if (fail) throw new InvalidOperationException("Injected failure after a completed recovery batch.");
                cancel!.Cancel();
            }
            return result;
        }
    }

    private sealed class ImportCommands : DbCommandInterceptor
    {
        public List<string> Selects { get; } = [];
        public DbTransaction? ReceiptTransaction { get; private set; }
        private void Executed(DbCommand command)
        {
            if (command.CommandText.Contains("INSERT", StringComparison.OrdinalIgnoreCase) &&
                command.CommandText.Contains("cellbridge_recovery_receipts", StringComparison.Ordinal))
                ReceiptTransaction = command.Transaction;
        }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken ct = default)
        { if (command.CommandText.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)) Selects.Add(command.CommandText); return ValueTask.FromResult(result); }
        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken ct = default)
        { Executed(command); return ValueTask.FromResult(result); }
        public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken ct = default)
        { Executed(command); return ValueTask.FromResult(result); }
    }

    private sealed class LoseCommitReply(ImportCommands commands) : DbTransactionInterceptor
    {
        public bool Triggered { get; private set; }
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken ct = default)
        {
            if (!Triggered && ReferenceEquals(transaction, commands.ReceiptTransaction))
            { Triggered = true; throw new IOException("Injected lost reply after recovery commit."); }
            return Task.CompletedTask;
        }
    }
}
