using CellBridge.FssHttpB;
using CellBridge.Tests;

public sealed class SelectiveFileGraphTests
{
    [Fact]
    public void SelfContainedObjectsPermitAnUnmappedUnusedAncestorAndReuseParsedSelection()
    {
        var fixture = new GraphFixture("self contained"u8.ToArray());
        fixture.MakeSelfContainedWithUnmappedAncestor();
        var graph = fixture.File();
        Assert.True(graph.IsSelfContainedFileGraph());
        Assert.Equal(fixture.Bytes, graph.Materialize());
        var metadataOnlyDelta = fixture.Elements.Select(e => new DataElement(e.DataElementType,
            e.DataElementExtendedGuid, e.SerialNumber) { Data = e.Data });
        var next = graph.Merge(metadataOnlyDelta, graph.StorageIndex);
        Parallel.For(0, 20, _ => Assert.Equal(fixture.Bytes, next.Materialize()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InheritedOrBlobObjectsRequireGeneralResolution(bool blob)
    {
        var fixture = new GraphFixture("resolved"u8.ToArray(), blob);
        var graph = fixture.File();
        Assert.False(graph.IsSelfContainedFileGraph());
        Assert.Equal(fixture.Bytes, graph.Materialize());
        Assert.Null(PartitionGraphSnapshot.ReadFileSaveObjectGroups(fixture.Elements, fixture.Index));
    }
}
