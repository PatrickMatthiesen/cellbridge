using CellBridge.FssHttpB;

namespace CellBridge.Storage.Abstractions;

public static partial class IndexedDocumentGraph
{
    /// <summary>
    /// Loads only object groups selected by the resolved file bindings and their BLOBs.
    /// The loader must verify each immutable physical payload against its recorded handle.
    /// </summary>
    public static async ValueTask<ResolvedFileGraph> LoadFileAsync(DocumentPackageRoot root,
        IEnumerable<IndexedObjectBinding> bindings,
        Func<IndexedGraphScope, ExtendedId, CancellationToken, ValueTask<DataElement>> readElement,
        CancellationToken cancellationToken = default)
    {
        var selected = BindingMap(root.Scope, bindings);
        var groups = new Dictionary<ExtendedId, IReadOnlyList<ObjectGroupObject>>();
        var blobs = new Dictionary<ExtendedId, byte[]>();
        var objects = new List<ObjectGroupObject>();
        foreach (var binding in selected.Values.Where(b => b.Key.Cell == root.Cell && b.Key.ObjectPartitionId == 1))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!groups.TryGetValue(binding.GroupElement, out var group))
            {
                var element = await readElement(root.Scope, binding.GroupElement, cancellationToken);
                CheckIdentity(element, binding.GroupElement, DataElementType.ObjectGroupDataElementData);
                groups.Add(binding.GroupElement, group = ObjectGroupDataElement.ParseOpaque(element).Objects);
            }
            if (binding.ObjectOrdinal >= group.Count) throw new StorageCorruptionException("Resolved object ordinal is outside its group.");
            var obj = group[binding.ObjectOrdinal];
            if (Capture(obj.ObjectGuid) != binding.Key.ObjectId || obj.Declaration.PartitionId != binding.Key.ObjectPartitionId)
                throw new StorageCorruptionException("Resolved object binding disagrees with its immutable group.");
            if (obj.Declaration.BlobReference is { } blobId)
            {
                var id = Capture(blobId);
                if (!blobs.TryGetValue(id, out var content))
                {
                    var blob = await readElement(root.Scope, id, cancellationToken);
                    CheckIdentity(blob, id, DataElementType.ObjectDataBLOBDataElementData);
                    blobs.Add(id, content = IndexedGraphRecords.ReadBlob(blob));
                }
                obj = obj with { Content = content };
            }
            objects.Add(obj);
        }
        return ResolvedFileGraph.Create(objects, Restore(root.RootObject));
    }

    private static void CheckIdentity(DataElement element, ExtendedId id, DataElementType type)
    {
        if (Capture(element.DataElementExtendedGuid) != id || element.DataElementType != type)
            throw new StorageCorruptionException("Immutable element lookup returned the wrong identity or type.");
    }
}
