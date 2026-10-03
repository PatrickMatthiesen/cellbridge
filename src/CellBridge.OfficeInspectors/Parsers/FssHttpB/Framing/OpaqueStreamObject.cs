namespace CellBridge.OfficeInspectors.Parsers;

/// <summary>An extension whose framing is understood but whose semantics are unknown.</summary>
public sealed class OpaqueStreamObject : BaseStructure
{
    public int Type { get; private set; }
    public byte[] Payload { get; private set; } = [];
    public List<OpaqueStreamObject> Children { get; } = [];

    public override void Parse(Stream stream) => Parse(stream, 0);

    private void Parse(Stream stream, int depth)
    {
        if (depth >= 64) throw new InvalidDataException("Extension nesting limit exceeded.");
        base.Parse(stream);
        var header = new StreamObjectHeader().TryParse(stream);
        int compound;
        int length;
        switch (header)
        {
            case bit16StreamObjectHeaderStart start:
                Type = (int)start.Type;
                compound = start.B;
                length = start.Length;
                break;
            case bit32StreamObjectHeaderStart start:
                Type = (int)start.Type;
                compound = start.B;
                length = start.GetDataLength();
                break;
            default:
                throw new InvalidDataException("Invalid extension header.");
        }
        Payload = ReadBytes(length);
        if (compound == 0) return;
        while (!ContainsStreamObjectEndHeader((ushort)Type))
        {
            var child = new OpaqueStreamObject();
            child.Parse(stream, depth + 1);
            Children.Add(child);
        }
        new bit8StreamObjectHeaderEnd().Parse(stream);
    }
}
