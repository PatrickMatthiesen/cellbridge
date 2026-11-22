using CellBridge.FssHttpB;

namespace CellBridge.FssHttpB.Tests;

public sealed class ResponseCodecTests
{
    [Theory]
    [InlineData(FsshttpbSerializationProfile.Current)]
    [InlineData(FsshttpbSerializationProfile.SharePoint13_11)]
    public void CompleteBufferRoundTripsTypedOperationsAndPackage(FsshttpbSerializationProfile profile)
    {
        var owner = Guid.NewGuid();
        var index = new ExGuid(77, owner);
        var response = EditorsTablePartitionBuilder.BuildQueryChangesResponse(1, [], knowledgeSequence: 19);
        var accessError = new ResponseError(ErrorType.HResult, 0x80070005, "Read only");
        response.SubResponses.AddRange([
            new FsshttpbSubResponse
            {
                RequestId = 2, RequestType = RequestTypes.QueryAccess,
                Data = new QueryAccessSubResponseData { WriteAccessError = accessError },
            },
            new FsshttpbSubResponse
            {
                RequestId = 3, RequestType = RequestTypes.PutChanges,
                Data = new PutChangesSubResponseData
                {
                    PutChangesResponse = new PutChangesResponse { AppliedStorageIndexID = index, DataElementAdded = { index } },
                    KnowledgeBytes = BinaryKnowledgeBuilder.FromElements([], index, 5),
                },
            },
            new FsshttpbSubResponse
            {
                RequestId = 4, RequestType = RequestTypes.AllocateExtendedGuidRange,
                Data = new AllocateExtendedGuidRangeSubResponseData { GuidComponent = owner, IntegerRangeMin = 5, IntegerRangeMax = 1008 },
            },
            new FsshttpbSubResponse
            {
                RequestId = 5, RequestType = RequestTypes.PutChanges,
                Status = true, Error = new ResponseError(ErrorType.Cell, (ulong)CellErrorCode.CoherencyFailure),
            },
        ]);

        byte[] wire = response.ToByteArray(profile);
        var decoded = FsshttpbResponse.FromBytes(wire);
        Assert.Equal(wire, decoded.ToByteArray(profile));
        Assert.Equal(response.DataElementPackage!.DataElements.Count, decoded.DataElementPackage!.DataElements.Count);
        Assert.Equal(new ulong[] { 1, 2, 3, 4, 5 }, decoded.SubResponses.Select(s => s.RequestId));
        Assert.Equal(19UL, Assert.IsType<QueryChangesSubResponseData>(decoded.SubResponses[0].Data).CellKnowledgeTo);
        var access = Assert.IsType<QueryAccessSubResponseData>(decoded.SubResponses[1].Data);
        Assert.Null(access.ReadAccessError);
        Assert.Equal(accessError.ErrorCode, access.WriteAccessError!.ErrorCode);
        var put = Assert.IsType<PutChangesSubResponseData>(decoded.SubResponses[2].Data);
        Assert.Equal(index, put.PutChangesResponse!.AppliedStorageIndexID);
        Assert.Equal(index, Assert.Single(put.PutChangesResponse.DataElementAdded));
        var allocation = Assert.IsType<AllocateExtendedGuidRangeSubResponseData>(decoded.SubResponses[3].Data);
        Assert.Equal(owner, allocation.GuidComponent);
        Assert.Equal(1008UL, allocation.IntegerRangeMax);
        Assert.True(decoded.SubResponses[4].Status);
        Assert.Equal((ulong)CellErrorCode.CoherencyFailure, decoded.SubResponses[4].Error!.ErrorCode);
    }

    [Fact]
    public void CompleteBufferRejectsTrailingBytesAndIncorrectResponseLength()
    {
        byte[] wire = new FsshttpbResponse().ToByteArray();
        byte[] padded = [..wire, 0xFF];
        Assert.Throws<InvalidDataException>(() => FsshttpbResponse.FromBytes(padded));

        var reader = new BinaryReaderEx(padded);
        FsshttpbResponse.Deserialize(reader);
        Assert.Equal(1, reader.Remaining);

        wire[14] ^= 0b00000010; // Change the length in the 32-bit response header.
        Assert.Throws<InvalidDataException>(() => FsshttpbResponse.FromBytes(wire));
    }
}
