using System.Collections.Immutable;
using CellBridge.Storage.Abstractions;

namespace CellBridge.Storage.SqlServer;

public sealed partial class SqlServerStateStore : IDocumentRepository, IIndexedSaveSource
{
    private readonly RelationalDocumentRepository<SqlServerDocumentDbContext> repository;
    ValueTask<DocumentHead?> IDocumentRepository.FindAsync(DocumentIdentity identity, CancellationToken ct) =>
        repository.FindAsync(identity, ct);
    ValueTask<IReadOnlyList<DocumentHead>> IDocumentRepository.ListAsync(int offset, int limit, CancellationToken ct) =>
        repository.ListAsync(offset, limit, ct);
    ValueTask<DocumentControlRead?> IDocumentRepository.ReadControlAsync(Guid id, CancellationToken ct) =>
        repository.ReadControlAsync(id, ct);
    ValueTask<DocumentRevisionPage?> IDocumentRepository.ReadRevisionsAsync(Guid id, int offset, int limit, CancellationToken ct) =>
        repository.ReadRevisionsAsync(id, offset, limit, ct);
    ValueTask<DocumentRevisionSelection?> IDocumentRepository.ReadRevisionAsync(Guid id, ulong number, CancellationToken ct) =>
        repository.ReadRevisionAsync(id, number, ct);
    ValueTask<SaveReceiptRead?> IDocumentRepository.ReadReceiptAsync(Guid id, SaveOperationIdentity operation, CancellationToken ct) =>
        repository.ReadReceiptAsync(id, operation, ct);
    ValueTask<PartitionRead?> IDocumentRepository.ReadPartitionAsync(Guid id, PartitionReadRequest request, CancellationToken ct) =>
        repository.ReadPartitionAsync(id, request, ct);
    ValueTask<SavePreparationRead?> IDocumentRepository.PrepareSaveAsync(Guid id, SavePreparationRequest request, CancellationToken ct) =>
        repository.PrepareSaveAsync(id, request, ct);
    ValueTask<CreateResult> IDocumentRepository.CreateAsync(PreparedDocumentCreate create, CancellationToken ct) =>
        repository.CreateAsync(create, ct);
    ValueTask<ControlResult> IDocumentRepository.ExecuteControlAsync(DocumentControlCommand command, CancellationToken ct) =>
        repository.ExecuteControlAsync(command, ct);
    ValueTask<bool> IDocumentRepository.ReserveContentAsync(DocumentActorAdmission admission, IReadOnlyCollection<ContentHandle> handles, CancellationToken ct) =>
        repository.ReserveContentAsync(admission, handles, ct);
    ValueTask<PublicationResult> IDocumentRepository.PublishAsync(PreparedDocumentPublication publication, CancellationToken ct) =>
        repository.PublishAsync(publication, ct);
    ValueTask<PathChangeResult> IDocumentRepository.ChangePathAsync(DocumentPathCommand command, CancellationToken ct) =>
        repository.ChangePathAsync(command, ct);
    ValueTask<LifecycleResult> IDocumentRepository.ChangeLifecycleAsync(DocumentLifecycleCommand command, CancellationToken ct) =>
        repository.ChangeLifecycleAsync(command, ct);
    ValueTask<DeliveryRead?> IDocumentRepository.ReadNextDeliveryAsync(Guid id, CancellationToken ct) =>
        repository.ReadNextDeliveryAsync(id, ct);
    ValueTask<IReadOnlyList<Guid>> IDocumentRepository.ListPendingDeliveriesAsync(int limit, CancellationToken ct) =>
        repository.ListPendingDeliveriesAsync(limit, ct);
    ValueTask<DeliveryResult> IDocumentRepository.UpdateDeliveryAsync(DocumentDeliveryCommand command, CancellationToken ct) =>
        repository.UpdateDeliveryAsync(command, ct);
    ValueTask IDocumentRepository.CheckHealthAsync(CancellationToken ct) => repository.CheckHealthAsync(ct);
    ValueTask<IndexedStorageMapping?> IIndexedGraphSource.FindMappingAsync(IndexedGraphScope scope, ExtendedId index, IndexedMappingKey key, CancellationToken ct) =>
        repository.FindMappingAsync(scope, index, key, ct);
    ValueTask<IndexedRevisionManifest?> IIndexedGraphSource.ReadRevisionAsync(IndexedGraphScope scope, ExtendedId manifest, CancellationToken ct) =>
        ((IIndexedGraphSource)repository).ReadRevisionAsync(scope, manifest, ct);
    ValueTask<ImmutableArray<IndexedObjectDefinition>> IIndexedGraphSource.ReadGroupAsync(IndexedGraphScope scope, ExtendedId group, CancellationToken ct) =>
        repository.ReadGroupAsync(scope, group, ct);
    ValueTask<ImmutableArray<ExtendedId>> IIndexedSaveSource.ReadVisibleElementIdsAsync(IndexedGraphScope scope, long sequence, IReadOnlyCollection<ExtendedId> ids, CancellationToken ct) =>
        repository.ReadVisibleElementIdsAsync(scope, sequence, ids, ct);
    ValueTask<ImmutableArray<GraphElementState>> IIndexedSaveSource.ReadElementsAsync(IndexedGraphScope scope, IReadOnlyCollection<ExtendedId> ids, CancellationToken ct) =>
        repository.ReadElementsAsync(scope, ids, ct);
    ValueTask<ImmutableArray<IndexedStorageMapping>> IIndexedSaveSource.ReadMappingClaimsAsync(IndexedGraphScope scope, IReadOnlyCollection<SerialId> serials, CancellationToken ct) =>
        repository.ReadMappingClaimsAsync(scope, serials, ct);
    ValueTask<ImmutableArray<IndexedStorageMapping>> IIndexedSaveSource.ReadMappingsAsync(IndexedGraphScope scope, long sequence, CancellationToken ct) =>
        repository.ReadMappingsAsync(scope, sequence, ct);
    ValueTask<NormalizedDocumentSnapshot?> IIndexedRepositoryRecovery.ExportDocumentAsync(Guid resourceId, CancellationToken ct) =>
        repository.ExportDocumentAsync(resourceId, ct);
    ValueTask IProviderRecoveryStore.CheckRecoveryAsync(CancellationToken ct) => repository.CheckRecoveryAsync(ct);
    ValueTask<ProviderSnapshot> IProviderRecoveryStore.CaptureRecoveryAsync(CancellationToken ct) => repository.CaptureRecoveryAsync(ct);
    ValueTask<RecoveryReceipt?> IProviderRecoveryStore.FindRecoveryReceiptAsync(Guid operationId, CancellationToken ct) => repository.FindRecoveryReceiptAsync(operationId, ct);
    ValueTask<RecoveryReceipt> IProviderRecoveryStore.ImportRecoveryAsync(ProviderSnapshot snapshot, RecoveryReceipt receipt, CancellationToken ct) =>
        repository.ImportRecoveryAsync(snapshot, receipt, ct);
}
