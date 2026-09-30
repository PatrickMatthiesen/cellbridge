using CellBridge.FssHttpB;

namespace CellBridge.FssHttpB.Tests;

public sealed class SharePoint13ProfileTests
{
    [Fact]
    public void ProfileUses13_11AndLegacyPackageElementFraming()
    {
        var response = new FsshttpbResponse
        {
            DataElementPackage = new DataElementPackage
            {
                DataElements =
                {
                    new DataElement(
                        DataElementType.StorageIndexDataElementData,
                        new ExGuid(1, Guid.Parse("10000000-0000-0000-0000-000000000001")),
                        SerialNumber.Null),
                },
            },
        };

        byte[] bytes = response.ToByteArray(FsshttpbSerializationProfile.SharePoint13_11);

        Assert.Equal((ushort)13, BitConverter.ToUInt16(bytes, 0));
        Assert.Equal((ushort)11, BitConverter.ToUInt16(bytes, 2));
        var packageStart = StreamObjectHeaderStart.Parse(new BinaryReaderEx(bytes) { Position = 17 });
        Assert.IsType<StreamObjectHeaderStart16Bit>(packageStart);
        Assert.Equal(StreamObjectTypeHeaderStart.DataElementPackage, packageStart.Type);
        Assert.Equal(1, packageStart.Length);

        var elementEnd = new BinaryWriterEx();
        new StreamObjectHeaderEnd8Bit(StreamObjectTypeHeaderEnd.DataElement).Serialize(elementEnd);
        Assert.Contains(elementEnd.ToArray()[0], bytes);
    }

    [Fact]
    public void CurrentProfileRemains12_11And32BitPackageFraming()
    {
        var response = new FsshttpbResponse { DataElementPackage = new DataElementPackage() };
        byte[] bytes = response.ToByteArray();

        Assert.Equal((ushort)12, BitConverter.ToUInt16(bytes, 0));
        var packageStart = StreamObjectHeaderStart.Parse(new BinaryReaderEx(bytes) { Position = 17 });
        Assert.IsType<StreamObjectHeaderStart32Bit>(packageStart);
    }

    [Fact]
    public void FileContentsStorageIndexShapeMatchesCapturedLength()
    {
        var storageGuid = Guid.Parse("aa9843aa-4fa5-464b-bafc-9196a4225025");
        var waterlineGuid = Guid.Parse("1cb8f2d0-75f4-4a00-b053-6e777cf1940a");
        var response = StorageManifestBuilder.BuildStorageIndexOnlyQueryChangesResponse(
            requestId: 1,
            storageIndex: new ExGuid(51, storageGuid),
            knowledgeSequence: 1,
            emitNullManifestMapping: false,
            includeCellKnowledge: false,
            waterlineCellStorage: new ExGuid(1, waterlineGuid));

        byte[] bytes = response.ToByteArray(FsshttpbSerializationProfile.SharePoint13_11);

        Assert.Equal(151, bytes.Length);
    }

}
