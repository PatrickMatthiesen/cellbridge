namespace CellBridge.Storage.SqlServer;

internal sealed class DocumentPointer
{
    public Guid ResourceId { get; set; }
    public required string PathKey { get; set; }
    public required byte[] PathHash { get; set; }
    public long StateVersion { get; set; }
    public bool IsDeleted { get; set; }
    public int RetiredPathCount { get; set; }
}
internal sealed class DocumentSnapshot
{
    public Guid ResourceId { get; set; }
    public long StateVersion { get; set; }
    public required byte[] Data { get; set; }
}
internal sealed class DocumentMetadata
{
    public Guid ResourceId { get; set; }
    public long StateVersion { get; set; }
    public long LifecycleGeneration { get; set; }
    public int RevisionCount { get; set; }
    public required byte[] Data { get; set; }
}
internal sealed class RevisionMetadata
{
    public Guid ResourceId { get; set; }
    public long LifecycleGeneration { get; set; }
    public decimal RevisionNumber { get; set; }
    public required byte[] Data { get; set; }
}
internal sealed class PathReservation
{
    public Guid ResourceId { get; set; }
    public int Ordinal { get; set; }
    public required string PathKey { get; set; }
    public required byte[] PathHash { get; set; }
}
internal sealed class RecoveryRecord
{
    public Guid OperationId { get; set; }
    public required byte[] Data { get; set; }
}
internal sealed class DocumentSchema
{
    public int Id { get; set; }
    public int Version { get; set; }
}
