namespace CellBridge.Storage.Shared;

internal sealed class SchemaVersionRow { public int Version { get; set; } }
internal sealed class DocumentRow
{
    public Guid ResourceId { get; set; }
    public required string PathKey { get; set; }
    public byte[] PathHash { get; set; } = [];
    public int RetiredPathCount { get; set; }
    public long StateVersion { get; set; }
    public bool IsDeleted { get; set; }
}
internal sealed class StateRow
{
    public Guid ResourceId { get; set; }
    public long StateVersion { get; set; }
    public required string StateJson { get; set; }
}
internal sealed class ReadHeadRow
{
    public Guid ResourceId { get; set; }
    public long StateVersion { get; set; }
    public long LifecycleGeneration { get; set; }
    public int RevisionCount { get; set; }
    public required string MetadataJson { get; set; }
}
internal sealed class RevisionSummaryRow
{
    public Guid ResourceId { get; set; }
    public long LifecycleGeneration { get; set; }
    public decimal RevisionNumber { get; set; }
    public required string SummaryJson { get; set; }
}
internal sealed class ObjectRow
{
    public required string ObjectKey { get; set; }
    public long Length { get; set; }
    public required string Sha256 { get; set; }
}
internal sealed class ChunkRow
{
    public required string ObjectKey { get; set; }
    public int Ordinal { get; set; }
    public required byte[] Bytes { get; set; }
}
internal sealed class UsageRow
{
    public bool Singleton { get; set; } = true;
    public long StoredBytes { get; set; }
    public long DocumentCount { get; set; }
    public long MaxStoredBytes { get; set; }
    public long MaxDocuments { get; set; }
}
internal sealed class RecoverySchemaRow { public int Version { get; set; } }
internal sealed class RecoveryReceiptRow
{
    public Guid OperationId { get; set; }
    public required string ReceiptJson { get; set; }
}
