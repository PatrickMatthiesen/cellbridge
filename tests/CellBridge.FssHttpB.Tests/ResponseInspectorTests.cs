using CellBridge.FssHttpB;

namespace CellBridge.FssHttpB.Tests;

public sealed class ResponseInspectorTests
{
    [Fact]
    public void QueryResponseKeepsStorageIndexDistinctFromWaterlineStorageId()
    {
        var data = new QueryChangesSubResponseData
        {
            StorageIndexExtendedGuid = new ExGuid(5, Guid.NewGuid()),
            WaterlineCellStorageExtendedGuid = new ExGuid(1, Guid.NewGuid()),
        };
        var writer = new BinaryWriterEx();
        data.Serialize(writer);
        var read = QueryChangesSubResponseData.Deserialize(new BinaryReaderEx(writer.ToArray()));
        Assert.Equal(data.StorageIndexExtendedGuid, read.StorageIndexExtendedGuid);
        Assert.Equal(data.WaterlineCellStorageExtendedGuid, read.WaterlineCellStorageExtendedGuid);
    }

    [Fact]
    public void LegacyWordSaveRejectionReportsItsInvalidUtf8StringFraming()
    {
        var bytes = Convert.FromBase64String(File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "Fixtures", "word-save-rejected.response.base64")));
        var inspection = FsshttpbResponseInspector.Inspect(bytes);
        Assert.NotEmpty(inspection.Issues);
        var sub = Assert.Single(inspection.SubResponses);
        Assert.Equal(RequestTypes.PutChanges, sub.RequestType);
        Assert.True(sub.Status);
    }

    [Fact]
    public void ValidSaveRejectionReportsProtocolErrorWithUtf16SupplementalString()
    {
        var response = new FsshttpbResponse { SubResponses =
        { new FsshttpbSubResponse { RequestId = 1, RequestType = RequestTypes.PutChanges, Status = true,
            Error = new(ErrorType.Protocol, (ulong)ProtocolErrorCode.RequestNotSupported, "Unsupported upload.") } } };
        var inspection = FsshttpbResponseInspector.Inspect(response.ToByteArray());
        Assert.Empty(inspection.Issues);
        Assert.Equal((ulong)ProtocolErrorCode.RequestNotSupported, Assert.Single(inspection.SubResponses).Error!.ErrorCode);
        Assert.Contains("error type=Protocol code=4", inspection.ToCanonicalText());
    }

    [Fact]
    public void TopLevelError_ReportsErrorCode()
    {
        var response = new FsshttpbResponse
        {
            Status = true,
            Error = new ResponseError(ErrorType.Protocol, (ulong)ProtocolErrorCode.RequestNotSupported),
        };
        var inspection = FsshttpbResponseInspector.Inspect(response.ToByteArray());
        Assert.Empty(inspection.Issues);
        Assert.Equal((ulong)ProtocolErrorCode.RequestNotSupported, inspection.Error!.ErrorCode);
    }

    [Fact]
    public void QueryChanges_ReportsElementSequenceAndGuidEdges()
    {
        var identity = new StorageManifestBuilder.StableIdentity(
            new ExGuid(1, Guid.Parse("10000000-0000-0000-0000-000000000001")),
            new ExGuid(2, Guid.Parse("10000000-0000-0000-0000-000000000002")),
            new ExGuid(3, Guid.Parse("10000000-0000-0000-0000-000000000003")),
            new ExGuid(4, Guid.Parse("10000000-0000-0000-0000-000000000004")),
            new ExGuid(5, Guid.Parse("10000000-0000-0000-0000-000000000005")),
            new ExGuid(6, Guid.Parse("10000000-0000-0000-0000-000000000006")),
            new ExGuid(7, Guid.Parse("10000000-0000-0000-0000-000000000007")),
            new CellId(
                new ExGuid(8, Guid.Parse("10000000-0000-0000-0000-000000000008")),
                new ExGuid(9, Guid.Parse("10000000-0000-0000-0000-000000000009"))),
            Guid.Parse("10000000-0000-0000-0000-00000000000a"));

        var response = StorageManifestBuilder.BuildQueryChangesResponse(7, [1, 2, 3], identity, 73508);
        var inspection = FsshttpbResponseInspector.Inspect(response.ToByteArray());

        Assert.Equal([DataElementType.StorageManifestDataElementData,
            DataElementType.CellManifestDataElementData,
            DataElementType.RevisionManifestDataElementData,
            DataElementType.ObjectGroupDataElementData,
            DataElementType.StorageIndexDataElementData],
            inspection.DataElements.Select(x => x.DataElementType));
        Assert.True(inspection.Issues.Count == 0, string.Join(Environment.NewLine, inspection.Issues));
        Assert.Contains(inspection.Relationships, x => x.Kind == "revision" && x.Target.StartsWith("7:"));
        Assert.Contains(inspection.Relationships, x => x.Kind == "object-group" && x.Target.StartsWith("4:"));
        Assert.DoesNotContain(inspection.Relationships, x => x.Kind == "object-ref");
        Assert.Contains(inspection.Relationships, x => x.Kind == "storage-index" && x.Target.StartsWith("5:"));
        Assert.Contains("element[0]", inspection.ToCanonicalText());
        Assert.Contains("sha256=", inspection.ToCanonicalText());
    }

    [Fact]
    public void QueryChanges_DoesNotTreatElementMarkersInsideBlobAsFraming()
    {
        static byte[] Serialize(Action<BinaryWriterEx> write)
        {
            var writer = new BinaryWriterEx();
            write(writer);
            return writer.ToArray();
        }

        var embeddedFollowingElement = new DataElement(DataElementType.ObjectDataBLOBDataElementData,
            new ExGuid(7, Guid.Parse("20000000-0000-0000-0000-000000000007")), SerialNumber.Null);
        byte[] embeddedElementBytes = Serialize(w => embeddedFollowingElement.Serialize(w));

        // Image payloads are arbitrary bytes. This sequence resembles a real
        // element boundary, including a valid following DataElement header.
        byte[] blobPayload = Serialize(w =>
        {
            new StreamObjectHeaderEnd8Bit(StreamObjectTypeHeaderEnd.DataElement).Serialize(w);
            w.WriteBytes(embeddedElementBytes);
        });
        byte[] blobObject = Serialize(w =>
        {
            new StreamObjectHeaderStart32Bit(StreamObjectTypeHeaderStart.ObjectDataBLOB, blobPayload.Length).Serialize(w);
            w.WriteBytes(blobPayload);
        });
        var first = new DataElement(DataElementType.ObjectDataBLOBDataElementData,
            new ExGuid(1, Guid.Parse("20000000-0000-0000-0000-000000000001")), SerialNumber.Null)
        { Data = blobObject };
        var second = new DataElement(DataElementType.ObjectDataBLOBDataElementData,
            new ExGuid(2, Guid.Parse("20000000-0000-0000-0000-000000000002")), SerialNumber.Null)
        { Data = Serialize(w =>
        {
            byte[] payload = [0x10, 0x20, 0x30, 0x40];
            new StreamObjectHeaderStart32Bit(StreamObjectTypeHeaderStart.ObjectDataBLOB, payload.Length).Serialize(w);
            w.WriteBytes(payload);
        }) };
        var response = new FsshttpbResponse
        {
            DataElementPackage = new DataElementPackage { DataElements = { first, second } },
            SubResponses =
            {
                new FsshttpbSubResponse
                {
                    RequestId = 1,
                    RequestType = RequestTypes.QueryChanges,
                    Data = new QueryChangesSubResponseData { PartialResult = true },
                },
            },
        };

        var inspection = FsshttpbResponseInspector.Inspect(response.ToByteArray());

        Assert.Equal(2, inspection.DataElements.Count);
        Assert.Equal(first.Data!.Length, inspection.DataElements[0].DataLength);
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(first.Data)).ToLowerInvariant(),
            inspection.DataElements[0].DataSha256);
        Assert.Equal(first.DataElementType, inspection.DataElements[0].DataElementType);
        Assert.Equal("1:20000000-0000-0000-0000-000000000001", inspection.DataElements[0].ExtendedGuid);
        Assert.Equal(second.Data!.Length, inspection.DataElements[1].DataLength);
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(second.Data)).ToLowerInvariant(),
            inspection.DataElements[1].DataSha256);
        Assert.Equal("2:20000000-0000-0000-0000-000000000002", inspection.DataElements[1].ExtendedGuid);
        Assert.Contains("partial=True", inspection.ToCanonicalText());
    }

    [Fact]
    public void QueryChanges_UsesSharePointManifestSchemaAndRootIdentities()
    {
        var response = StorageManifestBuilder.BuildQueryChangesResponse(8, [1, 2, 3]);
        var parsed = FsshttpbResponse.Deserialize(new BinaryReaderEx(response.ToByteArray()));
        var elements = parsed.DataElementPackage!.DataElements;
        var storage = Assert.Single(elements,
            x => x.DataElementType == DataElementType.StorageManifestDataElementData);
        var revision = Assert.Single(elements,
            x => x.DataElementType == DataElementType.RevisionManifestDataElementData);

        var schemaGuid = StorageManifestBuilder.StorageManifestSchemaGuid.ToByteArray();
        Assert.True(storage.Data.AsSpan().IndexOf(schemaGuid) >= 0,
            "Storage manifest does not contain the protocol schema GUID.");

        static byte[] Serialize(ExGuid value)
        {
            var writer = new BinaryWriterEx();
            value.Serialize(writer);
            return writer.ToArray();
        }

        var root = Serialize(new ExGuid(2, StorageManifestBuilder.RootExtendedGuid));
        Assert.True(storage.Data.AsSpan().IndexOf(root) >= 0,
            "Storage manifest root declaration does not use the SharePoint root identity.");
        Assert.True(revision.Data.AsSpan().IndexOf(root) >= 0,
            "Revision manifest root declaration does not use the SharePoint root identity.");

        var cell = Serialize(new ExGuid(1, StorageManifestBuilder.RootExtendedGuid));
        var secondCell = Serialize(new ExGuid(1, StorageManifestBuilder.CellSecondExtendedGuid));
        var cellSequence = cell.Concat(secondCell).ToArray();
        Assert.True(storage.Data.AsSpan().IndexOf(cellSequence) >= 0,
            "Storage manifest root cell ID does not use the SharePoint identities.");
    }

    [Fact]
    public void MalformedUnknownDataElementPayload_StopsWithoutGuessingItsBoundary()
    {
        var package = new DataElementPackage
        {
            DataElements =
            {
                new DataElement((DataElementType)99, new ExGuid(1, Guid.NewGuid()), SerialNumber.Null)
                {
                    Data = [0xFF, 0xFE, 0xFD],
                },
            },
        };
        var response = new FsshttpbResponse
        {
            DataElementPackage = package,
            SubResponses =
            {
                new FsshttpbSubResponse
                {
                    RequestId = 1,
                    RequestType = RequestTypes.QueryChanges,
                    Data = new QueryChangesSubResponseData(),
                },
            },
        };

        var inspection = FsshttpbResponseInspector.Inspect(response.ToByteArray());

        Assert.Single(inspection.DataElements);
        Assert.Equal((DataElementType)99, inspection.DataElements[0].DataElementType);
        Assert.NotEmpty(inspection.Issues);
        Assert.Contains("response parse stopped:", inspection.ToCanonicalText());
    }

    [Fact]
    public void Base64Inspection_MatchesBinaryInspection()
    {
        var response = StorageManifestBuilder.BuildQueryChangesResponse(3, [4, 5]);
        var bytes = response.ToByteArray();

        var binary = FsshttpbResponseInspector.Inspect(bytes);
        var base64 = FsshttpbResponseInspector.InspectBase64(Convert.ToBase64String(bytes));

        Assert.Equal(binary.ToCanonicalText(), base64.ToCanonicalText());
    }
}
