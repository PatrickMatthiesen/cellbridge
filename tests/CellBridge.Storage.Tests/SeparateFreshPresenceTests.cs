using System.Collections.Immutable;
using CellBridge.AspNetCore;
using CellBridge.FssHttp;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;
using CellBridge.TestFixtures;

namespace CellBridge.Storage.Tests;

public sealed class SeparateFreshPresenceTests
{
    [Theory]
    [InlineData(2000, "metadata")]
    [InlineData(2040, "metadata")]
    [InlineData(2000, "path")]
    [InlineData(2040, "path")]
    [InlineData(2000, "catalog")]
    [InlineData(2040, "catalog")]
    public async Task CurrentPresenceUsesProviderTimeAndExpiresItsPublishedEditorPartitionOnce(int year, string firstRead)
    {
        var clock = new ManualClock(year); var content = new InMemoryContentStore();
        var repository = new InMemoryDocumentRepository(content, clock: clock);
        var initial = (await new CellBridgeDocumentService(new(repository, content)).CreateAsync(
            "/shared/presence.docx", MinimalDocx.Create(), TestActor.Value))!;
        var join = new FssHttpSubRequest { Type = SubRequestType.EditorsTable,
            SubRequestDataAttributes = { ["EditorsTableRequestType"] = "JoinEditingSession", ["ClientID"] = Guid.NewGuid().ToString(),
                ["Timeout"] = "3600", ["AsEditor"] = "true" } };
        var joined = await repository.ExecuteControlAsync(new DocumentControlCommand.Office(initial.ResourceId, TestStorage.Admit(initial), join));
        Assert.Null(joined.ErrorCode); Assert.Single(joined.Current.Editors);
        Assert.Equal(1, joined.Current.Head.ActiveEditors);
        Assert.Equal(clock.GetUtcNow().UtcDateTime.AddSeconds(3600), joined.Current.Head.NextEditorExpiryUtc);
        var previousKnowledge = joined.Current.Partitions.Single(partition => partition.Kind == 2).Knowledge;
        var guarded = new PresenceOnlyRepository(repository);
        var service = new CellBridgeDocumentService(new(guarded, new RejectPayloads()));

        var active = await service.GetDocumentMetadataAsync(initial.ResourceId, TestActor.Value);
        Assert.Equal(1, active.ActiveEditors); Assert.Equal(joined.Current.Head.StateVersion, active.StateVersion);
        Assert.Equal(0, guarded.Expirations);
        clock.Advance(TimeSpan.FromSeconds(3601));
        var expired = firstRead switch
        {
            "metadata" => await service.GetDocumentMetadataAsync(initial.ResourceId, TestActor.Value),
            "path" => (await service.FindDocumentMetadataByPathKeyAsync(initial.PathKey, TestActor.Value, CancellationToken.None))!,
            _ => await CatalogHeadAsync(),
        };
        Assert.Equal(0, expired.ActiveEditors); Assert.Null(expired.NextEditorExpiryUtc);
        Assert.Equal(joined.Current.Head.StateVersion + 1, expired.StateVersion);
        Assert.Equal(initial.ContentVersion, expired.ContentVersion); Assert.Equal(initial.Package, expired.Package);
        Assert.Equal(1, guarded.Expirations);
        var persisted = (await repository.ReadControlAsync(initial.ResourceId))!;
        Assert.Empty(persisted.Editors);
        Assert.Equal(previousKnowledge + 1, persisted.Partitions.Single(partition => partition.Kind == 2).Knowledge);
        Assert.Equal(expired, persisted.Head);
        var controls = guarded.Controls;
        Assert.Equal(expired, await service.GetDocumentMetadataAsync(initial.ResourceId, TestActor.Value));
        Assert.Equal(expired, await service.FindDocumentMetadataByPathKeyAsync(initial.PathKey, TestActor.Value, CancellationToken.None));
        var summary = Assert.Single((await AuthorizedDocumentCatalog.ReadAsync(service, TestActor.Value, 0, 10)).Documents);
        Assert.Equal(0, summary.ActiveEditors);
        Assert.Equal(controls, guarded.Controls); Assert.Equal(1, guarded.Expirations);
        NormalizedDocumentRecovery.Validate((await repository.ExportDocumentAsync(initial.ResourceId))!, new());

        async Task<DocumentHead> CatalogHeadAsync()
        {
            var summary = Assert.Single((await AuthorizedDocumentCatalog.ReadAsync(service, TestActor.Value, 0, 10)).Documents);
            Assert.Equal(0, summary.ActiveEditors);
            return (await repository.FindAsync(new DocumentIdentity.Resource(summary.ResourceId)))!;
        }
    }

    [Fact]
    public async Task DocumentsWithoutParticipantsKeepMetadataPathAndCatalogReadsOnScalarHeads()
    {
        var memory = StorageTestDocuments.Memory();
        var initial = (await new CellBridgeDocumentService(memory).CreateAsync("/shared/no-editors.docx", MinimalDocx.Create(), TestActor.Value))!;
        var guarded = new PresenceOnlyRepository(memory.Documents) { RejectControls = true };
        var service = new CellBridgeDocumentService(new(guarded, new RejectPayloads()));
        Assert.Equal(initial, await service.GetDocumentMetadataAsync(initial.ResourceId, TestActor.Value));
        Assert.Equal(initial, await service.FindDocumentMetadataByPathKeyAsync(initial.PathKey, TestActor.Value, CancellationToken.None));
        Assert.Equal(0, Assert.Single((await AuthorizedDocumentCatalog.ReadAsync(service, TestActor.Value, 0, 10)).Documents).ActiveEditors);
        Assert.Equal(0, guarded.Controls); Assert.Equal(0, guarded.Expirations);
    }

    private sealed class ManualClock(int year) : TimeProvider
    {
        private DateTimeOffset _now = new(year, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }

    private sealed class RejectPayloads : IContentStore
    {
        public bool Durable => false;
        public bool Shared => false;
        public ValueTask<ContentHandle> WriteAsync(Stream source, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Presence reads must not stage payloads.");
        public ValueTask<Stream> OpenReadAsync(ContentHandle handle, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Presence reads must not open payloads.");
    }

    private sealed class PresenceOnlyRepository(IDocumentRepository inner) : RepositoryTestProxy(inner)
    {
        public int Controls { get; private set; }
        public int Expirations { get; private set; }
        public bool RejectControls { get; init; }
        public override ValueTask<DocumentControlRead?> ReadControlAsync(Guid id, CancellationToken ct = default)
        {
            if (RejectControls) throw new InvalidOperationException("A document without participants needs only scalar metadata.");
            Controls++; return Inner.ReadControlAsync(id, ct);
        }
        public override ValueTask<ControlResult> ExecuteControlAsync(DocumentControlCommand command, CancellationToken ct = default)
        { Assert.IsType<DocumentControlCommand.ExpireEditors>(command); Expirations++; return Inner.ExecuteControlAsync(command, ct); }
        public override ValueTask<DocumentRevisionPage?> ReadRevisionsAsync(Guid id, int offset, int limit, CancellationToken ct = default) => throw DeepRead();
        public override ValueTask<DocumentRevisionSelection?> ReadRevisionAsync(Guid id, ulong number, CancellationToken ct = default) => throw DeepRead();
        public override ValueTask<PartitionRead?> ReadPartitionAsync(Guid id, PartitionReadRequest request, CancellationToken ct = default) => throw DeepRead();
        public override ValueTask<SavePreparationRead?> PrepareSaveAsync(Guid id, SavePreparationRequest request, CancellationToken ct = default) => throw DeepRead();
        public override ValueTask<IndexedStorageMapping?> FindMappingAsync(IndexedGraphScope scope, ExtendedId storageIndex, IndexedMappingKey key, CancellationToken ct = default) => throw DeepRead();
        public override ValueTask<IndexedRevisionManifest?> ReadRevisionAsync(IndexedGraphScope scope, ExtendedId manifest, CancellationToken ct = default) => throw DeepRead();
        public override ValueTask<ImmutableArray<IndexedObjectDefinition>> ReadGroupAsync(IndexedGraphScope scope, ExtendedId group, CancellationToken ct = default) => throw DeepRead();
        public override ValueTask<ImmutableArray<GraphElementState>> ReadElementsAsync(IndexedGraphScope scope, IReadOnlyCollection<ExtendedId> ids, CancellationToken ct = default) => throw DeepRead();
        public override ValueTask<ImmutableArray<ExtendedId>> ReadVisibleElementIdsAsync(IndexedGraphScope scope, long sequence, IReadOnlyCollection<ExtendedId> ids, CancellationToken ct = default) => throw DeepRead();
        public override ValueTask<ImmutableArray<IndexedStorageMapping>> ReadMappingClaimsAsync(IndexedGraphScope scope, IReadOnlyCollection<SerialId> serials, CancellationToken ct = default) => throw DeepRead();
        public override ValueTask<ImmutableArray<IndexedStorageMapping>> ReadMappingsAsync(IndexedGraphScope scope, long sequence, CancellationToken ct = default) => throw DeepRead();
        private static InvalidOperationException DeepRead() => new("Presence reads must not load graph or history records.");
    }
}
