using CellBridge.FssHttpB;

namespace OfficeInspectors.Adapter;

public sealed class StorageIndexPatchFramingTests
{
    [Theory]
    [InlineData(FsshttpbSerializationProfile.Current)]
    [InlineData(FsshttpbSerializationProfile.SharePoint13_11)]
    public void MergedMappingHeadersMeetNormativeWidthAndIndependentDecoder(FsshttpbSerializationProfile profile)
    {
        var space = Guid.Parse("fa0ff240-b968-456a-a7b9-052042a51297");
        ExGuid Id(uint value) => new(value, space);
        var serial = new SerialNumber(space, ulong.MaxValue);
        StorageIndexMapping[] current = [
            new(StreamObjectTypeHeaderStart.StorageIndexManifestMapping, null, null, Id(1), serial),
            new(StreamObjectTypeHeaderStart.StorageIndexCellMapping, null, new(Id(2), Id(3)), Id(4), serial),
            new(StreamObjectTypeHeaderStart.StorageIndexRevisionMapping, Id(5), null, Id(6), serial) ];
        var updated = current[1] with { Target = Id(uint.MaxValue) };
        var bytes = StorageIndexPatch.Merge(current, [updated]);
        var reader = new BinaryReaderEx(bytes);
        foreach (var mapping in new[] { current[0], updated, current[2] })
        {
            var header = StreamObjectHeaderStart.Parse(reader);
            Assert.Equal(2, header.HeaderSize);
            Assert.Equal(mapping.Type, header.Type);
            Assert.Equal(0, header.Compound);
            reader.Skip(header.Length);
        }
        Assert.Equal(0, reader.Remaining);
        Assert.Equal(new[] { current[0], updated, current[2] }, StorageIndexMappingSerials.ReadMappings(bytes));
        var response = new FsshttpbResponse { DataElementPackage = new() };
        response.DataElementPackage.DataElements.Add(new(DataElementType.StorageIndexDataElementData, Id(10), serial) { Data = bytes });
        var inspection = OfficeInspectorsAdapter.ParseResponse(response.ToByteArray(profile));
        Assert.True(inspection.Parsed, inspection.Error ?? inspection.Summary);
    }
}
