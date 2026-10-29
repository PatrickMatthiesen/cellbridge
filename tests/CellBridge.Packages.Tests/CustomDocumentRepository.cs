using System.Collections.Immutable;
using CellBridge.Storage.Abstractions;
using CellBridge.Storage.InMemory;

namespace CellBridge.Packages.Tests;

// Implements the complete public repository boundary using package references only.
internal sealed class CustomDocumentRepository(IContentStore content) : IDocumentRepository
{
    private IDocumentRepository Inner { get; } = new InMemoryDocumentRepository(content);
    public int Creates { get; private set; }
    public int Finds { get; private set; }
    public bool Durable => Inner.Durable;
    public bool Shared => Inner.Shared;
    public ValueTask<DocumentHead?> FindAsync(DocumentIdentity identity, CancellationToken ct = default)
    { Finds++; return Inner.FindAsync(identity, ct); }
    public ValueTask<IReadOnlyList<DocumentHead>> ListAsync(int offset, int limit, CancellationToken ct = default) => Inner.ListAsync(offset, limit, ct);
    public ValueTask<DocumentControlRead?> ReadControlAsync(Guid id, CancellationToken ct = default) => Inner.ReadControlAsync(id, ct);
    public ValueTask<DocumentRevisionPage?> ReadRevisionsAsync(Guid id, int offset, int limit, CancellationToken ct = default) => Inner.ReadRevisionsAsync(id, offset, limit, ct);
    public ValueTask<DocumentRevisionSelection?> ReadRevisionAsync(Guid id, ulong number, CancellationToken ct = default) => Inner.ReadRevisionAsync(id, number, ct);
    public ValueTask<SaveReceiptRead?> ReadReceiptAsync(Guid id, SaveOperationIdentity operation, CancellationToken ct = default) => Inner.ReadReceiptAsync(id, operation, ct);
    public ValueTask<PartitionRead?> ReadPartitionAsync(Guid id, PartitionReadRequest request, CancellationToken ct = default) => Inner.ReadPartitionAsync(id, request, ct);
    public ValueTask<SavePreparationRead?> PrepareSaveAsync(Guid id, SavePreparationRequest request, CancellationToken ct = default) => Inner.PrepareSaveAsync(id, request, ct);
    public ValueTask<bool> ReserveContentAsync(DocumentActorAdmission admission, IReadOnlyCollection<ContentHandle> handles, CancellationToken ct = default) => Inner.ReserveContentAsync(admission, handles, ct);
    public ValueTask<CreateResult> CreateAsync(PreparedDocumentCreate create, CancellationToken ct = default)
    { Creates++; return Inner.CreateAsync(create, ct); }
    public ValueTask<ControlResult> ExecuteControlAsync(DocumentControlCommand command, CancellationToken ct = default) => Inner.ExecuteControlAsync(command, ct);
    public ValueTask<PublicationResult> PublishAsync(PreparedDocumentPublication publication, CancellationToken ct = default) => Inner.PublishAsync(publication, ct);
    public ValueTask<PathChangeResult> ChangePathAsync(DocumentPathCommand command, CancellationToken ct = default) => Inner.ChangePathAsync(command, ct);
    public ValueTask<LifecycleResult> ChangeLifecycleAsync(DocumentLifecycleCommand command, CancellationToken ct = default) => Inner.ChangeLifecycleAsync(command, ct);
    public ValueTask<DeliveryRead?> ReadNextDeliveryAsync(Guid id, CancellationToken ct = default) => Inner.ReadNextDeliveryAsync(id, ct);
    public ValueTask<IReadOnlyList<Guid>> ListPendingDeliveriesAsync(int limit, CancellationToken ct = default) => Inner.ListPendingDeliveriesAsync(limit, ct);
    public ValueTask<DeliveryResult> UpdateDeliveryAsync(DocumentDeliveryCommand command, CancellationToken ct = default) => Inner.UpdateDeliveryAsync(command, ct);
    public ValueTask CheckHealthAsync(CancellationToken ct = default) => Inner.CheckHealthAsync(ct);
    public ValueTask<IndexedStorageMapping?> FindMappingAsync(IndexedGraphScope scope, ExtendedId storageIndex, IndexedMappingKey key, CancellationToken ct = default) => Inner.FindMappingAsync(scope, storageIndex, key, ct);
    public ValueTask<IndexedRevisionManifest?> ReadRevisionAsync(IndexedGraphScope scope, ExtendedId manifest, CancellationToken ct = default) => Inner.ReadRevisionAsync(scope, manifest, ct);
    public ValueTask<ImmutableArray<IndexedObjectDefinition>> ReadGroupAsync(IndexedGraphScope scope, ExtendedId group, CancellationToken ct = default) => Inner.ReadGroupAsync(scope, group, ct);
    public ValueTask<ImmutableArray<ExtendedId>> ReadVisibleElementIdsAsync(IndexedGraphScope scope,long publicationSequence,
        IReadOnlyCollection<ExtendedId> elementIds,CancellationToken cancellationToken=default) => Inner.ReadVisibleElementIdsAsync(scope,publicationSequence,elementIds,cancellationToken);
    public ValueTask<ImmutableArray<GraphElementState>> ReadElementsAsync(IndexedGraphScope scope, IReadOnlyCollection<ExtendedId> ids, CancellationToken ct = default) => Inner.ReadElementsAsync(scope, ids, ct);
    public ValueTask<ImmutableArray<IndexedStorageMapping>> ReadMappingClaimsAsync(IndexedGraphScope scope, IReadOnlyCollection<SerialId> serials, CancellationToken ct = default) => Inner.ReadMappingClaimsAsync(scope, serials, ct);
    public ValueTask<ImmutableArray<IndexedStorageMapping>> ReadMappingsAsync(IndexedGraphScope scope, long sequence, CancellationToken ct = default) => Inner.ReadMappingsAsync(scope, sequence, ct);
    public ValueTask CheckRecoveryAsync(CancellationToken ct = default) => Inner.CheckRecoveryAsync(ct);
    public ValueTask<ProviderSnapshot> CaptureRecoveryAsync(CancellationToken ct = default) => Inner.CaptureRecoveryAsync(ct);
    public ValueTask<RecoveryReceipt?> FindRecoveryReceiptAsync(Guid operation, CancellationToken ct = default) => Inner.FindRecoveryReceiptAsync(operation, ct);
    public ValueTask<RecoveryReceipt> ImportRecoveryAsync(ProviderSnapshot snapshot, RecoveryReceipt receipt, CancellationToken ct = default) => Inner.ImportRecoveryAsync(snapshot, receipt, ct);
    public ValueTask<NormalizedDocumentSnapshot?> ExportDocumentAsync(Guid id, CancellationToken ct = default) => Inner.ExportDocumentAsync(id, ct);
}
