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
using CellBridge.TestFixtures;

namespace CellBridge.Storage.Tests;

public sealed class PortabilityTests
{
    internal static RecoveryContext Context { get; } = new(RecoveryAuthorizationContext.StoredGrants("test-subjects"), []);
    internal static StorageProvider Memory() => StorageTestDocuments.Memory();
    internal static string CanonicalSnapshots(IEnumerable<NormalizedDocumentSnapshot> snapshots)
    {
        static ImmutableArray<T> Ordered<T>(ImmutableArray<T> values) => values.OrderBy(value => JsonSerializer.Serialize(value), StringComparer.Ordinal).ToImmutableArray();
        return JsonSerializer.Serialize(snapshots.Select(snapshot => snapshot with
        {
            Current = snapshot.Current with { UtcNow = DateTime.UnixEpoch, Editors = Ordered(snapshot.Current.Editors), Partitions = Ordered(snapshot.Current.Partitions) },
            Partitions = Ordered(snapshot.Partitions), Elements = Ordered(snapshot.Elements), Visibility = Ordered(snapshot.Visibility),
            GraphRevisions = Ordered(snapshot.GraphRevisions), Objects = Ordered(snapshot.Objects), Cells = Ordered(snapshot.Cells), StorageManifests = Ordered(snapshot.StorageManifests),
            MappingIntervals = Ordered(snapshot.MappingIntervals), MappingRoots = Ordered(snapshot.MappingRoots), MappingClaims = Ordered(snapshot.MappingClaims),
            CurrentBindings = Ordered(snapshot.CurrentBindings), BindingDeltas = Ordered(snapshot.BindingDeltas), Checkpoints = Ordered(snapshot.Checkpoints),
            ApplicationRevisions = Ordered(snapshot.ApplicationRevisions), Receipts = Ordered(snapshot.Receipts), Deliveries = Ordered(snapshot.Deliveries),
            PathClaims = Ordered(snapshot.PathClaims), ContentReferences = Ordered(snapshot.ContentReferences),
        }).OrderBy(snapshot => snapshot.Current.Head.ResourceId));
    }

    [Fact]
    public async Task RoundTripPreservesIdentitiesOpaqueHistoryReceiptsAndRestoresOldPublication()
    {
        using var files = new TemporaryFiles(); var source = Memory();
        var document = await History(source);
        var before = await ((IProviderRecoveryStore)source.Documents).CaptureRecoveryAsync();
        var portable = new PortableArchive();
        await portable.ExportAsync(source, files.Path("export.zip"), Context);
        var destination = Memory();
        var receipt = await portable.ImportAsync(destination, files.Path("export.zip"), Context);
        var after = await ((IProviderRecoveryStore)destination.Documents).CaptureRecoveryAsync();
        Assert.Equal(JsonSerializer.Serialize(before.Heads), JsonSerializer.Serialize(after.Heads));
        Assert.Equal(CanonicalSnapshots(before.Snapshots), CanonicalSnapshots(after.Snapshots));
        Assert.Equal(receipt, Assert.Single(after.Receipts));
        var service = new CellBridgeDocumentService(destination);
        var restored = await service.RestoreRevisionAsync(document.ResourceId, 3, document.LatestRevisionNumber, "after-recovery", TestActor.Value);
        Assert.Equal((await source.Documents.ReadRevisionAsync(document.ResourceId, 3))!.Revision!.Package.Sha256, restored.Revision.Package.Sha256);
        Assert.Equal(document.LatestRevisionNumber + 1, restored.Revision.RevisionNumber);
        Assert.Equal(receipt, await portable.ImportAsync(destination, files.Path("export.zip"), Context));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetRevisionAsync(document.ResourceId, 1,
            new(new("foreign", "foreign", "foreign"))).AsTask());
        // Recovery receipts are independent of subsequent edits and survive another export/import.
        await portable.ExportAsync(destination, files.Path("second.zip"), Context);
        var third = Memory(); await portable.ImportAsync(third, files.Path("second.zip"), Context);
        Assert.Equal(receipt, await portable.ImportAsync(third, files.Path("export.zip"), Context));
    }

    internal static async Task<DocumentHead> History(StorageProvider provider, Guid? externalBinding = null)
    {
        var service = new CellBridgeDocumentService(provider);
        var original = (await service.CreateAsync("/shared/%2520%23%3F%C3%A6.docx", MinimalDocx.Create("initial"), TestActor.Value))!;
        if (externalBinding is { } binding)
            await provider.Documents.UpdateDeliveryAsync(new DocumentDeliveryCommand.Bind(original.ResourceId, original.LifecycleGeneration, original.StateVersion, new(binding, "remote", "r0", 1)));
        var metadata = await service.ExecuteAsync(original.ResourceId, DocumentPartitionKind.Metadata,
            StorageTestDocuments.MetadataRequest(new GraphFixture([8, 7, 6], blob: true)), new Dictionary<string, string>(), TestActor.Value);
        Assert.False(Assert.Single(metadata.Response.SubResponses).Status);
        var file = StorageTestDocuments.MetadataRequest(new GraphFixture(MinimalDocx.Create("inherited blob"), blob: true));
        var expected = (await StorageTestDocuments.GraphAsync(service, original)).StorageIndexes.Single();
        ((PutChangesSubRequestData)file.SubRequests[0].Data!).ExpectedStorageIndex = expected.DataElementExtendedGuid;
        file.DataElementPackage!.DataElements.Add(expected);
        var saved = await service.ExecuteAsync(original.ResourceId, DocumentPartitionKind.FileContents, file, new Dictionary<string, string>(), TestActor.Value);
        Assert.False(Assert.Single(saved.Response.SubResponses).Status);
        await service.RestoreRevisionAsync(original.ResourceId, 2, saved.State.LatestRevisionNumber, "before-recovery", TestActor.Value);
        return (await provider.Documents.FindAsync(new DocumentIdentity.Resource(original.ResourceId)))!;
    }

    [Fact]
    public async Task TombstonesRecreationAndReservedNamesRemainRetired()
    {
        using var files = new TemporaryFiles(); var source = Memory(); var service = new CellBridgeDocumentService(source);
        var original = (await service.CreateAsync("/old.docx", MinimalDocx.Create(), TestActor.Value))!;
        var renamed = (await source.Documents.ChangePathAsync(new(original.ResourceId, StorageTestDocuments.Admission(original),
            "/new.docx", "/NEW.DOCX", ImmutableDictionary<string, string>.Empty))).Current;
        Assert.True((await source.Documents.ChangeLifecycleAsync(new DocumentLifecycleCommand.Delete(renamed.ResourceId, 1, renamed.StateVersion))).Applied);
        var retired = (await source.Documents.FindAsync(new DocumentIdentity.Resource(renamed.ResourceId, true)))!;
        var replacement = await IndexedDocumentPublicationBuilder.CreateAsync(Guid.NewGuid(), "/new.docx", MinimalDocx.Create("replacement"),
            renamed.Security, source.Content, source.Limits, lifecycleGeneration: 2);
        Assert.True((await source.Documents.ChangeLifecycleAsync(new DocumentLifecycleCommand.Recreate(retired.ResourceId, 1, retired.StateVersion, replacement))).Applied);
        var portable = new PortableArchive(); await portable.ExportAsync(source, files.Path("lifecycle.zip"), Context);
        var destination = Memory(); await portable.ImportAsync(destination, files.Path("lifecycle.zip"), Context);
        Assert.Null(await destination.Documents.FindAsync(new DocumentIdentity.Resource(original.ResourceId)));
        Assert.Equal(replacement.Head.ResourceId, (await destination.Documents.FindAsync(new DocumentIdentity.Path("/NEW.DOCX")))!.ResourceId);
        Assert.Equal(JsonSerializer.Serialize(await source.Documents.FindAsync(new DocumentIdentity.Resource(original.ResourceId, true))),
            JsonSerializer.Serialize(await destination.Documents.FindAsync(new DocumentIdentity.Resource(original.ResourceId, true))));
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
        var content = new CountWrites(new InMemoryContentStore()); var destination = StorageTestDocuments.Memory(content);
        var error = await Record.ExceptionAsync(() => portable.ImportAsync(destination, files.Path("bad.zip"), Context).AsTask());
        Assert.True(error is StorageCorruptionException or NotSupportedException, error?.ToString());
        Assert.Equal(0, content.Writes); Assert.Empty((await destination.Documents.CaptureRecoveryAsync()).Snapshots);
        Assert.Empty((await ((IProviderRecoveryStore)destination.Documents).CaptureRecoveryAsync()).Receipts);
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
                    case "unknown-security": state["Current"]!["Head"]!["Security"]!["ExternalBinding"] = "foreign"; break;
                    case "missing-field": state["Current"]!["Head"]!.AsObject().Remove("ResourceId"); break;
                    case "incompatible": manifest["ArchiveVersion"] = 99; break;
                    case "history-id": state["ApplicationRevisions"]![0]!["ResourceId"] = Guid.NewGuid(); break;
                    case "mapping-serial":
                        var claims = state["MappingClaims"]!.AsArray();
                        claims[1]!["Serial"] = claims[0]!["Serial"]!.DeepClone(); break;
                    case "package-graph": state["Current"]!["Head"]!["Package"]!["Sha256"] = new string('0', 64); break;
                    case "missing-index": state["Partitions"]![0]!["Head"]!["StorageIndex"] = null; break;
                    case "null-identity": state["Current"]!["Partitions"]![2]!["Identity"]!["StorageManifest"] = null; break;
                    case "serial-bridge":
                        var elements = state["Elements"]!.AsArray();
                        var duplicate = elements[0]!.DeepClone(); duplicate["Element"]!["Serial"]!["Guid"] = Guid.NewGuid(); elements.Add(duplicate); break;
                    case "lost-reservation": state["PathClaims"] = new JsonArray(); break;
                }
                data = Encoding.UTF8.GetBytes(manifest.ToJsonString());
                if (defect == "duplicate-json") data = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(data).Replace("\"ArchiveVersion\":2", "\"ArchiveVersion\":2,\"ArchiveVersion\":2"));
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
        Assert.True((await source.Documents.UpdateDeliveryAsync(new DocumentDeliveryCommand.Bind(state.ResourceId, state.LifecycleGeneration,
            state.StateVersion, new(binding, "remote", "r0", 1)))).Applied);
        var locks = new SharedDocumentLocks(new(source));
        Assert.True((await locks.ApplyAsync(state.ResourceId, HostLockOperation.Acquire, "token", TestActor.Value)).Success);
        var context = Context with { ExternalDestinations = [new(binding, "remote", "receipt-ledger-1")] };
        var portable = new PortableArchive();
        await Assert.ThrowsAsync<RecoveryConflictException>(() => portable.ExportAsync(source, files.Path("denied.zip"), Context).AsTask());
        await portable.ExportAsync(source, files.Path("active.zip"), context);
        var writes = new CountWrites(new InMemoryContentStore()); var destination = StorageTestDocuments.Memory(writes);
        await Assert.ThrowsAsync<RecoveryConflictException>(() => portable.ImportAsync(destination, files.Path("active.zip"), context).AsTask());
        await Assert.ThrowsAsync<RecoveryConflictException>(() => portable.ImportAsync(destination, files.Path("active.zip"),
            context with { Authorization = RecoveryAuthorizationContext.StoredGrants("foreign-subjects") }).AsTask());
        await Assert.ThrowsAsync<RecoveryConflictException>(() => portable.ImportAsync(destination, files.Path("active.zip"),
            context with { ExternalDestinations = [new(binding, "remote", "wrong-ledger")] }).AsTask());
        Assert.Equal(0, writes.Writes);
        Assert.True((await locks.ApplyAsync(state.ResourceId, HostLockOperation.Release, "token", TestActor.Value)).Success);
        await portable.ExportAsync(source, files.Path("expired.zip"), context);
        await portable.ImportAsync(destination, files.Path("expired.zip"), context);
        Assert.Equal(JsonSerializer.Serialize(await source.Documents.FindAsync(new DocumentIdentity.Resource(state.ResourceId))),
            JsonSerializer.Serialize(await destination.Documents.FindAsync(new DocumentIdentity.Resource(state.ResourceId))));
    }

    [Fact]
    public async Task PublicationFailureAndUnknownCommitReplyHaveRepeatableRecovery()
    {
        using var files = new TemporaryFiles(); var source = Memory();
        await new CellBridgeDocumentService(source).CreateAsync("/a.docx", MinimalDocx.Create(), TestActor.Value);
        var portable = new PortableArchive(); await portable.ExportAsync(source, files.Path("export.zip"), Context);
        var writes = new CountWrites(new InMemoryContentStore()); var inner = new InMemoryDocumentRepository(writes);
        var state = new RecoveryFailure(inner); var destination = new StorageProvider(state, writes);
        state.BeforeCommit = true;
        await Assert.ThrowsAsync<IOException>(() => portable.ImportAsync(destination, files.Path("export.zip"), Context).AsTask());
        Assert.Empty((await inner.CaptureRecoveryAsync()).Snapshots);
        Assert.Empty((await inner.CaptureRecoveryAsync()).Receipts);
        Assert.True(writes.Writes > 0);
        state.BeforeCommit = false; state.LoseReply = true;
        await portable.ImportAsync(destination, files.Path("export.zip"), Context);
        Assert.Single((await inner.CaptureRecoveryAsync()).Snapshots);
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
        var occupied = Memory(); var live = (await new CellBridgeDocumentService(occupied).CreateAsync("/existing.docx", MinimalDocx.Create(), TestActor.Value))!;
        Assert.True((await occupied.Documents.ChangeLifecycleAsync(new DocumentLifecycleCommand.Delete(live.ResourceId, 1, live.StateVersion))).Applied);
        await Assert.ThrowsAsync<RecoveryConflictException>(() => portable.ImportAsync(occupied, files.Path("export.zip"), Context).AsTask());
        Assert.Empty(await occupied.Documents.ListAsync(0, 100));
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

    private sealed class RecoveryFailure(IDocumentRepository inner) : RepositoryTestProxy(inner)
    {
        public bool BeforeCommit; public bool LoseReply;
        public override async ValueTask<RecoveryReceipt> ImportRecoveryAsync(ProviderSnapshot s, RecoveryReceipt r, CancellationToken ct = default)
        {
            if (BeforeCommit) throw new IOException("Injected precommit interruption.");
            var result = await Inner.ImportRecoveryAsync(s, r, ct);
            if (LoseReply) throw new StorageUnavailableException("Injected lost commit reply.");
            return result;
        }

    }

    internal sealed class TemporaryFiles : IDisposable
    {
        private readonly string _root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cellbridge-portability-test-" + Guid.NewGuid().ToString("N"));
        public TemporaryFiles() => Directory.CreateDirectory(_root);
        public string Path(string name) => System.IO.Path.Combine(_root, name);
        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
