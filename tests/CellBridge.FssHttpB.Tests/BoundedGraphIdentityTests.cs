using CellBridge.Tests;
using Xunit.Abstractions;

namespace CellBridge.FssHttpB.Tests;

public sealed class BoundedGraphIdentityTests(ITestOutputHelper output)
{
    private const int Seed = 0x34_18;
    private static readonly GenericGraphLimits Limits = new(MaxElements: 11, MaxPayloadBytes: 16 * 1024,
        MaxRevisionDepth: 2, MaxObjectVisits: 24, MaxReferenceVisits: 64);

    [Fact]
    public void ReferenceOrderPreservesAllowedOpaqueCyclesAndFullIdentityTargets()
    {
        var random = new Random(Seed);
        for (int sample = 0; sample < 12; sample++)
        {
            var f = new GraphFixture([1, 2, 3], sample % 2 == 0, GraphFixture.IdentityMap(sample % 3));
            ExGuid[] objects = sample % 2 == 0 ? [f.Child, f.RootObject] : [f.RootObject, f.Child];
            CellId[] cells = sample % 2 == 0 ? [f.OtherCell, f.Cell] : [f.Cell, f.OtherCell];
            f.SetRootReferences(objects, cells);
            f.PermuteRecords(random);
            var graph = GenericPartitionGraphSnapshot.Create(f.Elements.OrderBy(_ => random.Next()), f.Index, Limits);
            var root = Assert.Single(graph.GetObjectPartitions(f.Cell, f.RootObject), p => p.Declaration.PartitionId == 1);
            Assert.Equal(objects, root.ObjectReferences); Assert.Equal(cells, root.CellReferences);
            Assert.Equal(f.Bytes, Assert.Single(graph.GetObjectPartitions(f.Cell, f.Child)).Content);
            Assert.Equal("other cell is not file content"u8.ToArray(), Assert.Single(graph.GetObjectPartitions(f.OtherCell, f.Child)).Content);
            output.WriteLine($"seed={Seed} sample={sample} mutation=object/cell-reference-order/self-cycles");
        }
    }

    [Fact]
    public void RenamedAndPermutedGraphsResolveFullIdentitiesWithinCellAndPartition()
    {
        var random = new Random(Seed);
        for (int sample = 0; sample < 32; sample++)
        {
            byte[] content = new byte[random.Next(1, 1025)]; random.NextBytes(content);
            var f = new GraphFixture(content, sample % 2 == 0, GraphFixture.IdentityMap(sample % 3), sample % 2 != 0);
            if (sample % 4 == 0) f.ConnectCells(true);
            byte[] expected = content;
            if (sample % 5 == 0) { expected = content.Reverse().ToArray(); f.OverrideChild(expected); }
            f.PermuteRecords(random);
            var elements = f.Elements.OrderBy(_ => random.Next()).ToArray();
            var graph = GenericPartitionGraphSnapshot.Create(elements, f.Index, Limits);
            Assert.Equal(f.Revision, graph.GetCurrentRevision(f.Cell));
            Assert.Equal(f.OtherRevision, graph.GetCurrentRevision(f.OtherCell));
            Assert.Equal(expected, Assert.Single(graph.GetObjectPartitions(f.Cell, f.Child)).Content);
            Assert.Equal(sample % 4 == 0 ? new byte[] { 7 } : "other cell is not file content"u8.ToArray(),
                Assert.Single(graph.GetObjectPartitions(f.OtherCell, f.Child)).Content);
            Assert.Equal(new ulong[] { 1, 4 }, graph.GetObjectPartitions(f.Cell, f.RootObject)
                .Select(o => o.Declaration.PartitionId).Order());
            Assert.Equal(expected, PartitionGraphSnapshot.Create(elements, f.Index).Materialize(maxBytes: expected.Length));
            output.WriteLine($"seed={Seed} sample={sample} identities={sample % 3} swapped-halves={sample % 2 != 0} mutation=paired-record/package-order");
        }
    }

    [Theory]
    [InlineData("object-guid")]
    [InlineData("object-integer")]
    [InlineData("cell-guid")]
    [InlineData("cell-integer")]
    [InlineData("cell-halves")]
    [InlineData("cross-cell")]
    [InlineData("wrong-kind")]
    [InlineData("duplicate-cell-mapping")]
    [InlineData("duplicate-revision-mapping")]
    public void NearMatchesAndConflictingMappingKeysCannotResolveReferences(string mutation)
    {
        var random = new Random(Seed);
        for (int sample = 0; sample < 6; sample++)
        {
            var f = new GraphFixture([1, 2, 3], true, GraphFixture.IdentityMap(sample % 3), sample % 2 != 0);
            switch (mutation)
            {
                case "object-guid": f.SetRootReferences([new(f.Child.Value, Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff"))], []); break;
                case "object-integer": f.SetRootReferences([new(f.Child.Value + 1, f.Child.Guid)], []); break;
                case "cell-guid": f.SetRootReferences([f.Child], [new(new(f.Cell.LongId.Value, Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff")), f.Cell.ShortId)]); break;
                case "cell-integer": f.SetRootReferences([f.Child], [new(new(f.Cell.LongId.Value + 1, f.Cell.LongId.Guid), f.Cell.ShortId)]); break;
                case "cell-halves": f.SetRootReferences([f.Child], [new(f.Cell.ShortId, f.Cell.LongId)]); break;
                case "cross-cell":
                    // A real object in the other cell must not satisfy this cell's reference.
                    f.Replace(GraphFixture.GroupWithForeignObject(f.OtherGroup, f.Child, f.OtherRoot));
                    var valid = GenericPartitionGraphSnapshot.Create(f.Elements, f.Index, Limits);
                    Assert.Equal(new byte[] { 9 }, Assert.Single(valid.GetObjectPartitions(f.OtherCell, f.OtherRoot)).Content);
                    f.SetRootReferences([f.OtherRoot], []); break;
                case "wrong-kind":
                    f.Replace(GraphFixture.Element(DataElementType.RevisionManifestDataElementData, f.RevisionElement, w =>
                    {
                        GraphFixture.Record(w, StreamObjectTypeHeaderStart.RevisionManifest, b => { f.Revision.Serialize(b); f.BaseRevision.Serialize(b); });
                        GraphFixture.Record(w, StreamObjectTypeHeaderStart.RevisionManifestRootDeclare, b => { f.Root.Serialize(b); f.RootObject.Serialize(b); });
                        GraphFixture.Record(w, StreamObjectTypeHeaderStart.RevisionManifestObjectGroupReferences, f.Blob.Serialize);
                    })); break;
                default:
                    var index = f.Elements.Single(e => e.DataElementExtendedGuid.Equals(f.Index));
                    var writer = new BinaryWriterEx(); writer.WriteBytes(index.Data!);
                    if (mutation == "duplicate-cell-mapping")
                        GraphFixture.Record(writer, StreamObjectTypeHeaderStart.StorageIndexCellMapping, b =>
                        { f.Cell.Serialize(b); f.OtherCellManifest.Serialize(b); SerialNumber.Null.Serialize(b); });
                    else GraphFixture.Record(writer, StreamObjectTypeHeaderStart.StorageIndexRevisionMapping, b =>
                        { f.Revision.Serialize(b); f.OtherRevisionElement.Serialize(b); SerialNumber.Null.Serialize(b); });
                    index.Data = writer.ToArray(); break;
            }
            f.PermuteRecords(random);
            var error = Assert.Throws<InvalidDataException>(() => GenericPartitionGraphSnapshot.Create(
                f.Elements.OrderBy(_ => random.Next()), f.Index, Limits));
            Assert.DoesNotContain("budget", error.Message, StringComparison.OrdinalIgnoreCase);
            if (mutation == "cross-cell") Assert.Contains("Unresolved object reference in its cell/revision scope", error.Message);
            output.WriteLine($"seed={Seed} sample={sample} mutation={mutation} error={error.Message}");
        }
    }
}
