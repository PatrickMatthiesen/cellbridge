using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CellBridge.AspNetCore;
using CellBridge.FssHttpB;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using CellBridge.Storage.PostgreSql;
using CellBridge.TestFixtures;

namespace CellBridge.Storage.Tests;

[Collection("PostgreSQL")]
public sealed class PortabilityProviderTests
{
    [PostgreSqlFact]
    public Task PostgreSqlToFileSystemToPostgreSqlRetainsEverySnapshotRootAndRestores() =>
        BudgetAndQueryTests.WithDatabase(new(), async sourceDb =>
        await BudgetAndQueryTests.WithDatabase(new(), async fileDb =>
        await BudgetAndQueryTests.WithDatabase(new(), async restoredDb =>
        {
            using var files = new PortabilityTests.TemporaryFiles();
            var source = new StorageProvider(new PostgreSqlStateStore(sourceDb), new PostgreSqlContentStore(sourceDb));
            var binding = Guid.NewGuid(); var original = await PortabilityTests.History(source, binding);
            var snapshot = await ((IProviderRecoveryStore)source.Documents).CaptureRecoveryAsync();
            Assert.All(snapshot.Snapshots, document => Assert.True(document.ApplicationRevisions.Length > 1));
            var document = Assert.Single(snapshot.Snapshots);
            var headReferences = document.Visibility.Where(value => value.StartSequence <= document.Partitions.Single(partition => partition.Head.Kind == value.Scope.TargetPartition).Head.PublicationSequence && value.EndSequence is null)
                .Select(value => document.Elements.Single(element => element.Scope == value.Scope && element.Element.Id == value.Element).Element.Payload.Key).ToHashSet();
            var oldOnly = document.ContentReferences.Select(reference => reference.Handle).Where(handle => !headReferences.Contains(handle.Key)).ToArray();
            Assert.NotEmpty(oldOnly);
            var fsContent = new PostgreSqlFileSystemContentStore(fileDb, files.Path("content"));
            var fs = new StorageProvider(new PostgreSqlStateStore(fileDb, validationContent: fsContent), fsContent);
            var portable = new PortableArchive(); var context = PortabilityTests.Context with
                { ExternalDestinations = [new(binding, "remote", "same-delivery-ledger")] };
            await portable.MigrateAsync(source, fs, files.Path("postgres.zip"), context);
            Assert.Equal(PortabilityTests.CanonicalSnapshots(snapshot.Snapshots), PortabilityTests.CanonicalSnapshots((await fs.Documents.CaptureRecoveryAsync()).Snapshots));
            foreach (var handle in oldOnly) Assert.Equal(await source.Content.ReadVerifiedAsync(handle), await fs.Content.ReadVerifiedAsync(handle));
            var target = new StorageProvider(new PostgreSqlStateStore(restoredDb), new PostgreSqlContentStore(restoredDb));
            var receipt = await portable.MigrateAsync(fs, target, files.Path("filesystem.zip"), context);
            var after = await ((IProviderRecoveryStore)target.Documents).CaptureRecoveryAsync();
            Assert.Equal(JsonSerializer.Serialize(snapshot.Heads), JsonSerializer.Serialize(after.Heads));
            Assert.Equal(PortabilityTests.CanonicalSnapshots(snapshot.Snapshots), PortabilityTests.CanonicalSnapshots(after.Snapshots));
            Assert.Equal(2, after.Receipts.Length);
            Assert.Equal(JsonSerializer.Serialize(original.Delivery), JsonSerializer.Serialize((await target.Documents.FindAsync(new DocumentIdentity.Resource(original.ResourceId)))!.Delivery));
            Assert.NotEmpty(document.Deliveries);
            var restored = await new CellBridgeDocumentService(target).RestoreRevisionAsync(original.ResourceId,
                3, original.LatestRevisionNumber, "durable-recovery-restore", TestActor.Value);
            Assert.Equal(document.ApplicationRevisions.Single(revision => revision.RevisionNumber == 3).Package.Sha256, restored.Revision.Package.Sha256);
            var reopened = new StorageProvider(new PostgreSqlStateStore(restoredDb), new PostgreSqlContentStore(restoredDb));
            Assert.Equal(receipt, await portable.ImportAsync(reopened, files.Path("filesystem.zip"), context));
            await new PostgreSqlStateStore(restoredDb).InitializeAsync();
            await using (var orphan = new MemoryStream([91, 92, 93]))
                await target.Content.WriteAsync(orphan);
            var collected = await new StorageMaintenance(restoredDb).CollectOrphansAsync(apply: true, quiescent: true);
            Assert.True(collected.OrphanObjects > 0);
            Assert.Equal(receipt, await portable.ImportAsync(reopened, files.Path("filesystem.zip"), context));
        })));

    [PostgreSqlFact]
    public Task UnknownPersistedFieldsAndRecoverySchemaFailBeforeDestinationWrites() =>
        BudgetAndQueryTests.WithDatabase(new(), async db =>
        {
            using var files = new PortabilityTests.TemporaryFiles();
            var source = new StorageProvider(new PostgreSqlStateStore(db), new PostgreSqlContentStore(db));
            var original = (await new CellBridgeDocumentService(source).CreateAsync("/unknown.docx", MinimalDocx.Create(), TestActor.Value))!;
            foreach (var defect in new[] { "nested", "duplicate", "missing" })
            {
                var state = JsonNode.Parse(JsonSerializer.Serialize(original))!;
                if (defect == "nested") state["Security"]!["ForeignPolicy"] = "unsupported";
                if (defect == "missing") state.AsObject().Remove("ContentVersion");
                var json = state.ToJsonString();
                if (defect == "duplicate") json = json.Replace("\"ContentVersion\":1", "\"ContentVersion\":1,\"ContentVersion\":1");
                await using var change = db.CreateCommand("UPDATE cellbridge_repository_heads SET \"Json\"=$1 WHERE \"ResourceId\"=$2");
                change.Parameters.AddWithValue(json); change.Parameters.AddWithValue(original.ResourceId); await change.ExecuteNonQueryAsync();
                await Assert.ThrowsAsync<StorageCorruptionException>(() => new PortableArchive().ExportAsync(source,
                    files.Path(defect + ".zip"), PortabilityTests.Context).AsTask());
                Assert.False(File.Exists(files.Path(defect + ".zip")));
            }
            var memory = PortabilityTests.Memory();
            await new CellBridgeDocumentService(memory).CreateAsync("/a.docx", MinimalDocx.Create(), TestActor.Value);
            await new PortableArchive().ExportAsync(memory, files.Path("good.zip"), PortabilityTests.Context);
            var writes = new PortabilityTests.CountWrites(source.Content);
            var destination = new StorageProvider(source.Documents, writes);
            await using (var schema = db.CreateCommand("UPDATE cellbridge_recovery_schema SET version=2")) await schema.ExecuteNonQueryAsync();
            await Assert.ThrowsAsync<StorageUnavailableException>(() => new PortableArchive().ImportAsync(destination,
                files.Path("good.zip"), PortabilityTests.Context).AsTask());
            Assert.Equal(0, writes.Writes);
            await using (var schema = db.CreateCommand("DROP TABLE cellbridge_recovery_schema")) await schema.ExecuteNonQueryAsync();
            await Assert.ThrowsAsync<StorageUnavailableException>(() => new PortableArchive().ImportAsync(destination,
                files.Path("good.zip"), PortabilityTests.Context).AsTask());
            Assert.Equal(0, writes.Writes);
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OpaqueProviderKeysAreRemappedAndCancellationLeavesNothingCurrent(bool interrupt)
    {
        using var files = new PortabilityTests.TemporaryFiles();
        var source = StorageTestDocuments.Memory(new RenamedKeys(new InMemoryContentStore(), "source/../"));
        var original = (await new CellBridgeDocumentService(source).CreateAsync("/a.docx", MinimalDocx.Create(), TestActor.Value))!;
        var portable = new PortableArchive(); await portable.ExportAsync(source, files.Path("export.zip"), PortabilityTests.Context);
        var content = new RenamedKeys(new InMemoryContentStore(), "target:");
        var destination = StorageTestDocuments.Memory(content);
        if (interrupt)
        {
            using var cancel = new CancellationTokenSource(); content.Cancel = cancel;
            await Assert.ThrowsAsync<OperationCanceledException>(() => portable.ImportAsync(destination,
                files.Path("export.zip"), PortabilityTests.Context, cancel.Token).AsTask());
            Assert.Empty((await destination.Documents.CaptureRecoveryAsync()).Snapshots);
            Assert.Empty((await ((IProviderRecoveryStore)destination.Documents).CaptureRecoveryAsync()).Receipts);
            content.Cancel = null;
        }
        await portable.ImportAsync(destination, files.Path("export.zip"), PortabilityTests.Context);
        var state = (await destination.Documents.FindAsync(new DocumentIdentity.Resource(original.ResourceId)))!;
        var exported = (await destination.Documents.ExportDocumentAsync(state.ResourceId))!;
        Assert.All(exported.ContentReferences, reference => Assert.StartsWith("target:", reference.Handle.Key));
        Assert.Equal(original.Package.Sha256, state.Package.Sha256);
        Assert.Equal(await StorageTestDocuments.PackageAsync(source, original.Package), await StorageTestDocuments.PackageAsync(destination, state.Package));
        Assert.Equal(JsonSerializer.Serialize((await source.Documents.ReadControlAsync(original.ResourceId))!.Partitions.Select(p => p.Identity)), JsonSerializer.Serialize((await destination.Documents.ReadControlAsync(state.ResourceId))!.Partitions.Select(p => p.Identity)));
    }

    [Fact]
    public async Task ExportUsesCapturedHeadsWhileSaveRenameRecreationAndRestoreProceed()
    {
        using var files = new PortabilityTests.TemporaryFiles(); var source = PortabilityTests.Memory();
        var original = await PortabilityTests.History(source);
        var frozen = await ((IProviderRecoveryStore)source.Documents).CaptureRecoveryAsync();
        var content = new BlockingContent(source.Content);
        var task = new PortableArchive().ExportAsync(new(source.Documents, content), files.Path("concurrent.zip"), PortabilityTests.Context).AsTask();
        await content.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var service = new CellBridgeDocumentService(source);
        await service.RestoreRevisionAsync(original.ResourceId, 3, original.LatestRevisionNumber, "during-export", TestActor.Value);
        var live = (await source.Documents.FindAsync(new DocumentIdentity.Resource(original.ResourceId)))!;
        live = (await source.Documents.ChangePathAsync(new(live.ResourceId, StorageTestDocuments.Admission(live), "/renamed.docx", "/RENAMED.DOCX", ImmutableDictionary<string, string>.Empty))).Current;
        Assert.True((await source.Documents.ChangeLifecycleAsync(new DocumentLifecycleCommand.Delete(live.ResourceId, live.LifecycleGeneration, live.StateVersion))).Applied);
        var retired = (await source.Documents.FindAsync(new DocumentIdentity.Resource(live.ResourceId, true)))!;
        var replacement = await IndexedDocumentPublicationBuilder.CreateAsync(Guid.NewGuid(), retired.Path, MinimalDocx.Create("new incarnation"),
            original.Security, source.Content, source.Limits, lifecycleGeneration: 2);
        Assert.True((await source.Documents.ChangeLifecycleAsync(new DocumentLifecycleCommand.Recreate(retired.ResourceId, 1, retired.StateVersion, replacement))).Applied);
        content.Proceed.TrySetResult(); await task;
        var destination = PortabilityTests.Memory(); await new PortableArchive().ImportAsync(destination, files.Path("concurrent.zip"), PortabilityTests.Context);
        var after = await ((IProviderRecoveryStore)destination.Documents).CaptureRecoveryAsync();
        Assert.Equal(PortabilityTests.CanonicalSnapshots(frozen.Snapshots), PortabilityTests.CanonicalSnapshots(after.Snapshots));
    }

    [Fact]
    public async Task ConcurrentCreationWinsWithoutExposingAnyImportedHead()
    {
        using var files = new PortabilityTests.TemporaryFiles(); var source = PortabilityTests.Memory();
        var sourceService = new CellBridgeDocumentService(source);
        await sourceService.CreateAsync("/a.docx", MinimalDocx.Create(), TestActor.Value);
        await sourceService.CreateAsync("/b.docx", MinimalDocx.Create(), TestActor.Value);
        var portable = new PortableArchive(); await portable.ExportAsync(source, files.Path("export.zip"), PortabilityTests.Context);
        var inner = new InMemoryContentStore(); var content = new BlockingContent(inner) { BlockWrites = true };
        var destination = StorageTestDocuments.Memory(content);
        var importing = portable.ImportAsync(destination, files.Path("export.zip"), PortabilityTests.Context).AsTask();
        await content.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Empty((await destination.Documents.CaptureRecoveryAsync()).Snapshots);
        // Different writer uses the same immutable content without waiting for the recovery writer.
        var winner = await new CellBridgeDocumentService(new(destination.Documents, inner)).CreateAsync("/winner.docx", MinimalDocx.Create(), TestActor.Value);
        content.Proceed.TrySetResult();
        await Assert.ThrowsAsync<RecoveryConflictException>(() => importing);
        Assert.Equal(winner!.ResourceId, Assert.Single(await destination.Documents.ListAsync(0, 100)).ResourceId);
        Assert.Empty((await ((IProviderRecoveryStore)destination.Documents).CaptureRecoveryAsync()).Receipts);
    }

    [Theory]
    [InlineData("document")]
    [InlineData("graph")]
    public async Task DestinationAdmissionChecksOldRevisionEvenWithoutItsProviderSnapshot(string budget)
    {
        using var files = new PortabilityTests.TemporaryFiles(); var source = PortabilityTests.Memory();
        var service = new CellBridgeDocumentService(source);
        var original = (await service.CreateAsync("/history.docx", MinimalDocx.Create(), TestActor.Value))!;
        var large = MinimalDocx.Create(string.Concat(Enumerable.Range(0, 2000).Select(_ => Guid.NewGuid().ToString("N"))));
        var locks = new SharedDocumentLocks(service);
        Assert.True((await locks.ApplyAsync(original.ResourceId, HostLockOperation.Acquire, "lock", TestActor.Value)).Success);
        using (var input = new MemoryStream(large)) await service.SaveWholeFileAsync(original.ResourceId, input, "large", TestActor.Value, "lock");
        using (var input = new MemoryStream(MinimalDocx.Create("small"))) await service.SaveWholeFileAsync(original.ResourceId, input, "small", TestActor.Value, "lock");
        Assert.True((await locks.ApplyAsync(original.ResourceId, HostLockOperation.Release, "lock", TestActor.Value)).Success);
        var before = await ((IProviderRecoveryStore)source.Documents).CaptureRecoveryAsync(); Assert.Single(before.Snapshots);
        var portable = new PortableArchive(); await portable.ExportAsync(source, files.Path("history.zip"), PortabilityTests.Context);
        var limits = budget == "document" ? new StorageLimits { MaxDocumentBytes = 8000 }
            : new StorageLimits { MaxGraphElements = before.Snapshots[0].Current.Head.Counters.GraphElements - 1 };
        var content = new PortabilityTests.CountWrites(new InMemoryContentStore()); var destination = StorageTestDocuments.Memory(content, limits);
        await Assert.ThrowsAsync<StorageQuotaExceededException>(() => portable.ImportAsync(destination, files.Path("history.zip"), PortabilityTests.Context).AsTask());
        Assert.Equal(0, content.Writes); Assert.Empty((await destination.Documents.CaptureRecoveryAsync()).Snapshots);
    }

    [Theory]
    [InlineData("editor")]
    [InlineData("schema")]
    [InlineData("exclusive")]
    public async Task EveryActiveCoordinationKindBlocksImport(string kind)
    {
        using var files = new PortabilityTests.TemporaryFiles(); var source = PortabilityTests.Memory();
        var seed = await IndexedDocumentPublicationBuilder.CreateAsync(Guid.NewGuid(), "/active.docx", MinimalDocx.Create(),
            DocumentSecurity.Create(TestActor.Value.Identity), source.Content, source.Limits);
        var now = DateTime.UtcNow;
        var lease = new LeaseState("id", "client", now.AddMinutes(5), 1, null, TestActor.Value.Identity.Subject);
        seed = kind switch
        {
            "schema" => seed with { Coordination = seed.Coordination with { SchemaOwners = [lease] } },
            "exclusive" => seed with { Coordination = seed.Coordination with { Exclusive = lease } },
            _ => seed with { Editors = [new(Guid.NewGuid(), "user", now, now, now.AddMinutes(5), 300, true, 1,
                ImmutableDictionary<string, ImmutableArray<byte>>.Empty, TestActor.Value.Identity)] },
        };
        Assert.True((await source.Documents.CreateAsync(seed)).Created);
        var portable = new PortableArchive(); await portable.ExportAsync(source, files.Path("active.zip"), PortabilityTests.Context);
        var writes = new PortabilityTests.CountWrites(new InMemoryContentStore()); var destination = StorageTestDocuments.Memory(writes);
        await Assert.ThrowsAsync<RecoveryConflictException>(() => portable.ImportAsync(destination, files.Path("active.zip"), PortabilityTests.Context).AsTask());
        Assert.Equal(0, writes.Writes);
    }

    [PostgreSqlFact]
    public Task AtomicDurableImportsReplayAcrossConcurrentRequests() =>
        BudgetAndQueryTests.WithDatabase(new(), async db =>
        {
            using var files = new PortabilityTests.TemporaryFiles(); var memory = PortabilityTests.Memory();
            await new CellBridgeDocumentService(memory).CreateAsync("/a.docx", MinimalDocx.Create(), TestActor.Value);
            var portable = new PortableArchive(); await portable.ExportAsync(memory, files.Path("export.zip"), PortabilityTests.Context);
            var destination = new StorageProvider(new PostgreSqlStateStore(db), new PostgreSqlContentStore(db));
            var receipts = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => portable.ImportAsync(destination,
                files.Path("export.zip"), PortabilityTests.Context).AsTask()));
            Assert.All(receipts, r => Assert.Equal(receipts[0], r));
            Assert.Single(await destination.Documents.ListAsync(0, 100));
        });

    private sealed class RenamedKeys(IContentStore inner, string prefix) : IContentStore
    {
        public CancellationTokenSource? Cancel;
        public bool Durable => inner.Durable; public bool Shared => inner.Shared;
        public async ValueTask<ContentHandle> WriteAsync(Stream s, CancellationToken ct = default)
        { var handle = await inner.WriteAsync(s, ct); Cancel?.Cancel(); return handle with { Key = prefix + handle.Key }; }
        public ValueTask<Stream> OpenReadAsync(ContentHandle h, CancellationToken ct = default) => inner.OpenReadAsync(h with { Key = h.Key[prefix.Length..] }, ct);
    }

    private sealed class BlockingContent(IContentStore inner) : IContentStore
    {
        public bool BlockWrites;
        private int _blocked;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Proceed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Durable => inner.Durable; public bool Shared => inner.Shared;
        private async Task Gate(CancellationToken ct)
        { if (Interlocked.Exchange(ref _blocked, 1) == 0) { Started.TrySetResult(); await Proceed.Task.WaitAsync(ct); } }
        public async ValueTask<ContentHandle> WriteAsync(Stream s, CancellationToken ct = default)
        { if (BlockWrites) await Gate(ct); return await inner.WriteAsync(s, ct); }
        public async ValueTask<Stream> OpenReadAsync(ContentHandle h, CancellationToken ct = default)
        { if (!BlockWrites) await Gate(ct); return await inner.OpenReadAsync(h, ct); }
    }
}
