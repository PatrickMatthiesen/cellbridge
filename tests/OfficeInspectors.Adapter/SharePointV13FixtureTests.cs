using CellBridge.OfficeInspectors.Parsers;
using ServerFss = CellBridge.FssHttpB;
using Xunit.Abstractions;

namespace OfficeInspectors.Adapter;

/// <summary>
/// Regression coverage for the four FSSHTTPB payloads captured from SharePoint.
/// The fixture bytes are base64 encoded so the test data remains reviewable and
/// does not require replaying the packet capture.
/// </summary>
public sealed class SharePointV13FixtureTests
{
    private readonly ITestOutputHelper _output;

    public SharePointV13FixtureTests(ITestOutputHelper output) => _output = output;

    public static IEnumerable<object[]> PartitionFixtures =>
    [
        ["editors-table-token-8.fsshttpb.b64", 1342, 2],
        ["metadata-token-7.fsshttpb.b64", 364, 2],
        ["file-contents-token-6.fsshttpb.b64", 151, 2],
    ];

    [Theory]
    [MemberData(nameof(PartitionFixtures))]
    public void SharePointPayload_AdvertisesV13AndIsConsumedFully(
        string fixtureName,
        int expectedLength,
        int expectedRequestType)
    {
        var (response, consumed, bytes) = ParseFixture(fixtureName);

        Assert.Equal(expectedLength, bytes.Length);
        Assert.Equal(13, response.ProtocolVersion);
        Assert.Equal(11, response.MinimumVersion);
        Assert.Equal(0, response.Status);
        Assert.Equal(bytes.Length, consumed);
        Assert.NotNull(response.ResponseEnd);

        var subresponses = response.SubResponses ?? [];
        var subresponse = Assert.Single(subresponses);
        Assert.Equal((ulong)1, subresponse.RequestID.GetUint(subresponse.RequestID));
        Assert.Equal((ulong)expectedRequestType, subresponse.RequestType.GetUint(subresponse.RequestType));
        Assert.Equal(0, subresponse.Status);

        _output.WriteLine($"{fixtureName}: {bytes.Length} bytes, version " +
            $"{response.ProtocolVersion}/{response.MinimumVersion}, consumed {consumed}");
    }

    [Fact]
    public void PortableInspector_ConsumesQueryAccessPayloadWithoutIssues()
    {
        var bytes = Convert.FromBase64String(File.ReadAllText(
            FindFixture("query-access-token-13.fsshttpb.b64")).Trim());
        var inspection = ServerFss.FsshttpbResponseInspector.Inspect(bytes);

        Assert.Equal(140, inspection.ByteLength);
        Assert.Equal(13, inspection.ProtocolVersion);
        Assert.Equal(11, inspection.MinimumVersion);
        Assert.False(inspection.Status);
        Assert.Empty(inspection.Issues);
        Assert.Single(inspection.SubResponses);
    }

    [Theory]
    [InlineData("editors-table-token-8.fsshttpb.b64", "RevisionManifestDataElement,ObjectGroupDataElements,StorageManifestDataElement,CellManifestDataElement,ObjectGroupDataElements,StorageIndexDataElement")]
    [InlineData("metadata-token-7.fsshttpb.b64", "StorageIndexDataElement")]
    [InlineData("file-contents-token-6.fsshttpb.b64", "StorageIndexDataElement")]
    public void PartitionPayload_HasExpectedElementSequence(string fixtureName, string expectedSequence)
    {
        var (response, _, _) = ParseFixture(fixtureName);
        Assert.NotNull(response.DataElementPackage);

        var actualSequence = string.Join(',', response.DataElementPackage!.DataElements.Select(x => x.GetType().Name));
        Assert.Equal(expectedSequence, actualSequence);
    }

    [Fact]
    public void QueryAccessPayload_HasReadAndWriteAccessSubresponses()
    {
        var (response, _, _) = ParseFixture("query-access-token-13.fsshttpb.b64");

        // Compare the independent parsers, retaining the third access result
        // as an opaque extension rather than inventing its semantics.
        var bytes = Convert.FromBase64String(File.ReadAllText(FindFixture("query-access-token-13.fsshttpb.b64")).Trim());
        var inspection = ServerFss.FsshttpbResponseInspector.Inspect(bytes);
        Assert.Equal(13, inspection.ProtocolVersion);
        Assert.Equal(11, inspection.MinimumVersion);
        Assert.Equal(bytes.Length, inspection.ByteLength);
        Assert.Empty(inspection.Issues);
        var subresponse = Assert.Single(inspection.SubResponses);
        Assert.Equal(ServerFss.RequestTypes.QueryAccess, subresponse.RequestType);
        Assert.Equal(3, subresponse.Objects.Count);
        Assert.Equal([67, 70, 164], subresponse.Objects.Select(x => x.TypeValue));

        var officeSubresponse = Assert.Single(response.SubResponses!);
        var access = Assert.IsType<QueryAccessResponse>(officeSubresponse.SubResponseData);
        var extension = Assert.Single(access.AdditionalAccessResponses);
        Assert.Equal(164, extension.Type);
        Assert.Empty(extension.Payload);
        Assert.Single(extension.Children);
    }

    [Fact]
    public void PartitionPayload_QueryChangesStorageIndexMatchesTheStorageIndexElement()
    {
        foreach (var fixtureName in new[]
        {
            "editors-table-token-8.fsshttpb.b64",
            "metadata-token-7.fsshttpb.b64",
            "file-contents-token-6.fsshttpb.b64",
        })
        {
            var (response, _, _) = ParseFixture(fixtureName);
            var subresponse = Assert.Single(response.SubResponses!);
            var changes = Assert.IsType<QueryChangesResponse>(subresponse.SubResponseData);
            var storageIndex = Assert.Single(response.DataElementPackage!.DataElements.OfType<StorageIndexDataElement>());

            var responseGuid = changes.StorageIndexExtendedGUID.GetGUID(changes.StorageIndexExtendedGUID);
            var elementGuid = storageIndex.DataElementExtendedGUID.GetGUID(storageIndex.DataElementExtendedGUID);
            Assert.NotEqual(Guid.Empty, responseGuid);
            Assert.Equal(elementGuid, responseGuid);
        }
    }

    [Fact]
    public void EditorsPayload_ContainsManifestAndObjectGroupGuidReferences()
    {
        var (response, _, _) = ParseFixture("editors-table-token-8.fsshttpb.b64");
        var elements = response.DataElementPackage!.DataElements;
        var revision = Assert.IsType<RevisionManifestDataElement>(elements[0]);
        var groups = elements.OfType<ObjectGroupDataElements>().ToArray();
        var storage = Assert.IsType<StorageManifestDataElement>(elements[2]);
        var cell = Assert.IsType<CellManifestDataElement>(elements[3]);

        var revisionGuid = revision.DataElementExtendedGUID.GetGUID(revision.DataElementExtendedGUID);
        var storageGuid = storage.DataElementExtendedGUID.GetGUID(storage.DataElementExtendedGUID);
        var cellGuid = cell.DataElementExtendedGUID.GetGUID(cell.DataElementExtendedGUID);
        Assert.NotEqual(Guid.Empty, revisionGuid);
        Assert.NotEqual(Guid.Empty, storageGuid);
        Assert.NotEqual(Guid.Empty, cellGuid);
        Assert.Equal(2, groups.Length);
        Assert.All(groups, group => Assert.NotEqual(Guid.Empty,
            group.DataElementExtendedGUID.GetGUID(group.DataElementExtendedGUID)));

        var revisionChildren = revision.RevisionManifestDataElementsData ?? [];
        Assert.NotEmpty(revisionChildren);
        Assert.Contains(revisionChildren, child => child is RevisionManifestRootDeclareValues);

        _output.WriteLine($"revision={revisionGuid} storage={storageGuid} cell={cellGuid}");
    }

    private static (CellBridge.OfficeInspectors.Parsers.FsshttpbResponse Response, long Consumed, byte[] Bytes) ParseFixture(string fixtureName)
    {
        var fixturePath = FindFixture(fixtureName);
        var bytes = Convert.FromBase64String(File.ReadAllText(fixturePath).Trim());
        var (response, consumed) = OfficeInspectorsAdapter.ParseIsolated(bytes);
        return (response, consumed, bytes);
    }

    private static string FindFixture(string fixtureName)
    {
        return Path.Combine(AppContext.BaseDirectory, "Fixtures", fixtureName);
    }
}
