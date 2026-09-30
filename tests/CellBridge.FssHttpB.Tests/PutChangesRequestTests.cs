using CellBridge.FssHttpB;

namespace CellBridge.FssHttpB.Tests;

public sealed class PutChangesRequestTests
{
    [Fact]
    public void QueryChangesFlagsAndByteBudgetArePayloadFields()
    {
        var request = new FsshttpbCellRequest();
        request.SubRequests.Add(new FsshttpbCellSubRequest(RequestTypes.QueryChanges)
        {
            RequestId = 1,
            Data = new QueryChangesSubRequestData
            {
                IncludeStorageManifest = true,
                IncludeCellChanges = true,
                MaxDataElements = 16777216,
            },
        });
        var reader = new BinaryReaderEx(request.ToByteArray());
        var parsed = Assert.IsType<QueryChangesSubRequestData>(Assert.Single(
            FsshttpbCellRequest.Deserialize(reader).SubRequests).Data);
        Assert.True(parsed.IncludeStorageManifest);
        Assert.True(parsed.IncludeCellChanges);
        Assert.Equal(16777216UL, parsed.MaxDataElements);
        Assert.Equal(0, reader.Remaining);
    }

    [Fact]
    public void FixedFieldsPreserveCoherencyAndAuthors()
    {
        var request = new PutChangesSubRequestData
        {
            StorageIndex = new ExGuid(3, Guid.NewGuid()),
            ExpectedStorageIndex = new ExGuid(2, Guid.NewGuid()),
            Flags = 0x48,
            ContentVersionCoherencyCheck = [1, 2, 3],
            AuthorLogins = ["dev\\user", "editor"],
        };
        var writer = new BinaryWriterEx();
        request.Serialize(writer);
        var reader = new BinaryReaderEx(writer.ToArray());
        var parsed = PutChangesSubRequestData.Deserialize(reader);
        Assert.Equal(request.StorageIndex, parsed.StorageIndex);
        Assert.Equal(request.ExpectedStorageIndex, parsed.ExpectedStorageIndex);
        Assert.Equal(request.Flags, parsed.Flags);
        Assert.Equal(request.ContentVersionCoherencyCheck, parsed.ContentVersionCoherencyCheck);
        Assert.Equal(request.AuthorLogins, parsed.AuthorLogins);
        Assert.Equal(0, reader.Remaining);
    }

    [Fact]
    public void CompoundSubRequestKeepsFixedPreambleSeparateFromPutFields()
    {
        var lockId = Guid.NewGuid();
        var request = new FsshttpbCellRequest();
        request.SubRequests.Add(new FsshttpbCellSubRequest(RequestTypes.PutChanges)
        {
            RequestId = 17,
            Data = new PutChangesSubRequestData { LockId = lockId, Flags = 0x48 },
        });
        var reader = new BinaryReaderEx(request.ToByteArray());
        var parsed = FsshttpbCellRequest.Deserialize(reader);
        var operation = Assert.Single(parsed.SubRequests);
        Assert.Equal(17UL, operation.RequestId);
        Assert.Equal(lockId, Assert.IsType<PutChangesSubRequestData>(operation.Data).LockId);
        Assert.Equal(0, reader.Remaining);
    }

    [Fact]
    public void TruncatedFixedFieldsCannotConsumeNextStreamObject()
    {
        var writer = new BinaryWriterEx();
        new StreamObjectHeaderStart32Bit(StreamObjectTypeHeaderStart.PutChangesRequest, 1).Serialize(writer);
        writer.WriteByte(0);
        writer.WriteBytes(new byte[32]);
        var reader = new BinaryReaderEx(writer.ToArray());
        Assert.Throws<EndOfStreamException>(() => PutChangesSubRequestData.Deserialize(reader));
        Assert.Equal(32, reader.Remaining);
    }
}

