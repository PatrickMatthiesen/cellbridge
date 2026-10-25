using CellBridge.FssHttpB;
using CellBridge.Storage;

namespace CellBridge.AspNetCore;

public static class CellPartitionSelector
{
    internal static bool MatchesTarget(FsshttpbCellSubRequest request, DocumentPartitionKind kind) =>
        TryResolveTarget(request, kind, out var selected) && selected == kind;

    internal static bool TryResolveTarget(FsshttpbCellSubRequest request, DocumentPartitionKind fallback,
        out DocumentPartitionKind kind)
    {
        kind = fallback;
        return request.TargetPartitionId is not { } target || TryResolve(target, out kind);
    }

    public static bool TryResolve(IReadOnlyDictionary<string, string> attributes, out DocumentPartitionKind kind)
    {
        kind = DocumentPartitionKind.FileContents;
        // GetFileProps requests response metadata; it is not a partition selector.
        // A missing SOAP selector uses the default file partition, as observed
        // in Word's inline QueryAccess request. Explicit zero is also default.
        if (!attributes.TryGetValue("PartitionID", out var text)) return true;
        if (!Guid.TryParse(text, out var id)) return false;
        return TryResolve(id, out kind);
    }

    public static bool TryResolve(Guid id, out DocumentPartitionKind kind)
    {
        kind = DocumentPartitionKind.FileContents;
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
