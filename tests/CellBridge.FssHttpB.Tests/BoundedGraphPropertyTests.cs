using CellBridge.Tests;
using Xunit.Abstractions;

namespace CellBridge.FssHttpB.Tests;

public sealed class BoundedGraphPropertyTests(ITestOutputHelper output)
{
    private const int Seed = 0x34_18;

    [Fact]
    public void GeneratedInheritedGraphsPreserveCellPartitionBlobAndNearestDefinition()
    {
        var random = new Random(Seed);
        for (int sample = 0; sample < 32; sample++)
        {
            byte[] bytes = new byte[random.Next(1, 4097)]; random.NextBytes(bytes);
            bool blob = sample % 2 == 0, replace = sample % 3 == 0, cycle = sample % 5 == 0;
            var f = new GraphFixture(bytes, blob);
            byte[] expected = bytes;
            if (cycle) f.ConnectCells(true); // Opaque cell cycles are allowed.
            if (replace) { expected = bytes.Reverse().ToArray(); f.OverrideChild(expected); }
            output.WriteLine($"seed={Seed} sample={sample} bytes={bytes.Length} blob={blob} override={replace} cycle={cycle}");
            var graph = f.Generic();
            Assert.Equal(2, graph.Cells.Count);
            Assert.Equal(expected, Assert.Single(graph.GetObjectPartitions(f.Cell, f.Child)).Content);
            Assert.NotEqual(expected, Assert.Single(graph.GetObjectPartitions(f.OtherCell, f.Child)).Content);
            Assert.Equal(new ulong[] { 1, 4 }, graph.GetObjectPartitions(f.Cell, f.RootObject)
                .Select(o => o.Declaration.PartitionId).Order());
            Assert.Equal(blob && !replace ? f.Blob : null,
                Assert.Single(graph.GetObjectPartitions(f.Cell, f.Child)).Declaration.BlobReference);
            Assert.Equal(expected, f.File().Materialize(maxBytes: expected.Length));
            if (blob) Assert.Contains(f.Blob, graph.RequiredElements);

            var duplicate = new GraphFixture(bytes, blob);
            duplicate.Elements.Add(duplicate.Elements[0]);
            Assert.Contains("duplicate", Assert.Throws<InvalidDataException>(() => duplicate.Generic()).Message);
            var revisionCycle = new GraphFixture(bytes, blob); revisionCycle.SetBase(revisionCycle.Revision);
            Assert.Contains("cycle", Assert.Throws<InvalidDataException>(() => revisionCycle.Generic()).Message);
            var missingBlob = new GraphFixture(bytes, true);
            missingBlob.Elements.RemoveAll(e => e.DataElementExtendedGuid.Equals(missingBlob.Blob));
            Assert.Throws<InvalidDataException>(() => missingBlob.Generic());
        }
    }

    [Theory]
    [InlineData("elements")]
    [InlineData("payload")]
    [InlineData("depth")]
    [InlineData("objects")]
    [InlineData("references")]
    public void ExactGraphBudgetBoundaryAcceptsAndOneLessRejects(string budget)
    {
        var f = new GraphFixture([1, 2, 3], blob: true);
        // This model has five declarations visited once while parsing and again
        // while resolving cells; six index edges, two roots, six revision records,
        // three ancestry visits, two revision roots and one effective object edge.
        long boundary = budget switch
        {
            "elements" => f.Elements.Count, "payload" => f.Elements.Sum(e => e.Data!.LongLength),
            "depth" => 2, "objects" => 10, "references" => 20, _ => throw new ArgumentException(budget),
        };
        GenericGraphLimits Limits(long value) => budget switch
        {
            "elements" => new(MaxElements: (int)value), "payload" => new(MaxPayloadBytes: value),
            "depth" => new(MaxRevisionDepth: (int)value), "objects" => new(MaxObjectVisits: (int)value),
            "references" => new(MaxReferenceVisits: (int)value), _ => throw new ArgumentException(budget),
        };
        output.WriteLine($"seed={Seed} model=GraphFixture budget={budget} boundary={boundary}");
        Assert.Equal(f.Elements.Count, GenericPartitionGraphSnapshot.Create(f.Elements, f.Index, Limits(boundary)).ElementCount);
        Assert.Throws<InvalidDataException>(() => GenericPartitionGraphSnapshot.Create(f.Elements, f.Index, Limits(boundary - 1)));
    }
}
