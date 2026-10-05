using CellBridge.FssHttpB;
using CellBridge.Tests;

public class InheritedFileGraphTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OverriddenAncestorCellReferenceDoesNotWidenCurrentScopeOrFail(bool missing)
    {
        var f = new GraphFixture([1]);
        f.SetOverriddenAncestorReference(missing ? new CellId(GraphFixture.Id(90), GraphFixture.Id(91)) : f.OtherCell);
        var graph = f.Generic();
        Assert.Contains(f.BaseGroup, graph.GetRequiredElements(f.Cell));
        Assert.DoesNotContain(f.OtherGroup, graph.GetRequiredElements(f.Cell));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CellDependencyClosureIncludesReferencedCellsAndTerminatesCycles(bool cycle)
    {
        var f = new GraphFixture([1], blob: true);
        Assert.DoesNotContain(f.OtherGroup, f.Generic().GetRequiredElements(f.Cell));
        f.ConnectCells(cycle);
        var graph = f.Generic();
        var expected = graph.GetRequiredElements(f.Cell).OrderBy(i => i.Value).ToArray();
        Assert.Contains(f.OtherGroup, expected);
        for (int i = 0; i < 10; i++)
            Assert.Equal(expected, graph.GetRequiredElements(f.Cell).OrderBy(id => id.Value));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FileAdapterResolvesInheritedFilePartitionWithoutMetadataOrCrossCellLeakage(bool blob)
    {
        var f = new GraphFixture("inherited bytes"u8.ToArray(), blob);
        var graph = f.File();
        Assert.Equal(f.Bytes, graph.Materialize());
        using var destination = new MemoryStream();
        Assert.Equal(f.Bytes.Length, await graph.MaterializeToAsync(destination));
        Assert.Equal(f.Bytes, destination.ToArray());
        Assert.Equal(2, f.Generic().GetObjectPartitions(f.Cell, f.RootObject).Count);
    }

    [Fact]
    public void NearestFileDefinitionOverridesTheAncestor()
    {
        var f = new GraphFixture("old bytes"u8.ToArray());
        f.OverrideChild("new bytes"u8.ToArray());
        Assert.Equal("new bytes"u8.ToArray(), f.File().Materialize());
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("cycle")]
    [InlineData("blob")]
    public void InvalidInheritedGraphLeavesDestinationUntouched(string fault)
    {
        var f = new GraphFixture([1, 2, 3], blob: fault == "blob");
        if (fault == "missing") f.SetBase(GraphFixture.Id(99));
        if (fault == "cycle") f.SetBase(f.Revision);
        if (fault == "blob") f.Elements.RemoveAll(e => e.DataElementExtendedGuid.Equals(f.Blob));
        using var destination = new MemoryStream();
        Assert.Throws<InvalidDataException>(() => f.File().MaterializeTo(destination));
        Assert.Equal(0, destination.Length);
    }
}
