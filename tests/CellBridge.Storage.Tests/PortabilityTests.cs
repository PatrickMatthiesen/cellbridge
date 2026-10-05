using System.Collections.Immutable;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CellBridge.AspNetCore;
using CellBridge.FssHttpB;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using CellBridge.Storage.PostgreSql;
using CellBridge.Tests;
using CellBridge.Web;

namespace CellBridge.Storage.Tests;

public sealed class PortabilityTests
{
    internal static RecoveryContext Context { get; } = new(RecoveryAuthorizationContext.StoredGrants("test-subjects"), []);
    internal static StorageProvider Memory() => new(new InMemoryStateStore(), new InMemoryContentStore());

    [Fact]
    public async Task RoundTripPreservesIdentitiesOpaqueHistoryReceiptsAndRestoresOldPublication()
    {
        using var files = new TemporaryFiles(); var source = Memory();
        var document = await History(source);
        var before = await ((IProviderRecoveryStore)source.State).CaptureRecoveryAsync();
        var portable = new PortableArchive();
        await portable.ExportAsync(source, files.Path("export.zip"), Context);
        var destination = Memory();
        var receipt = await portable.ImportAsync(destination, files.Path("export.zip"), Context);
        var after = await ((IProviderRecoveryStore)destination.State).CaptureRecoveryAsync();
        Assert.Equal(JsonSerializer.Serialize(before.Heads), JsonSerializer.Serialize(after.Heads));
        Assert.Equal(JsonSerializer.Serialize(before.Snapshots), JsonSerializer.Serialize(after.Snapshots));
        Assert.Equal(receipt, Assert.Single(after.Receipts));
        var service = new CellBridgeDocumentService(destination);
        var restored = await service.RestoreRevisionAsync(document.ResourceId, 3, RevisionHistory.Latest(document), "after-recovery", TestActor.Value);
        Assert.Equal(document.Revisions[2].Content, restored.Revision.Content);
        Assert.Equal(RevisionHistory.Latest(document) + 1, restored.Revision.RevisionNumber);
        Assert.Equal(receipt, await portable.ImportAsync(destination, files.Path("export.zip"), Context));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetRevisionAsync(document.ResourceId, 1,
            new(new("foreign", "foreign", "foreign"))).AsTask());
        // Recovery receipts are independent of subsequent edits and survive another export/import.
        await portable.ExportAsync(destination, files.Path("second.zip"), Context);
        var third = Memory(); await portable.ImportAsync(third, files.Path("second.zip"), Context);
        Assert.Equal(receipt, await portable.ImportAsync(third, files.Path("export.zip"), Context));
    }

    internal static async Task<DocumentState> History(StorageProvider provider, Guid? externalBinding = null)
    {
        var service = new CellBridgeDocumentService(provider);
        var original = (await service.CreateAsync("/shared/%2520%23%3F%C3%A6.docx", MinimalDocx.Create("initial"), TestActor.Value))!;
        if (externalBinding is { } binding)
            await provider.State.TransitionAsync(original.ResourceId, (s, _) => new StateTransition<bool>(s with
                { Publication = new(binding, "remote", "r0", 1, []) }, true));
        var metadata = await service.ExecuteAsync(original.ResourceId, DocumentPartitionKind.Metadata,
            MetadataPublicationTests.Initial(new GraphFixture([8, 7, 6], blob: true)), new Dictionary<string, string>(), TestActor.Value);
        Assert.False(Assert.Single(metadata.Response.SubResponses).Status);
        var file = MetadataPublicationTests.Initial(new GraphFixture(MinimalDocx.Create("inherited blob"), blob: true));
        ((PutChangesSubRequestData)file.SubRequests[0].Data!).ExpectedStorageIndex = StorageIds.Restore(original.Partitions.Single(p => p.Kind == 0).StorageIndex!);
        var saved = await service.ExecuteAsync(original.ResourceId, DocumentPartitionKind.FileContents, file, new Dictionary<string, string>(), TestActor.Value);
        Assert.False(Assert.Single(saved.Response.SubResponses).Status);
        await service.RestoreRevisionAsync(original.ResourceId, 2, RevisionHistory.Latest(saved.State), "before-recovery", TestActor.Value);
        return (await provider.State.FindByResourceIdAsync(original.ResourceId))!;
    }

    [Fact]
    public async Task TombstonesRecreationAndReservedNamesRemainRetired()
    {
        using var files = new TemporaryFiles(); var source = Memory(); var service = new CellBridgeDocumentService(source);
        var original = (await service.CreateAsync("/old.docx", MinimalDocx.Create(), TestActor.Value))!;
        await ((IAtomicDocumentRenameStore)source.State).RenameAsync(original.ResourceId, (s, _) =>
            new StateTransition<bool>(s with { Path = "/new.docx", PathKey = "/NEW.DOCX" }, true));
        var renamed = (await source.State.FindByResourceIdAsync(original.ResourceId))!;
        var lifecycle = (IDocumentLifecycleStore)source.State;
        Assert.True(await lifecycle.TryDeleteAsync(renamed.ResourceId, 1, renamed.StateVersion));
        var retired = (await lifecycle.FindLifecycleAsync(renamed.ResourceId))!;
        var replacement = await new DocumentStore().Put("/new.docx", MinimalDocx.Create("replacement")).CaptureAsync(source.Content);
        replacement = RevisionHistory.Initialize(replacement with { LifecycleGeneration = 2, Security = renamed.Security });
        Assert.True(await lifecycle.TryRecreateAsync(retired.ResourceId, 1, retired.StateVersion, replacement));
        var portable = new PortableArchive(); await portable.ExportAsync(source, files.Path("lifecycle.zip"), Context);
        var destination = Memory(); await portable.ImportAsync(destination, files.Path("lifecycle.zip"), Context);
        Assert.Null(await destination.State.FindByResourceIdAsync(original.ResourceId));
        Assert.Equal(replacement.ResourceId, (await destination.State.FindByPathKeyAsync("/NEW.DOCX"))!.ResourceId);
        Assert.Equal(JsonSerializer.Serialize(await lifecycle.FindLifecycleAsync(original.ResourceId)),
            JsonSerializer.Serialize(await ((IDocumentLifecycleStore)destination.State).FindLifecycleAsync(original.ResourceId)));
        Assert.Null(await new CellBridgeDocumentService(destination).CreateAsync("/old.docx", MinimalDocx.Create(), TestActor.Value));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("corrupt")]
    [InlineData("duplicate")]
    [InlineData("unexpected")]
    [InlineData("unknown-field")]
    [InlineData("unknown-security")]
    [InlineData("duplicate-json")]
    [InlineData("missing-field")]
    [InlineData("incompatible")]
    [InlineData("mapping-serial")]
    [InlineData("history-id")]
    [InlineData("package-graph")]
    [InlineData("missing-index")]
    [InlineData("null-identity")]
    [InlineData("serial-bridge")]
    [InlineData("lost-reservation")]
    public async Task CorruptOrIncompleteInputNeverWritesDestinationContent(string defect)
    {
        using var files = new TemporaryFiles(); var source = Memory();
        await new CellBridgeDocumentService(source).CreateAsync("/a.docx", MinimalDocx.Create(), TestActor.Value);
        var portable = new PortableArchive(); await portable.ExportAsync(source, files.Path("valid.zip"), Context);
        Rewrite(files.Path("valid.zip"), files.Path("bad.zip"), defect);
        var content = new CountWrites(new InMemoryContentStore()); var destination = new StorageProvider(new InMemoryStateStore(), content);
        var error = await Record.ExceptionAsync(() => portable.ImportAsync(destination, files.Path("bad.zip"), Context).AsTask());
        Assert.True(error is StorageCorruptionException or NotSupportedException, error?.ToString());
        Assert.Equal(0, content.Writes); Assert.Empty(await destination.State.ListAsync(0, 100));
        Assert.Empty((await ((IProviderRecoveryStore)destination.State).CaptureRecoveryAsync()).Receipts);
    }

    internal static void Rewrite(string source, string destination, string defect)
    {
        using var input = ZipFile.OpenRead(source); using var output = ZipFile.Open(destination, ZipArchiveMode.Create);
        foreach (var entry in input.Entries)
        {
            if (defect == "missing" && entry.FullName == "objects/00000000") continue;
            using var bytes = new MemoryStream(); using (var stream = entry.Open()) stream.CopyTo(bytes);
            var data = bytes.ToArray();
            if (entry.FullName == "manifest.json")
            {
                var manifest = JsonNode.Parse(data)!;
                var state = manifest["State"]!["Snapshots"]![0]!;
                switch (defect)
                {
                    case "unknown-field": state["NewProtocolField"] = 1; break;
                    case "unknown-security": state["Security"]!["ExternalBinding"] = "foreign"; break;
                    case "missing-field": state.AsObject().Remove("ResourceId"); break;
                    case "incompatible": manifest["ArchiveVersion"] = 99; break;
                    case "history-id": state["Revisions"]![0]!["ResourceId"] = Guid.NewGuid(); break;
                    case "mapping-serial":
                        var element = state["Partitions"]![0]!["Elements"]!.AsArray().First(e => e!["MappingSerials"]!.AsArray().Count > 0)!;
                        element["MappingSerials"]![0]!["Value"] = 123456; break;
                    case "package-graph":
                        var empty = manifest["Objects"]!.AsArray().Single(h => h!["Length"]!.GetValue<long>() == 0)!;
                        state["Content"] = empty.DeepClone(); state["Partitions"]![0]!["Content"] = empty.DeepClone(); break;
                    case "missing-index": state["Partitions"]![0]!["StorageIndex"] = null; break;
                    case "null-identity": state["Partitions"]![2]!["Identity"]!["StorageManifest"] = null; break;
                    case "serial-bridge":
                        state["Revisions"] = new JsonArray();
                        var versions = new JsonArray(); var serialA = Guid.NewGuid(); var serialB = Guid.NewGuid();
                        for (int i = 0; i < 3; i++)
                        {
                            var copy = state.DeepClone(); copy["StateVersion"] = i;
                            copy["Partitions"]![0]!["Elements"]![0]!["Serial"]!["Guid"] = i == 0 ? serialA : i == 1 ? Guid.Empty : serialB;
                            versions.Add(copy);
                        }
                        manifest["State"]!["Snapshots"] = versions; manifest["State"]!["Heads"]![0]!["StateVersion"] = 2; break;
                    case "lost-reservation":
                        var old = state.DeepClone(); old["Path"] = "/old.docx"; old["PathKey"] = "/OLD.DOCX";
                        state["StateVersion"] = 1;
                        manifest["State"]!["Snapshots"] = new JsonArray(old, state.DeepClone());
                        manifest["State"]!["Heads"]![0]!["StateVersion"] = 1; break;
                }
                data = Encoding.UTF8.GetBytes(manifest.ToJsonString());
                if (defect == "duplicate-json") data = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(data).Replace("\"ArchiveVersion\":1", "\"ArchiveVersion\":1,\"ArchiveVersion\":1"));
            }
            if (defect == "corrupt" && entry.FullName == "objects/00000000") data = [3, 4, 5];
            using (var stream = output.CreateEntry(entry.FullName).Open()) stream.Write(data);
            if (defect == "duplicate" && entry.FullName == "manifest.json")
                using (var stream = output.CreateEntry(entry.FullName).Open()) stream.Write(data);
        }
        if (defect == "unexpected") output.CreateEntry("../not-an-object");
    }

    [Fact]
    public async Task ContextAndActiveCoordinationGatesPrecedeDestinationWrites()
    {
        using var files = new TemporaryFiles(); var source = Memory(); var state =
            (await new CellBridgeDocumentService(source).CreateAsync("/a.docx", MinimalDocx.Create(), TestActor.Value))!;
        var binding = Guid.NewGuid();
        await source.State.TransitionAsync(state.ResourceId, (s, _) => new StateTransition<bool>(s with
        {
            Coordination = s.Coordination with { HostLock = new("token", TestActor.Value.Identity.Subject, DateTime.UtcNow.AddMinutes(5)) },
            Publication = new(binding, "remote", "r0", 1, []),
        }, true));
        var context = Context with { ExternalDestinations = [new(binding, "remote", "receipt-ledger-1")] };
        var portable = new PortableArchive();
        await Assert.ThrowsAsync<RecoveryConflictException>(() => portable.ExportAsync(source, files.Path("denied.zip"), Context).AsTask());
        await portable.ExportAsync(source, files.Path("active.zip"), context);
        var writes = new CountWrites(new InMemoryContentStore()); var destination = new StorageProvider(new InMemoryStateStore(), writes);
        await Assert.ThrowsAsync<RecoveryConflictException>(() => portable.ImportAsync(destination, files.Path("active.zip"), context).AsTask());
        await Assert.ThrowsAsync<RecoveryConflictException>(() => portable.ImportAsync(destination, files.Path("active.zip"),
            context with { Authorization = RecoveryAuthorizationContext.StoredGrants("foreign-subjects") }).AsTask());
        await Assert.ThrowsAsync<RecoveryConflictException>(() => portable.ImportAsync(destination, files.Path("active.zip"),
            context with { ExternalDestinations = [new(binding, "remote", "wrong-ledger")] }).AsTask());
        Assert.Equal(0, writes.Writes);
        await source.State.TransitionAsync(state.ResourceId, (s, _) => new StateTransition<bool>(s with
            { Coordination = s.Coordination with { HostLock = s.Coordination.HostLock! with { ExpiresUtc = DateTime.UtcNow.AddMinutes(-1) } } }, true));
        await portable.ExportAsync(source, files.Path("expired.zip"), context);
        await portable.ImportAsync(destination, files.Path("expired.zip"), context);
        Assert.Equal(JsonSerializer.Serialize(await source.State.FindByResourceIdAsync(state.ResourceId)),
            JsonSerializer.Serialize(await destination.State.FindByResourceIdAsync(state.ResourceId)));
    }

    [Fact]
    public async Task PublicationFailureAndUnknownCommitReplyHaveRepeatableRecovery()
    {
        using var files = new TemporaryFiles(); var source = Memory();
        await new CellBridgeDocumentService(source).CreateAsync("/a.docx", MinimalDocx.Create(), TestActor.Value);
        var portable = new PortableArchive(); await portable.ExportAsync(source, files.Path("export.zip"), Context);
        var inner = new InMemoryStateStore(); var writes = new CountWrites(new InMemoryContentStore());
        var state = new RecoveryFailure(inner); var destination = new StorageProvider(state, writes);
        state.BeforeCommit = true;
        await Assert.ThrowsAsync<IOException>(() => portable.ImportAsync(destination, files.Path("export.zip"), Context).AsTask());
        Assert.Empty(await inner.ListAsync(0, 100));
        Assert.Empty((await inner.CaptureRecoveryAsync()).Receipts);
        Assert.True(writes.Writes > 0);
        state.BeforeCommit = false; state.LoseReply = true;
        await Assert.ThrowsAsync<StorageUnavailableException>(() => portable.ImportAsync(destination, files.Path("export.zip"), Context).AsTask());
        Assert.Single(await inner.ListAsync(0, 100));
        var count = writes.Writes; var receipt = await portable.ImportAsync(destination, files.Path("export.zip"), Context);
        Assert.Equal(count, writes.Writes); Assert.Equal(receipt, Assert.Single((await inner.CaptureRecoveryAsync()).Receipts));
        var other = Memory(); await new CellBridgeDocumentService(other).CreateAsync("/different.docx", MinimalDocx.Create(), TestActor.Value);
        await portable.ExportAsync(other, files.Path("different.zip"), Context);
        RewriteArchiveIdentity(files.Path("different.zip"), files.Path("collision.zip"), receipt.OperationId);
        await Assert.ThrowsAsync<RecoveryConflictException>(() => portable.ImportAsync(destination, files.Path("collision.zip"), Context).AsTask());
        Assert.Equal(count, writes.Writes);
    }

    private static void RewriteArchiveIdentity(string source, string destination, Guid id)
    {
        using var input = ZipFile.OpenRead(source); using var output = ZipFile.Open(destination, ZipArchiveMode.Create);
        foreach (var entry in input.Entries)
        {
            using var stream = entry.Open(); using var bytes = new MemoryStream(); stream.CopyTo(bytes);
            var data = bytes.ToArray();
            if (entry.FullName == "manifest.json")
            { var json = JsonNode.Parse(data)!; json["ArchiveId"] = id; data = Encoding.UTF8.GetBytes(json.ToJsonString()); }
            using var target = output.CreateEntry(entry.FullName).Open(); target.Write(data);
        }
    }

    [Fact]
    public async Task OccupiedRetiredIdentityAndQuotasNeverExposePartialImports()
    {
        using var files = new TemporaryFiles(); var source = Memory();
        for (int i = 0; i < 2; i++) await new CellBridgeDocumentService(source).CreateAsync($"/{i}.docx", MinimalDocx.Create(), TestActor.Value);
        var portable = new PortableArchive(); await portable.ExportAsync(source, files.Path("export.zip"), Context);
        var limits = new StorageLimits { MaxDocuments = 1 };
        var destination = new StorageProvider(new InMemoryStateStore(), new InMemoryContentStore(), limits);
        await Assert.ThrowsAsync<StorageQuotaExceededException>(() => portable.ImportAsync(destination, files.Path("export.zip"), Context).AsTask());
        Assert.Empty(await destination.State.ListAsync(0, 100));
        var occupied = Memory(); var live = (await new CellBridgeDocumentService(occupied).CreateAsync("/existing.docx", MinimalDocx.Create(), TestActor.Value))!;
        Assert.True(await ((IDocumentLifecycleStore)occupied.State).TryDeleteAsync(live.ResourceId, 1, live.StateVersion));
        await Assert.ThrowsAsync<RecoveryConflictException>(() => portable.ImportAsync(occupied, files.Path("export.zip"), Context).AsTask());
        Assert.Empty(await occupied.State.ListAsync(0, 100));
        await Assert.ThrowsAsync<StorageQuotaExceededException>(() => new PortableArchive(recoveryLimits: new() { MaxEntries = 1 })
            .ValidateAsync(files.Path("export.zip"), Context).AsTask());
        await Assert.ThrowsAsync<StorageQuotaExceededException>(() => new PortableArchive(recoveryLimits: new() { MaxExpandedBytes = 1 })
            .ValidateAsync(files.Path("export.zip"), Context).AsTask());
        await Assert.ThrowsAsync<StorageQuotaExceededException>(() => new PortableArchive(recoveryLimits: new() { MaxArchiveBytes = 1 })
            .ValidateAsync(files.Path("export.zip"), Context).AsTask());
    }

    internal sealed class CountWrites(IContentStore inner) : IContentStore
    {
        public int Writes { get; private set; }
        public bool Durable => inner.Durable; public bool Shared => inner.Shared;
        public async ValueTask<ContentHandle> WriteAsync(Stream source, CancellationToken cancellationToken = default)
        { Writes++; return await inner.WriteAsync(source, cancellationToken); }
        public ValueTask<Stream> OpenReadAsync(ContentHandle h, CancellationToken cancellationToken = default) => inner.OpenReadAsync(h, cancellationToken);
    }

    private sealed class RecoveryFailure(InMemoryStateStore inner) : IDocumentStateStore, IProviderRecoveryStore
    {
        public bool BeforeCommit; public bool LoseReply;
        public bool Durable => false; public bool Shared => false;
        public ValueTask CheckRecoveryAsync(CancellationToken ct = default) => inner.CheckRecoveryAsync(ct);
        public ValueTask<ProviderSnapshot> CaptureRecoveryAsync(CancellationToken ct = default) => inner.CaptureRecoveryAsync(ct);
        public ValueTask<RecoveryReceipt?> FindRecoveryReceiptAsync(Guid id, CancellationToken ct = default) => inner.FindRecoveryReceiptAsync(id, ct);
        public async ValueTask<RecoveryReceipt> ImportRecoveryAsync(ProviderSnapshot s, RecoveryReceipt r, CancellationToken ct = default)
        {
            if (BeforeCommit) throw new IOException("Injected precommit interruption.");
            var result = await inner.ImportRecoveryAsync(s, r, ct);
            if (LoseReply) throw new StorageUnavailableException("Injected lost commit reply.");
            return result;
        }
        public ValueTask<DocumentState?> FindByResourceIdAsync(Guid id, CancellationToken ct = default) => inner.FindByResourceIdAsync(id, ct);
        public ValueTask<DocumentState?> FindByPathKeyAsync(string key, CancellationToken ct = default) => inner.FindByPathKeyAsync(key, ct);
        public ValueTask<IReadOnlyList<DocumentSummary>> ListAsync(int offset, int limit, CancellationToken ct = default) => inner.ListAsync(offset, limit, ct);
        public ValueTask<bool> TryCreateAsync(DocumentState s, CancellationToken ct = default) => inner.TryCreateAsync(s, ct);
        public ValueTask<T> TransitionAsync<T>(Guid id, Func<DocumentState, DateTime, StateTransition<T>> transition, CancellationToken ct = default) => inner.TransitionAsync(id, transition, ct);
        public ValueTask CheckHealthAsync(CancellationToken ct = default) => inner.CheckHealthAsync(ct);
    }

    internal sealed class TemporaryFiles : IDisposable
    {
        private readonly string _root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cellbridge-portability-test-" + Guid.NewGuid().ToString("N"));
        public TemporaryFiles() => Directory.CreateDirectory(_root);
        public string Path(string name) => System.IO.Path.Combine(_root, name);
        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
