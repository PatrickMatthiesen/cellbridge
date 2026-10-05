using CellBridge.FssHttpB;

public sealed class AllocateExtendedGuidRangeTests
{
    [Theory]
    [InlineData(0UL)]
    [InlineData(1UL)]
    [InlineData(100_000UL)]
    [InlineData(ulong.MaxValue)]
    public void RequestRoundTripsAllCompactCounts(ulong count)
    {
        var request = new FsshttpbCellRequest();
        request.SubRequests.Add(new(RequestTypes.AllocateExtendedGuidRange)
        { RequestId = 17, Data = new AllocateExtendedGuidRangeSubRequestData { RequestIdCount = count } });
        var decoded = FsshttpbCellRequest.Deserialize(new BinaryReaderEx(request.ToByteArray()));
        var operation = Assert.Single(decoded.SubRequests);
        Assert.Equal(17UL, operation.RequestId);
        Assert.Equal(count, Assert.IsType<AllocateExtendedGuidRangeSubRequestData>(operation.Data).RequestIdCount);
    }

    [Fact]
    public void RequestIgnoresReservedByteButRejectsTrailingDataAndCompoundHeader()
    {
        var writer = new BinaryWriterEx();
        new AllocateExtendedGuidRangeSubRequestData { RequestIdCount = 10 }.Serialize(writer);
        byte[] bytes = writer.ToArray();
        Assert.Equal(0, bytes[^1]);
        bytes[^1] = 0xFF;
        Assert.Equal(10UL, AllocateExtendedGuidRangeSubRequestData.Deserialize(new BinaryReaderEx(bytes)).RequestIdCount);
        var trailing = new BinaryWriterEx();
        new StreamObjectHeaderStart32Bit(StreamObjectTypeHeaderStart.AllocateExtendedGUIDRangeRequest, 3).Serialize(trailing);
        trailing.WriteBytes([10 << 1 | 1, 0, 99]);
        Assert.Throws<InvalidDataException>(() => AllocateExtendedGuidRangeSubRequestData.Deserialize(new BinaryReaderEx(trailing.ToArray())));
        bytes[0] |= 4;
        Assert.Throws<InvalidDataException>(() => AllocateExtendedGuidRangeSubRequestData.Deserialize(new BinaryReaderEx(bytes)));
    }

    [Theory]
    [InlineData(0UL, 1000UL)]
    [InlineData(999UL, 1000UL)]
    [InlineData(0UL, 100_000UL)]
    public void ResponseRoundTripsExclusiveBounds(ulong minimum, ulong maximum)
    {
        Guid guid = Guid.NewGuid();
        var response = new FsshttpbResponse();
        response.SubResponses.Add(new() { RequestType = RequestTypes.AllocateExtendedGuidRange,
            RequestId = 34, Data = new AllocateExtendedGuidRangeSubResponseData
            { GuidComponent = guid, IntegerRangeMin = minimum, IntegerRangeMax = maximum } });
        var decoded = FsshttpbResponse.Deserialize(new BinaryReaderEx(response.ToByteArray()));
        var data = Assert.IsType<AllocateExtendedGuidRangeSubResponseData>(Assert.Single(decoded.SubResponses).Data);
        Assert.Equal(guid, data.GuidComponent);
        Assert.Equal(minimum, data.IntegerRangeMin);
        Assert.Equal(maximum, data.IntegerRangeMax);
    }

    [Theory]
    [InlineData(0UL, 999UL)]
    [InlineData(1000UL, 1000UL)]
    [InlineData(0UL, 100_001UL)]
    public void ResponseRejectsInvalidBoundsOnReadAndWrite(ulong minimum, ulong maximum)
    {
        var data = new AllocateExtendedGuidRangeSubResponseData
        { GuidComponent = Guid.NewGuid(), IntegerRangeMin = minimum, IntegerRangeMax = maximum };
        Assert.Throws<InvalidDataException>(() => data.Serialize(new BinaryWriterEx()));
        var body = new BinaryWriterEx();
        body.WriteBytes(data.GuidComponent.ToByteArray());
        new Compact64bitInt(minimum).Serialize(body);
        new Compact64bitInt(maximum).Serialize(body);
        var wire = new BinaryWriterEx();
        new StreamObjectHeaderStart32Bit(StreamObjectTypeHeaderStart.AllocateExtendedGUIDRangeResponse, body.Length).Serialize(wire);
        wire.WriteBytes(body.ToArray());
        Assert.Throws<InvalidDataException>(() => AllocateExtendedGuidRangeSubResponseData.Deserialize(new BinaryReaderEx(wire.ToArray())));
    }

    [Fact]
    public void RequestRejectsTruncatedFixedBody()
    {
        var writer = new BinaryWriterEx();
        new AllocateExtendedGuidRangeSubRequestData { RequestIdCount = 100_000 }.Serialize(writer);
        byte[] wire = writer.ToArray();
        for (int i = 0; i < wire.Length; i++)
            Assert.ThrowsAny<Exception>(() => AllocateExtendedGuidRangeSubRequestData.Deserialize(new BinaryReaderEx(wire[..i])));
    }
}
