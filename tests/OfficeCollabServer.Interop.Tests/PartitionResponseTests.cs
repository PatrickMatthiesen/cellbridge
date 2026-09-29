using Microsoft.Protocols.TestSuites.SharedAdapter;
using ServerCellId = OfficeCollabServer.FssHttpB.CellId;
using ServerRequestTypes = OfficeCollabServer.FssHttpB.RequestTypes;

namespace OfficeCollabServer.Interop.Tests;

/// <summary>
/// Exercises each Cell partition with the vendored Microsoft FSSHTTPB parser
/// and the server's diagnostic inspector.
/// Keeping these checks offline makes a malformed graph fail before a Word
/// session is needed. The opt-in live test below uses the identical decoders.
/// </summary>
public sealed class PartitionResponseTests
{
    private static readonly Guid MetadataPartitionId =
        new("383ADC0B-E66E-4438-95E6-E39EF9720122");

    private static readonly Guid EditorsTablePartitionId =
        new("7808F4DD-2385-49D6-B7CE-37ACA5E43602");

    [Fact]
    public void MetadataQueryChangesResponse_PassesBothParsers()
    {
        var response = OfficeCollabServer.FssHttpB.StorageManifestBuilder.BuildQueryChangesResponse(
            requestId: 21,
            fileContent: "<Metadata ContentVersion=\"7\" Modified=\"638923456000000000\" />"u8.ToArray(),
            CreateIdentity(100),
            knowledgeSequence: 73507);

        var parsed = ParseWithMicrosoft(response);

        Assert.Equal(
            new[]
            {
                DataElementType.StorageManifestDataElementData,
                DataElementType.CellManifestDataElementData,
                DataElementType.RevisionManifestDataElementData,
                DataElementType.ObjectGroupDataElementData,
                DataElementType.StorageIndexDataElementData,
            },
            parsed.DataElementPackage!.DataElements.Select(element => element.DataElementType));
    }

    [Fact]
    public void FileContentsQueryChangesResponse_PassesBothParsers()
    {
        byte[] document = "interop document bytes"u8.ToArray();
        var identity = CreateIdentity(200);
        var response = OfficeCollabServer.FssHttpB.StorageManifestBuilder.BuildQueryChangesResponse(
            requestId: 22, document, identity, knowledgeSequence: 73508);

        var parsed = ParseWithMicrosoft(response);
        var group = Assert.Single(parsed.DataElementPackage!.DataElements,
            element => element.DataElementType == DataElementType.ObjectGroupDataElementData);
        var objects = group.GetData<ObjectGroupDataElementData>()
            .ObjectGroupData.ObjectGroupObjectDataList;
        Assert.Contains(objects, item => item.Data.Content.SequenceEqual(document));

        var storageIndex = Assert.Single(parsed.DataElementPackage.DataElements,
            element => element.DataElementType == DataElementType.StorageIndexDataElementData);
        var index = storageIndex.GetData<StorageIndexDataElementData>();
        Assert.Equal(identity.StorageManifestGuid.Guid,
            index.StorageIndexManifestMapping.ManifestMappingExtendedGUID.GUID);
        var cellMapping = Assert.Single(index.StorageIndexCellMappingList);
        Assert.Equal(identity.CellManifestGuid.Guid,
            cellMapping.CellMappingExtendedGUID.GUID);
        var revisionMapping = Assert.Single(index.StorageIndexRevisionMappingList);
        Assert.Equal(identity.RevisionId.Guid,
            revisionMapping.RevisionExtendedGUID.GUID);
        Assert.Equal(identity.RevisionManifestGuid.Guid,
            revisionMapping.RevisionMappingExtendedGUID.GUID);

        var cell = Assert.Single(parsed.DataElementPackage.DataElements,
            element => element.DataElementType == DataElementType.CellManifestDataElementData);
        Assert.Equal(identity.RevisionId.Guid,
            cell.GetData<CellManifestDataElementData>().CellManifestCurrentRevision
                .CellManifestCurrentRevisionExtendedGUID.GUID);
        var revision = Assert.Single(parsed.DataElementPackage.DataElements,
            element => element.DataElementType == DataElementType.RevisionManifestDataElementData);
        Assert.Equal(identity.ObjectGuid.Guid,
            revision.GetData<RevisionManifestDataElementData>().RevisionManifestRootDeclareList[0]
                .ObjectExtendedGUID.GUID);
    }

    [Fact]
    public void EditorsTableQueryChangesResponse_PassesBothParsers()
    {
        var response = OfficeCollabServer.FssHttpB.EditorsTablePartitionBuilder.BuildQueryChangesResponse(
            requestId: 23,
            editors: new[]
            {
                new OfficeCollabServer.FssHttpB.EditorsTableEditor(
                    "client-a", 638923456000000000, "A User", "a.user",
                    HasEditorPermission: true),
            },
            identity: CreateEditorsIdentity());

        var parsed = ParseWithMicrosoft(response);
        var group = Assert.Single(parsed.DataElementPackage!.DataElements,
            element => element.DataElementType == DataElementType.ObjectGroupDataElementData);
        var data = group.GetData<ObjectGroupDataElementData>();

        // The editors stream starts with the fixed eight-byte header followed
        // by compressed XML. The Microsoft parser must read both objects.
        Assert.True(data.ObjectGroupData.ObjectGroupObjectDataList.Count >= 2);
        Assert.Equal(
            OfficeCollabServer.FssHttpB.EditorsTablePartitionBuilder.ZipStreamHeader,
            data.ObjectGroupData.ObjectGroupObjectDataList[0].Data.Content);
    }

    [Theory]
    [InlineData(43)]
    [InlineData(1024)]
    public void QueryChangesResponseLengthMatchesVariableWidthExGuid(uint storageIndex)
    {
        Guid identityGuid = Guid.Parse("AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE");
        var response = OfficeCollabServer.FssHttpB.EditorsTablePartitionBuilder.BuildSharePointV13QueryChangesResponse(
            requestId: 24,
            editors: Array.Empty<OfficeCollabServer.FssHttpB.EditorsTableEditor>(),
            cellId: CreateEditorsIdentity().CellId,
            identityGuid,
            knowledgeSequence: 12);
        response.SubResponses[0].Data = new OfficeCollabServer.FssHttpB.QueryChangesSubResponseData
        {
            StorageIndexExtendedGuid = new OfficeCollabServer.FssHttpB.ExGuid(storageIndex, identityGuid),
            CellKnowledgeCellGuid = identityGuid,
            CellKnowledgeTo = 12,
            Waterline = 12,
        };

        var bytes = response.ToByteArray();
        var inspection = OfficeCollabServer.FssHttpB.FsshttpbResponseInspector.Inspect(bytes);
        Assert.True(inspection.SubResponses.Count == 1, inspection.ToCanonicalText());
        Assert.Equal(24UL, inspection.SubResponses[0].RequestId);
        var ownParsed = OfficeCollabServer.FssHttpB.FsshttpbResponse.Deserialize(
            new OfficeCollabServer.FssHttpB.BinaryReaderEx(bytes));
        Assert.Equal(storageIndex, Assert.IsType<OfficeCollabServer.FssHttpB.QueryChangesSubResponseData>(
            ownParsed.SubResponses[0].Data).StorageIndexExtendedGuid.Value);
        var parsed = ParseWithMicrosoft(response);
        Assert.NotNull(parsed.DataElementPackage);
    }

    private static Microsoft.Protocols.TestSuites.SharedAdapter.FsshttpbResponse ParseWithMicrosoft(
        OfficeCollabServer.FssHttpB.FsshttpbResponse response)
    {
        byte[] bytes = response.ToByteArray();
        var inspection = OfficeCollabServer.FssHttpB.FsshttpbResponseInspector.Inspect(bytes);
        Assert.Equal(bytes.Length, inspection.ByteLength);
        Assert.Single(inspection.SubResponses);
        Assert.Equal(ServerRequestTypes.QueryChanges, inspection.SubResponses[0].RequestType);
        Assert.NotEmpty(inspection.DataElements);

        var parsed = Microsoft.Protocols.TestSuites.SharedAdapter.FsshttpbResponse
            .DeserializeResponseFromByteArray(bytes, 0);
        Assert.False(parsed.Status);
        Assert.Single(parsed.CellSubResponses);
        Assert.Equal((ulong)RequestTypes.QueryChanges,
            parsed.CellSubResponses[0].RequestType.DecodedValue);
        return parsed;
    }

    private static OfficeCollabServer.FssHttpB.StorageManifestBuilder.StableIdentity CreateIdentity(uint seed)
    {
        Guid guid(int suffix) => Guid.Parse($"{seed:X8}-0000-0000-0000-{suffix:X12}");
        return new(
            new(1, guid(1)), new(2, guid(2)), new(3, guid(3)),
            new(4, guid(4)), new(5, guid(5)), new(6, guid(6)),
            new(7, guid(7)),
            new ServerCellId(new(8, guid(8)), new(9, guid(9))),
            guid(10));
    }

    private static OfficeCollabServer.FssHttpB.EditorsTablePartitionBuilder.EditorsTableIdentity CreateEditorsIdentity()
    {
        Guid guid(int suffix) => Guid.Parse($"{suffix:X8}-1111-2222-3333-444444444444");
        return new(
            new(1, guid(1)), new(2, guid(2)), new(3, guid(3)), new(4, guid(4)),
            new(5, guid(5)), new(6, guid(6)), new(7, guid(7)),
            new ServerCellId(new(8, guid(8)), new(9, guid(9))), guid(10));
    }
}

public sealed class LivePartitionTests
{
    private static readonly Guid MetadataPartitionId =
        new("383ADC0B-E66E-4438-95E6-E39EF9720122");

    private static readonly Guid EditorsTablePartitionId =
        new("7808F4DD-2385-49D6-B7CE-37ACA5E43602");

    [LiveInteropFact]
    public async Task QueryChanges_ParsesEditorsMetadataAndFileContentsPartitions()
    {
        var endpointText = Environment.GetEnvironmentVariable("OFFICECOLLABSERVER_INTEROP_ENDPOINT");
        Assert.False(string.IsNullOrWhiteSpace(endpointText));

        using var http = new HttpClient(new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        });
        var client = new CellStorageClient(http, new Uri(endpointText));

        await AssertPartition(client, EditorsTablePartitionId, "editors", requestId: 31);
        await AssertPartition(client, MetadataPartitionId, "metadata", requestId: 32);

        // FileContents is selected by GetFileProps and has no PartitionID.
        var file = await client.SendCellAsync(
            "/shared/test.docx", InteropRequestFactory.QueryChanges(33),
            partitionId: null, getFileProps: true);
        Assert.Equal(System.Net.HttpStatusCode.OK, file.StatusCode);
        AssertParsesSuccessfully(file.ParseBinaryResponse(), file.Binary!);
    }

    private static async Task AssertPartition(
        CellStorageClient client, Guid partitionId, string name, ulong requestId)
    {
        var result = await client.SendCellAsync(
            "/shared/test.docx", InteropRequestFactory.QueryChanges(requestId), partitionId);
        Assert.Equal(System.Net.HttpStatusCode.OK, result.StatusCode);
        Assert.NotNull(result.Binary);
        AssertParsesSuccessfully(result.ParseBinaryResponse(), result.Binary!);
    }

    private static void AssertParsesSuccessfully(
        Microsoft.Protocols.TestSuites.SharedAdapter.FsshttpbResponse parsed, byte[] bytes)
    {
        var inspection = OfficeCollabServer.FssHttpB.FsshttpbResponseInspector.Inspect(bytes);
        Assert.Equal(bytes.Length, inspection.ByteLength);
        Assert.Single(inspection.SubResponses);
        Assert.Equal(ServerRequestTypes.QueryChanges, inspection.SubResponses[0].RequestType);
        Assert.False(parsed.Status);
        Assert.Single(parsed.CellSubResponses);
        Assert.NotNull(parsed.DataElementPackage);
        Assert.NotEmpty(parsed.DataElementPackage!.DataElements);
    }
}
