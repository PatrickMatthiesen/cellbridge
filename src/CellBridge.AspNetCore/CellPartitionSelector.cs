using CellBridge.Storage;

namespace CellBridge.Web;

public static class CellPartitionSelector
{
    public static bool TryResolve(IReadOnlyDictionary<string, string> attributes, out DocumentPartitionKind kind)
    {
        kind = DocumentPartitionKind.FileContents;
        // GetFileProps requests response metadata; it is not a partition selector.
        // A missing SOAP selector uses the default file partition, as observed
        // in Word's inline QueryAccess request. Explicit zero is also default.
        if (!attributes.TryGetValue("PartitionID", out var text)) return true;
        if (!Guid.TryParse(text, out var id)) return false;
        if (id == Guid.Empty) return true;
        if (id == StoredDocument.MetadataPartitionId)
        {
            kind = DocumentPartitionKind.Metadata;
            return true;
        }
        if (id == StoredDocument.EditorsTablePartitionId)
        {
            kind = DocumentPartitionKind.EditorsTable;
            return true;
        }
        return false;
    }
}
