using System.Buffers.Binary;

namespace CellBridge.FssHttpB;

/// <summary>Projects complete inline object groups for QueryChanges. Stored elements remain unchanged.</summary>
public static class ObjectGroupWireHash
{
    public static DataElement Project(DataElement element, RequestHashOptions? negotiation, ProtocolHashingOptions configuration)
    {
        if (negotiation is { Schema: not 1 }) throw new InvalidDataException("Unsupported request hashing schema.");
        if (!configuration.Enabled || negotiation?.Requested != true ||
            element.DataElementType != DataElementType.ObjectGroupDataElementData)
            return element;
        var group = ObjectGroupDataElement.ParseOpaque(element);
        // BLOB/cache-injection forms have no inline Object Data binary item to hash.
        // A zero-length concatenation cannot represent an MS-PCCRC content range.
        if (group.Objects.Any(o => o.Declaration.BlobReference is not null) ||
            group.Objects.All(o => o.Content.Length == 0)) return element;
        if (group.Objects.Any(o => o.ObjectGuid.IsNull || o.Declaration.DataSize != (ulong)o.Content.Length) ||
            group.Objects.Select(o => (o.ObjectGuid, o.Declaration.PartitionId)).Distinct().Count() != group.Objects.Count)
            throw new InvalidDataException("Hashing requires unique object/partition identities and exact declared sizes.");
        var ordered = group.Objects.OrderBy(o => o, ObjectOrder.Instance).Select(o => (ReadOnlyMemory<byte>)o.Content).ToArray();
        var hash = PccrcContentInformation.Create(ordered, configuration.ServerSecret);
        var writer = new BinaryWriterEx();
        new DataElementWireHash(1, hash).Serialize(writer);
        if (!negotiation.HashesInsteadOfData) writer.WriteBytes(element.Data!);
        else
        {
            // Preserve the declaration and optional metadata bytes exactly. Only data records change.
            var reader = new BinaryReaderEx(element.Data!);
            SkipCompound(reader, StreamObjectTypeHeaderStart.ObjectGroupDeclarations, StreamObjectTypeHeaderEnd.ObjectGroupDeclarations);
            var position = reader.Position;
            var header = StreamObjectHeaderStart.Parse(reader);
            if (header.Type == StreamObjectTypeHeaderStart.ObjectGroupMetadataDeclarations)
            {
                reader.Position = position;
                SkipCompound(reader, StreamObjectTypeHeaderStart.ObjectGroupMetadataDeclarations, StreamObjectTypeHeaderEnd.ObjectGroupMetadataDeclarations);
                header = StreamObjectHeaderStart.Parse(reader);
            }
            if (header.Type != StreamObjectTypeHeaderStart.ObjectGroupData || header.Compound != 1 || header.Length != 0)
                throw new InvalidDataException("Invalid object-group data header.");
            writer.WriteBytes(element.Data!.AsSpan(0, reader.Position));
            foreach (var obj in group.Objects)
                new ObjectGroupExcludedData(obj.ObjectReferences, obj.CellReferences, (ulong)obj.Content.Length).Serialize(writer);
            new StreamObjectHeaderEnd8Bit(StreamObjectTypeHeaderEnd.ObjectGroupData).Serialize(writer);
        }
        return new(element.DataElementType, new ExGuid(element.DataElementExtendedGuid.Value, element.DataElementExtendedGuid.Guid),
            new SerialNumber(element.SerialNumber.Guid, element.SerialNumber.Value)) { Data = writer.ToArray() };
    }

    private static void SkipCompound(BinaryReaderEx reader, StreamObjectTypeHeaderStart type, StreamObjectTypeHeaderEnd endType)
    {
        var start = StreamObjectHeaderStart.Parse(reader);
        if (start.Type != type || start.Compound != 1 || start.Length != 0) throw new InvalidDataException("Invalid group header.");
        while (!IsEnd(reader))
        {
            var child = StreamObjectHeaderStart.Parse(reader);
            if (child.Compound != 0) throw new InvalidDataException("Unexpected compound group child.");
            reader.Skip(child.Length);
        }
        if (StreamObjectHeaderEnd.Parse(reader).Type != endType) throw new InvalidDataException("Invalid group end.");
    }

    private static bool IsEnd(BinaryReaderEx reader)
    {
        int position = reader.Position;
        int kind = reader.ReadByte() & 3;
        reader.Position = position;
        return kind is 1 or 3;
    }

    private sealed class ObjectOrder : IComparer<ObjectGroupObject>
    {
        public static ObjectOrder Instance { get; } = new();
        public int Compare(ObjectGroupObject? a, ObjectGroupObject? b)
        {
            int compare = a!.ObjectGuid.Value.CompareTo(b!.ObjectGuid.Value);
            if (compare != 0) return compare;
            Span<byte> left = stackalloc byte[16], right = stackalloc byte[16];
            a.ObjectGuid.Guid.TryWriteBytes(left); b.ObjectGuid.Guid.TryWriteBytes(right);
            compare = BinaryPrimitives.ReadUInt32LittleEndian(left).CompareTo(BinaryPrimitives.ReadUInt32LittleEndian(right));
            if (compare != 0) return compare;
            compare = BinaryPrimitives.ReadUInt16LittleEndian(left[4..]).CompareTo(BinaryPrimitives.ReadUInt16LittleEndian(right[4..]));
            if (compare != 0) return compare;
            compare = BinaryPrimitives.ReadUInt16LittleEndian(left[6..]).CompareTo(BinaryPrimitives.ReadUInt16LittleEndian(right[6..]));
            if (compare != 0) return compare;
            compare = left[8..].SequenceCompareTo(right[8..]);
            return compare != 0 ? compare : a.Declaration.PartitionId.CompareTo(b.Declaration.PartitionId);
        }
    }
}

/// <summary>Typed schema-1 hash prefix, MS-FSSHTTPB 2.2.1.12.6.6.</summary>
public sealed record DataElementWireHash(ulong Schema, byte[] ContentInformation)
{
    public void Serialize(BinaryWriterEx writer)
    {
        if (Schema != 1) throw new InvalidDataException("Data-element hashing schema must be 1.");
        var body = new BinaryWriterEx();
        new Compact64bitInt(Schema).Serialize(body);
        new BinaryItem(ContentInformation).Serialize(body);
        new StreamObjectHeaderStart32Bit(StreamObjectTypeHeaderStart.DataElementHash, body.Length).Serialize(writer);
        writer.WriteBytes(body.ToArray());
    }

    public static DataElementWireHash Deserialize(BinaryReaderEx reader)
    {
        var header = StreamObjectHeaderStart.Parse(reader);
        if (header.Type != StreamObjectTypeHeaderStart.DataElementHash || header.Compound != 0)
            throw new InvalidDataException("Invalid data-element hash header.");
        var body = new BinaryReaderEx(reader.ReadMemory(header.Length));
        var schema = Compact64bitInt.Deserialize(body).Value;
        var content = BinaryItem.Deserialize(body).Content;
        if (schema != 1 || body.Remaining != 0) throw new InvalidDataException("Invalid data-element hash schema or length.");
        return new(schema, content);
    }
}

/// <summary>Cache-only excluded bytes, MS-FSSHTTPB 2.2.1.12.6.4. This is never materializable content.</summary>
public sealed record ObjectGroupExcludedData(IReadOnlyList<ExGuid> ObjectReferences, IReadOnlyList<CellId> CellReferences, ulong DataSize)
{
    public void Serialize(BinaryWriterEx writer)
    {
        var body = new BinaryWriterEx();
        new Compact64bitInt((ulong)ObjectReferences.Count).Serialize(body);
        foreach (var reference in ObjectReferences) reference.Serialize(body);
        new Compact64bitInt((ulong)CellReferences.Count).Serialize(body);
        foreach (var cell in CellReferences) cell.Serialize(body);
        new Compact64bitInt(DataSize).Serialize(body);
        new StreamObjectHeaderStart32Bit(StreamObjectTypeHeaderStart.ObjectGroupObjectExcludedData, body.Length).Serialize(writer);
        writer.WriteBytes(body.ToArray());
    }

    public static ObjectGroupExcludedData Deserialize(BinaryReaderEx reader)
    {
        var header = StreamObjectHeaderStart.Parse(reader);
        if (header.Type != StreamObjectTypeHeaderStart.ObjectGroupObjectExcludedData || header.Compound != 0)
            throw new InvalidDataException("Invalid excluded-data header.");
        var body = new BinaryReaderEx(reader.ReadMemory(header.Length));
        var count = Compact64bitInt.Deserialize(body).Value;
        if (count > 100_000 || count > (ulong)body.Remaining) throw new InvalidDataException("Excluded object reference limit exceeded.");
        var refs = new List<ExGuid>();
        for (ulong i = 0; i < count; i++) refs.Add(ExGuid.Deserialize(body));
        count = Compact64bitInt.Deserialize(body).Value;
        if (count > 100_000 || count > (ulong)body.Remaining / 2) throw new InvalidDataException("Excluded cell reference limit exceeded.");
        var cells = new List<CellId>();
        for (ulong i = 0; i < count; i++) cells.Add(CellId.Deserialize(body));
        var size = Compact64bitInt.Deserialize(body).Value;
        if (body.Remaining != 0) throw new InvalidDataException("Invalid excluded-data length.");
        return new(refs.AsReadOnly(), cells.AsReadOnly(), size);
    }
}
