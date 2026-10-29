using System.Collections.Immutable;
using CellBridge.Storage.Abstractions;

namespace CellBridge.Storage.Tests;

/// <summary>Intercepts typed repository boundaries for concurrency and failure tests.</summary>
internal abstract class RepositoryTestProxy(IDocumentRepository inner) : IDocumentRepository
{
    protected IDocumentRepository Inner { get; } = inner;
    public bool Durable => Inner.Durable;
    public bool Shared => Inner.Shared;
    public virtual ValueTask<DocumentHead?> FindAsync(DocumentIdentity identity, CancellationToken ct = default) => Inner.FindAsync(identity, ct);
    public virtual ValueTask<IReadOnlyList<DocumentHead>> ListAsync(int offset, int limit, CancellationToken ct = default) => Inner.ListAsync(offset, limit, ct);
    public virtual ValueTask<DocumentControlRead?> ReadControlAsync(Guid id, CancellationToken ct = default) => Inner.ReadControlAsync(id, ct);
    public virtual ValueTask<DocumentRevisionPage?> ReadRevisionsAsync(Guid id, int offset, int limit, CancellationToken ct = default) => Inner.ReadRevisionsAsync(id, offset, limit, ct);
    public virtual ValueTask<DocumentRevisionSelection?> ReadRevisionAsync(Guid id, ulong number, CancellationToken ct = default) => Inner.ReadRevisionAsync(id, number, ct);
    public virtual ValueTask<SaveReceiptRead?> ReadReceiptAsync(Guid id, SaveOperationIdentity operation, CancellationToken ct = default) => Inner.ReadReceiptAsync(id, operation, ct);
    public virtual ValueTask<PartitionRead?> ReadPartitionAsync(Guid id, PartitionReadRequest request, CancellationToken ct = default) => Inner.ReadPartitionAsync(id, request, ct);
    public virtual ValueTask<SavePreparationRead?> PrepareSaveAsync(Guid id, SavePreparationRequest request, CancellationToken ct = default) => Inner.PrepareSaveAsync(id, request, ct);
    public virtual ValueTask<bool> ReserveContentAsync(DocumentActorAdmission admission, IReadOnlyCollection<ContentHandle> handles, CancellationToken ct = default) => Inner.ReserveContentAsync(admission, handles, ct);
    public virtual ValueTask<CreateResult> CreateAsync(PreparedDocumentCreate create, CancellationToken ct = default) => Inner.CreateAsync(create, ct);
    public virtual ValueTask<ControlResult> ExecuteControlAsync(DocumentControlCommand command, CancellationToken ct = default) => Inner.ExecuteControlAsync(command, ct);
    public virtual ValueTask<PublicationResult> PublishAsync(PreparedDocumentPublication publication, CancellationToken ct = default) => Inner.PublishAsync(publication, ct);
    public virtual ValueTask<PathChangeResult> ChangePathAsync(DocumentPathCommand command, CancellationToken ct = default) => Inner.ChangePathAsync(command, ct);
    public virtual ValueTask<LifecycleResult> ChangeLifecycleAsync(DocumentLifecycleCommand command, CancellationToken ct = default) => Inner.ChangeLifecycleAsync(command, ct);
    public virtual ValueTask<DeliveryRead?> ReadNextDeliveryAsync(Guid id, CancellationToken ct = default) => Inner.ReadNextDeliveryAsync(id, ct);
    public virtual ValueTask<IReadOnlyList<Guid>> ListPendingDeliveriesAsync(int limit, CancellationToken ct = default) => Inner.ListPendingDeliveriesAsync(limit, ct);
    public virtual ValueTask<DeliveryResult> UpdateDeliveryAsync(DocumentDeliveryCommand command, CancellationToken ct = default) => Inner.UpdateDeliveryAsync(command, ct);
    public virtual ValueTask CheckHealthAsync(CancellationToken ct = default) => Inner.CheckHealthAsync(ct);
    public virtual ValueTask<IndexedStorageMapping?> FindMappingAsync(IndexedGraphScope scope, ExtendedId storageIndex, IndexedMappingKey key, CancellationToken ct = default) => Inner.FindMappingAsync(scope, storageIndex, key, ct);
    public virtual ValueTask<IndexedRevisionManifest?> ReadRevisionAsync(IndexedGraphScope scope, ExtendedId manifest, CancellationToken ct = default) => Inner.ReadRevisionAsync(scope, manifest, ct);
    public virtual ValueTask<ImmutableArray<IndexedObjectDefinition>> ReadGroupAsync(IndexedGraphScope scope, ExtendedId group, CancellationToken ct = default) => Inner.ReadGroupAsync(scope, group, ct);
    public virtual ValueTask<ImmutableArray<ExtendedId>> ReadVisibleElementIdsAsync(IndexedGraphScope scope,long publicationSequence,
        IReadOnlyCollection<ExtendedId> elementIds,CancellationToken cancellationToken=default) => Inner.ReadVisibleElementIdsAsync(scope,publicationSequence,elementIds,cancellationToken);
    public virtual ValueTask<ImmutableArray<GraphElementState>> ReadElementsAsync(IndexedGraphScope scope, IReadOnlyCollection<ExtendedId> ids, CancellationToken ct = default) => Inner.ReadElementsAsync(scope, ids, ct);
    public virtual ValueTask<ImmutableArray<IndexedStorageMapping>> ReadMappingClaimsAsync(IndexedGraphScope scope, IReadOnlyCollection<SerialId> serials, CancellationToken ct = default) => Inner.ReadMappingClaimsAsync(scope, serials, ct);
    public virtual ValueTask<ImmutableArray<IndexedStorageMapping>> ReadMappingsAsync(IndexedGraphScope scope, long sequence, CancellationToken ct = default) => Inner.ReadMappingsAsync(scope, sequence, ct);
    public virtual ValueTask CheckRecoveryAsync(CancellationToken ct = default) => Inner.CheckRecoveryAsync(ct);
    public virtual ValueTask<ProviderSnapshot> CaptureRecoveryAsync(CancellationToken ct = default) => Inner.CaptureRecoveryAsync(ct);
    public virtual ValueTask<RecoveryReceipt?> FindRecoveryReceiptAsync(Guid operation, CancellationToken ct = default) => Inner.FindRecoveryReceiptAsync(operation, ct);
    public virtual ValueTask<RecoveryReceipt> ImportRecoveryAsync(ProviderSnapshot snapshot, RecoveryReceipt receipt, CancellationToken ct = default) => Inner.ImportRecoveryAsync(snapshot, receipt, ct);
    public virtual ValueTask<NormalizedDocumentSnapshot?> ExportDocumentAsync(Guid id, CancellationToken ct = default) => Inner.ExportDocumentAsync(id, ct);
}
