using CellBridge.FssHttpB;

public sealed class QueryChangesVersioningTests
{
    [Theory]
    [InlineData(0x05, 0x00, 0x00)]
    [InlineData(0x03, 0x00, 0x00)]
    public void PreservesCapturedThreeByteSelectorAsOpaqueToken(byte first, byte second, byte third)
    {
        var wire = new BinaryWriterEx();
        new StreamObjectHeaderStart32Bit(StreamObjectTypeHeaderStart.QueryChangesRequest, 1).Serialize(wire);
        wire.WriteByte(0);
        new StreamObjectHeaderStart16Bit(StreamObjectTypeHeaderStart.QueryChangesVersioning, 3).Serialize(wire);
        wire.WriteBytes([first, second, third]);
        new StreamObjectHeaderEnd16Bit(StreamObjectTypeHeaderEnd.SubRequest).Serialize(wire);

        var reader = new BinaryReaderEx(wire.ToArray());
        var query = QueryChangesSubRequestData.Deserialize(reader);

        Assert.Null(query.Version?.MajorVersion);
        Assert.Null(query.Version?.MinorVersion);
        Assert.Equal(new byte[] { first, second, third }, query.Version!.VersionToken);
        Assert.Equal(2, reader.Remaining); // The enclosing subrequest consumes its end header.
    }

    [Fact]
    public void NumericVersionAndOpaqueTokenRoundTrip()
    {
        var expected = new QueryChangesVersion(2, 3, []);
        var source = new QueryChangesSubRequestData { Version = expected, IncludeCellChanges = true };
        var wire = new BinaryWriterEx();
        source.Serialize(wire);
        new StreamObjectHeaderEnd16Bit(StreamObjectTypeHeaderEnd.SubRequest).Serialize(wire);

        var actual = QueryChangesSubRequestData.Deserialize(new(wire.ToArray()));

        Assert.Equal((uint?)2, actual.Version?.MajorVersion);
        Assert.Equal((uint?)3, actual.Version?.MinorVersion);
        Assert.Equal(expected.VersionToken, actual.Version?.VersionToken);
        Assert.True(actual.IncludeCellChanges);
    }

    [Fact]
    public void OpaqueTokenRoundTripsWithoutInterpretingItsBytes()
    {
        var expected = new QueryChangesVersion(null, null, [0x05, 0x00, 0x00]);
        var source = new QueryChangesSubRequestData { Version = expected };
        var wire = new BinaryWriterEx();
        source.Serialize(wire);
        new StreamObjectHeaderEnd16Bit(StreamObjectTypeHeaderEnd.SubRequest).Serialize(wire);

        var actual = QueryChangesSubRequestData.Deserialize(new(wire.ToArray()));

        Assert.Null(actual.Version?.MajorVersion);
        Assert.Null(actual.Version?.MinorVersion);
        Assert.Equal(expected.VersionToken, actual.Version?.VersionToken);
    }

    [Fact]
    public void RejectsMalformedOrOversizedVersioningPayloads()
    {
        var truncated = new BinaryWriterEx();
        new StreamObjectHeaderStart32Bit(StreamObjectTypeHeaderStart.QueryChangesRequest, 1).Serialize(truncated);
        truncated.WriteByte(0);
        new StreamObjectHeaderStart16Bit(StreamObjectTypeHeaderStart.QueryChangesVersioning, 3).Serialize(truncated);
        truncated.WriteBytes([0x05, 0x00]);
        Assert.Throws<EndOfStreamException>(() => QueryChangesSubRequestData.Deserialize(new(truncated.ToArray())));

        var wideHeader = new BinaryWriterEx();
        new StreamObjectHeaderStart32Bit(StreamObjectTypeHeaderStart.QueryChangesRequest, 1).Serialize(wideHeader);
        wideHeader.WriteByte(0);
        new StreamObjectHeaderStart32Bit(StreamObjectTypeHeaderStart.QueryChangesVersioning, 3).Serialize(wideHeader);
        wideHeader.WriteBytes([0x05, 0x00, 0x00]);
        new StreamObjectHeaderEnd16Bit(StreamObjectTypeHeaderEnd.SubRequest).Serialize(wideHeader);
        Assert.Throws<InvalidDataException>(() => QueryChangesSubRequestData.Deserialize(new(wideHeader.ToArray())));

        var numeric = new BinaryWriterEx();
        numeric.WriteUInt32(2);
        numeric.WriteUInt32(0);
        Assert.Equal(2U, ParseVersioning(numeric.Length, numeric.ToArray()).Version?.MajorVersion);
    }

    [Fact]
    public void RejectsDuplicateVersioningObjects()
    {
        var wire = new BinaryWriterEx();
        new StreamObjectHeaderStart32Bit(StreamObjectTypeHeaderStart.QueryChangesRequest, 1).Serialize(wire);
        wire.WriteByte(0);
        for (int i = 0; i < 2; i++)
        {
            new StreamObjectHeaderStart16Bit(StreamObjectTypeHeaderStart.QueryChangesVersioning, 3).Serialize(wire);
            wire.WriteBytes([0x05, 0x00, 0x00]);
        }
        new StreamObjectHeaderEnd16Bit(StreamObjectTypeHeaderEnd.SubRequest).Serialize(wire);

        Assert.Throws<InvalidDataException>(() => QueryChangesSubRequestData.Deserialize(new(wire.ToArray())));
    }

    private static QueryChangesSubRequestData ParseVersioning(int length, byte[] payload)
    {
        var wire = new BinaryWriterEx();
        new StreamObjectHeaderStart32Bit(StreamObjectTypeHeaderStart.QueryChangesRequest, 1).Serialize(wire);
        wire.WriteByte(0);
        new StreamObjectHeaderStart16Bit(StreamObjectTypeHeaderStart.QueryChangesVersioning, length).Serialize(wire);
        wire.WriteBytes(payload);
        new StreamObjectHeaderEnd16Bit(StreamObjectTypeHeaderEnd.SubRequest).Serialize(wire);
        return QueryChangesSubRequestData.Deserialize(new(wire.ToArray()));
    }
}
