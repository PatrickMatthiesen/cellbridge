using CellBridge.FssHttpB;
using CellBridge.Tests;

namespace CellBridge.FssHttpB.Tests;

public sealed class HistoricalGraphRestorerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RestoreRetainsInheritedObjectsBlobRootsAndCells(bool blob)
    {
        var fixture = new GraphFixture("historical bytes"u8.ToArray(), blob);
        var original = fixture.Generic();
        var publication = HistoricalGraphRestorer.Rebase(original, original, Guid.NewGuid(), 5000);
        Assert.Equal(original.SchemaGuid, publication.Graph.SchemaGuid);
        Assert.Equal(original.Cells.OrderBy(c => c.LongId.Value), publication.Graph.Cells.OrderBy(c => c.LongId.Value));
        Assert.Equal(original.Roots, publication.Graph.Roots);
        Assert.Equal(fixture.Bytes, PartitionGraphSnapshot.Create(publication.Graph.Elements, publication.Graph.StorageIndex).Materialize());
        foreach (var cell in original.Cells)
        {
            Assert.NotEqual(original.GetCurrentRevision(cell), publication.Graph.GetCurrentRevision(cell));
            Assert.Equal(original.GetRevisionRoots(cell), publication.Graph.GetRevisionRoots(cell));
        }
        foreach (var element in original.Elements)
            Assert.Contains(publication.Graph.Elements, e => e.DataElementExtendedGuid.Equals(element.DataElementExtendedGuid) &&
                e.SerialNumber.Equals(element.SerialNumber) && (e.Data ?? []).SequenceEqual(element.Data ?? []));
        Assert.True(publication.Knowledge > 5000);
    }

    [Fact]
    public void RestoringEarlierCellSetDoesNotSelectCurrentOnlyCells()
    {
        var fixture = new GraphFixture([1, 2, 3]);
        var current = fixture.Generic();
        var selectedElements = fixture.Elements.Select(e => new DataElement(e.DataElementType,
            e.DataElementExtendedGuid, e.SerialNumber) { Data = e.Data }).ToList();
        var newManifestId = new ExGuid(1, Guid.NewGuid());
        var manifest = StorageManifestBuilder.BuildStorageManifestDataElement(newManifestId, new(Guid.NewGuid(), 1), fixture.Root, fixture.Cell);
        selectedElements.Add(manifest);
        var newIndex = new ExGuid(1, Guid.NewGuid());
        selectedElements.Add(StorageManifestBuilder.BuildStorageIndexDataElement(newIndex, new(Guid.NewGuid(), 2),
            newManifestId, manifest.SerialNumber, fixture.Cell, fixture.CellManifest, GraphFixture.Mapping(fixture.CellManifest),
            fixture.Revision, fixture.RevisionElement, GraphFixture.Mapping(fixture.RevisionElement)));
        // The inherited base also needs a mapping in the selected index.
        var index = selectedElements[^1];
        var writer = new BinaryWriterEx(); writer.WriteBytes(index.Data!);
        GraphFixture.Record(writer, StreamObjectTypeHeaderStart.StorageIndexRevisionMapping, b =>
        { fixture.BaseRevision.Serialize(b); fixture.BaseElement.Serialize(b); GraphFixture.Mapping(fixture.BaseElement).Serialize(b); });
        index.Data = writer.ToArray();
        var selected = GenericPartitionGraphSnapshot.Create(selectedElements, newIndex);
        var restored = HistoricalGraphRestorer.Rebase(current, selected, Guid.NewGuid(), 0).Graph;
        Assert.Single(restored.Cells);
        Assert.DoesNotContain(fixture.OtherCell, restored.Cells);
        Assert.Contains(restored.Elements, e => e.DataElementExtendedGuid.Equals(fixture.OtherGroup));
    }

    [Fact]
    public void ConflictingHistoricalIdentityFailsWithoutMutatingInput()
    {
        var fixture = new GraphFixture([1, 2]);
        var original = fixture.Generic();
        fixture.OverrideChild([3, 4]);
        Assert.Throws<InvalidDataException>(() => HistoricalGraphRestorer.Rebase(original, fixture.Generic(), Guid.NewGuid(), 0));
        Assert.Equal(new byte[] { 1, 2 }, PartitionGraphSnapshot.Create(original.Elements, original.StorageIndex).Materialize());
    }
}
