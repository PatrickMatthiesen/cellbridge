using CellBridge.FssHttpB;

namespace CellBridge.FssHttpB.Tests;

public sealed class QueryKnowledgeResponseTests
{
    [Fact]
    public void QueryKnowledgeRetainsAllSerialAndMappingRangesAndOwnsItsBytes()
    {
        var guid = Guid.NewGuid(); var other = Guid.NewGuid();
        var serials = new[] { new SerialNumber(guid, 1), new SerialNumber(guid, 3), new SerialNumber(other, 7) };
        var elements = serials.Select((s, i) => new DataElement(DataElementType.ObjectGroupDataElementData, new((uint)(i + 1), guid), s)).ToArray();
        var mappings = new[] { new SerialNumber(other, 20), new SerialNumber(guid, 8) };
        var knowledge = BinaryKnowledgeBuilder.FromElements(elements, new(2, other), 23, mappingSerials: mappings);
        var data = new QueryChangesSubResponseData { StorageIndexExtendedGuid = new(999, guid), PartialResult = true, KnowledgeBytes = knowledge };
        var bytes = Encode(data);
        byte[] padded = [0xAA, 0xBB, ..bytes, 0xCC];
        var reader = new BinaryReaderEx(padded, 2, bytes.Length);
        var decoded = QueryChangesSubResponseData.Deserialize(reader);
        Assert.Equal(0, reader.Remaining); Assert.Equal(bytes.Length + 2, reader.Position);
        Assert.Equal(knowledge, decoded.KnowledgeBytes);
        Assert.Equal(bytes, Encode(decoded));
        padded.AsSpan().Clear();
        Assert.Equal(knowledge, decoded.KnowledgeBytes);
        var client = ClientKnowledge.Deserialize(new(decoded.KnowledgeBytes!));
        Assert.All(serials.Concat(mappings), serial => Assert.True(client.Contains(serial)));
        Assert.False(client.Contains(new(guid, 2)));
    }

    [Fact]
    public void OmittedCellKnowledgeIsNotFabricatedOnRoundTrip()
    {
        var data = new QueryChangesSubResponseData { StorageIndexExtendedGuid = new(1, Guid.NewGuid()), IncludeCellKnowledge = false, Waterline = 7 };
        var bytes = Encode(data);
        var decoded = QueryChangesSubResponseData.Deserialize(new(bytes));
        Assert.NotNull(decoded.KnowledgeBytes);
        Assert.Equal(bytes, Encode(decoded));
        // Scalar fields deliberately rebuild only after the retained object is cleared.
        decoded.KnowledgeBytes = null; decoded.IncludeCellKnowledge = false; decoded.Waterline = 8;
        Assert.Equal(8UL, QueryChangesSubResponseData.Deserialize(new(Encode(decoded))).Waterline);
    }

    [Fact]
    public void RetentionDoesNotBypassMissingWrongOrUnsupportedKnowledgeValidation()
    {
        var data = new QueryChangesSubResponseData { StorageIndexExtendedGuid = new(1, Guid.NewGuid()) };
        var bytes = Encode(data);
        Assert.Throws<EndOfStreamException>(() => QueryChangesSubResponseData.Deserialize(new(bytes[..^1])));
        var wrongEnd = new BinaryWriterEx(); new StreamObjectHeaderEnd8Bit(StreamObjectTypeHeaderEnd.CellKnowledge).Serialize(wrongEnd);
        bytes[^1] = Assert.Single(wrongEnd.ToArray());
        Assert.Throws<InvalidDataException>(() => QueryChangesSubResponseData.Deserialize(new(bytes)));
        data.KnowledgeBytes = BinaryKnowledgeBuilder.FromElements([], ExGuid.Null, 1, versionToken: [1]);
        Assert.Throws<InvalidDataException>(() => QueryChangesSubResponseData.Deserialize(new(Encode(data))));
    }

    private static byte[] Encode(QueryChangesSubResponseData data)
    { var writer = new BinaryWriterEx(); data.Serialize(writer); return writer.ToArray(); }
}
