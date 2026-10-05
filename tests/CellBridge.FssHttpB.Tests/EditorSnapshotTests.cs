using CellBridge.FssHttpB;

namespace CellBridge.FssHttpB.Tests;

public sealed class EditorSnapshotTests
{
    [Fact]
    public void RuntimeSnapshotsAreStableForSameContentAndDistinctForChangesAndDocuments()
    {
        var cell = new CellId(new(1, Guid.NewGuid()), new(1, Guid.NewGuid()));
        var identity = Guid.NewGuid();
        var editors = new[] { new EditorsTableEditor("client", 638923456000000000, "Editor", HasEditorPermission: true) };
        FsshttpbResponse Build(IEnumerable<EditorsTableEditor> entries, Guid owner, ulong sequence = 1) =>
            EditorsTablePartitionBuilder.BuildSharePointV13QueryChangesResponse(1, entries, cell, owner, sequence);
        var first = Build(editors, identity);
        var repeat = Build(editors, identity, 2);
        Assert.Equal(first.DataElementPackage!.DataElements.Select(e => e.DataElementExtendedGuid), repeat.DataElementPackage!.DataElements.Select(e => e.DataElementExtendedGuid));
        Assert.All(first.DataElementPackage.DataElements.Zip(repeat.DataElementPackage.DataElements), pair => Assert.Equal(pair.First.Data, pair.Second.Data));
        var known = ClientKnowledge.Deserialize(new(Assert.IsType<QueryChangesSubResponseData>(first.SubResponses[0].Data).KnowledgeBytes!));
        Assert.All(first.DataElementPackage.DataElements, e => Assert.True(known.Contains(e.SerialNumber)));
        foreach (var changed in new[] { Build([editors[0] with { HasEditorPermission = false }], identity), Build(editors, Guid.NewGuid()), Build([], identity) })
        {
            Assert.All(changed.DataElementPackage!.DataElements, e => Assert.False(known.Contains(e.SerialNumber)));
            Assert.Empty(first.DataElementPackage.DataElements.Select(e => e.DataElementExtendedGuid).Intersect(changed.DataElementPackage.DataElements.Select(e => e.DataElementExtendedGuid)));
        }
    }
}
