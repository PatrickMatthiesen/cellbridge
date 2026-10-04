namespace CellBridge.FssHttpB.Tests;

public sealed class BinaryParsingBoundsTests
{
    [Theory]
    [InlineData(33)]
    [InlineData(34)]
    [InlineData(1000)]
    public void PutChangesResponseNestingIncludesOuterKnowledgeObject(int levels)
    {
        var wire = new BinaryWriterEx();
        new StreamObjectHeaderStart16Bit(StreamObjectTypeHeaderStart.Knowledge, 0).Serialize(wire);
        for (int i = 1; i < levels; i++)
            new StreamObjectHeaderStart16Bit(StreamObjectTypeHeaderStart.ObjectGroupDeclarations, 0).Serialize(wire);
        for (int i = 1; i < levels; i++)
            new StreamObjectHeaderEnd8Bit(StreamObjectTypeHeaderEnd.ObjectGroupDeclarations).Serialize(wire);
        new StreamObjectHeaderEnd8Bit(StreamObjectTypeHeaderEnd.Knowledge).Serialize(wire);
        var bytes = wire.ToArray();
        var reader = new BinaryReaderEx(bytes);
        if (levels == 33)
        {
            Assert.Equal(bytes, PutChangesSubResponseData.Deserialize(reader).KnowledgeBytes);
            Assert.Equal(0, reader.Remaining);
        }
        else
            Assert.Contains("nesting limit", Assert.Throws<InvalidDataException>(() =>
                PutChangesSubResponseData.Deserialize(reader)).Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PutChangesResponseChecksHugeLengthsBeforeOffsetArithmetic(bool child)
    {
        var wire = new BinaryWriterEx();
        if (child)
            new StreamObjectHeaderStart16Bit(StreamObjectTypeHeaderStart.Knowledge, 0).Serialize(wire);
        new StreamObjectHeaderStart32Bit(child ? StreamObjectTypeHeaderStart.UserAgentGUID :
            StreamObjectTypeHeaderStart.Knowledge, int.MaxValue).Serialize(wire);
        Assert.Throws<EndOfStreamException>(() => PutChangesSubResponseData.Deserialize(new(wire.ToArray())));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PutChangesResponseRejectsMissingOrWrongKnowledgeEnd(bool wrongEnd)
    {
        var wire = new BinaryWriterEx();
        new StreamObjectHeaderStart16Bit(StreamObjectTypeHeaderStart.Knowledge, 0).Serialize(wire);
        if (wrongEnd)
            new StreamObjectHeaderEnd8Bit(StreamObjectTypeHeaderEnd.ObjectGroupDeclarations).Serialize(wire);
        if (wrongEnd)
            Assert.Throws<InvalidDataException>(() => PutChangesSubResponseData.Deserialize(new(wire.ToArray())));
        else
            Assert.Throws<EndOfStreamException>(() => PutChangesSubResponseData.Deserialize(new(wire.ToArray())));
    }

    [Theory]
    [InlineData(0x80000000UL)]
    [InlineData(0x100000000UL)]
    [InlineData(0x100000001UL)]
    [InlineData(ulong.MaxValue)]
    public void LargeLengthsCannotWrapIntoSupportedLengths(ulong length)
    {
        var header = new StreamObjectHeaderStart32Bit(StreamObjectTypeHeaderStart.UserAgentGUID,
            StreamObjectHeaderStart.LargeLengthSentinel) { LargeLength = new(length) };
        var wire = new BinaryWriterEx();
        header.Serialize(wire);
        var error = Assert.Throws<InvalidDataException>(() => StreamObjectHeaderStart.Parse(new(wire.ToArray())));
        Assert.Contains("length", error.Message);
    }

    [Fact]
    public void MaximumSupportedHeaderLengthRemainsRepresentable()
    {
        var wire = new BinaryWriterEx();
        new StreamObjectHeaderStart32Bit(StreamObjectTypeHeaderStart.UserAgentGUID, int.MaxValue).Serialize(wire);
        Assert.Equal(int.MaxValue, StreamObjectHeaderStart.Parse(new(wire.ToArray())).Length);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new StreamObjectHeaderStart16Bit(StreamObjectTypeHeaderStart.UserAgentGUID, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new StreamObjectHeaderStart32Bit(StreamObjectTypeHeaderStart.UserAgentGUID, -1));
    }

    [Theory]
    [InlineData(FsshttpbSerializationProfile.Current, 33)]
    [InlineData(FsshttpbSerializationProfile.Current, 34)]
    [InlineData(FsshttpbSerializationProfile.SharePoint13_11, 33)]
    [InlineData(FsshttpbSerializationProfile.SharePoint13_11, 34)]
    public void DataElementNestingIsBoundedForBothWireProfiles(FsshttpbSerializationProfile profile, int levels)
    {
        var element = NestedElement(levels);
        var wire = new BinaryWriterEx();
        element.Serialize(wire, profile);
        var reader = new BinaryReaderEx(wire.ToArray());
        if (levels == 33)
        {
            Assert.Equal(element.Data, DataElement.Deserialize(reader).Data);
            Assert.Equal(0, reader.Remaining);
        }
        else
        {
            var error = Assert.Throws<InvalidDataException>(() => DataElement.Deserialize(reader));
            Assert.Contains("nesting limit", error.Message);
        }
    }

    [Fact]
    public void DataElementRejectsMismatchedEndAndTruncatedBody()
    {
        var element = NestedElement(1);
        element.Data![^1] = (byte)((int)StreamObjectTypeHeaderEnd.ObjectGroupData << 2 | 1);
        var wire = new BinaryWriterEx();
        element.Serialize(wire);
        Assert.Throws<InvalidDataException>(() => DataElement.Deserialize(new(wire.ToArray())));

        var body = new BinaryWriterEx();
        new StreamObjectHeaderStart32Bit(StreamObjectTypeHeaderStart.UserAgentGUID, 64).Serialize(body);
        element.Data = body.ToArray();
        wire = new BinaryWriterEx();
        element.Serialize(wire);
        var error = Assert.Throws<InvalidDataException>(() => DataElement.Deserialize(new(wire.ToArray())));
        Assert.Contains("remaining payload", error.Message);
    }

    [Fact]
    public void DataElementRejectsHugeMetadataLengthBeforeOffsetArithmetic()
    {
        var wire = new BinaryWriterEx();
        new StreamObjectHeaderStart32Bit(StreamObjectTypeHeaderStart.DataElement, int.MaxValue).Serialize(wire);
        var error = Assert.Throws<InvalidDataException>(() => DataElement.Deserialize(new(wire.ToArray())));
        Assert.Contains("metadata length", error.Message);
    }

    [Fact]
    public void DiagnosticParserReportsExcessiveNestingWithoutLosingLaterElements()
    {
        var response = new FsshttpbResponse
        {
            DataElementPackage = new() { DataElements = { NestedElement(1000), NestedElement(1) } },
        };
        var inspected = FsshttpbResponseInspector.Inspect(response.ToByteArray());
        Assert.Equal(2, inspected.DataElements.Count);
        Assert.Contains(inspected.Issues, issue => issue.Contains("nesting limit"));
        Assert.Single(inspected.DataElements[1].Objects);
    }

    private static DataElement NestedElement(int levels)
    {
        var payload = new BinaryWriterEx();
        for (int i = 0; i < levels; i++)
            new StreamObjectHeaderStart16Bit(StreamObjectTypeHeaderStart.ObjectGroupDeclarations, 0).Serialize(payload);
        for (int i = 0; i < levels; i++)
            new StreamObjectHeaderEnd8Bit(StreamObjectTypeHeaderEnd.ObjectGroupDeclarations).Serialize(payload);
        return new((DataElementType)99, new(1, Guid.NewGuid()), SerialNumber.Null) { Data = payload.ToArray() };
    }
}
