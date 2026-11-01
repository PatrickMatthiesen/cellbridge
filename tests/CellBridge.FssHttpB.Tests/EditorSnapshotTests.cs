using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using CellBridge.FssHttpB;

namespace CellBridge.FssHttpB.Tests;

public sealed class EditorSnapshotTests
{
    [Fact]
    public void RuntimeSnapshotsAreStableForSameContentAndDistinctForChangesAndDocuments()
    {
        var cell = new CellId(new(1, Guid.NewGuid()), new(1, Guid.NewGuid()));
        var identity = Guid.NewGuid();
        var editors = new[]
        {
            new EditorsTableEditor("client", 638923456000000000, "Editor", HasEditorPermission: true,
                Metadata: new Dictionary<string, byte[]> { ["CustomValue"] = Encoding.UTF8.GetBytes("before") }),
        };
        FsshttpbResponse Build(IEnumerable<EditorsTableEditor> entries, Guid owner, ulong sequence = 1) =>
            EditorsTablePartitionBuilder.BuildSharePointV13QueryChangesResponse(1, entries, cell, owner, sequence);
        var first = Build(editors, identity);
        var repeat = Build(editors, identity, 2);
        Assert.Equal(first.DataElementPackage!.DataElements.Select(e => e.DataElementExtendedGuid), repeat.DataElementPackage!.DataElements.Select(e => e.DataElementExtendedGuid));
        Assert.All(first.DataElementPackage.DataElements.Zip(repeat.DataElementPackage.DataElements), pair => Assert.Equal(pair.First.Data, pair.Second.Data));
        var known = ClientKnowledge.Deserialize(new(Assert.IsType<QueryChangesSubResponseData>(first.SubResponses[0].Data).KnowledgeBytes!));
        Assert.All(first.DataElementPackage.DataElements, e => Assert.True(known.Contains(e.SerialNumber)));
        foreach (var invalidated in new[]
        {
            Build([editors[0] with { HasEditorPermission = false }], identity),
            Build(editors, Guid.NewGuid()),
            Build([], identity),
        })
        {
            Assert.All(invalidated.DataElementPackage!.DataElements, e => Assert.False(known.Contains(e.SerialNumber)));
            Assert.Empty(first.DataElementPackage.DataElements.Select(e => e.DataElementExtendedGuid)
                .Intersect(invalidated.DataElementPackage.DataElements.Select(e => e.DataElementExtendedGuid)));
        }

        var changed = Build([editors[0] with
        {
            Metadata = new Dictionary<string, byte[]> { ["CustomValue"] = Encoding.UTF8.GetBytes("after") },
        }], identity);
        Assert.All(changed.DataElementPackage!.DataElements, e => Assert.False(known.Contains(e.SerialNumber)));
        Assert.Empty(first.DataElementPackage.DataElements.Select(e => e.DataElementExtendedGuid)
            .Intersect(changed.DataElementPackage.DataElements.Select(e => e.DataElementExtendedGuid)));

        var changedSubresponse = Assert.IsType<QueryChangesSubResponseData>(changed.SubResponses[0].Data);
        var selected = PartitionGraphSnapshot.Create(changed.DataElementPackage.DataElements,
            changedSubresponse.StorageIndexExtendedGuid);
        var materialized = selected.Materialize();
        Assert.Equal(EditorsTablePartitionBuilder.ZipStreamHeader, materialized[..EditorsTablePartitionBuilder.ZipStreamHeader.Length]);
        using var compressed = new MemoryStream(materialized, EditorsTablePartitionBuilder.ZipStreamHeader.Length,
            materialized.Length - EditorsTablePartitionBuilder.ZipStreamHeader.Length);
        using var deflate = new DeflateStream(compressed, CompressionMode.Decompress);
        var xml = XDocument.Load(deflate);
        Assert.Equal("client", xml.Root!.Element("Editor")!.Element("CacheID")!.Value);
        Assert.Equal("YWZ0ZXI=", xml.Root.Element("Editor")!.Element("Metadata")!.Element("CustomValue")!.Value);
    }
}
