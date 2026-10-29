namespace CellBridge.Storage.Shared;

// JSON columns contain exactly one bounded record, never an aggregate's retained history.
internal sealed class RepositoryHeadRow
{
    public required string PathKey { get; set; }
    public Guid ResourceId { get; set; }
    public required string PathHash { get; set; }
    public long Lifecycle { get; set; }
    public long StateVersion { get; set; }
    public bool IsDeleted { get; set; }
    public int PendingDeliveries { get; set; }
    public bool DeliveryBlocked { get; set; }
    public required string Json { get; set; }
}
internal sealed class RepositoryCoordinationRow
{
    public Guid ResourceId { get; set; }
    public required string Json { get; set; }
}
internal sealed class RepositoryEditorRow
{
    public Guid ResourceId { get; set; }
    public Guid ClientId { get; set; }
    public DateTime ExpiresUtc { get; set; }
    public required string Json { get; set; }
}
internal sealed class RepositoryPartitionRow
{
    public Guid ResourceId { get; set; }
    public long Lifecycle { get; set; }
    public int Partition { get; set; }
    public long Sequence { get; set; }
    public long CheckpointSequence { get; set; }
    public int ReplayChanges { get; set; }
    public long ReplayBytes { get; set; }
    public required string Json { get; set; }
}
internal sealed class RepositoryElementRow
{
    public Guid ResourceId { get; set; }
    public long Lifecycle { get; set; }
    public int Partition { get; set; }
    public required string ElementKey { get; set; }
    public Guid SerialGuid { get; set; }
    public decimal SerialValue { get; set; }
    public required string Json { get; set; }
}
internal sealed class RepositoryVisibilityRow
{
    public Guid ResourceId { get; set; }
    public long Lifecycle { get; set; }
    public int Partition { get; set; }
    public required string ElementKey { get; set; }
    public long StartSequence { get; set; }
    public long? EndSequence { get; set; }
}
internal sealed class RepositoryGraphRow
{
    public Guid ResourceId { get; set; }
    public long Lifecycle { get; set; }
    public int Partition { get; set; }
    public required string Kind { get; set; }
    public required string OwnerKey { get; set; }
    public required string RecordKey { get; set; }
    public required string Json { get; set; }
}
internal sealed class RepositoryBindingRow
{
    public Guid ResourceId { get; set; }
    public long Lifecycle { get; set; }
    public int Partition { get; set; }
    public required string BindingKey { get; set; }
    public required string Json { get; set; }
}
internal sealed class RepositoryBindingChangeRow
{
    public Guid ResourceId { get; set; }
    public long Lifecycle { get; set; }
    public int Partition { get; set; }
    public long Sequence { get; set; }
    public required string BindingKey { get; set; }
    public required string Json { get; set; }
}
internal sealed class RepositoryCheckpointRow
{
    public Guid ResourceId { get; set; }
    public long Lifecycle { get; set; }
    public int Partition { get; set; }
    public long Sequence { get; set; }
    public required string BindingKey { get; set; }
    public required string Json { get; set; }
}
internal sealed class RepositoryRevisionRow
{
    public Guid ResourceId { get; set; }
    public long Lifecycle { get; set; }
    public decimal RevisionNumber { get; set; }
    public required string Json { get; set; }
}
internal sealed class RepositoryReceiptRow
{
    public Guid ResourceId { get; set; }
    public long Lifecycle { get; set; }
    public int Family { get; set; }
    public int Partition { get; set; }
    public required string OperationHash { get; set; }
    public required string Json { get; set; }
}
internal sealed class RepositoryPathRow
{
    public required string PathHash { get; set; }
    public required string PathKey { get; set; }
    public Guid ResourceId { get; set; }
}
internal sealed class RepositoryDeliveryRow
{
    public Guid ResourceId { get; set; }
    public Guid OperationId { get; set; }
    public long Sequence { get; set; }
    public required string Json { get; set; }
}
internal sealed class RepositoryReferenceRow
{
    public Guid ResourceId { get; set; }
    public required string ObjectKey { get; set; }
    public long Length { get; set; }
    public required string Sha256 { get; set; }
    public long ReferenceCount { get; set; }
    public bool IsPending { get; set; }
    public long AdmissionBytes { get; set; }
}
internal sealed class RepositoryDeltaRow
{
    public Guid ResourceId { get; set; }
    public long Lifecycle { get; set; }
    public int Partition { get; set; }
    public long Sequence { get; set; }
    public long PreviousSequence { get; set; }
    public long EncodedBytes { get; set; }
    public int ChangeCount { get; set; }
}
internal sealed class RepositoryMappingRow
{
    public long StartSequence { get; set; }
    public Guid ResourceId { get; set; }
    public long Lifecycle { get; set; }
    public int Partition { get; set; }
    public required string MappingKey { get; set; }
    public required string Json { get; set; }
}
internal sealed class RepositoryMappingIntervalRow
{
    public Guid ResourceId { get; set; }
    public long Lifecycle { get; set; }
    public int Partition { get; set; }
    public required string MappingKey { get; set; }
    public long StartSequence { get; set; }
    public long? EndSequence { get; set; }
    public required string Json { get; set; }
}
internal sealed class RepositoryMappingRootRow
{
    public required string Json { get; set; }
    public Guid SerialGuid { get; set; }
    public decimal SerialValue { get; set; }
    public Guid ResourceId { get; set; }
    public long Lifecycle { get; set; }
    public int Partition { get; set; }
    public required string IndexKey { get; set; }
    public long Sequence { get; set; }
}
internal sealed class RepositoryMappingClaimRow
{
    public Guid ResourceId { get; set; }
    public long Lifecycle { get; set; }
    public int Partition { get; set; }
    public Guid SerialGuid { get; set; }
    public decimal SerialValue { get; set; }
    public required string Json { get; set; }
}
