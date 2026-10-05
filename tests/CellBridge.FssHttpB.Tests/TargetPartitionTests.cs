namespace CellBridge.FssHttpB.Tests;

public sealed class TargetPartitionTests
{
    [Theory]
    [InlineData(15, false)]
    [InlineData(17, false)]
    [InlineData(16, true)]
    public void MalformedSelectorFramingIsRejectedBeforeOperation(int length, bool compound)
    {
        var writer = new BinaryWriterEx();
        new StreamObjectHeaderStart32Bit(StreamObjectTypeHeaderStart.SubRequest, 3).Serialize(writer);
        writer.WriteBytes([3, 3, 0]); // Request 1, QueryAccess, priority 0.
        var target = new StreamObjectHeaderStart32Bit(StreamObjectTypeHeaderStart.TargetPartitionId, length)
            { Compound = compound ? 1 : 0 };
        target.Serialize(writer); writer.WriteBytes(new byte[length]);
        new StreamObjectHeaderEnd16Bit(StreamObjectTypeHeaderEnd.SubRequest).Serialize(writer);
        Assert.Throws<InvalidDataException>(() => FsshttpbCellSubRequest.Deserialize(new(writer.ToArray())));
    }

    [Theory]
    [InlineData(RequestTypes.QueryAccess)]
    [InlineData(RequestTypes.QueryChanges)]
    [InlineData(RequestTypes.PutChanges)]
    [InlineData(RequestTypes.AllocateExtendedGuidRange)]
    public void SelectorPrecedesOperationDataAndPreservesAlignment(RequestTypes type)
    {
        foreach (var target in new Guid?[] { null, Guid.Empty, Guid.NewGuid() })
        {
            ISubRequestData? data = type switch
            {
                RequestTypes.QueryChanges => new QueryChangesSubRequestData(),
                RequestTypes.PutChanges => new PutChangesSubRequestData(),
                RequestTypes.AllocateExtendedGuidRange => new AllocateExtendedGuidRangeSubRequestData { RequestIdCount = 1 },
                _ => null,
            };
            var request = new FsshttpbCellSubRequest(type) { RequestId = 3, TargetPartitionId = target, Data = data };
            var writer = new BinaryWriterEx(); request.Serialize(writer);
            var reader = new BinaryReaderEx(writer.ToArray());
            var parsed = FsshttpbCellSubRequest.Deserialize(reader);
            Assert.Equal(target, parsed.TargetPartitionId);
            Assert.Equal(type, parsed.RequestType);
            Assert.Equal(0, reader.Remaining);
        }
    }

    [Theory]
    [InlineData(RequestTypes.QueryAccess, false)]
    [InlineData(RequestTypes.QueryChanges, false)]
    [InlineData(RequestTypes.PutChanges, false)]
    [InlineData(RequestTypes.QueryChanges, true)]
    [InlineData(RequestTypes.PutChanges, true)]
    public void DuplicateAndMisplacedSelectorsAreRejected(RequestTypes type, bool misplaced)
    {
        var writer = new BinaryWriterEx();
        var preamble = new BinaryWriterEx();
        new Compact64bitInt(1).Serialize(preamble); new Compact64bitInt((ulong)type).Serialize(preamble); new Compact64bitInt(0).Serialize(preamble);
        new StreamObjectHeaderStart32Bit(StreamObjectTypeHeaderStart.SubRequest, preamble.Length).Serialize(writer);
        writer.WriteBytes(preamble.ToArray());
        if (!misplaced) WriteTarget();
        if (misplaced)
        {
            if (type == RequestTypes.QueryChanges) new QueryChangesSubRequestData().Serialize(writer);
            else new PutChangesSubRequestData().Serialize(writer);
        }
        WriteTarget();
        new StreamObjectHeaderEnd16Bit(StreamObjectTypeHeaderEnd.SubRequest).Serialize(writer);
        Assert.Throws<InvalidDataException>(() => FsshttpbCellSubRequest.Deserialize(new(writer.ToArray())));
        void WriteTarget()
        {
            new StreamObjectHeaderStart32Bit(StreamObjectTypeHeaderStart.TargetPartitionId, 16).Serialize(writer);
            writer.WriteBytes(Guid.NewGuid().ToByteArray());
        }
    }
}
