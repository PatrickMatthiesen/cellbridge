using System.Security.Cryptography;
using System.Text.Json;
using CellBridge.FssHttpB;
using CellBridge.Tests;

namespace CellBridge.FssHttpB.Tests;

public sealed class ProtocolHashingTests
{
    [Fact]
    public void ConfiguredSecretIsOwnedAndStableAcrossConfigurationRecreation()
    {
        var secret = SHA256.HashData("independent test secret"u8);
        var sameSecret = secret.ToArray();
        var options = new ProtocolHashingOptions(secret);
        Array.Fill(secret, (byte)0xff);
        var group = Group((new ExGuid(1, Guid.NewGuid()), 1, new byte[] { 1, 2, 3 }));
        var first = ObjectGroupWireHash.Project(group, new(IncludeHashes: true), options);
        var recreated = ObjectGroupWireHash.Project(group, new(IncludeHashes: true), new(sameSecret));
        Assert.Equal(first.Data, recreated.Data);
        Assert.Throws<ArgumentException>(() => new ProtocolHashingOptions(new byte[31]));
    }

    [Theory]
    [InlineData(12)] [InlineData(13)] [InlineData(14)]
    public void AdvertisedInputSubsetValidatesHashPlacementAndRetainsOmittedUserAgentCompatibility(int version)
    {
        var request = new FsshttpbCellRequest { ProtocolVersion = (ushort)version, HashOptions = new(IncludeHashes: true),
            SubRequests = { new(RequestTypes.QueryAccess) { RequestId = 1 } } };
        var bytes = request.ToByteArray();
        int position = FindHashOptions(bytes);
        var omitted = bytes[..16].Concat(bytes[position..]).ToArray();
        Assert.Equal(request.HashOptions, FsshttpbCellRequest.Deserialize(new(omitted)).HashOptions);
        var afterQuery = bytes[..position].Concat(bytes[(position + 6)..^2]).Concat(bytes[position..(position + 6)]).Concat(bytes[^2..]).ToArray();
        Assert.Throws<InvalidDataException>(() => FsshttpbCellRequest.Deserialize(new(afterQuery)));
        var roundtrip = new BinaryWriterEx(); new StreamObjectHeaderStart32Bit(StreamObjectTypeHeaderStart.CellRoundtripOptions, 1).Serialize(roundtrip); roundtrip.WriteByte(0);
        var afterRoundtrip = bytes[..position].Concat(roundtrip.ToArray()).Concat(bytes[position..]).ToArray();
        Assert.Throws<InvalidDataException>(() => FsshttpbCellRequest.Deserialize(new(afterRoundtrip)));
    }
    [Fact]
    public void PccrcMatchesIndependentPythonVectorsAcrossBlockAndSegmentBoundaries()
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures/hash-vectors.json")));
        var secret = Convert.FromHexString(json.RootElement.GetProperty("secretHex").GetString()!);
        foreach (var vector in json.RootElement.GetProperty("vectors").EnumerateArray())
        {
            int length = vector.GetProperty("length").GetInt32();
            var bytes = new byte[length];
            for (int i = 0; i < length; i++) bytes[i] = (byte)(i % 251);
            int split = Math.Min(65535, length);
            var encoded = PccrcContentInformation.Create([ReadOnlyMemory<byte>.Empty, bytes.AsMemory(0, split), bytes.AsMemory(split)], secret);
            Assert.Equal(vector.GetProperty("encodedLength").GetInt32(), encoded.Length);
            Assert.Equal(vector.GetProperty("encodedSha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(encoded)));
            if (vector.GetProperty("hex").ValueKind == JsonValueKind.String)
                Assert.Equal(Convert.FromHexString(vector.GetProperty("hex").GetString()!), encoded);
        }
        Assert.Throws<ArgumentException>(() => PccrcContentInformation.Create([ReadOnlyMemory<byte>.Empty], secret));
    }

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public void NegotiationAndProjectionPreserveOriginalAndKeepExcludedSizesDistinct(bool instead, bool include)
    {
        var request = new FsshttpbCellRequest { HashOptions = new(1, instead, include),
            SubRequests = { new(RequestTypes.QueryChanges) { RequestId = 1, Data = new QueryChangesSubRequestData() } } };
        Assert.Equal(request.HashOptions, FsshttpbCellRequest.Deserialize(new(request.ToByteArray())).HashOptions);
        var original = Group((new ExGuid(2, Guid.NewGuid()), 1, new byte[] { 1, 2, 3 }));
        var bytes = original.Data!.ToArray();
        var result = ObjectGroupWireHash.Project(original, request.HashOptions, new(new byte[32]));
        Assert.Equal(bytes, original.Data);
        if (!instead && !include) { Assert.Same(original, result); return; }
        var reader = new BinaryReaderEx(result.Data!);
        Assert.Equal(1UL, DataElementWireHash.Deserialize(reader).Schema);
        if (!instead)
        {
            Assert.Equal(bytes, reader.ReadBytes(reader.Remaining));
            return;
        }
        var excluded = ReadExcluded(reader);
        Assert.Equal(3UL, Assert.Single(excluded).DataSize);
        Assert.Throws<InvalidDataException>(() => ObjectGroupDataElement.ParseOpaque(result));
        Assert.Throws<InvalidDataException>(() => ObjectGroupDataElement.Parse(result));
    }

    [Fact]
    public void HashInputUsesUnsignedGuidComponentsAndPartitionOrderRatherThanWireOrder()
    {
        var guid = Guid.Parse("80000000-8000-8000-8000-000000000001");
        var smaller = Guid.Parse("7fffffff-ffff-ffff-ffff-ffffffffffff");
        var items = new[] {
            (new ExGuid(uint.MaxValue, smaller), 0UL, new byte[] { 6 }),
            (new ExGuid(0, guid), ulong.MaxValue, new byte[] { 5 }),
            (new ExGuid(0, guid), 1UL, new byte[] { 4 }),
            (new ExGuid(0, smaller), 0UL, new byte[] { 3 }),
            (new ExGuid(0, Guid.Parse("7fffffff-7fff-ffff-ffff-ffffffffffff")), 0UL, new byte[] { 2 }),
            (new ExGuid(0, Guid.Parse("7fffffff-7fff-7fff-ffff-ffffffffffff")), 0UL, new byte[] { 1 }) };
        var expected = PccrcContentInformation.Create([new byte[] { 1, 2, 3, 4, 5, 6 }], new byte[32]);
        foreach (var order in new[] { items, items.Reverse().ToArray() })
        {
            var hash = ObjectGroupWireHash.Project(Group(order), new(IncludeHashes: true), new(new byte[32]));
            Assert.Equal(expected, DataElementWireHash.Deserialize(new(hash.Data!)).ContentInformation);
        }
    }

    [Fact]
    public void EmptyBlobAbsentAndDisabledGroupsStayFullAndDuplicatesFail()
    {
        var fixture = new GraphFixture([1], blob: true);
        var blob = fixture.Elements.Single(e => e.DataElementExtendedGuid.Equals(fixture.BaseGroup));
        var empty = Group((new ExGuid(1, Guid.NewGuid()), 1, Array.Empty<byte>()));
        foreach (var element in new[] { blob, empty })
            Assert.Same(element, ObjectGroupWireHash.Project(element, new(IncludeHashes: true), new(new byte[32])));
        Assert.Same(blob, ObjectGroupWireHash.Project(blob, null, new(new byte[32])));
        Assert.Same(blob, ObjectGroupWireHash.Project(blob, new(IncludeHashes: true), ProtocolHashingOptions.Disabled));
        var id = new ExGuid(1, Guid.NewGuid());
        Assert.Throws<InvalidDataException>(() => ObjectGroupWireHash.Project(Group((id, 1, new byte[] { 1 }), (id, 1, new byte[] { 2 })), new(IncludeHashes: true), new(new byte[32])));
    }

    [Fact]
    public void NegotiationRejectsMalformedFramingAndUnknownSchemasButIgnoresReservedFlags()
    {
        var request = new FsshttpbCellRequest { HashOptions = new(IncludeHashes: true), SubRequests = { new(RequestTypes.QueryAccess) } };
        var wire = request.ToByteArray();
        int position = FindHashOptions(wire);
        wire[position + 5] |= 0xf3;
        Assert.Equal(new RequestHashOptions(IncludeHashes: true), FsshttpbCellRequest.Deserialize(new(wire)).HashOptions);
        var normal = request.ToByteArray();
        var duplicate = normal[..position].Concat(normal[position..(position + 6)]).Concat(normal[position..]).ToArray();
        Assert.Throws<InvalidDataException>(() => FsshttpbCellRequest.Deserialize(new(duplicate)));
        var unknown = normal.ToArray(); unknown[position + 4] = 5; // compact value 2
        Assert.Throws<InvalidDataException>(() => FsshttpbCellRequest.Deserialize(new(unknown)));
        var compound = normal.ToArray(); compound[position] |= 4;
        Assert.Throws<InvalidDataException>(() => FsshttpbCellRequest.Deserialize(new(compound)));
        var trailing = normal[..(position + 6)].Concat(new byte[] { 0 }).Concat(normal[(position + 6)..]).ToArray();
        var header = new BinaryWriterEx(); new StreamObjectHeaderStart32Bit(StreamObjectTypeHeaderStart.RequestHashOptions, 3).Serialize(header);
        header.ToArray().CopyTo(trailing, position);
        Assert.Throws<InvalidDataException>(() => FsshttpbCellRequest.Deserialize(new(trailing)));
        Assert.Throws<EndOfStreamException>(() => FsshttpbCellRequest.Deserialize(new(normal[..(position + 5)])));
    }

    internal static int FindHashOptions(byte[] wire)
    {
        var writer = new BinaryWriterEx(); new StreamObjectHeaderStart32Bit(StreamObjectTypeHeaderStart.RequestHashOptions, 2).Serialize(writer);
        var marker = writer.ToArray();
        for (int i = 12; i <= wire.Length - marker.Length; i++) if (wire.AsSpan(i, marker.Length).SequenceEqual(marker)) return i;
        throw new Exception("Hash marker absent.");
    }

    private static List<ObjectGroupExcludedData> ReadExcluded(BinaryReaderEx reader)
    {
        Assert.Equal(StreamObjectTypeHeaderStart.ObjectGroupDeclarations, StreamObjectHeaderStart.Parse(reader).Type);
        while (true)
        {
            int position = reader.Position;
            if ((reader.ReadByte() & 3) is 1 or 3) { reader.Position = position; break; }
            reader.Position = position;
            reader.Skip(StreamObjectHeaderStart.Parse(reader).Length);
        }
        Assert.Equal(StreamObjectTypeHeaderEnd.ObjectGroupDeclarations, StreamObjectHeaderEnd.Parse(reader).Type);
        Assert.Equal(StreamObjectTypeHeaderStart.ObjectGroupData, StreamObjectHeaderStart.Parse(reader).Type);
        var list = new List<ObjectGroupExcludedData>();
        while (reader.Remaining > 1) list.Add(ObjectGroupExcludedData.Deserialize(reader));
        Assert.Equal(StreamObjectTypeHeaderEnd.ObjectGroupData, StreamObjectHeaderEnd.Parse(reader).Type);
        return list;
    }

    private static DataElement Group(params (ExGuid Id, ulong Partition, byte[] Data)[] items) => GraphFixture.Element(
        DataElementType.ObjectGroupDataElementData, new(1, Guid.NewGuid()), writer =>
        {
            new StreamObjectHeaderStart32Bit(StreamObjectTypeHeaderStart.ObjectGroupDeclarations, 0).Serialize(writer);
            foreach (var item in items) GraphFixture.Record(writer, StreamObjectTypeHeaderStart.ObjectGroupObjectDeclare, b => {
                item.Id.Serialize(b); new Compact64bitInt(item.Partition).Serialize(b); new Compact64bitInt((ulong)item.Data.Length).Serialize(b);
                new Compact64bitInt(0).Serialize(b); new Compact64bitInt(0).Serialize(b); });
            new StreamObjectHeaderEnd8Bit(StreamObjectTypeHeaderEnd.ObjectGroupDeclarations).Serialize(writer);
            new StreamObjectHeaderStart32Bit(StreamObjectTypeHeaderStart.ObjectGroupData, 0).Serialize(writer);
            foreach (var item in items) GraphFixture.Record(writer, StreamObjectTypeHeaderStart.ObjectGroupObjectData, b => {
                new Compact64bitInt(0).Serialize(b); new Compact64bitInt(0).Serialize(b); new BinaryItem(item.Data).Serialize(b); });
            new StreamObjectHeaderEnd8Bit(StreamObjectTypeHeaderEnd.ObjectGroupData).Serialize(writer);
        });
}
