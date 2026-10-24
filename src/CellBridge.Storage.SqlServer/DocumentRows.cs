namespace CellBridge.Storage.SqlServer;

internal sealed class PathReservation
{
    public Guid ResourceId { get; set; }
    public int Ordinal { get; set; }
    public required string PathKey { get; set; }
    public required byte[] PathHash { get; set; }
}
internal sealed class DocumentSchema
{
    public int Id { get; set; }
    public int Version { get; set; }
}
