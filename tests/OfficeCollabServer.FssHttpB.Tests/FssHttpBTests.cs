using OfficeCollabServer.FssHttpB;

namespace OfficeCollabServer.FssHttpB.Tests;

public class Compact64bitIntTests
{
    [Theory]
    [InlineData(0UL, 1)]
    [InlineData(1UL, 1)]
    [InlineData(0x7FUL, 1)]
    [InlineData(0x80UL, 2)]
    [InlineData(0x3FFFUL, 2)]
    [InlineData(0x4000UL, 3)]
    [InlineData(0x1FFFFFUL, 3)]
    [InlineData(0x200000UL, 4)]
    [InlineData(0xFFFFFFFUL, 4)]
    [InlineData(0x100000000UL, 5)]
    [InlineData(0x7FFFFFFFFUL, 5)]
    [InlineData(0x8000000000UL, 6)]
    [InlineData(0x3FFFFFFFFFFUL, 6)]
    [InlineData(0x40000000000UL, 7)]
    [InlineData(0x1FFFFFFFFFFFFUL, 7)]
    [InlineData(0x2000000000000UL, 9)]
    [InlineData(0xFFFFFFFFFFFFFFFFUL, 9)]
    public void RoundTrips_WithExpectedEncodedSize(ulong value, int expectedSize)
    {
        var writer = new BinaryWriterEx();
        new Compact64bitInt(value).Serialize(writer);
        byte[] bytes = writer.ToArray();

        Assert.Equal(expectedSize, bytes.Length);

        var reader = new BinaryReaderEx(bytes);
        var decoded = Compact64bitInt.Deserialize(reader);
        Assert.Equal(value, decoded.Value);
        Assert.Equal(bytes.Length, reader.Position);
    }
}

public class ExGuidTests
{
    [Fact]
    public void NullEncoding_IsSingleZeroByte()
    {
        var writer = new BinaryWriterEx();
        ExGuid.Null.Serialize(writer);
        byte[] bytes = writer.ToArray();

        Assert.Equal(1, bytes.Length);
        Assert.Equal(0x00, bytes[0]);

        var decoded = ExGuid.Deserialize(new BinaryReaderEx(bytes));
        Assert.True(decoded.IsNull);
    }

    [Theory]
    [InlineData(0u, 17)]
    [InlineData(0x1Fu, 17)]
    [InlineData(0x20u, 18)]
    [InlineData(0x3FFu, 18)]
    [InlineData(0x400u, 19)]
    [InlineData(0x1FFFFu, 19)]
    [InlineData(0x20000u, 21)]
    [InlineData(0xFFFFFFFFu, 21)]
    public void RoundTrips_WithExpectedEncodedSize(uint value, int expectedSize)
    {
        var guid = Guid.Parse("E731B87E-DD45-44AA-AB80-0C75FBD1530E");
        var writer = new BinaryWriterEx();
        new ExGuid(value, guid).Serialize(writer);
        byte[] bytes = writer.ToArray();

        Assert.Equal(expectedSize, bytes.Length);

        var decoded = ExGuid.Deserialize(new BinaryReaderEx(bytes));
        Assert.Equal(value, decoded.Value);
        Assert.Equal(guid, decoded.Guid);
    }

    [Fact]
    public void Equality_Works()
    {
        var guid = Guid.NewGuid();
        Assert.Equal(new ExGuid(5, guid), new ExGuid(5, guid));
        Assert.NotEqual(new ExGuid(5, guid), new ExGuid(6, guid));
    }
}

public class SerialNumberTests
{
    [Fact]
    public void NullEncoding_IsSingleZeroByte()
    {
        var writer = new BinaryWriterEx();
        SerialNumber.Null.Serialize(writer);
        byte[] bytes = writer.ToArray();

        Assert.Equal(1, bytes.Length);
        Assert.True(SerialNumber.Deserialize(new BinaryReaderEx(bytes)).IsNull);
    }

    [Fact]
    public void RoundTrips()
    {
        var guid = Guid.NewGuid();
        var writer = new BinaryWriterEx();
        new SerialNumber(guid, 0x123456789ABCDEF0).Serialize(writer);
        byte[] bytes = writer.ToArray();

        Assert.Equal(25, bytes.Length);

        var decoded = SerialNumber.Deserialize(new BinaryReaderEx(bytes));
        Assert.Equal(guid, decoded.Guid);
        Assert.Equal(0x123456789ABCDEF0UL, decoded.Value);
    }
}

public class StreamObjectHeaderTests
{
    [Fact]
    public void Header16Bit_RoundTrips()
    {
        // 16-bit headers carry a 6-bit type, so only types <= 0x3F fit.
        var header = new StreamObjectHeaderStart16Bit(StreamObjectTypeHeaderStart.DataElementPackage, 16);
        var writer = new BinaryWriterEx();
        header.Serialize(writer);
        byte[] bytes = writer.ToArray();

        Assert.Equal(2, bytes.Length);

        var decoded = StreamObjectHeaderStart.Parse(new BinaryReaderEx(bytes));
        Assert.IsType<StreamObjectHeaderStart16Bit>(decoded);
        Assert.Equal(StreamObjectTypeHeaderStart.DataElementPackage, decoded.Type);
        Assert.Equal(16, decoded.Length);
        Assert.Equal(2, decoded.HeaderSize);
    }

    [Fact]
    public void Header32Bit_RoundTrips()
    {
        var header = new StreamObjectHeaderStart32Bit(StreamObjectTypeHeaderStart.SubRequest, 100);
        var writer = new BinaryWriterEx();
        header.Serialize(writer);
        byte[] bytes = writer.ToArray();

        Assert.Equal(4, bytes.Length);

        var decoded = StreamObjectHeaderStart.Parse(new BinaryReaderEx(bytes));
        Assert.IsType<StreamObjectHeaderStart32Bit>(decoded);
        Assert.Equal(StreamObjectTypeHeaderStart.SubRequest, decoded.Type);
        Assert.Equal(100, decoded.Length);
        Assert.Equal(4, decoded.HeaderSize);
    }

    [Fact]
    public void Header32Bit_LargeLength_UsesCompactInt()
    {
        const int length = 40000;
        var header = new StreamObjectHeaderStart32Bit(StreamObjectTypeHeaderStart.DataElement, length);
        var writer = new BinaryWriterEx();
        header.Serialize(writer);
        byte[] bytes = writer.ToArray();

        // 4-byte header + 3-byte compact large length (40000 needs 42 bits... actually 3 bytes).
        Assert.Equal(7, bytes.Length);

        var decoded = StreamObjectHeaderStart.Parse(new BinaryReaderEx(bytes));
        Assert.Equal(length, decoded.Length);
        Assert.Equal(7, decoded.HeaderSize);
    }

    [Fact]
    public void HeaderEnd8Bit_RoundTrips()
    {
        // 8-bit end headers carry a 6-bit type, so only types <= 0x3F fit.
        var end = new StreamObjectHeaderEnd8Bit(StreamObjectTypeHeaderEnd.DataElement);
        var writer = new BinaryWriterEx();
        end.Serialize(writer);
        byte[] bytes = writer.ToArray();

        Assert.Equal(1, bytes.Length);

        var decoded = StreamObjectHeaderEnd.Parse(new BinaryReaderEx(bytes));
        Assert.IsType<StreamObjectHeaderEnd8Bit>(decoded);
        Assert.Equal(StreamObjectTypeHeaderEnd.DataElement, decoded.Type);
    }

    [Fact]
    public void HeaderEnd16Bit_RoundTrips()
    {
        var end = new StreamObjectHeaderEnd16Bit(StreamObjectTypeHeaderEnd.Request);
        var writer = new BinaryWriterEx();
        end.Serialize(writer);
        byte[] bytes = writer.ToArray();

        Assert.Equal(2, bytes.Length);

        var decoded = StreamObjectHeaderEnd.Parse(new BinaryReaderEx(bytes));
        Assert.IsType<StreamObjectHeaderEnd16Bit>(decoded);
        Assert.Equal(StreamObjectTypeHeaderEnd.Request, decoded.Type);
    }

    [Fact]
    public void CompoundTypes_HaveCompoundBitSet()
    {
        var header = new StreamObjectHeaderStart32Bit(StreamObjectTypeHeaderStart.DataElement, 10);
        Assert.Equal(1, header.Compound);

        var single = new StreamObjectHeaderStart32Bit(StreamObjectTypeHeaderStart.UserAgentGUID, 16);
        Assert.Equal(0, single.Compound);
    }
}

public class BasicTypesTests
{
    [Fact]
    public void BinaryItem_RoundTrips()
    {
        byte[] content = { 0x01, 0x02, 0x03, 0x04, 0x05 };
        var writer = new BinaryWriterEx();
        new BinaryItem(content).Serialize(writer);
        byte[] bytes = writer.ToArray();

        var decoded = BinaryItem.Deserialize(new BinaryReaderEx(bytes));
        Assert.Equal(content, decoded.Content);
    }

    [Fact]
    public void StringItem_RoundTrips()
    {
        const string value = "Hello, FSSHTTPB!";
        var writer = new BinaryWriterEx();
        new StringItem(value).Serialize(writer);
        byte[] bytes = writer.ToArray();

        var decoded = StringItem.Deserialize(new BinaryReaderEx(bytes));
        Assert.Equal(value, decoded.Value);
    }

    [Fact]
    public void CellId_RoundTrips()
    {
        var cellId = new CellId(
            new ExGuid(1, Guid.NewGuid()),
            new ExGuid(2, Guid.NewGuid()));

        var writer = new BinaryWriterEx();
        cellId.Serialize(writer);
        byte[] bytes = writer.ToArray();

        var decoded = CellId.Deserialize(new BinaryReaderEx(bytes));
        Assert.Equal(cellId, decoded);
    }
}

public class ResponseErrorTests
{
    [Fact]
    public void RoundTrips_WithoutMessage()
    {
        var error = new ResponseError(ErrorType.HResult, 0x8000FFFF);
        var writer = new BinaryWriterEx();
        error.Serialize(writer);
        byte[] bytes = writer.ToArray();

        var decoded = ResponseError.Deserialize(new BinaryReaderEx(bytes));
        Assert.Equal(ErrorType.HResult, decoded.Type);
        Assert.Equal(0x8000FFFFUL, decoded.ErrorCode);
        Assert.Null(decoded.ErrorMessage);
    }

    [Fact]
    public void RoundTrips_WithMessage()
    {
        var error = new ResponseError(ErrorType.Win32, 5, "Access is denied.");
        var writer = new BinaryWriterEx();
        error.Serialize(writer);
        byte[] bytes = writer.ToArray();

        var decoded = ResponseError.Deserialize(new BinaryReaderEx(bytes));
        Assert.Equal(ErrorType.Win32, decoded.Type);
        Assert.Equal(5UL, decoded.ErrorCode);
        Assert.Equal("Access is denied.", decoded.ErrorMessage);
    }
}

public class FsshttpbCellRequestTests
{
    [Fact]
    public void CapturedWordQueryChangesRequest_ParsesFully()
    {
        byte[] payload =
        [
            0x0e, 0x00, 0x0b, 0x00, 0x9c, 0xcf, 0x29, 0xf3, 0x39, 0x94, 0x06, 0x9b, 0x06, 0x02, 0x00, 0x00,
            0xee, 0x02, 0x00, 0x00, 0xaa, 0x02, 0x20, 0x00, 0x8c, 0x10, 0x84, 0x19, 0x93, 0x4b, 0xeb, 0x4e,
            0xb3, 0x20, 0x91, 0x94, 0x32, 0xd6, 0xe5, 0x93, 0x5a, 0x04, 0x16, 0x00, 0x0d, 0x6d, 0x73, 0x77,
            0x6f, 0x72, 0x64, 0x07, 0x77, 0x69, 0x6e, 0x7a, 0x02, 0x08, 0x00, 0x46, 0xf0, 0x70, 0x3b, 0x77,
            0x01, 0x16, 0x02, 0x06, 0x00, 0x03, 0x05, 0x00, 0x8a, 0x02, 0x04, 0x00, 0x00, 0xf0, 0xda, 0x02,
            0x06, 0x00, 0x03, 0x00, 0x00, 0xca, 0x02, 0x08, 0x00, 0x08, 0x00, 0x80, 0x03, 0x84, 0x00, 0x41,
            0x0b, 0x01, 0xac, 0x02, 0x00, 0x55, 0x03, 0x01,
        ];
        var reader = new BinaryReaderEx(payload);

        var request = FsshttpbCellRequest.Deserialize(reader);

        Assert.Equal(Guid.Parse("1984108c-4b93-4eeb-b320-919432d6e593"), request.UserAgentGuid);
        Assert.Equal(997257286U, request.UserAgentVersionValue);
        var subRequest = Assert.Single(request.SubRequests);
        Assert.Equal(1UL, subRequest.RequestId);
        Assert.Equal(RequestTypes.QueryChanges, subRequest.RequestType);
        Assert.Equal(0UL, subRequest.Priority);
        Assert.Equal(0, reader.Remaining);
    }

    [Fact]
    public void UserAgentGuidAndVersion_AreReadAsFixedPayloads()
    {
        var request = new FsshttpbCellRequest
        {
            UserAgentGuid = Guid.Parse("E731B87E-DD45-44AA-AB80-0C75FBD1530E"),
            UserAgentVersionValue = 0x01020304,
            SubRequests =
            {
                new FsshttpbCellSubRequest(RequestTypes.QueryAccess)
                {
                    Data = new QueryAccessSubRequestData(),
                },
            },
        };

        var decoded = FsshttpbCellRequest.Deserialize(new BinaryReaderEx(request.ToByteArray()));

        Assert.Equal(request.UserAgentGuid, decoded.UserAgentGuid);
        Assert.Equal(request.UserAgentVersionValue, decoded.UserAgentVersionValue);
    }

    [Fact]
    public void QueryAccessRequest_RoundTrips()
    {
        var request = new FsshttpbCellRequest
        {
            UserAgentGuid = Guid.Parse("E731B87E-DD45-44AA-AB80-0C75FBD1530E"),
            UserAgentVersionValue = 0x01020304,
            SubRequests =
            {
                new FsshttpbCellSubRequest(RequestTypes.QueryAccess)
                {
                    Data = new QueryAccessSubRequestData(),
                },
            },
        };

        byte[] bytes = request.ToByteArray();

        var decoded = FsshttpbCellRequest.Deserialize(new BinaryReaderEx(bytes));
        Assert.Equal(FsshttpbCellRequest.CurrentProtocolVersion, decoded.ProtocolVersion);
        Assert.Equal(FsshttpbCellRequest.MinimumProtocolVersion, decoded.MinimumVersion);
        Assert.Equal(FsshttpbCellRequest.RequestSignature, decoded.Signature);
        Assert.Equal(request.UserAgentGuid, decoded.UserAgentGuid);
        Assert.Equal(request.UserAgentVersionValue, decoded.UserAgentVersionValue);
        Assert.Single(decoded.SubRequests);
        Assert.Equal(RequestTypes.QueryAccess, decoded.SubRequests[0].RequestType);
    }

    [Fact]
    public void RequestWithoutSubRequests_Throws()
    {
        var request = new FsshttpbCellRequest();
        Assert.Throws<InvalidOperationException>(() => request.ToByteArray());
    }

    [Fact]
    public void ToBase64_ProducesDecodableString()
    {
        var request = new FsshttpbCellRequest
        {
            UserAgentGuid = Guid.NewGuid(),
            SubRequests = { new FsshttpbCellSubRequest(RequestTypes.QueryAccess) },
        };

        string base64 = request.ToBase64();
        byte[] bytes = Convert.FromBase64String(base64);
        Assert.NotEmpty(bytes);
    }

    [Fact]
    public void InvalidSignature_Throws()
    {
        var request = new FsshttpbCellRequest
        {
            Signature = 0xDEADBEEF,
            SubRequests = { new FsshttpbCellSubRequest(RequestTypes.QueryAccess) },
        };
        byte[] bytes = request.ToByteArray();
        bytes[10] = 0xFF; // Corrupt the signature.

        Assert.Throws<InvalidDataException>(
            () => FsshttpbCellRequest.Deserialize(new BinaryReaderEx(bytes)));
    }
}

public class FsshttpbResponseTests
{
    [Fact]
    public void StableIdentities_ProduceDistinctPartitionStorageIndices()
    {
        var file = StorageManifestBuilder.BuildQueryChangesResponse(
            1, [1], CreateIdentity(1), 73508);
        var metadata = StorageManifestBuilder.BuildQueryChangesResponse(
            1, [2], CreateIdentity(20), 73508);
        var editors = StorageManifestBuilder.BuildQueryChangesResponse(
            1, [3], CreateIdentity(40), 73508);

        var fileData = Assert.IsType<QueryChangesSubResponseData>(file.SubResponses[0].Data);
        var metadataData = Assert.IsType<QueryChangesSubResponseData>(metadata.SubResponses[0].Data);
        var editorsData = Assert.IsType<QueryChangesSubResponseData>(editors.SubResponses[0].Data);

        Assert.NotEqual(fileData.StorageIndexExtendedGuid, metadataData.StorageIndexExtendedGuid);
        Assert.NotEqual(fileData.StorageIndexExtendedGuid, editorsData.StorageIndexExtendedGuid);
        Assert.NotEqual(metadataData.StorageIndexExtendedGuid, editorsData.StorageIndexExtendedGuid);
    }

    [Fact]
    public void QueryChangesResponseWithKnowledge_RoundTrips()
    {
        byte[] content = new byte[] { 1, 2, 3, 4 };
        var response = StorageManifestBuilder.BuildQueryChangesResponse(7, content);

        byte[] bytes = response.ToByteArray();
        var decoded = FsshttpbResponse.Deserialize(new BinaryReaderEx(bytes));

        var data = Assert.IsType<QueryChangesSubResponseData>(decoded.SubResponses[0].Data);
        Assert.False(data.PartialResult);
        Assert.False(data.StorageIndexExtendedGuid.IsNull);
        Assert.False(data.CellKnowledgeCellGuid == Guid.Empty);
        Assert.Equal(73507UL, data.CellKnowledgeTo);
        Assert.Equal(75507UL, data.Waterline);
        Assert.NotNull(decoded.DataElementPackage);
        Assert.Equal(5, decoded.DataElementPackage!.DataElements.Count);
    }

    [Fact]
    public void QueryAccessResponse_RoundTrips()
    {
        var response = new FsshttpbResponse
        {
            SubResponses =
            {
                new FsshttpbSubResponse
                {
                    RequestId = 1,
                    RequestType = RequestTypes.QueryAccess,
                    Data = new QueryAccessSubResponseData(),
                },
            },
        };

        byte[] bytes = response.ToByteArray();

        var decoded = FsshttpbResponse.Deserialize(new BinaryReaderEx(bytes));
        Assert.Equal(FsshttpbResponse.ResponseSignature, decoded.Signature);
        Assert.False(decoded.Status);
        Assert.Single(decoded.SubResponses);

        var subResponse = decoded.SubResponses[0];
        Assert.Equal(1UL, subResponse.RequestId);
        Assert.Equal(RequestTypes.QueryAccess, subResponse.RequestType);
        Assert.False(subResponse.Status);

        var data = Assert.IsType<QueryAccessSubResponseData>(subResponse.Data);
        Assert.Null(data.ReadAccessError);
        Assert.Null(data.WriteAccessError);
    }

    [Fact]
    public void FailedResponse_CarriesError()
    {
        var response = new FsshttpbResponse
        {
            Status = true,
            Error = new ResponseError(ErrorType.Protocol, 0x21),
        };

        byte[] bytes = response.ToByteArray();

        var decoded = FsshttpbResponse.Deserialize(new BinaryReaderEx(bytes));
        Assert.True(decoded.Status);
        Assert.NotNull(decoded.Error);
        Assert.Equal(ErrorType.Protocol, decoded.Error!.Type);
        Assert.Equal(0x21UL, decoded.Error.ErrorCode);
    }

    [Fact]
    public void FailedSubResponse_CarriesError()
    {
        var response = new FsshttpbResponse
        {
            SubResponses =
            {
                new FsshttpbSubResponse
                {
                    RequestId = 2,
                    RequestType = RequestTypes.QueryChanges,
                    Status = true,
                    Error = new ResponseError(ErrorType.Win32, 32, "Sharing violation"),
                },
            },
        };

        byte[] bytes = response.ToByteArray();

        var decoded = FsshttpbResponse.Deserialize(new BinaryReaderEx(bytes));
        var subResponse = decoded.SubResponses[0];
        Assert.True(subResponse.Status);
        Assert.Equal("Sharing violation", subResponse.Error!.ErrorMessage);
    }

    private static StorageManifestBuilder.StableIdentity CreateIdentity(uint value) => new(
        new ExGuid(value, Guid.NewGuid()),
        new ExGuid(value + 1, Guid.NewGuid()),
        new ExGuid(value + 2, Guid.NewGuid()),
        new ExGuid(value + 3, Guid.NewGuid()),
        new ExGuid(value + 4, Guid.NewGuid()),
        new ExGuid(value + 5, Guid.NewGuid()),
        new ExGuid(value + 6, Guid.NewGuid()),
        new CellId(new ExGuid(value + 7, Guid.NewGuid()), new ExGuid(value + 8, Guid.NewGuid())),
        Guid.NewGuid());
}

public class DataElementPackageTests
{
    [Fact]
    public void EmptyPackage_RoundTrips()
    {
        var package = new DataElementPackage();
        var writer = new BinaryWriterEx();
        package.Serialize(writer);
        byte[] bytes = writer.ToArray();

        var decoded = DataElementPackage.Deserialize(new BinaryReaderEx(bytes));
        Assert.Empty(decoded.DataElements);
    }

    [Theory]
    [InlineData(FsshttpbSerializationProfile.Current)]
    [InlineData(FsshttpbSerializationProfile.SharePoint13_11)]
    public void PackageWithDataElements_RoundTrips(FsshttpbSerializationProfile profile)
    {
        var package = new DataElementPackage
        {
            DataElements =
            {
                StorageManifestBuilder.BuildObjectDataBlobDataElement(
                    new ExGuid(1, Guid.NewGuid()),
                    new SerialNumber(Guid.NewGuid(), 42),
                    // Both end-marker encodings inside the BLOB must stay data.
                    new byte[] { 0xAA, 0x07, 0x00, 0x00, 0x05, 0xCC }),
                new DataElement(
                    DataElementType.StorageManifestDataElementData,
                    new ExGuid(2, Guid.NewGuid()),
                    new SerialNumber(Guid.NewGuid(), 43)),
            },
        };

        var writer = new BinaryWriterEx();
        package.Serialize(writer, profile);
        byte[] bytes = writer.ToArray();

        var decoded = DataElementPackage.Deserialize(new BinaryReaderEx(bytes));
        Assert.Equal(2, decoded.DataElements.Count);

        var first = decoded.DataElements[0];
        Assert.Equal(DataElementType.ObjectDataBLOBDataElementData, first.DataElementType);
        Assert.Equal(package.DataElements[0].DataElementExtendedGuid, first.DataElementExtendedGuid);
        Assert.Equal(package.DataElements[0].SerialNumber, first.SerialNumber);
        Assert.Equal(package.DataElements[0].Data, first.Data);

        var second = decoded.DataElements[1];
        Assert.Equal(DataElementType.StorageManifestDataElementData, second.DataElementType);
        Assert.Null(second.Data);
    }
}
