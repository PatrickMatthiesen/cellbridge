namespace CellBridge.FssHttpB;

/// <summary>Request-wide hashing negotiation, MS-FSSHTTPB 2.2.2.</summary>
public sealed record RequestHashOptions(ulong Schema = 1, bool HashesInsteadOfData = false, bool IncludeHashes = false)
{
    public bool Requested => HashesInsteadOfData || IncludeHashes;

    public void Serialize(BinaryWriterEx writer)
    {
        if (Schema != 1) throw new InvalidDataException("Request hashing schema must be 1.");
        var body = new BinaryWriterEx();
        new Compact64bitInt(Schema).Serialize(body);
        body.WriteByte((byte)((HashesInsteadOfData ? 0x04 : 0) | (IncludeHashes ? 0x08 : 0)));
        new StreamObjectHeaderStart32Bit(StreamObjectTypeHeaderStart.RequestHashOptions, body.Length).Serialize(writer);
        writer.WriteBytes(body.ToArray());
    }

    public static RequestHashOptions Deserialize(BinaryReaderEx reader, StreamObjectHeaderStart header)
    {
        if (header.Type != StreamObjectTypeHeaderStart.RequestHashOptions || header.HeaderSize != 4 || header.Compound != 0)
            throw new InvalidDataException("Invalid Request Hashing Options declaration.");
        var body = new BinaryReaderEx(reader.ReadMemory(header.Length));
        var schema = Compact64bitInt.Deserialize(body).Value;
        var flags = body.ReadByte();
        if (schema != 1 || body.Remaining != 0)
            throw new InvalidDataException("Request hashing requires schema 1 and exactly one flag byte.");
        // A, B and E MUST be ignored on receipt, including nonzero values.
        return new(schema, (flags & 0x04) != 0, (flags & 0x08) != 0);
    }
}
