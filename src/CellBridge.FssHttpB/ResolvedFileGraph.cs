namespace CellBridge.FssHttpB;

/// <summary>Materializes a resolved cell without reopening protocol revision/base chains.</summary>
public sealed class ResolvedFileGraph
{
    private readonly ObjectGroupGraph graph;
    private readonly ExGuid root;

    private ResolvedFileGraph(IEnumerable<ObjectGroupObject> objects, ExGuid root)
    {
        this.root = new(root.Value, root.Guid);
        // The existing adapter interprets file nodes, excludes other object
        // partitions and preserves ordered references and exact leaf bytes.
        graph = ObjectGroupGraph.FromOpaqueObjects(objects);
    }

    public static ResolvedFileGraph Create(IEnumerable<ObjectGroupObject> resolvedObjects, ExGuid root) =>
        new(resolvedObjects, root);
    public byte[] Materialize(long maxBytes = int.MaxValue) => graph.Materialize(root, maxBytes);
    public ValueTask<long> MaterializeToAsync(Stream target, long maxBytes = int.MaxValue,
        CancellationToken cancellationToken = default) => graph.MaterializeToAsync(target, root, maxBytes, cancellationToken);
}
