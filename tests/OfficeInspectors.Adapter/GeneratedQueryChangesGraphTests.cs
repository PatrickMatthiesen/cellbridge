#if OFFICE_INSPECTORS
using CellBridge.FssHttpB;

namespace OfficeInspectors.Adapter;

public sealed class GeneratedQueryChangesGraphTests
{
    [OfficeInspectorsFact]
    public void GeneratedFileGraphIsConsumedByOfficeInspectorsParser()
    {
        var response = StorageManifestBuilder.BuildQueryChangesResponse(
            requestId: 7,
            fileContent: "file"u8.ToArray());

        var result = OfficeInspectorsAdapter.ParseResponse(
            response.ToByteArray(FsshttpbSerializationProfile.SharePoint13_11));

        Assert.True(result.Available);
        Assert.True(result.Parsed, result.Error ?? result.Summary);
        Assert.Contains("StorageManifestDataElement", result.Summary);
        Assert.Contains("CellManifestDataElement", result.Summary);
        Assert.Contains("RevisionManifestDataElement", result.Summary);
        Assert.Contains("ObjectGroupDataElement", result.Summary);
        Assert.Contains("StorageIndexDataElement", result.Summary);
    }
}
#endif
