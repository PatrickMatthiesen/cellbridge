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
    public void ActualWordSaveRejection_ReportsProtocolErrorWithoutFalseFramingFailure()
    {
        var bytes = Convert.FromBase64String(File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "Fixtures", "word-save-rejected.response.base64")));
        var inspection = FsshttpbResponseInspector.Inspect(bytes);
        Assert.Empty(inspection.Issues);
        var sub = Assert.Single(inspection.SubResponses);
        Assert.Equal(RequestTypes.PutChanges, sub.RequestType);
        Assert.True(sub.Status);
        Assert.Equal((ulong)ProtocolErrorCode.RequestNotSupported, sub.Error!.ErrorCode);
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
    public void UnknownDataElementPayload_IsRetainedAsDiagnostic()
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
        Assert.Contains("element[0] object", inspection.ToCanonicalText());
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
