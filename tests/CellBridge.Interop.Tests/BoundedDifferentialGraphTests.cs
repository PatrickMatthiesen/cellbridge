using CellBridge.Tests;
using Xunit.Abstractions;
using Server = CellBridge.FssHttpB;
using Reference = Microsoft.Protocols.TestSuites.SharedAdapter;

namespace CellBridge.Interop.Tests;

public sealed class BoundedDifferentialGraphTests(ITestOutputHelper output)
{
    [Fact]
    public void MicrosoftDecoderPreservesGeneratedGraphIdentitiesMappingsPartitionsAndBlobReferences()
    {
        const int seed = 0x18_34;
        var random = new Random(seed);
        for (int sample = 0; sample < 32; sample++)
        {
            byte[] content = new byte[random.Next(1, 4097)]; random.NextBytes(content);
            bool blob = sample % 2 == 0;
            var f = new GraphFixture(content, blob);
            bool cycle = sample % 3 == 0, replace = sample % 5 == 0;
            if (cycle) f.ConnectCells(true);
            if (replace) f.OverrideChild(content.Reverse().ToArray());
            var response = new Server.FsshttpbResponse { DataElementPackage = new() };
            // Vary package order independently of graph identity and traversal.
            response.DataElementPackage.DataElements.AddRange(f.Elements.OrderBy(_ => random.Next()));
            response.SubResponses.Add(new() { RequestId = (ulong)sample + 1, RequestType = Server.RequestTypes.QueryChanges,
                Data = new Server.QueryChangesSubResponseData { StorageIndexExtendedGuid = f.Index } });
            byte[] wire = response.ToByteArray(Server.FsshttpbSerializationProfile.SharePoint13_11);
            Assert.InRange(wire.Length, 1, 16 * 1024);
            output.WriteLine($"seed={seed} sample={sample} bytes={wire.Length} blob={blob} cycle={cycle} override={replace} mutation=package-order");
            // This independent parser checks full response consumption internally.
            var parsed = Reference.FsshttpbResponse.DeserializeResponseFromByteArray(wire, 0);
            Assert.Equal(13, parsed.ProtocolVersion); Assert.Equal(11, parsed.MinimumVersion);
            Assert.False(parsed.Status);
            var sub = Assert.Single(parsed.CellSubResponses);
            Assert.Equal((ulong)sample + 1, sub.RequestID.DecodedValue);
            Assert.Equal((ulong)Server.RequestTypes.QueryChanges, sub.RequestType.DecodedValue);
            Assert.False(sub.Status);
            Equal(f.Index, sub.GetSubResponseData<Reference.QueryChangesSubResponseData>().StorageIndexExtendedGUID);
            var elements = parsed.DataElementPackage.DataElements;
            Assert.Equal(blob ? 11 : 10, elements.Count);
            Assert.Equal(Enumerable.Range(1, 11).Where(i => blob || i != 8).Select(i => (uint)i).Order(),
                elements.Select(e => e.DataElementExtendedGUID.Value).Order());
            Assert.All(elements, e => Assert.Equal(f.Index.Guid, e.DataElementExtendedGUID.GUID));
            Reference.DataElement Find(Server.ExGuid id) => Assert.Single(elements,
                e => e.DataElementExtendedGUID.Value == id.Value && e.DataElementExtendedGUID.GUID == id.Guid);

            var index = Assert.IsType<Reference.StorageIndexDataElementData>(Find(f.Index).Data);
            Equal(f.Manifest, index.StorageIndexManifestMapping.ManifestMappingExtendedGUID);
            Assert.Equal(2, index.StorageIndexCellMappingList.Count);
            foreach (var (cell, target) in new[] { (f.Cell, f.CellManifest), (f.OtherCell, f.OtherCellManifest) })
            {
                var mapping = Assert.Single(index.StorageIndexCellMappingList, m => m.CellID.ExtendGUID1.Value == cell.LongId.Value);
                Equal(cell.LongId, mapping.CellID.ExtendGUID1); Equal(cell.ShortId, mapping.CellID.ExtendGUID2);
                Equal(target, mapping.CellMappingExtendedGUID);
                Assert.Equal(GraphFixture.Mapping(target).Value, mapping.CellMappingSerialNumber.Value);
            }
            Assert.Equal(3, index.StorageIndexRevisionMappingList.Count);
            foreach (var (revision, target, root, obj, group) in new[]
            {
                (f.Revision, f.RevisionElement, f.Root, f.RootObject, f.CurrentGroup),
                (f.BaseRevision, f.BaseElement, f.Root, f.RootObject, f.BaseGroup),
                (f.OtherRevision, f.OtherRevisionElement, f.OtherRoot, f.Child, f.OtherGroup),
            })
            {
                var mapping = Assert.Single(index.StorageIndexRevisionMappingList, m => m.RevisionExtendedGUID.Value == revision.Value);
                Equal(revision, mapping.RevisionExtendedGUID); Equal(target, mapping.RevisionMappingExtendedGUID);
                var manifest = Assert.IsType<Reference.RevisionManifestDataElementData>(Find(target).Data);
                Equal(revision, manifest.RevisionManifest.RevisionID);
                Equal(revision.Equals(f.Revision) ? f.BaseRevision : Server.ExGuid.Null, manifest.RevisionManifest.BaseRevisionID);
                var rootDeclaration = Assert.Single(manifest.RevisionManifestRootDeclareList);
                Equal(root, rootDeclaration.RootExtendedGUID); Equal(obj, rootDeclaration.ObjectExtendedGUID);
                Equal(group, Assert.Single(manifest.RevisionManifestObjectGroupReferencesList).ObjectGroupExtendedGUID);
            }
            var storage = Assert.IsType<Reference.StorageManifestDataElementData>(Find(f.Manifest).Data);
            Assert.Equal(Server.StorageManifestBuilder.StorageManifestSchemaGuid, storage.StorageManifestSchemaGUID.GUID);
            Assert.Equal(2, storage.StorageManifestRootDeclareList.Count);
            Equal(f.Root, storage.StorageManifestRootDeclareList[0].RootExtendedGUID);
            Equal(f.Cell.LongId, storage.StorageManifestRootDeclareList[0].CellID.ExtendGUID1);
            Equal(f.Cell.ShortId, storage.StorageManifestRootDeclareList[0].CellID.ExtendGUID2);
            Equal(f.OtherRoot, storage.StorageManifestRootDeclareList[1].RootExtendedGUID);
            Equal(f.OtherCell.LongId, storage.StorageManifestRootDeclareList[1].CellID.ExtendGUID1);
            Equal(f.OtherCell.ShortId, storage.StorageManifestRootDeclareList[1].CellID.ExtendGUID2);
            Equal(f.Revision, Assert.IsType<Reference.CellManifestDataElementData>(Find(f.CellManifest).Data)
                .CellManifestCurrentRevision.CellManifestCurrentRevisionExtendedGUID);
            Equal(f.OtherRevision, Assert.IsType<Reference.CellManifestDataElementData>(Find(f.OtherCellManifest).Data)
                .CellManifestCurrentRevision.CellManifestCurrentRevisionExtendedGUID);
            var current = Assert.IsType<Reference.ObjectGroupDataElementData>(Find(f.CurrentGroup).Data);
            Assert.Equal(replace ? new ulong[] { 1, 4, 1 } : new ulong[] { 1, 4 },
                current.ObjectGroupDeclarations.ObjectDeclarationList.Select(d => d.ObjectPartitionID.DecodedValue));
            foreach (var declaration in current.ObjectGroupDeclarations.ObjectDeclarationList.Take(2))
                Equal(f.RootObject, declaration.ObjectExtendedGUID);
            if (replace)
            {
                Equal(f.Child, current.ObjectGroupDeclarations.ObjectDeclarationList[2].ObjectExtendedGUID);
                Assert.Equal(content.Reverse(), current.ObjectGroupData.ObjectGroupObjectDataList[2].Data.Content);
            }
            var parent = Assert.IsType<Reference.ObjectGroupDataElementData>(Find(f.BaseGroup).Data);
            if (blob)
            {
                var declaration = Assert.Single(parent.ObjectGroupDeclarations.ObjectGroupObjectBLOBDataDeclarationList);
                Equal(f.Child, declaration.ObjectExGUID); Equal(f.Blob, declaration.ObjectDataBLOBExGUID);
                Equal(f.Blob, Assert.Single(parent.ObjectGroupData.ObjectGroupObjectDataBLOBReferenceList).BLOBExtendedGUID);
                Assert.Equal(content, Assert.IsType<Reference.ObjectDataBLOBDataElementData>(Find(f.Blob).Data).ObjectDataBLOB.Data);
            }
            else
            {
                Equal(f.Child, parent.ObjectGroupDeclarations.ObjectDeclarationList[1].ObjectExtendedGUID);
                Assert.Equal(content, parent.ObjectGroupData.ObjectGroupObjectDataList[1].Data.Content);
            }
        }
    }

    private static void Equal(Server.ExGuid expected, Reference.ExGuid actual)
    {
        Assert.Equal(expected.Value, actual.Value); Assert.Equal(expected.Guid, actual.GUID);
    }
}
