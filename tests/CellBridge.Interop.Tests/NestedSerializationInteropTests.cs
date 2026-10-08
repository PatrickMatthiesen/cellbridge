using Server = CellBridge.FssHttpB;
using Reference = Microsoft.Protocols.TestSuites.SharedAdapter;

namespace CellBridge.Interop.Tests;

public sealed class NestedSerializationInteropTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(127)]
    [InlineData(128)]
    [InlineData(32761)]
    [InlineData(32762)]
    [InlineData(32763)]
    [InlineData(1048576)]
    public void MicrosoftDecoderPreservesNestedPayloadsDeclarationsAndReferences(int size)
    {
        var guid = new Guid("00112233-4455-6677-8899-aabbccddeeff");
        var identity = new Server.StorageManifestBuilder.StableIdentity(new(1, guid), new(2, guid), new(3, guid),
            new(4, guid), new(5, guid), new(6, guid), new(7, guid), new(new(8, guid), new(9, guid)), guid);
        byte[] payload = new byte[size];
        new Random(42).NextBytes(payload);
        foreach (var profile in new[] { Server.FsshttpbSerializationProfile.Current, Server.FsshttpbSerializationProfile.SharePoint13_11 })
        foreach (bool file in new[] { false, true })
        {
            var response = file
                ? Server.FileContentPartitionBuilder.BuildQueryChangesResponse(1, payload, identity, 1)
                : Server.StorageManifestBuilder.BuildQueryChangesResponse(1, payload, identity, 1);
            var wire = response.ToByteArray(profile);
            // The vendored Microsoft decoder validates complete response consumption.
            var parsed = Reference.FsshttpbResponse.DeserializeResponseFromByteArray(wire, 0);
            Assert.False(parsed.Status);
            var sub = Assert.Single(parsed.CellSubResponses);
            Assert.False(sub.Status);
            Assert.Equal(1UL, sub.RequestID.DecodedValue);
            Assert.Equal((ulong)Reference.RequestTypes.QueryChanges, sub.RequestType.DecodedValue);
            var element = Assert.Single(parsed.DataElementPackage.DataElements,
                e => e.DataElementExtendedGUID.Value == identity.ObjectGroupGuid.Value &&
                     e.DataElementExtendedGUID.GUID == guid);
            var group = Assert.IsType<Reference.ObjectGroupDataElementData>(element.Data);
            var declarations = group.ObjectGroupDeclarations.ObjectDeclarationList;
            var objects = group.ObjectGroupData.ObjectGroupObjectDataList;
            Assert.Equal(file ? 3 : 1, declarations.Count);
            Assert.Equal(declarations.Count, objects.Count);
            for (int i = 0; i < declarations.Count; i++)
            {
                Assert.Equal(guid, declarations[i].ObjectExtendedGUID.GUID);
                Assert.Equal(1UL, declarations[i].ObjectPartitionID.DecodedValue);
                Assert.Equal((ulong)objects[i].Data.Content.Count, declarations[i].ObjectDataSize.DecodedValue);
                Assert.Equal((ulong)objects[i].ObjectExGUIDArray.Content.Count, declarations[i].ObjectReferencesCount.DecodedValue);
                Assert.Empty(objects[i].CellIDArray.Content);
                Assert.Equal(0UL, declarations[i].CellReferencesCount.DecodedValue);
            }
            Assert.Equal(payload, objects[^1].Data.Content.ToArray());
            Assert.Empty(objects[^1].ObjectExGUIDArray.Content);
            Assert.Equal(identity.ObjectGuid.Value, declarations[0].ObjectExtendedGUID.Value);
            if (file)
            {
                Assert.Equal(identity.ObjectGuid.Value ^ 0x80000000U, declarations[1].ObjectExtendedGUID.Value);
                Assert.Equal(identity.ObjectGuid.Value ^ 0x40000000U, declarations[2].ObjectExtendedGUID.Value);
                for (int i = 0; i < 2; i++)
                {
                    var reference = Assert.Single(objects[i].ObjectExGUIDArray.Content);
                    Assert.Equal(declarations[i + 1].ObjectExtendedGUID.Value, reference.Value);
                    Assert.Equal(guid, reference.GUID);
                }
                var decoded = Server.FsshttpbResponse.Deserialize(new Server.BinaryReaderEx(wire));
                var graph = Server.PartitionGraphSnapshot.Create(decoded.DataElementPackage!.DataElements, identity.ObjectDataBlobGuid);
                Assert.Equal(payload, graph.Materialize());
            }
        }
    }
}
