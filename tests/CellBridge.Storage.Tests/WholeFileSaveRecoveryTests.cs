using System.Collections.Immutable;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using CellBridge.AspNetCore;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using CellBridge.Storage.PostgreSql;
using CellBridge.Tests;
using CellBridge.TestFixtures;
using Npgsql;

namespace CellBridge.Storage.Tests;

[Collection("PostgreSQL")]
public sealed class WholeFileSaveRecoveryTests
{
    private static StorageProvider Memory() => StorageTestDocuments.Memory();
    [Fact]
    public Task LostCommitResponseReturnsReceiptAndFreshServiceDoesNotPublishAgain() => LostReply(Memory());
    [PostgreSqlFact]
    public async Task PostgreSqlLostCommitResponseReplaysThroughIndependentProvider()
    {
        var connection = Environment.GetEnvironmentVariable("ConnectionStrings__cellbridge")!;
        await using var first = NpgsqlDataSource.Create(connection);
        await using var second = NpgsqlDataSource.Create(connection);
        await LostReply(new(new PostgreSqlStateStore(first), new PostgreSqlContentStore(first)),
            new(new PostgreSqlStateStore(second), new PostgreSqlContentStore(second)));
    }
    private static async Task LostReply(StorageProvider provider, StorageProvider? peer = null)
    {
        peer ??= provider;
        var initial = await Locked(provider);
        Assert.True((await provider.Documents.UpdateDeliveryAsync(new DocumentDeliveryCommand.Bind(initial.ResourceId, initial.LifecycleGeneration,
            initial.StateVersion, new(Guid.NewGuid(), "host-destination", "original-revision", 1)))).Applied);
        var bytes = MinimalDocx.Create("lost whole-file reply");
        var lost = new CellBridgeDocumentService(new(new LostReplyRepository(provider.Documents), provider.Content));
        var accepted = await Save(lost, initial.ResourceId, bytes, "lost");
        Assert.True(accepted.IsReplay);
        var after = (await peer.Documents.ExportDocumentAsync(initial.ResourceId))!;
        Assert.Equal(2, after.ApplicationRevisions.Length);
        Assert.Single(after.Receipts, receipt => receipt.Operation.Family == SaveOperationFamily.WholeFile);
        var pending = Assert.Single(after.Deliveries);
        Assert.Equal(accepted.Revision.Package, pending.Package);
        Assert.Equal(bytes, await StorageTestDocuments.PackageAsync(peer, after.Current.Head.Package));
        Assert.True((await Save(new(peer), initial.ResourceId, bytes, "lost")).IsReplay);
        Assert.Equal(PortabilityTests.CanonicalSnapshots([after]), PortabilityTests.CanonicalSnapshots([(await peer.Documents.ExportDocumentAsync(initial.ResourceId))!]));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OversizedNonseekableInputCannotCommitIncludingReceiptReplay(bool replay)
    {
        var provider = StorageTestDocuments.Memory(limits: new() { MaxDocumentBytes = 2048 });
        var initial = await Locked(provider);
        var service = new CellBridgeDocumentService(provider);
        if (replay) await Save(service, initial.ResourceId, MinimalDocx.Create("accepted"), "bounded");
        var before = (await provider.Documents.FindAsync(new DocumentIdentity.Resource(initial.ResourceId)))!;
        using var input = new CountingNonseekableStream(new byte[1_000_000]);
        await Assert.ThrowsAsync<StorageQuotaExceededException>(() => service.SaveWholeFileAsync(initial.ResourceId, input, "bounded", TestActor.Value, "lock").AsTask());
        Assert.InRange(input.BytesRead, 2049, 65536);
        Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(await provider.Documents.FindAsync(new DocumentIdentity.Resource(initial.ResourceId))));
    }
    [Fact]
    public async Task InvalidOfficePackageCannotCommitOrAppendPublication()
    {
        var provider = Memory(); var initial = await Locked(provider);
        var before = (await provider.Documents.ExportDocumentAsync(initial.ResourceId))!;
        await Assert.ThrowsAsync<InvalidDataException>(() => Save(new(provider), initial.ResourceId, [1, 2, 3], "malformed"));
        Assert.Equal(PortabilityTests.CanonicalSnapshots([before]), PortabilityTests.CanonicalSnapshots([(await provider.Documents.ExportDocumentAsync(initial.ResourceId))!]));
    }
    [Fact]
    public async Task WholeFileSavePreservesOpaqueMetadataAndItsProtocolReceipt()
    {
        var provider = Memory(); var service = new CellBridgeDocumentService(provider);
        var initial = (await service.CreateAsync("/opaque-whole.docx", MinimalDocx.Create(), TestActor.Value))!;
        var metadata = await service.ExecuteAsync(initial.ResourceId, DocumentPartitionKind.Metadata,
            StorageTestDocuments.MetadataRequest(new GraphFixture([8, 7, 6], blob: true)), ImmutableDictionary<string, string>.Empty, TestActor.Value);
        Assert.False(Assert.Single(metadata.Response.SubResponses).Status);
        var before = (await provider.Documents.ExportDocumentAsync(initial.ResourceId))!;
        Assert.True((await new SharedDocumentLocks(service).ApplyAsync(initial.ResourceId, HostLockOperation.Acquire, "lock", TestActor.Value)).Success);
        await Save(service, initial.ResourceId, MinimalDocx.Create("new file, retained metadata"), "opaque");
        var after = (await provider.Documents.ExportDocumentAsync(initial.ResourceId))!;
        var opaque = before.Partitions.Single(partition => partition.Head.Kind == 1).Head;
        Assert.Equal(JsonSerializer.Serialize(opaque), JsonSerializer.Serialize(after.Partitions.Single(partition => partition.Head.Kind == 1).Head));
        var receipt = Assert.Single(before.Receipts);
        Assert.Contains(after.Receipts, current => JsonSerializer.Serialize(receipt) == JsonSerializer.Serialize(current));
        Assert.Contains(after.ContentReferences, reference => reference.Handle == receipt.Response);
        Assert.Equal(JsonSerializer.Serialize(opaque), JsonSerializer.Serialize(after.ApplicationRevisions[^1].Partitions.Single(partition => partition.Kind == 1)));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PortableRecoveryRetainsWholeFileReceiptAndOriginalReplayRevision(bool reordered)
    {
        using var files = new PortabilityTests.TemporaryFiles();
        var backup = await ExportRetainedWholeFileHistory(files.Path("whole.zip"));
        if (reordered) RewriteSnapshots(files.Path("whole.zip"), snapshots =>
        {
            var receipts = snapshots[0]!["Receipts"]!.AsArray();
            snapshots[0]!["Receipts"] = new JsonArray(receipts.Reverse().Select(receipt => receipt!.DeepClone()).ToArray());
        });
        var portable = new PortableArchive(); var destination = Memory();
        await portable.ImportAsync(destination, files.Path("whole.zip"), backup.Context);
        var service = new CellBridgeDocumentService(destination);
        var replay = await Save(service, backup.Head.ResourceId, backup.Bytes, "portable");
        Assert.True(replay.IsReplay);
        Assert.Equal(JsonSerializer.Serialize(backup.Accepted.Revision), JsonSerializer.Serialize(replay.Revision));
        var current = (await destination.Documents.ExportDocumentAsync(backup.Head.ResourceId))!;
        Assert.Equal(backup.Head.Package, current.Current.Head.Package);
        Assert.Equal(3, current.ApplicationRevisions.Length); Assert.Equal(2, current.Receipts.Length); Assert.Equal(2, current.Deliveries.Length);
        Assert.True((await new SharedDocumentLocks(service).ApplyAsync(backup.Head.ResourceId, HostLockOperation.Acquire, "lock", TestActor.Value)).Success);
        await Save(service, backup.Head.ResourceId, MinimalDocx.Create("continued after recovery"), "continued");
        var appended = (await destination.Documents.ExportDocumentAsync(backup.Head.ResourceId))!;
        Assert.Equal(3, appended.Receipts.Length); Assert.Equal(3, appended.Deliveries.Length);
        Assert.True((await Save(service, backup.Head.ResourceId, backup.Bytes, "portable")).IsReplay);
        Assert.True((await new SharedDocumentLocks(service).ApplyAsync(backup.Head.ResourceId, HostLockOperation.Release, "lock", TestActor.Value)).Success);
        await portable.ExportAsync(destination, files.Path("continued.zip"), backup.Context);
    }
    [Theory]
    [InlineData("omitted")]
    [InlineData("digest")]
    [InlineData("revision")]
    [InlineData("owner")]
    [InlineData("duplicate")]
    [InlineData("key")]
    public async Task PortableRecoveryRejectsMissingOrInvalidWholeFileReceiptsBeforeDestinationWrites(string defect)
    {
        using var files = new PortabilityTests.TemporaryFiles();
        var backup = await ExportRetainedWholeFileHistory(files.Path("tampered.zip"));
        RewriteSnapshots(files.Path("tampered.zip"), snapshots =>
        {
            var receipts = snapshots[0]!["Receipts"]!.AsArray();
            if (defect == "omitted") receipts.RemoveAt(0);
            else if (defect == "duplicate") receipts.Add(receipts[0]!.DeepClone());
            else if (defect == "digest") receipts[0]!["Digest"] = null;
            else if (defect == "revision") receipts[0]!["RevisionNumber"] = 999;
            else if (defect == "owner") receipts[0]!["OwnerSubject"] = "";
            else receipts[0]!["Operation"]!["OperationKey"] = "";
        });
        var content = new PortabilityTests.CountWrites(new InMemoryContentStore()); var destination = StorageTestDocuments.Memory(content);
        await Assert.ThrowsAsync<StorageCorruptionException>(() => new PortableArchive().ImportAsync(destination, files.Path("tampered.zip"), backup.Context).AsTask());
        Assert.Equal(0, content.Writes);
        var after = await destination.Documents.CaptureRecoveryAsync(); Assert.Empty(after.Snapshots); Assert.Empty(after.Receipts);
    }
    private static async Task<(DocumentHead Head, WholeFileSaveResult Accepted, byte[] Bytes, RecoveryContext Context)> ExportRetainedWholeFileHistory(string path)
    {
        var provider = Memory(); var initial = await Locked(provider); var binding = Guid.NewGuid();
        Assert.True((await provider.Documents.UpdateDeliveryAsync(new DocumentDeliveryCommand.Bind(initial.ResourceId, initial.LifecycleGeneration,
            initial.StateVersion, new(binding, "host-destination", "original-revision", 1)))).Applied);
        var service = new CellBridgeDocumentService(provider); var bytes = MinimalDocx.Create("first portable whole save");
        var accepted = await Save(service, initial.ResourceId, bytes, "portable");
        await Save(service, initial.ResourceId, MinimalDocx.Create("later portable whole save"), "later");
        Assert.True((await new SharedDocumentLocks(service).ApplyAsync(initial.ResourceId, HostLockOperation.Release, "lock", TestActor.Value)).Success);
        var head = (await provider.Documents.FindAsync(new DocumentIdentity.Resource(initial.ResourceId)))!;
        var context = PortabilityTests.Context with { ExternalDestinations = [new(binding, "host-destination", "test-receipts")] };
        await new PortableArchive().ExportAsync(provider, path, context);
        return (head, accepted, bytes, context);
    }
    private static void RewriteSnapshots(string path, Action<JsonArray> edit)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Update); var entry = zip.GetEntry("manifest.json")!;
        JsonNode manifest; using (var reader = new StreamReader(entry.Open())) manifest = JsonNode.Parse(reader.ReadToEnd())!;
        edit(manifest["State"]!["Snapshots"]!.AsArray()); entry.Delete();
        using var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open()); writer.Write(manifest.ToJsonString());
    }
    [Fact]
    public async Task RecoveryKeepsRetiredReceiptsSeparateFromTheRecreatedResource()
    {
        using var files = new PortabilityTests.TemporaryFiles(); var provider = Memory(); var service = new CellBridgeDocumentService(provider);
        var initial = await Locked(provider);
        await Save(service, initial.ResourceId, MinimalDocx.Create("retired accepted save"), "same-key");
        await new SharedDocumentLocks(service).ApplyAsync(initial.ResourceId, HostLockOperation.Release, "lock", TestActor.Value);
        var current = (await provider.Documents.FindAsync(new DocumentIdentity.Resource(initial.ResourceId)))!;
        Assert.True((await provider.Documents.ChangeLifecycleAsync(new DocumentLifecycleCommand.Delete(current.ResourceId, current.LifecycleGeneration, current.StateVersion))).Applied);
        var retired = (await provider.Documents.FindAsync(new DocumentIdentity.Resource(current.ResourceId, true)))!;
        var replacement = await IndexedDocumentPublicationBuilder.CreateAsync(Guid.NewGuid(), current.Path, MinimalDocx.Create("recreated baseline"), current.Security,
            provider.Content, provider.Limits, lifecycleGeneration: 2);
        Assert.True((await provider.Documents.ChangeLifecycleAsync(new DocumentLifecycleCommand.Recreate(retired.ResourceId, 1, retired.StateVersion, replacement))).Applied);
        Assert.True((await new SharedDocumentLocks(service).ApplyAsync(replacement.Head.ResourceId, HostLockOperation.Acquire, "lock", TestActor.Value)).Success);
        var bytes = MinimalDocx.Create("recreated accepted save"); var accepted = await Save(service, replacement.Head.ResourceId, bytes, "same-key");
        Assert.False(accepted.IsReplay);
        await new SharedDocumentLocks(service).ApplyAsync(replacement.Head.ResourceId, HostLockOperation.Release, "lock", TestActor.Value);
        var portable = new PortableArchive(); await portable.ExportAsync(provider, files.Path("lifecycles.zip"), PortabilityTests.Context);
        var destination = Memory(); await portable.ImportAsync(destination, files.Path("lifecycles.zip"), PortabilityTests.Context);
        Assert.Equal(1, Assert.Single((await destination.Documents.ExportDocumentAsync(retired.ResourceId))!.Receipts).Operation.LifecycleGeneration);
        var restored = (await destination.Documents.ExportDocumentAsync(replacement.Head.ResourceId))!;
        Assert.Equal(2, Assert.Single(restored.Receipts).Operation.LifecycleGeneration);
        var replay = await Save(new(destination), restored.Current.Head.ResourceId, bytes, "same-key");
        Assert.True(replay.IsReplay); Assert.Equal(JsonSerializer.Serialize(accepted.Revision), JsonSerializer.Serialize(replay.Revision));
    }
    private static async Task<DocumentHead> Locked(StorageProvider provider)
    {
        var service = new CellBridgeDocumentService(provider);
        var initial = (await service.CreateAsync("/whole-recovery-" + Guid.NewGuid().ToString("N") + ".docx", MinimalDocx.Create(), TestActor.Value))!;
        Assert.True((await new SharedDocumentLocks(service).ApplyAsync(initial.ResourceId, HostLockOperation.Acquire, "lock", TestActor.Value)).Success);
        return (await provider.Documents.FindAsync(new DocumentIdentity.Resource(initial.ResourceId)))!;
    }
    private static async Task<WholeFileSaveResult> Save(CellBridgeDocumentService service, Guid id, byte[] bytes, string key)
    { using var input = new MemoryStream(bytes); return await service.SaveWholeFileAsync(id, input, key, TestActor.Value, "lock"); }
    private sealed class CountingNonseekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public int BytesRead { get; private set; }
        public override bool CanSeek => false;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        { var read = await base.ReadAsync(buffer, ct); BytesRead += read; return read; }
    }
    private sealed class LostReplyRepository(IDocumentRepository inner) : RepositoryTestProxy(inner)
    {
        public override async ValueTask<PublicationResult> PublishAsync(PreparedDocumentPublication publication, CancellationToken ct = default)
        { var result = await Inner.PublishAsync(publication, ct); if (result.Status == PublicationStatus.Accepted) throw new StorageUnavailableException("Injected lost whole-file commit response."); return result; }
    }
}
