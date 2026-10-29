using CellBridge.AspNetCore;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.FileSystem;
using CellBridge.Storage.InMemory;
using CellBridge.Storage.PostgreSql;
using CellBridge.TestFixtures;
using CellBridge.Tests;

namespace CellBridge.Storage.Tests;

[Collection("PostgreSQL")]
public sealed class DocumentStagingTests
{
    [Fact]
    public Task RejectedMemoryServiceSavesStayBounded() => DocumentStagingScenario.RejectedServiceSavesStayBoundedAsync(
        StorageTestDocuments.Memory(limits: DocumentStagingScenario.Limits));

    [Fact]
    public Task MemoryReservationsDeduplicateAndDoNotAdvanceVersions() => DocumentStagingScenario.ReservationDeduplicatesAndDoesNotAdvanceVersionsAsync(StorageTestDocuments.Memory());

    [Fact]
    public Task ConcurrentMemoryReservationsStayWithinDocumentCapacity() => DocumentStagingScenario.ConcurrentReservationsStayWithinDocumentCapacityAsync(
        StorageTestDocuments.Memory(limits: DocumentStagingScenario.Limits));

    [PostgreSqlFact]
    public Task ConcurrentPostgreSqlReservationsStayWithinDocumentCapacity() => BudgetAndQueryTests.WithDatabase(DocumentStagingScenario.Limits, source =>
        DocumentStagingScenario.ConcurrentReservationsStayWithinDocumentCapacityAsync(new(new PostgreSqlStateStore(source, DocumentStagingScenario.Limits),
            new PostgreSqlContentStore(source), DocumentStagingScenario.Limits)));

    [Fact]
    public Task AcceptedMemoryServiceSaveCompletesReservations() => DocumentStagingScenario.SuccessfulServiceSaveCompletesReservationsAsync(StorageTestDocuments.Memory());

    [Fact]
    public async Task RejectedFileSystemServiceSavesStayBounded()
    {
        var root = Path.Combine(Path.GetTempPath(), "cellbridge-staging-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var provider = StorageTestDocuments.Memory(new FileSystemContentStore(root), DocumentStagingScenario.Limits);
            await DocumentStagingScenario.RejectedServiceSavesStayBoundedAsync(provider);
            Assert.True(Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length) <= provider.Limits.MaxDocumentStoredBytes);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task RejectedPostgreSqlServiceSavesStayBounded(bool filesystem) => BudgetAndQueryTests.WithDatabase(DocumentStagingScenario.Limits, async source =>
    {
        var root = Path.Combine(Path.GetTempPath(), "cellbridge-pg-staging-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            IContentStore content = filesystem ? new PostgreSqlFileSystemContentStore(source, root) : new PostgreSqlContentStore(source);
            var provider = new StorageProvider(new PostgreSqlStateStore(source, DocumentStagingScenario.Limits, validationContent: content), content, DocumentStagingScenario.Limits);
            await DocumentStagingScenario.RejectedServiceSavesStayBoundedAsync(provider);
            var before = await provider.Documents.CaptureRecoveryAsync();
            var pending = before.Snapshots.Sum(snapshot => snapshot.ContentReferences.Count(reference => reference.IsPending));
            Assert.True(pending > 0);
            var maintenance = new StorageMaintenance(source, provider.Limits);
            await maintenance.CollectOrphansAsync(fileSystemRoot: filesystem ? root : null);
            Assert.Equal(pending, (await provider.Documents.CaptureRecoveryAsync()).Snapshots.Sum(snapshot => snapshot.ContentReferences.Count(reference => reference.IsPending)));
            await maintenance.CollectOrphansAsync(true, true, filesystem ? root : null);
            var after = await provider.Documents.CaptureRecoveryAsync();
            Assert.All(after.Snapshots, snapshot => Assert.DoesNotContain(snapshot.ContentReferences, reference => reference.IsPending));
            Assert.Equal(before.Snapshots[0].Current.Head.StateVersion, after.Snapshots[0].Current.Head.StateVersion);
            Assert.True(after.Snapshots[0].Current.Head.Counters.StoredBytes < before.Snapshots[0].Current.Head.Counters.StoredBytes);
            Assert.Equal(0, (await maintenance.CollectOrphansAsync()).OrphanObjects);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    });

    [PostgreSqlFact]
    public Task PostgreSqlReservationsDeduplicateAndDoNotAdvanceVersions() => BudgetAndQueryTests.WithDatabase(new(), async source =>
    {
        var content = new PostgreSqlContentStore(source);
        await DocumentStagingScenario.ReservationDeduplicatesAndDoesNotAdvanceVersionsAsync(new(new PostgreSqlStateStore(source), content));
        await DocumentStagingScenario.SuccessfulServiceSaveCompletesReservationsAsync(new(new PostgreSqlStateStore(source), content));
    });

    [PostgreSqlFact]
    public Task PostgreSqlRecoveryPreservesMissingPendingReservations() => BudgetAndQueryTests.WithDatabase(new(), source =>
        DocumentStagingScenario.MissingPendingContentSurvivesPortableRecoveryAsync(new(new PostgreSqlStateStore(source), new PostgreSqlContentStore(source))));

    [Fact]
    public async Task CancellationAfterReservationLeavesChargedMissingContentAndUnchangedVersions()
    {
        var content = new CancelWriteContent(new InMemoryContentStore());
        var provider = StorageTestDocuments.Memory(content);
        var service = new CellBridgeDocumentService(provider);
        var head = (await service.CreateAsync("/cancelled-stage.docx", MinimalDocx.Create(), TestActor.Value))!;
        Assert.True((await new SharedDocumentLocks(service).ApplyAsync(head.ResourceId, HostLockOperation.Acquire, "host", TestActor.Value)).Success);
        var before = (await provider.Documents.ReadControlAsync(head.ResourceId))!.Head;
        using var cancellation = new CancellationTokenSource();
        content.Cancel = cancellation;
        using var input = new MemoryStream(MinimalDocx.Create("cancelled staged content"));
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SaveWholeFileAsync(head.ResourceId, input,
            "cancelled", TestActor.Value, "host", cancellation.Token).AsTask());
        Assert.Equal(cancellation.Token, error.CancellationToken);
        var snapshot = (await provider.Documents.ExportDocumentAsync(head.ResourceId))!;
        Assert.Equal(before.StateVersion, snapshot.Current.Head.StateVersion);
        Assert.Equal(before.ContentVersion, snapshot.Current.Head.ContentVersion);
        Assert.Empty(snapshot.Receipts);
        Assert.True(snapshot.Current.Head.Counters.StoredBytes > before.Counters.StoredBytes);
        var pending = snapshot.ContentReferences.Where(reference => reference.IsPending).ToArray();
        Assert.NotEmpty(pending);
        foreach (var reference in pending)
            await Assert.ThrowsAsync<StorageCorruptionException>(() => content.OpenReadAsync(reference.Handle).AsTask());
        var destination = new InMemoryDocumentRepository(content, clock: new RecoveryClock());
        await destination.ImportRecoveryAsync(new([snapshot], []), new(Guid.NewGuid(), new string('a', 64), 1, 1));
        Assert.Equal(PortabilityTests.CanonicalSnapshots([snapshot]),
            PortabilityTests.CanonicalSnapshots([(await destination.ExportDocumentAsync(head.ResourceId))!]));
    }

    [Fact]
    public async Task RecoveryPreservesMissingPendingReservationsWithoutReadingThem()
    {
        var content = new InMemoryContentStore();
        var provider = StorageTestDocuments.Memory(content);
        var head = (await new CellBridgeDocumentService(provider).CreateAsync("/missing-stage.docx", MinimalDocx.Create(), TestActor.Value))!;
        var missing = DocumentStagingScenario.Handle([123, 124, 125]);
        Assert.True(await provider.Documents.ReserveContentAsync(StorageTestDocuments.Admission(head), [missing]));
        await Assert.ThrowsAsync<StorageCorruptionException>(() => content.OpenReadAsync(missing).AsTask());
        var snapshot = await provider.Documents.CaptureRecoveryAsync();
        var destination = new InMemoryDocumentRepository(content);
        var receipt = new RecoveryReceipt(Guid.NewGuid(), new string('a', 64), 1, 1);
        await destination.ImportRecoveryAsync(snapshot, receipt);
        var recovered = (await destination.ExportDocumentAsync(head.ResourceId))!;
        Assert.Equal(PortabilityTests.CanonicalSnapshots(snapshot.Snapshots), PortabilityTests.CanonicalSnapshots([recovered]));
        Assert.True(Assert.Single(recovered.ContentReferences.Where(reference => reference.Handle == missing)).IsPending);

        using var files = new PortabilityTests.TemporaryFiles();
        var archive = new PortableArchive();
        await archive.ExportAsync(provider, files.Path("pending.zip"), PortabilityTests.Context);
        var portableDestination = StorageTestDocuments.Memory();
        await archive.ImportAsync(portableDestination, files.Path("pending.zip"), PortabilityTests.Context);
        Assert.Equal(PortabilityTests.CanonicalSnapshots(snapshot.Snapshots), PortabilityTests.CanonicalSnapshots(
            (await portableDestination.Documents.CaptureRecoveryAsync()).Snapshots));
    }

    private sealed class CancelWriteContent(IContentStore inner) : IContentStore
    {
        public CancellationTokenSource? Cancel;
        public bool Durable => inner.Durable;
        public bool Shared => inner.Shared;
        public ValueTask<ContentHandle> WriteAsync(Stream source, CancellationToken cancellationToken = default)
        {
            Cancel?.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            return inner.WriteAsync(source, cancellationToken);
        }
        public ValueTask<Stream> OpenReadAsync(ContentHandle handle, CancellationToken cancellationToken = default) => inner.OpenReadAsync(handle, cancellationToken);
    }

    private sealed class RecoveryClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow.AddDays(1);
    }
}
