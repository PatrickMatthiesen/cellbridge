using CellBridge.FssHttpB;
using Xunit.Abstractions;

namespace OfficeInspectors.Adapter;

public sealed class OfficeInspectorsAdapterTests
{
    private readonly ITestOutputHelper _output;

    public OfficeInspectorsAdapterTests(ITestOutputHelper output) => _output = output;

    [OfficeInspectorsFact]
    public void GeneratedFileMetadataAndEditorsResponses_ExposeLegacyHeaderMismatch()
    {
        var responses = new[]
        {
            StorageManifestBuilder.BuildQueryChangesResponse(6, "file"u8.ToArray()),
            StorageManifestBuilder.BuildQueryChangesResponse(7, "<Metadata ContentVersion=\"1\" />"u8.ToArray()),
            EditorsTablePartitionBuilder.BuildQueryChangesResponse(
                8,
                new[] { new EditorsTableEditor("client", DateTime.UtcNow.Ticks, "User") }),
        };

        foreach (var response in responses)
        {
            var result = OfficeInspectorsAdapter.ParseResponse(response.ToByteArray());
            Assert.True(result.Available);

            _output.WriteLine(result.Summary);
            if (result.Error is not null)
                _output.WriteLine(result.Error);
            Assert.False(result.Parsed);
            Assert.Contains("consumed=19", result.Summary);
            Assert.Contains("data-elements= subresponses=0", result.Summary);
        }
    }

    [OfficeInspectorsFact]
    public void SharePoint13ProfileResponses_AreConsumedCompletely()
    {
        var identity = Guid.Parse("10000000-0000-0000-0000-000000000001");
        var index = new ExGuid(43, identity);
        var cellId = new CellId(new ExGuid(40, identity), new ExGuid(41, identity));
        var editors = new FsshttpbResponse
        {
            DataElementPackage = new DataElementPackage
            {
                DataElements =
                [
                    ..EditorsTablePartitionBuilder.BuildSharePointV13DataElements(
                        [new EditorsTableEditor("client", DateTime.UtcNow.Ticks, "User")],
                        cellId,
                        identity),
                ],
            },
            SubResponses =
            {
                new FsshttpbSubResponse
                {
                    RequestId = 1,
                    RequestType = RequestTypes.QueryChanges,
                    Data = new QueryChangesSubResponseData
                    {
                        StorageIndexExtendedGuid = index,
                        CellKnowledgeCellGuid = identity,
                        CellKnowledgeTo = 1,
                        Waterline = 1,
                    },
                },
            },
        };
        var storageIndexOnly = StorageManifestBuilder.BuildStorageIndexOnlyQueryChangesResponse(
            1, index, 1);

        foreach (var response in new[] { editors, storageIndexOnly })
        {
            var bytes = response.ToByteArray(FsshttpbSerializationProfile.SharePoint13_11);
            var result = OfficeInspectorsAdapter.ParseResponse(bytes);
            Assert.True(result.Available);
            Assert.True(result.Parsed, result.Error ?? result.Summary);
        }
    }
}
