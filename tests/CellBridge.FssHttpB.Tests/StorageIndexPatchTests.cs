using CellBridge.Tests;

namespace CellBridge.FssHttpB.Tests;

public sealed class StorageIndexPatchTests
{
    [Fact]
    public void IndependentKeyUpdatesPreserveCurrentMappingsAndCompareMappedValues()
    {
        var f = new GraphFixture([1]);
        var index = f.Elements.Single(e => e.DataElementExtendedGuid.Equals(f.Index));
        var current = StorageIndexPatch.Read(index);
        var target = new ExGuid(99, Guid.NewGuid());
        var cell = current.Single(m => m.Cell?.Equals(f.Cell) == true);
        var update = cell with { Target = target, Serial = new(Guid.NewGuid(), 10) };
        Assert.True(StorageIndexPatch.IsCoherent(current, [update], [cell with { Serial = SerialNumber.Null }], true));
        Assert.False(StorageIndexPatch.IsCoherent(current, [update], [], true));
        Assert.True(StorageIndexPatch.IsCoherent(current, [update], [], false));
        var merged = new DataElement(DataElementType.StorageIndexDataElementData, new(1, Guid.NewGuid()), SerialNumber.Null)
            { Data = StorageIndexPatch.Merge(current, [update]) };
        var mappings = StorageIndexPatch.Read(merged);
        Assert.Equal(current.Count, mappings.Count);
        Assert.Contains(update, mappings);
        Assert.Contains(current.Single(m => m.Cell?.Equals(f.OtherCell) == true), mappings);
        Assert.False(StorageIndexPatch.IsCoherent(mappings, [update], [cell], true));
    }

    [Fact]
    public void EmptyIndexIsValidAndAManifestStillRequiresRoots()
    {
        var indexId = new ExGuid(1, Guid.NewGuid());
        var empty = new DataElement(DataElementType.StorageIndexDataElementData, indexId, SerialNumber.Null) { Data = [] };
        var graph = GenericPartitionGraphSnapshot.Create([empty], indexId);
        Assert.Empty(graph.Cells); Assert.Empty(graph.Roots); Assert.Single(graph.RequiredElements);
        var f = new GraphFixture([1]);
        f.Replace(GraphFixture.Element(DataElementType.StorageManifestDataElementData, f.Manifest, w =>
            GraphFixture.Record(w, StreamObjectTypeHeaderStart.StorageManifestSchemaGUID, b => b.WriteBytes(Guid.NewGuid().ToByteArray()))));
        Assert.Throws<InvalidDataException>(() => f.Generic());
        var mappings = StorageIndexPatch.Read(f.Elements[0]);
        Assert.Equal(StorageIndexPatch.Serialize(mappings), StorageIndexPatch.Merge(mappings, []));
    }

    [Fact]
    public void DuplicateMappingKeysAndDanglingDeletedTargetsAreRejected()
    {
        var f = new GraphFixture([1]);
        var mapping = StorageIndexPatch.Read(f.Elements[0])[0];
        var index = new DataElement(DataElementType.StorageIndexDataElementData, f.Index, SerialNumber.Null)
            { Data = StorageIndexPatch.Serialize([mapping, mapping]) };
        Assert.Throws<InvalidDataException>(() => StorageIndexPatch.Read(index));
        var mappings = StorageIndexPatch.Read(f.Elements[0]);
        f.Replace(new(DataElementType.StorageIndexDataElementData, f.Index, SerialNumber.Null)
            { Data = StorageIndexPatch.Serialize(mappings.Select(m => m.Cell?.Equals(f.Cell) == true ? m with { Target = ExGuid.Null } : m)) });
        Assert.Throws<InvalidDataException>(() => f.Generic());
    }
}
