namespace Microsoft.Protocols.TestSuites.SharedAdapter;

/// <summary>
/// Minimal FSSHTTPD node types needed to compile the vendored object-group
/// data-element builder. The S11/S12 parser does not construct FSSHTTPD nodes.
/// </summary>
public abstract class NodeObject : StreamObject
{
    protected NodeObject(StreamObjectTypeHeaderStart type) : base(type) { }
    public ExGuid ExGuid { get; set; } = new();
    public List<LeafNodeObject> IntermediateNodeObjectList { get; set; } = [];
    public virtual List<byte> GetContent() => [];
    protected override void DeserializeItemsFromByteArray(byte[] bytes, ref int index, int length) => index += length;
    protected override int SerializeItemsToByteList(List<byte> bytes) => 0;
}

public sealed class IntermediateNodeObject : NodeObject
{
    public IntermediateNodeObject() : base(StreamObjectTypeHeaderStart.IntermediateNodeObject) { }
}

public sealed class LeafNodeObject : NodeObject
{
    public LeafNodeObject() : base(StreamObjectTypeHeaderStart.LeafNodeObject) { }
    public DataNodeObjectData? DataNodeObjectData { get; set; }
}

public sealed class DataNodeObjectData
{
    public ExGuid ExGuid { get; set; } = new();
    public byte[] ObjectData { get; set; } = [];
}
