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
using CellBridge.Web;

namespace CellBridge.Storage.Tests;

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
            var snapshot = await ((IProviderRecoveryStore)source.State).CaptureRecoveryAsync();
            Assert.True(snapshot.Snapshots.Length > snapshot.Heads.Length);
            var headReferences = StorageReferences.Handles(original).Select(h => h.Key).ToHashSet();
            var oldOnly = ContentStoreReader.UniqueHandles(snapshot.Snapshots.SelectMany(StorageReferences.Handles))
                .Where(h => !headReferences.Contains(h.Key)).ToArray();
            Assert.NotEmpty(oldOnly);
            var fs = new StorageProvider(new PostgreSqlStateStore(fileDb), new PostgreSqlFileSystemContentStore(fileDb, files.Path("content")));
            var portable = new PortableArchive(); var context = PortabilityTests.Context with
                { ExternalDestinations = [new(binding, "remote", "same-delivery-ledger")] };
            await portable.MigrateAsync(source, fs, files.Path("postgres.zip"), context);
            Assert.Equal(JsonSerializer.Serialize(snapshot.Snapshots), JsonSerializer.Serialize((await ((IProviderRecoveryStore)fs.State).CaptureRecoveryAsync()).Snapshots));
            foreach (var handle in oldOnly) Assert.Equal(await source.Content.ReadVerifiedAsync(handle), await fs.Content.ReadVerifiedAsync(handle));
            var target = new StorageProvider(new PostgreSqlStateStore(restoredDb), new PostgreSqlContentStore(restoredDb));
            var receipt = await portable.MigrateAsync(fs, target, files.Path("filesystem.zip"), context);
            var after = await ((IProviderRecoveryStore)target.State).CaptureRecoveryAsync();
            Assert.Equal(JsonSerializer.Serialize(snapshot.Heads), JsonSerializer.Serialize(after.Heads));
            Assert.Equal(JsonSerializer.Serialize(snapshot.Snapshots), JsonSerializer.Serialize(after.Snapshots));
            Assert.Equal(2, after.Receipts.Length);
            Assert.Equal(JsonSerializer.Serialize(original.Publication), JsonSerializer.Serialize((await target.State.FindByResourceIdAsync(original.ResourceId))!.Publication));
            Assert.NotEmpty(original.Publication!.Pending);
            var restored = await new CellBridgeDocumentService(target).RestoreRevisionAsync(original.ResourceId,
                3, RevisionHistory.Latest(original), "durable-recovery-restore", TestActor.Value);
            Assert.Equal(original.Revisions[2].Content, restored.Revision.Content);
            var reopened = new StorageProvider(new PostgreSqlStateStore(restoredDb), new PostgreSqlContentStore(restoredDb));
            Assert.Equal(receipt, await portable.ImportAsync(reopened, files.Path("filesystem.zip"), context));
            await new PostgreSqlStateStore(restoredDb).InitializeAsync();
            await using (var orphan = new MemoryStream([91, 92, 93]))
                await target.Content.WriteAsync(orphan);
            var collected = await new StorageMaintenance(restoredDb).CollectOrphansAsync(apply: true, quiescent: true);
            Assert.True(collected.OrphanObjects > 0);
            Assert.Equal(receipt, await portable.ImportAsync(reopened, files.Path("filesystem.zip"), context));
            await using var usage = restoredDb.CreateCommand("""
                SELECT (SELECT stored_bytes FROM cellbridge_usage) =
                    (SELECT COALESCE(SUM(length),0) FROM cellbridge_objects) +
                    (SELECT COALESCE(SUM(octet_length(state_json)),0) FROM cellbridge_states) +
                    (SELECT COALESCE(SUM(octet_length(receipt_json)),0) FROM cellbridge_recovery_receipts)
                """);
            Assert.Equal(true, await usage.ExecuteScalarAsync());
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
                if (defect == "duplicate") json = json.Replace("\"FormatVersion\":2", "\"FormatVersion\":2,\"FormatVersion\":2");
                await using var change = db.CreateCommand("UPDATE cellbridge_states SET state_json=$1 WHERE resource_id=$2");
                change.Parameters.AddWithValue(json); change.Parameters.AddWithValue(original.ResourceId); await change.ExecuteNonQueryAsync();
                await Assert.ThrowsAsync<StorageCorruptionException>(() => new PortableArchive().ExportAsync(source,
                    files.Path(defect + ".zip"), PortabilityTests.Context).AsTask());
                Assert.False(File.Exists(files.Path(defect + ".zip")));
            }
            var memory = PortabilityTests.Memory();
            await new CellBridgeDocumentService(memory).CreateAsync("/a.docx", MinimalDocx.Create(), TestActor.Value);
            await new PortableArchive().ExportAsync(memory, files.Path("good.zip"), PortabilityTests.Context);
            var writes = new PortabilityTests.CountWrites(source.Content);
            var destination = new StorageProvider(source.State, writes);
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
        var source = new StorageProvider(new InMemoryStateStore(), new RenamedKeys(new InMemoryContentStore(), "source/../"));
        var original = (await new CellBridgeDocumentService(source).CreateAsync("/a.docx", MinimalDocx.Create(), TestActor.Value))!;
        var portable = new PortableArchive(); await portable.ExportAsync(source, files.Path("export.zip"), PortabilityTests.Context);
        var content = new RenamedKeys(new InMemoryContentStore(), "target:");
        var destination = new StorageProvider(new InMemoryStateStore(), content);
        if (interrupt)
        {
            using var cancel = new CancellationTokenSource(); content.Cancel = cancel;
            await Assert.ThrowsAsync<OperationCanceledException>(() => portable.ImportAsync(destination,
                files.Path("export.zip"), PortabilityTests.Context, cancel.Token).AsTask());
            Assert.Empty(await destination.State.ListAsync(0, 100));
            Assert.Empty((await ((IProviderRecoveryStore)destination.State).CaptureRecoveryAsync()).Receipts);
            content.Cancel = null;
        }
        await portable.ImportAsync(destination, files.Path("export.zip"), PortabilityTests.Context);
        var state = (await destination.State.FindByResourceIdAsync(original.ResourceId))!;
        Assert.All(StorageReferences.Handles(state), h => Assert.StartsWith("target:", h.Key));
        Assert.Equal(original.Content.Sha256, state.Content.Sha256);
        Assert.Equal(await source.Content.ReadVerifiedAsync(original.Content), await destination.Content.ReadVerifiedAsync(state.Content));
        Assert.Equal(JsonSerializer.Serialize(original.Partitions.Select(p => p.Identity)), JsonSerializer.Serialize(state.Partitions.Select(p => p.Identity)));
    }

    [Fact]
    public async Task ExportUsesCapturedHeadsWhileSaveRenameRecreationAndRestoreProceed()
    {
        using var files = new PortabilityTests.TemporaryFiles(); var source = PortabilityTests.Memory();
        var original = await PortabilityTests.History(source);
        var frozen = await ((IProviderRecoveryStore)source.State).CaptureRecoveryAsync();
        var content = new BlockingContent(source.Content);
        var task = new PortableArchive().ExportAsync(new(source.State, content), files.Path("concurrent.zip"), PortabilityTests.Context).AsTask();
        await content.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var service = new CellBridgeDocumentService(source);
        await service.RestoreRevisionAsync(original.ResourceId, 3, RevisionHistory.Latest(original), "during-export", TestActor.Value);
        await ((IAtomicDocumentRenameStore)source.State).RenameAsync(original.ResourceId, (s, _) =>
            new StateTransition<bool>(s with { Path = "/renamed.docx", PathKey = "/RENAMED.DOCX" }, true));
        var live = (await source.State.FindByResourceIdAsync(original.ResourceId))!;
        await ((IDocumentLifecycleStore)source.State).TryDeleteAsync(live.ResourceId, live.LifecycleGeneration, live.StateVersion);
        var retired = (await ((IDocumentLifecycleStore)source.State).FindLifecycleAsync(live.ResourceId))!;
        var replacement = await new DocumentStore().Put(retired.Path, MinimalDocx.Create("new incarnation")).CaptureAsync(source.Content);
        replacement = RevisionHistory.Initialize(replacement with { LifecycleGeneration = 2, Security = original.Security });
        Assert.True(await ((IDocumentLifecycleStore)source.State).TryRecreateAsync(retired.ResourceId, 1, retired.StateVersion, replacement));
        content.Proceed.TrySetResult(); await task;
        var destination = PortabilityTests.Memory(); await new PortableArchive().ImportAsync(destination, files.Path("concurrent.zip"), PortabilityTests.Context);
        var after = await ((IProviderRecoveryStore)destination.State).CaptureRecoveryAsync();
        Assert.Equal(JsonSerializer.Serialize(frozen.Snapshots), JsonSerializer.Serialize(after.Snapshots));
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
        var destination = new StorageProvider(new InMemoryStateStore(), content);
        var importing = portable.ImportAsync(destination, files.Path("export.zip"), PortabilityTests.Context).AsTask();
        await content.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Empty(await destination.State.ListAsync(0, 100));
        // Different writer uses the same immutable content without waiting for the recovery writer.
        var winner = await new CellBridgeDocumentService(new(destination.State, inner)).CreateAsync("/winner.docx", MinimalDocx.Create(), TestActor.Value);
        content.Proceed.TrySetResult();
        await Assert.ThrowsAsync<RecoveryConflictException>(() => importing);
        Assert.Equal(winner!.ResourceId, Assert.Single(await destination.State.ListAsync(0, 100)).ResourceId);
        Assert.Empty((await ((IProviderRecoveryStore)destination.State).CaptureRecoveryAsync()).Receipts);
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
        var graph = new CellBridge.Tests.GraphFixture(large, blob: true);
        var request = MetadataPublicationTests.Initial(graph);
        ((PutChangesSubRequestData)request.SubRequests[0].Data!).ExpectedStorageIndex = StorageIds.Restore(original.Partitions[0].StorageIndex!);
        var old = await service.ExecuteAsync(original.ResourceId, DocumentPartitionKind.FileContents, request, new Dictionary<string, string>(), TestActor.Value);
        Assert.False(Assert.Single(old.Response.SubResponses).Status);
        var small = await new DocumentStore().Put(original.Path, MinimalDocx.Create("small")).CaptureAsync(source.Content);
        var selected = small.Partitions.Select(p => p with { Identity = original.Partitions.Single(o => o.Kind == p.Kind).Identity }).ToImmutableArray();
        // A legacy provider can have pruned its old metadata snapshot while preserving immutable revision history.
        await source.State.TransitionAsync(original.ResourceId, (current, now) => new StateTransition<bool>(RevisionHistory.Append(current,
            current with { Content = small.Content, ContentVersion = current.ContentVersion + 1, Partitions = selected, ModifiedUtc = now,
                Receipts = current.Receipts.Select(r => r with { Response = null }).ToImmutableArray() }, TestActor.Value.Identity, now), true));
        var before = await ((IProviderRecoveryStore)source.State).CaptureRecoveryAsync(); Assert.Single(before.Snapshots);
        var portable = new PortableArchive(); await portable.ExportAsync(source, files.Path("history.zip"), PortabilityTests.Context);
        var limits = budget == "document" ? new StorageLimits { MaxDocumentBytes = 8000 }
            : new StorageLimits { MaxGraphElements = selected.Sum(p => p.Elements.Length) };
        var content = new PortabilityTests.CountWrites(new InMemoryContentStore()); var destination = new StorageProvider(new InMemoryStateStore(new(limits)), content);
        await Assert.ThrowsAsync<StorageQuotaExceededException>(() => portable.ImportAsync(destination, files.Path("history.zip"), PortabilityTests.Context).AsTask());
        Assert.Equal(0, content.Writes); Assert.Empty(await destination.State.ListAsync(0, 100));
    }

    [Theory]
    [InlineData("editor")]
    [InlineData("schema")]
    [InlineData("exclusive")]
    public async Task EveryActiveCoordinationKindBlocksImport(string kind)
    {
        using var files = new PortabilityTests.TemporaryFiles(); var source = PortabilityTests.Memory();
        var original = (await new CellBridgeDocumentService(source).CreateAsync("/active.docx", MinimalDocx.Create(), TestActor.Value))!;
        await source.State.TransitionAsync(original.ResourceId, (current, now) =>
        {
            var lease = new LeaseState("id", "client", now.AddMinutes(5), 1, null, TestActor.Value.Identity.Subject);
            var state = kind switch
            {
                "schema" => current with { Coordination = current.Coordination with { SchemaOwners = [lease] } },
                "exclusive" => current with { Coordination = current.Coordination with { Exclusive = lease } },
                _ => current with { Editors = [new(Guid.NewGuid(), "user", now, now, now.AddMinutes(5), 300, true, 1,
                    ImmutableDictionary<string, ImmutableArray<byte>>.Empty, TestActor.Value.Identity)] },
            };
            return new StateTransition<bool>(state, true);
        });
        var portable = new PortableArchive(); await portable.ExportAsync(source, files.Path("active.zip"), PortabilityTests.Context);
        var writes = new PortabilityTests.CountWrites(new InMemoryContentStore()); var destination = new StorageProvider(new InMemoryStateStore(), writes);
        await Assert.ThrowsAsync<RecoveryConflictException>(() => portable.ImportAsync(destination, files.Path("active.zip"), PortabilityTests.Context).AsTask());
        Assert.Equal(0, writes.Writes);
    }

    [PostgreSqlFact]
    public Task AtomicDurableImportsRaceAndChargeExactRecoveryBudget() =>
        BudgetAndQueryTests.WithDatabase(new(), async db =>
        {
            using var files = new PortabilityTests.TemporaryFiles(); var memory = PortabilityTests.Memory();
            await new CellBridgeDocumentService(memory).CreateAsync("/a.docx", MinimalDocx.Create(), TestActor.Value);
            var portable = new PortableArchive(); await portable.ExportAsync(memory, files.Path("export.zip"), PortabilityTests.Context);
            var destination = new StorageProvider(new PostgreSqlStateStore(db), new PostgreSqlContentStore(db));
            var receipts = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => portable.ImportAsync(destination,
                files.Path("export.zip"), PortabilityTests.Context).AsTask()));
            Assert.All(receipts, r => Assert.Equal(receipts[0], r));
            Assert.Single(await destination.State.ListAsync(0, 100));
            await using var usage = db.CreateCommand("SELECT stored_bytes FROM cellbridge_usage");
            var required = (long)(await usage.ExecuteScalarAsync())!;
            await BudgetAndQueryTests.WithDatabase(new() { MaxStoredBytes = required - 1 }, async quotaDb =>
            {
                var limits = new StorageLimits { MaxStoredBytes = required - 1 };
                var limited = new StorageProvider(new PostgreSqlStateStore(quotaDb, limits), new PostgreSqlContentStore(quotaDb), limits);
                await Assert.ThrowsAsync<StorageQuotaExceededException>(() => portable.ImportAsync(limited, files.Path("export.zip"), PortabilityTests.Context).AsTask());
                Assert.Empty(await limited.State.ListAsync(0, 100));
                Assert.Empty((await ((IProviderRecoveryStore)limited.State).CaptureRecoveryAsync()).Receipts);
                await new PostgreSqlStateStore(quotaDb, limits with { MaxStoredBytes = required }).InitializeAsync();
                await portable.ImportAsync(limited, files.Path("export.zip"), PortabilityTests.Context);
                Assert.Single(await limited.State.ListAsync(0, 100));
            });
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
