using System.Collections.Immutable;
using System.Text.Json;
using CellBridge.AspNetCore;
using CellBridge.Storage;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using CellBridge.Storage.PostgreSql;
using CellBridge.Tests;
using CellBridge.Web;
using Npgsql;

namespace CellBridge.Storage.Tests;

public sealed class WholeFileSaveRecoveryTests
{
    private static StorageProvider Memory() => new(new InMemoryStateStore(), new InMemoryContentStore());

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
        await provider.State.TransitionAsync(initial.ResourceId, (state, _) => new StateTransition<bool>(state with
            { Publication = new(Guid.NewGuid(), "host-destination", "original-revision", 1, []) }, true));
        var bytes = MinimalDocx.Create("lost whole-file reply");
        var lost = new CellBridgeDocumentService(new(new LostReplyState(provider.State), provider.Content));
        var accepted = await Save(lost, initial.ResourceId, bytes, "lost");
        Assert.True(accepted.IsReplay);
        var after = (await peer.State.FindByResourceIdAsync(initial.ResourceId))!;
        Assert.Equal(2, after.Revisions.Length);
        Assert.Single(after.WholeFileSaveReceipts);
        var pending = Assert.Single(after.Publication!.Pending);
        Assert.Equal(accepted.Revision.Content, pending.Content);
        Assert.Equal(bytes, await peer.Content.ReadVerifiedAsync(after.Content));
        var replay = await Save(new(peer), initial.ResourceId, bytes, "lost");
        Assert.True(replay.IsReplay);
        Assert.Equal(JsonSerializer.Serialize(accepted.Revision), JsonSerializer.Serialize(replay.Revision));
        Assert.Equal(JsonSerializer.Serialize(after), JsonSerializer.Serialize(await peer.State.FindByResourceIdAsync(initial.ResourceId)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OversizedNonseekableInputCannotCommitIncludingReceiptReplay(bool replay)
    {
        var provider = new StorageProvider(new InMemoryStateStore(), new InMemoryContentStore(),
            new StorageLimits { MaxDocumentBytes = 2048 });
        var initial = await Locked(provider);
        var service = new CellBridgeDocumentService(provider);
        if (replay) await Save(service, initial.ResourceId, MinimalDocx.Create("accepted"), "bounded");
        var before = (await provider.State.FindByResourceIdAsync(initial.ResourceId))!;
        var restricted = new CellBridgeDocumentService(provider);
        using var input = new CountingNonseekableStream(new byte[1_000_000]);
        await Assert.ThrowsAsync<StorageQuotaExceededException>(() => restricted.SaveWholeFileAsync(initial.ResourceId,
            input, "bounded", TestActor.Value, "lock").AsTask());
        Assert.InRange(input.BytesRead, 2049, 65536);
        Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(await provider.State.FindByResourceIdAsync(initial.ResourceId)));
    }

    [Fact]
    public async Task InvalidOfficePackageCannotCommitOrAppendPublication()
    {
        var provider = Memory();
        var initial = await Locked(provider);
        await provider.State.TransitionAsync(initial.ResourceId, (state, _) => new StateTransition<bool>(state with
            { Publication = new(Guid.NewGuid(), "host-destination", "original-revision", 1, []) }, true));
        var before = (await provider.State.FindByResourceIdAsync(initial.ResourceId))!;
        await Assert.ThrowsAsync<InvalidDataException>(() => Save(new(provider), initial.ResourceId, [1, 2, 3], "malformed"));
        Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(await provider.State.FindByResourceIdAsync(initial.ResourceId)));
    }

    [Fact]
    public async Task WholeFileSavePreservesOpaqueMetadataAndItsProtocolReceipt()
    {
        var provider = Memory();
        var service = new CellBridgeDocumentService(provider);
        var initial = (await service.CreateAsync("/opaque-whole.docx", MinimalDocx.Create(), TestActor.Value))!;
        var metadata = await service.ExecuteAsync(initial.ResourceId, DocumentPartitionKind.Metadata,
            MetadataPublicationTests.Initial(new GraphFixture([8, 7, 6], blob: true)), new Dictionary<string, string>(), TestActor.Value);
        Assert.False(Assert.Single(metadata.Response.SubResponses).Status);
        var opaque = metadata.State.Partitions.Single(p => p.Kind == 1);
        var receipt = Assert.Single(metadata.State.Receipts);
        await new SharedDocumentLocks(service).ApplyAsync(initial.ResourceId, HostLockOperation.Acquire, "lock", TestActor.Value);
        await Save(service, initial.ResourceId, MinimalDocx.Create("new file, retained metadata"), "opaque");
        var after = (await provider.State.FindByResourceIdAsync(initial.ResourceId))!;
        Assert.Equal(JsonSerializer.Serialize(opaque), JsonSerializer.Serialize(after.Partitions.Single(p => p.Kind == 1)));
        Assert.Equal(receipt, Assert.Single(after.Receipts));
        Assert.Contains(receipt.Response!, StorageReferences.Handles(after));
        Assert.Equal(JsonSerializer.Serialize(opaque), JsonSerializer.Serialize(after.Revisions[^1].Partitions.Single(p => p.Kind == 1)));
    }

    [Fact]
    public async Task PortableRecoveryRetainsWholeFileReceiptAndOriginalReplayRevision()
    {
        using var files = new PortabilityTests.TemporaryFiles();
        var provider = Memory();
        var initial = await Locked(provider);
        var bytes = MinimalDocx.Create("first portable whole save");
        var accepted = await Save(new(provider), initial.ResourceId, bytes, "portable");
        await Save(new(provider), initial.ResourceId, MinimalDocx.Create("later portable whole save"), "later");
        await provider.State.TransitionAsync(initial.ResourceId, (state, _) => new StateTransition<bool>(state with
            { Coordination = CoordinationState.Empty }, true));
        var portable = new PortableArchive();
        await portable.ExportAsync(provider, files.Path("whole.zip"), PortabilityTests.Context);
        var destination = Memory();
        await portable.ImportAsync(destination, files.Path("whole.zip"), PortabilityTests.Context);
        var replay = await Save(new(destination), initial.ResourceId, bytes, "portable");
        Assert.True(replay.IsReplay);
        Assert.Equal(JsonSerializer.Serialize(accepted.Revision), JsonSerializer.Serialize(replay.Revision));
        var current = (await destination.State.FindByResourceIdAsync(initial.ResourceId))!;
        Assert.Equal(3, current.Revisions.Length);
        Assert.Equal(2, current.WholeFileSaveReceipts.Length);
    }

    [Theory]
    [InlineData("missing-revision")]
    [InlineData("duplicate")]
    [InlineData("null-digest")]
    public async Task PortableRecoveryRejectsInvalidWholeFileReceipts(string defect)
    {
        using var files = new PortabilityTests.TemporaryFiles();
        var provider = Memory();
        var initial = (await new CellBridgeDocumentService(provider).CreateAsync("/bad-receipt.docx", MinimalDocx.Create(), TestActor.Value))!;
        var receipt = new WholeFileSaveReceipt(1, TestActor.Value.Identity.Subject, "key", new string('a', 64), 1);
        var receipts = defect switch
        {
            "duplicate" => ImmutableArray.Create(receipt, receipt),
            "null-digest" => [receipt with { Digest = null! }],
            _ => [receipt with { RevisionNumber = 999 }],
        };
        await provider.State.TransitionAsync(initial.ResourceId, (state, _) => new StateTransition<bool>(state with
            { WholeFileSaveReceipts = receipts }, true));
        await Assert.ThrowsAsync<StorageCorruptionException>(() => new PortableArchive().ExportAsync(provider,
            files.Path("invalid.zip"), PortabilityTests.Context).AsTask());
    }

    [Fact]
    public async Task RecreationCannotImportWholeFileReceiptsFromTheRetiredLifecycle()
    {
        var provider = Memory();
        var initial = (await new CellBridgeDocumentService(provider).CreateAsync("/retired-whole.docx", MinimalDocx.Create(), TestActor.Value))!;
        var retired = DocumentLifecycle.Delete(initial, initial.LifecycleGeneration, initial.StateVersion)!;
        var replacement = await new DocumentStore().Put(initial.Path, MinimalDocx.Create("new lifecycle")).CaptureAsync(provider.Content);
        replacement = RevisionHistory.Initialize(replacement with { LifecycleGeneration = 2 });
        Assert.True(DocumentLifecycle.CanRecreate(retired, 1, initial.StateVersion, replacement));
        replacement = replacement with { WholeFileSaveReceipts = [new(1, TestActor.Value.Identity.Subject, "old", new string('a', 64), 1)] };
        Assert.False(DocumentLifecycle.CanRecreate(retired, 1, initial.StateVersion, replacement));
    }

    private static async Task<DocumentState> Locked(StorageProvider provider)
    {
        var service = new CellBridgeDocumentService(provider);
        var initial = (await service.CreateAsync("/whole-recovery-" + Guid.NewGuid().ToString("N") + ".docx",
            MinimalDocx.Create(), TestActor.Value))!;
        Assert.True((await new SharedDocumentLocks(service).ApplyAsync(initial.ResourceId, HostLockOperation.Acquire, "lock", TestActor.Value)).Success);
        return (await provider.State.FindByResourceIdAsync(initial.ResourceId))!;
    }

    private static async Task<WholeFileSaveResult> Save(CellBridgeDocumentService service, Guid id, byte[] bytes, string key)
    {
        using var input = new MemoryStream(bytes);
        return await service.SaveWholeFileAsync(id, input, key, TestActor.Value, "lock");
    }

    private sealed class CountingNonseekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public int BytesRead { get; private set; }
        public override bool CanSeek => false;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await base.ReadAsync(buffer, cancellationToken);
            BytesRead += read;
            return read;
        }
    }

    private sealed class LostReplyState(IDocumentStateStore inner) : IDocumentStateStore
    {
        public bool Durable => inner.Durable;
        public bool Shared => inner.Shared;
        public ValueTask<DocumentState?> FindByResourceIdAsync(Guid id, CancellationToken ct = default) => inner.FindByResourceIdAsync(id, ct);
        public ValueTask<DocumentState?> FindByPathKeyAsync(string key, CancellationToken ct = default) => inner.FindByPathKeyAsync(key, ct);
        public ValueTask<IReadOnlyList<DocumentSummary>> ListAsync(int offset, int limit, CancellationToken ct = default) => inner.ListAsync(offset, limit, ct);
        public ValueTask<bool> TryCreateAsync(DocumentState state, CancellationToken ct = default) => inner.TryCreateAsync(state, ct);
        public ValueTask CheckHealthAsync(CancellationToken ct = default) => inner.CheckHealthAsync(ct);
        public async ValueTask<T> TransitionAsync<T>(Guid id, Func<DocumentState, DateTime, StateTransition<T>> callback, CancellationToken ct = default)
        {
            bool committed = false;
            var result = await inner.TransitionAsync(id, (state, now) =>
            {
                var next = callback(state, now);
                committed = next.Next is not null;
                return next;
            }, ct);
            if (committed) throw new StorageUnavailableException("Injected lost whole-file commit response.");
            return result;
        }
    }
}
