using System.IO.Compression;
using System.Text.Json;
using CellBridge.FssHttp;

namespace CellBridge.FssHttpB.Tests;

public sealed class PartitionGraphSnapshotTests
{
    [Fact]
    public void GeneratedFilePartition_ResolvesIndexManifestChainAndMaterializes()
    {
        var guid = Guid.Parse("15c7b2a2-822a-4a04-8bb0-46c7de4bc901");
        var identity = new StorageManifestBuilder.StableIdentity(
            new ExGuid(1, guid), new ExGuid(2, guid), new ExGuid(3, guid),
            new ExGuid(4, guid), new ExGuid(5, guid), new ExGuid(6, guid),
            new ExGuid(7, guid),
            new CellId(new ExGuid(8, guid), new ExGuid(9,
                StorageManifestBuilder.CellSecondExtendedGuid)), guid);
        var content = Enumerable.Range(0, 4097).Select(i => (byte)(i % 251)).ToArray();
        var response = FileContentPartitionBuilder.BuildQueryChangesResponse(
            1, content, identity, knowledgeSequence: 73507);

        var snapshot = PartitionGraphSnapshot.Create(
            response.DataElementPackage!.DataElements,
            identity.ObjectDataBlobGuid);

        Assert.Equal(identity.ObjectDataBlobGuid, snapshot.StorageIndex);
        Assert.Equal(identity.ObjectGuid, snapshot.RootObject);
        Assert.Equal(identity.RevisionId, snapshot.Revision);
        Assert.Equal(content, snapshot.Materialize());
    }

    [Fact]
    public void CapturedSaveThenSaveSecond_MergesDeltaWithoutMutatingPriorSnapshot()
    {
        var firstElements = ReadFixture("save-first");
        var secondElements = ReadFixture("save-second");
        var firstIndex = new ExGuid(1,
            Guid.Parse("d4b03a71-d96a-49c0-914f-0894afd37295"));
        var secondIndex = new ExGuid(1,
            Guid.Parse("6fba7790-4e0a-4e18-a34e-f7716c3c0680"));

        var first = PartitionGraphSnapshot.Create(firstElements, firstIndex);
        var firstContent = first.Materialize();
        var merged = first.Merge(secondElements, secondIndex);
        var mergedContent = merged.Materialize();

        Assert.Equal(firstIndex, first.StorageIndex);
        Assert.Equal(secondIndex, merged.StorageIndex);
        Assert.Equal(firstContent, first.Materialize());
        Assert.NotEqual(Convert.ToHexString(firstContent), Convert.ToHexString(mergedContent));
        Assert.Equal(23, first.Elements.Count);
        Assert.Equal(38, merged.Elements.Count);

        using var firstZip = new ZipArchive(new MemoryStream(firstContent), ZipArchiveMode.Read);
        using var mergedZip = new ZipArchive(new MemoryStream(mergedContent), ZipArchiveMode.Read);
        Assert.Contains(firstZip.Entries, x => x.FullName == "word/document.xml");
        Assert.Contains(mergedZip.Entries, x => x.FullName == "word/document.xml");
    }

    [Fact]
    public void MissingStorageIndexMapping_IsRejected()
    {
        var elements = ReadFixture("save-first");
        var index = new ExGuid(1,
            Guid.Parse("d4b03a71-d96a-49c0-914f-0894afd37295"));
        var broken = elements.Where(x => !x.DataElementExtendedGuid.Equals(index)).ToArray();

        var exception = Assert.Throws<InvalidDataException>(() =>
            PartitionGraphSnapshot.Create(broken, index));
        Assert.Contains("not present", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PutChangesExpectedIndex_ComparesUpdatedKeysAndIgnoresMappingSerials()
    {
        var identity = CreateIdentity();
        var response = FileContentPartitionBuilder.BuildQueryChangesResponse(
            1, [1, 2, 3], identity, knowledgeSequence: 73507);
        var elements = response.DataElementPackage!.DataElements;
        var snapshot = PartitionGraphSnapshot.Create(elements, identity.ObjectDataBlobGuid);
        var alternateIndex = new ExGuid(1,
            Guid.Parse("e7654c48-3dc2-482d-9a34-1e3f8a90c8c7"));

        var alternate = StorageManifestBuilder.BuildStorageIndexDataElement(
            alternateIndex,
            new SerialNumber(Guid.Parse("e7654c48-3dc2-482d-9a34-1e3f8a90c8c7"), 99),
            identity.StorageManifestGuid,
            FindSerial(elements, identity.StorageManifestGuid),
            identity.CellId,
            identity.CellManifestGuid,
            FindSerial(elements, identity.CellManifestGuid),
            identity.RevisionId,
            identity.RevisionManifestGuid,
            FindSerial(elements, identity.RevisionManifestGuid));

        var candidate = elements.Append(alternate);

        Assert.True(snapshot.MatchesPutChanges(
            candidate, alternateIndex, identity.ObjectDataBlobGuid, implyNullExpected: false));
    }

    [Fact]
    public void PutChangesMissingExpectedKey_UsesImplyNullExpectedFlag()
    {
        var identity = CreateIdentity();
        var response = FileContentPartitionBuilder.BuildQueryChangesResponse(
            1, [1, 2, 3], identity, knowledgeSequence: 73507);
        var elements = response.DataElementPackage!.DataElements;
        var snapshot = PartitionGraphSnapshot.Create(elements, identity.ObjectDataBlobGuid);
        var alternateIndex = new ExGuid(1,
            Guid.Parse("f0e9d8c7-b6a5-4938-8716-150413121110"));
        var alternate = StorageManifestBuilder.BuildStorageIndexDataElement(
            alternateIndex, SerialNumber.Null,
            identity.StorageManifestGuid,
            FindSerial(elements, identity.StorageManifestGuid),
            identity.CellId,
            identity.CellManifestGuid,
            FindSerial(elements, identity.CellManifestGuid),
            identity.RevisionId,
            identity.RevisionManifestGuid,
            FindSerial(elements, identity.RevisionManifestGuid));

        var candidate = elements.Append(alternate);
        Assert.True(snapshot.MatchesPutChanges(
            candidate, alternateIndex, ExGuid.Null, implyNullExpected: false));
        Assert.False(snapshot.MatchesPutChanges(
            candidate, alternateIndex, ExGuid.Null, implyNullExpected: true));

        var mismatched = StorageManifestBuilder.BuildStorageIndexDataElement(
            new ExGuid(1, Guid.Parse("9f8e7d6c-5b4a-4938-8716-150413121110")),
            SerialNumber.Null,
            new ExGuid(1, Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")),
            SerialNumber.Null,
            identity.CellId,
            identity.CellManifestGuid,
            FindSerial(elements, identity.CellManifestGuid),
            identity.RevisionId,
            identity.RevisionManifestGuid,
            FindSerial(elements, identity.RevisionManifestGuid));
        Assert.False(snapshot.MatchesPutChanges(
            elements.Append(mismatched), identity.ObjectDataBlobGuid,
            mismatched.DataElementExtendedGuid, implyNullExpected: false));
    }

    private static StorageManifestBuilder.StableIdentity CreateIdentity()
    {
        var guid = Guid.Parse("15c7b2a2-822a-4a04-8bb0-46c7de4bc901");
        return new StorageManifestBuilder.StableIdentity(
            new ExGuid(1, guid), new ExGuid(2, guid), new ExGuid(3, guid),
            new ExGuid(4, guid), new ExGuid(5, guid), new ExGuid(6, guid),
            new ExGuid(7, guid),
            new CellId(new ExGuid(8, guid), new ExGuid(9,
                StorageManifestBuilder.CellSecondExtendedGuid)), guid);
    }

    private static SerialNumber FindSerial(IEnumerable<DataElement> elements, ExGuid id) =>
        elements.Single(x => x.DataElementExtendedGuid.Equals(id)).SerialNumber;

    private static IReadOnlyList<DataElement> ReadFixture(string name)
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", name + ".json")));
        var side = fixture.RootElement.GetProperty("request");
        var bytes = Convert.FromBase64String(side.GetProperty("bodyBase64").GetString()!);
        var parts = MtomMessageParser.Parse(bytes, side.GetProperty("contentType").GetString()!);
        var binary = Assert.Single(parts, x => x.ContentType.Contains(
            "application/octet-stream", StringComparison.OrdinalIgnoreCase));
        var reader = new BinaryReaderEx(binary.Content);
        var request = FsshttpbCellRequest.Deserialize(reader);
        Assert.Equal(0, reader.Remaining);
        return request.DataElementPackage!.DataElements;
    }
}
