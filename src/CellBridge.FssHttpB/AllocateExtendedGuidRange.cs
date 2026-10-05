namespace CellBridge.FssHttpB;

/// <summary>MS-FSSHTTPB 2.2.2.1.5 allocation request.</summary>
public sealed class AllocateExtendedGuidRangeSubRequestData : ISubRequestData
{
    public ulong RequestIdCount { get; set; }

    public void Serialize(BinaryWriterEx writer)
    {
        var body = new BinaryWriterEx();
        new Compact64bitInt(RequestIdCount).Serialize(body);
        body.WriteByte(0); // Reserved, written as zero and ignored on receipt.
        new StreamObjectHeaderStart32Bit(StreamObjectTypeHeaderStart.AllocateExtendedGUIDRangeRequest,
            body.Length).Serialize(writer);
        writer.WriteBytes(body.ToArray());
    }

    public static AllocateExtendedGuidRangeSubRequestData Deserialize(BinaryReaderEx reader)
    {
        var header = StreamObjectHeaderStart.Parse(reader);
        if (header.Type != StreamObjectTypeHeaderStart.AllocateExtendedGUIDRangeRequest ||
            header.HeaderType != StreamObjectHeaderStart.HeaderType32Bit || header.Compound != 0)
            throw new InvalidDataException("Expected a 32-bit Allocate Extended GUID Range request.");
        var body = new BinaryReaderEx(reader.ReadMemory(header.Length));
        var result = new AllocateExtendedGuidRangeSubRequestData { RequestIdCount = Compact64bitInt.Deserialize(body).Value };
        body.ReadByte();
        if (body.Remaining != 0) throw new InvalidDataException("Allocation request has trailing bytes.");
        return result;
    }
}

/// <summary>MS-FSSHTTPB 2.2.3.1.4 allocation response; the maximum is exclusive.</summary>
public sealed class AllocateExtendedGuidRangeSubResponseData : ISubResponseData
{
    public Guid GuidComponent { get; set; }
    public ulong IntegerRangeMin { get; set; }
    public ulong IntegerRangeMax { get; set; }

    public void Serialize(BinaryWriterEx writer)
    {
        Validate();
        var body = new BinaryWriterEx();
        ExGuid.WriteGuid(body, GuidComponent);
        new Compact64bitInt(IntegerRangeMin).Serialize(body);
        new Compact64bitInt(IntegerRangeMax).Serialize(body);
        new StreamObjectHeaderStart32Bit(StreamObjectTypeHeaderStart.AllocateExtendedGUIDRangeResponse,
            body.Length).Serialize(writer);
        writer.WriteBytes(body.ToArray());
    }

    public static AllocateExtendedGuidRangeSubResponseData Deserialize(BinaryReaderEx reader)
    {
        var header = StreamObjectHeaderStart.Parse(reader);
        if (header.Type != StreamObjectTypeHeaderStart.AllocateExtendedGUIDRangeResponse ||
            header.HeaderType != StreamObjectHeaderStart.HeaderType32Bit || header.Compound != 0)
            throw new InvalidDataException("Expected a 32-bit Allocate Extended GUID Range response.");
        var body = new BinaryReaderEx(reader.ReadMemory(header.Length));
        var result = new AllocateExtendedGuidRangeSubResponseData
        {
            GuidComponent = ExGuid.ReadGuid(body),
            IntegerRangeMin = Compact64bitInt.Deserialize(body).Value,
            IntegerRangeMax = Compact64bitInt.Deserialize(body).Value,
        };
        if (body.Remaining != 0) throw new InvalidDataException("Allocation response has trailing bytes.");
        result.Validate();
        return result;
    }

    private void Validate()
    {
        if (GuidComponent == Guid.Empty || IntegerRangeMax is < 1000 or > 100_000 || IntegerRangeMin >= IntegerRangeMax)
            throw new InvalidDataException("Invalid allocated GUID namespace or integer range.");
    }
}
