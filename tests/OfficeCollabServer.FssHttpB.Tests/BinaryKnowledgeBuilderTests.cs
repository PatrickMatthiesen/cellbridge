using OfficeCollabServer.FssHttpB;

namespace OfficeCollabServer.FssHttpB.Tests;

public sealed class BinaryKnowledgeBuilderTests
{
    [Fact]
    public void FromElements_UsesSerialRangesAndWaterline()
    {
        var serialGuid = Guid.NewGuid();
        var elements = new[]
        {
            new DataElement(DataElementType.ObjectGroupDataElementData, new ExGuid(1, Guid.NewGuid()), new SerialNumber(serialGuid, 2)),
            new DataElement(DataElementType.ObjectGroupDataElementData, new ExGuid(2, Guid.NewGuid()), new SerialNumber(serialGuid, 3)),
            new DataElement(DataElementType.ObjectGroupDataElementData, new ExGuid(3, Guid.NewGuid()), new SerialNumber(serialGuid, 7)),
            new DataElement(DataElementType.ObjectGroupDataElementData, new ExGuid(4, Guid.NewGuid()), SerialNumber.Null),
        };

        byte[] bytes = BinaryKnowledgeBuilder.FromElements(elements, new ExGuid(9, Guid.NewGuid()), 12);
        var reader = new BinaryReaderEx(bytes);
        var knowledge = StreamObjectHeaderStart.Parse(reader);
        Assert.Equal(StreamObjectTypeHeaderStart.Knowledge, knowledge.Type);

        var specialized = StreamObjectHeaderStart.Parse(reader);
        Assert.Equal(StreamObjectTypeHeaderStart.SpecializedKnowledge, specialized.Type);
        Assert.Equal(Guid.Parse("327A35F6-0761-4414-9686-51E900667A4D"), new Guid(reader.ReadBytes(16)));
        var cell = StreamObjectHeaderStart.Parse(reader);
        Assert.Equal(StreamObjectTypeHeaderStart.CellKnowledge, cell.Type);
        var first = StreamObjectHeaderStart.Parse(reader);
        Assert.Equal(StreamObjectTypeHeaderStart.CellKnowledgeRange, first.Type);
        Assert.Equal(serialGuid, new Guid(reader.ReadBytes(16)));
        Assert.Equal(2UL, Compact64bitInt.Deserialize(reader).Value);
        Assert.Equal(3UL, Compact64bitInt.Deserialize(reader).Value);
        var second = StreamObjectHeaderStart.Parse(reader);
        Assert.Equal(StreamObjectTypeHeaderStart.CellKnowledgeRange, second.Type);
        Assert.Equal(serialGuid, new Guid(reader.ReadBytes(16)));
        Assert.Equal(7UL, Compact64bitInt.Deserialize(reader).Value);
        Assert.Equal(7UL, Compact64bitInt.Deserialize(reader).Value);
    }

    [Fact]
    public void PutChanges_PreservesAdditionalFlagsAndCheckForIdReuse()
    {
        var request = new FsshttpbCellRequest();
        request.SubRequests.Add(new FsshttpbCellSubRequest(RequestTypes.PutChanges)
        {
            RequestId = 1,
            Data = new PutChangesSubRequestData
            {
                Flags = 0x49,
                HasAdditionalFlags = true,
                AdditionalFlagsBits = 0x0004,
                AdditionalFlagsTrailingBytes = [0],
            },
        });

        var decoded = FsshttpbCellRequest.Deserialize(new BinaryReaderEx(request.ToByteArray()));
        var put = Assert.IsType<PutChangesSubRequestData>(decoded.SubRequests[0].Data);
        Assert.True(put.HasAdditionalFlags);
        Assert.True(put.CheckForIdReuse);
        Assert.Equal((ushort)0x0004, put.AdditionalFlagsBits);
        Assert.Equal(new byte[] { 0 }, put.AdditionalFlagsTrailingBytes);
    }
}
