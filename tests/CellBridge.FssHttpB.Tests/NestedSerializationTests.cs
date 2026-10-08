using System.Security.Cryptography;

namespace CellBridge.FssHttpB.Tests;

public sealed class NestedSerializationTests
{
    // SHA-256 goldens from merged baseline 58958fc, seed 42 and the fixed identity below.
    // At 32762 payload bytes the ObjectGroupObjectData body reaches the 32767 sentinel.
    [Theory]
    [InlineData(0, "2d1274a9a7d604413e209bedf0f5e32b6f19ce76e5f056392d96cad116c0adf5", "1ea16a6b2f7d8f7ced21d236ef9577863c2c4cfa4906c4ba6af8c00bdc913895", "7786ad78169796b13c3069b551846fdf7ae2a3554235618f210aff664ccf2f37")]
    [InlineData(1, "ac3ab0fc76e24bfa97858a0f7c102cab835dd589dd8304cb44ce61f02d7c40d8", "05d6c427d213c92def882764e78414dec4ef470526ac3a336678669702c38732", "8ff3796afa429011feef4c1c41e8141154794d5529bb78041b63e532231d8d9c")]
    [InlineData(127, "eab409d13be9bbf36fb28bb42c0c0d3e7e11e2c4074243afd48979700d096990", "bac66382500e1e08e026d67b491bf0318da247b8ee108703b3f9066d7098a8d5", "91d2092fe117e5b7f74287d9145b83b350ac787909c91488abb51c8094dd9f61")]
    [InlineData(128, "2027786d94409400cb414920b99eac076b116e76097bc1dc65bae7ff316bef2b", "8631b74bf8df719bc73db2be0263d7186853503a29d87b159c1de57262921523", "b23f942672a02df8a0b168ec614ed73e7478d99c14e833168020eb205b1c45a4")]
    [InlineData(1024, "e1fea8a778df6be3adc088fefc024af80258e5a92f9eacf8c864d0f59bc886cb", "32b30f7f7260cefaa88e40fd54a64d164b06c893963e2d93364428463df539c6", "407034ada880d70d7b04cea958ac1a17f0e25ce5bf2b65d29187682d92311426")]
    [InlineData(32761, "7be4f280a1ac8f357db7e05b0a7e530098cc087c388f4595bb0e96e900c5b8de", "d56de4d23aa462e65baed9fb1940aaf8e2917695afca9133c68f16c04096456a", "e4dfe245dc1b035b01fb63874f4474e452e2f3b3a7f3e8a9d4670db10dc9e494")]
    [InlineData(32762, "f6199f3ec94c965605a18213fe4ab04fae8b6dcdb3677673e4de2950ee3ae9d4", "12f20dd781984ec428748162323e61ac0ffd1a78c365d0848e230a2ba6142f15", "aeeeb43ad62e9f6993a1a1b350de98ea027146a1d360297ea40a6ea7d1e70c8e")]
    [InlineData(32763, "50dcbe908eba2bcef91f95deb96a9dfc28f371d487da77ffca604e57a8fbb706", "367747e6a79902c610b705c66e1ded1ce9555f8821d47e82d8abf5c964510ac3", "462bfb5e8d02bf87e5fb59d8eaf3f8c60447babfb3699ec18b58b2964d8b96c5")]
    [InlineData(65536, "287a09fabe5f0b6b204564486eedee12c7dbe3bbe36b91fc356f6d2b54ff4e2a", "8f077174108f6c1ed54c78056bb57564d2d0e1d8cfc68b7e3e8454949f0ba1c7", "62d8b78ad7c1a534be40b74f83384f5ae5078027b7cd9b85e640fc7a7dc7d8fd")]
    public void BuildersPreserveBaselineWireAtEncodedLengthBoundaries(int size, string nestedHash,
        string currentHash, string legacyHash)
    {
        byte[] payload = new byte[size];
        new Random(42).NextBytes(payload);
        var identity = Identity();
        var element = StorageManifestBuilder.BuildObjectGroupDataElement(identity.ObjectGroupGuid,
            new(identity.SerialGuid, 1), identity.ObjectGuid, (ulong)size, payload);
        Assert.Equal(nestedHash, Hash(element.Data!));
        var response = FileContentPartitionBuilder.BuildQueryChangesResponse(1, payload, identity, 1);
        Assert.Equal(currentHash, Hash(response.ToByteArray(FsshttpbSerializationProfile.Current)));
        Assert.Equal(legacyHash, Hash(response.ToByteArray(FsshttpbSerializationProfile.SharePoint13_11)));
        var graph = PartitionGraphSnapshot.Create(response.DataElementPackage!.DataElements, identity.ObjectDataBlobGuid);
        Assert.Equal(payload, graph.Materialize());
    }

    [Fact]
    public void BuildersOwnInputAndIndependentResultsAcrossLargeBufferGrowth()
    {
        byte[] payload = new byte[1024 * 1024];
        new Random(42).NextBytes(payload);
        var expected = payload.ToArray();
        var identity = Identity();
        DataElement Build() => StorageManifestBuilder.BuildObjectGroupDataElement(identity.ObjectGroupGuid,
            new(identity.SerialGuid, 1), identity.ObjectGuid, (ulong)payload.Length, payload);
        var first = Build();
        var second = Build();
        var file = FileContentPartitionBuilder.BuildQueryChangesResponse(1, payload, identity, 1);
        Array.Clear(payload);
        Assert.Equal(expected, Assert.Single(ObjectGroupDataElement.ParseOpaque(first).Objects).Content);
        Array.Clear(first.Data!);
        Assert.Equal(expected, Assert.Single(ObjectGroupDataElement.ParseOpaque(second).Objects).Content);
        var graph = PartitionGraphSnapshot.Create(file.DataElementPackage!.DataElements, identity.ObjectDataBlobGuid);
        Assert.Equal(expected, graph.Materialize());
        var writer = new BinaryWriterEx();
        file.Serialize(writer);
        var independent = writer.ToArray();
        Array.Clear(independent);
        writer.WriteBytes(new byte[2 * 1024 * 1024]);
        Assert.Equal(file.ToByteArray(), writer.ToArray().AsSpan(0, writer.Length - 2 * 1024 * 1024).ToArray());
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static StorageManifestBuilder.StableIdentity Identity()
    {
        var guid = new Guid("00112233-4455-6677-8899-aabbccddeeff");
        return new(new(1, guid), new(2, guid), new(3, guid), new(4, guid), new(5, guid), new(6, guid),
            new(7, guid), new(new(8, guid), new(9, guid)), guid);
    }
}
