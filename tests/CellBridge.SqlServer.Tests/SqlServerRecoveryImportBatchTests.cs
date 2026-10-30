using System.Data.Common;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CellBridge.AspNetCore;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.SqlServer;
using CellBridge.Storage.Tests;
using CellBridge.TestFixtures;
using CellBridge.TestInfrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;

namespace CellBridge.SqlServer.Tests;

[Collection("SQL Server")]
public sealed class SqlServerRecoveryImportBatchTests(SqlServerFixture fixture)
{
    [Fact]
    public async Task BatchesPreserveNormalizedRecordsConvertersCountersAndUniquePhysicalReads()
    {
        var (database, snapshot) = await PrepareAsync(48);
        var reads = new PayloadReads(database.Provider().Content); var batches = new BatchAudit();
        IDocumentRepository repository = new SqlServerStateStore(database.Options(false, batches), validationContent: reads);
        var receipt = Receipt(snapshot);
        Assert.Equal(receipt, await repository.ImportRecoveryAsync(snapshot, receipt));
        Assert.True(batches.Sizes.Count > 1);
        Assert.All(batches.Sizes, size =>
        {
            Assert.InRange(size.Rows, 1, 1024);
            Assert.True(size.Bytes <= 1024 * 1024 || size.Rows == 1);
        });
        var unique = ContentStoreReader.UniqueHandles(snapshot.Snapshots.SelectMany(s => s.ContentReferences).Select(r => r.Handle));
        Assert.Equal(unique.Count, reads.Counts.Count); Assert.All(reads.Counts.Values, count => Assert.Equal(1, count));
        await AssertRecoveredAsync(repository, snapshot, receipt);
        var saves = batches.Sizes.Count;
        Assert.Equal(receipt, await repository.ImportRecoveryAsync(snapshot, receipt));
        Assert.Equal(saves, batches.Sizes.Count); Assert.All(reads.Counts.Values, count => Assert.Equal(1, count));
        await database.AssertInventoryAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IntermediateFlushFailureRollsBackAllHeadsAndTheLedger(bool cancel)
    {
        var (database, snapshot) = await PrepareAsync(48);
        using var cancellation = new CancellationTokenSource();
        var batches = new BatchAudit(cancel ? cancellation : null, fail: !cancel);
        var content = database.Provider().Content;
        IDocumentRepository repository = new SqlServerStateStore(database.Options(false, batches), validationContent: content);
        var receipt = Receipt(snapshot);
        if (cancel)
        {
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repository.ImportRecoveryAsync(snapshot, receipt, cancellation.Token).AsTask());
            Assert.Equal(cancellation.Token, error.CancellationToken);
        }
        else
            Assert.Equal("Injected failure after a completed SQL recovery batch.",
                (await Assert.ThrowsAsync<InvalidOperationException>(() => repository.ImportRecoveryAsync(snapshot, receipt).AsTask())).Message);
        Assert.Single(batches.Sizes); Assert.True(batches.HeadsBeforeFailure > 0); Assert.Equal(0, batches.LedgerBeforeFailure);
        var rolledBack = await repository.CaptureRecoveryAsync(); Assert.Empty(rolledBack.Snapshots); Assert.Empty(rolledBack.Receipts);
        var retry = database.Provider().Documents;
        Assert.Equal(receipt, await retry.ImportRecoveryAsync(snapshot, receipt));
        Assert.Equal(receipt, await retry.ImportRecoveryAsync(snapshot, receipt));
        await AssertRecoveredAsync(retry, snapshot, receipt);
    }

    [Fact]
    public async Task LostCommitReplyResolvesTheOriginalAtomicReceiptWithoutRepeatingPayloadReads()
    {
        var (database, snapshot) = await PrepareAsync(2);
        var reads = new PayloadReads(database.Provider().Content); var commands = new ReceiptCommands(); var commit = new LostReply(commands);
        IDocumentRepository repository = new SqlServerStateStore(database.Options(false, commands, commit), validationContent: reads);
        var receipt = Receipt(snapshot);
        var error = await Assert.ThrowsAsync<StorageUnavailableException>(() => repository.ImportRecoveryAsync(snapshot, receipt).AsTask());
        Assert.IsType<IOException>(error.InnerException); Assert.True(commit.Triggered);
        Assert.Equal(receipt, await repository.FindRecoveryReceiptAsync(receipt.OperationId));
        Assert.Equal(receipt, await repository.ImportRecoveryAsync(snapshot, receipt));
        Assert.All(reads.Counts.Values, count => Assert.Equal(1, count));
        await AssertRecoveredAsync(repository, snapshot, receipt);
        await Assert.ThrowsAsync<RecoveryConflictException>(() => repository.ImportRecoveryAsync(snapshot,
            receipt with { Fingerprint = new string('b', 64) }).AsTask());
    }

    private async Task<(SqlServerDocuments Database, ProviderSnapshot Snapshot)> PrepareAsync(int clones)
    {
        var source = (await SqlServerDocuments.CreateAsync(fixture)).Provider();
        var head = (await new CellBridgeDocumentService(source).CreateAsync("/template.docx", MinimalDocx.Create("before"), SqlServerDocuments.Actor))!;
        head = await SqlServerDocuments.SaveAsync(source, head, "after");
        var template = (await source.Documents.ExportDocumentAsync(head.ResourceId))!;
        var database = await SqlServerDocuments.CreateAsync(fixture); var destination = database.Provider();
        await SqlServerNamespaceTests.CopyContent(source, destination, new([template], []));
        var capture = new CaptureImport(destination.Documents);
        await DocumentLoadSeeder.SeedAsync(new(capture, destination.Content), template,
            Enumerable.Range(0, clones).Select(index => ($"/clone-{index:D4}.docx", SqlServerDocuments.Actor.Identity)).ToArray());
        return (database, capture.Snapshot!);
    }

    private static RecoveryReceipt Receipt(ProviderSnapshot snapshot) => new(Guid.NewGuid(), new string('a', 64), snapshot.Snapshots.Length, snapshot.Snapshots.Length);
    private static async Task AssertRecoveredAsync(IDocumentRepository repository, ProviderSnapshot expected, RecoveryReceipt receipt)
    {
        var actual = await repository.CaptureRecoveryAsync();
        Assert.Equal(receipt, Assert.Single(actual.Receipts));
        Assert.Equal(expected.Snapshots.Length, actual.Snapshots.Length);
        var documents = actual.Snapshots.ToDictionary(s => s.Current.Head.ResourceId);
        foreach (var snapshot in expected.Snapshots)
        {
            var restored = documents[snapshot.Current.Head.ResourceId];
            NormalizedDocumentRecovery.Validate(restored, new());
            Assert.Equal(Canonical(snapshot), Canonical(restored));
        }
    }
    private static string Canonical(NormalizedDocumentSnapshot snapshot)
    {
        var root = JsonSerializer.SerializeToNode(snapshot with { Current = snapshot.Current with { UtcNow = default } })!.AsObject();
        foreach (var property in root.ToArray())
            if (property.Value is JsonArray array)
                root[property.Key] = new JsonArray(array.Select(value => value!.DeepClone()).OrderBy(value => value.ToJsonString(), StringComparer.Ordinal).ToArray());
        return root.ToJsonString();
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
        public List<(int Rows, long Bytes)> Sizes { get; } = [];
        public long HeadsBeforeFailure { get; private set; }
        public long LedgerBeforeFailure { get; private set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
        {
            var entries = eventData.Context!.ChangeTracker.Entries().ToArray();
            Sizes.Add((entries.Length, entries.SelectMany(e => e.Properties).Where(p => p.CurrentValue is string)
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
                command.CommandText = "SELECT COUNT_BIG(*) FROM cellbridge_documents.repository_heads";
                HeadsBeforeFailure = (long)(await command.ExecuteScalarAsync(ct))!;
                command.CommandText = "SELECT COUNT_BIG(*) FROM cellbridge_documents.recovery_receipts";
                LedgerBeforeFailure = (long)(await command.ExecuteScalarAsync(ct))!;
                if (fail) throw new InvalidOperationException("Injected failure after a completed SQL recovery batch.");
                cancel!.Cancel();
            }
            return result;
        }
    }
    private sealed class ReceiptCommands : DbCommandInterceptor
    {
        public DbTransaction? Transaction { get; private set; }
        private void Executed(DbCommand command)
        { if (command.CommandText.Contains("INSERT", StringComparison.OrdinalIgnoreCase) && command.CommandText.Contains("[recovery_receipts]", StringComparison.Ordinal)) Transaction = command.Transaction; }
        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken ct = default)
        { Executed(command); return ValueTask.FromResult(result); }
        public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken ct = default)
        { Executed(command); return ValueTask.FromResult(result); }
    }
    private sealed class LostReply(ReceiptCommands commands) : DbTransactionInterceptor
    {
        public bool Triggered { get; private set; }
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken ct = default)
        {
            if (!Triggered && ReferenceEquals(transaction, commands.Transaction))
            { Triggered = true; throw new IOException("Injected lost SQL recovery commit acknowledgement."); }
            return Task.CompletedTask;
        }
    }
}
