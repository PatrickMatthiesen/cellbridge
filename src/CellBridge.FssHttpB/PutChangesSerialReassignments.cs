namespace CellBridge.FssHttpB;

/// <summary>Serializes the accepted identity-to-serial assignments in a Put Changes response.</summary>
public static class PutChangesSerialReassignments
{
    public static byte[] Serialize(IEnumerable<DataElement> elements)
    {
        var writer = new BinaryWriterEx();
        new StreamObjectHeaderStart32Bit(StreamObjectTypeHeaderStart.PutChangesResponseSerialNumberReassignAll, 1).Serialize(writer);
        SerialNumber.Null.Serialize(writer);
        foreach (var element in elements)
        {
            var body = new BinaryWriterEx();
            element.DataElementExtendedGuid.Serialize(body);
            element.SerialNumber.Serialize(body);
            new StreamObjectHeaderStart32Bit(StreamObjectTypeHeaderStart.PutChangesResponseSerialNumberReassign, body.Length).Serialize(writer);
            writer.WriteBytes(body.ToArray());
        }
        return writer.ToArray();
    }

}
