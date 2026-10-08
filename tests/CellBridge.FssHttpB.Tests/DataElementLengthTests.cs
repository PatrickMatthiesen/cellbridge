namespace CellBridge.FssHttpB.Tests;

public sealed class DataElementLengthTests
{
    [Theory]
    [InlineData(FsshttpbSerializationProfile.Current)]
    [InlineData(FsshttpbSerializationProfile.SharePoint13_11)]
    public void MeasurementMatchesWireAcrossIdentityAndPayloadBoundaries(FsshttpbSerializationProfile profile)
    {
        foreach (uint index in new uint[] { 0, 31, 32, 1023, 1024, 131071, 131072, uint.MaxValue })
        foreach (bool nullIdentity in new[] { false, true })
        foreach (int length in new[] { -1, 0, 127, 128, 32767, 32768, 1024 * 1024 })
        {
            var element = new DataElement(DataElementType.ObjectDataBLOBDataElementData,
                nullIdentity ? ExGuid.Null : new ExGuid(index, Guid.NewGuid()),
                nullIdentity ? SerialNumber.Null : new SerialNumber(Guid.NewGuid(), ulong.MaxValue))
            { Data = length < 0 ? null : new byte[length] };
            var writer = new BinaryWriterEx();
            element.Serialize(writer, profile);
            Assert.Equal((long)writer.Length, element.GetSerializedLength(profile));
            if (profile == FsshttpbSerializationProfile.Current)
                Assert.Equal((long)writer.Length, element.GetSerializedLength());

            // Payload length must not change the immediate metadata framing.
            var metadata = StreamObjectHeaderStart.Parse(new BinaryReaderEx(writer.ToArray()));
            element.Data = null;
            var prefix = new BinaryWriterEx();
            element.Serialize(prefix, profile);
            var emptyMetadata = StreamObjectHeaderStart.Parse(new BinaryReaderEx(prefix.ToArray()));
            Assert.Equal(emptyMetadata.Length, metadata.Length);
            Assert.Equal(emptyMetadata.HeaderSize, metadata.HeaderSize);
        }
    }

    [Fact]
    public void MeasurementRejectsUnknownProfilesLikeSerialization()
    {
        var element = new DataElement(DataElementType.None, ExGuid.Null, SerialNumber.Null);
        var profile = (FsshttpbSerializationProfile)99;
        Assert.Throws<ArgumentOutOfRangeException>(() => element.Serialize(new BinaryWriterEx(), profile));
        Assert.Throws<ArgumentOutOfRangeException>(() => element.GetSerializedLength(profile));
    }

    [Fact]
    public void MeasurementAllocationsDoNotScaleWithPayloadSize()
    {
        var element = new DataElement(DataElementType.ObjectDataBLOBDataElementData,
            new ExGuid(1, Guid.NewGuid()), new SerialNumber(Guid.NewGuid(), 1));
        var small = new byte[1];
        var large = new byte[8 * 1024 * 1024];
        long Measure(byte[] payload)
        {
            element.Data = payload;
            for (int i = 0; i < 16; i++) element.GetSerializedLength();
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 64; i++) element.GetSerializedLength();
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }
        long smallBytes = Measure(small);
        long largeBytes = Measure(large);
        Assert.InRange(largeBytes, 0, smallBytes + 16 * 1024);
        Assert.True(largeBytes < 1024 * 1024, $"Length measurement allocated {largeBytes} bytes.");
    }
}
