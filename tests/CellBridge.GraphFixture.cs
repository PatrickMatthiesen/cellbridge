using CellBridge.FssHttpB;

namespace CellBridge.Tests;

/// <summary>Deterministic multi-cell graph with inherited file data and an unrelated metadata partition.</summary>
public sealed class GraphFixture
{
    private static readonly Guid Namespace = Guid.Parse("f43ef647-0b13-4b0d-a8ea-43d834c8a778");
    public ExGuid Index = Id(1), Manifest = Id(2), CellManifest = Id(3), RevisionElement = Id(4),
        BaseElement = Id(5), CurrentGroup = Id(6), BaseGroup = Id(7), Blob = Id(8),
        OtherCellManifest = Id(9), OtherRevisionElement = Id(10), OtherGroup = Id(11),
        Revision = Id(20), BaseRevision = Id(21), OtherRevision = Id(22),
        RootObject = Id(30), Child = Id(31), OtherRoot = Id(32);
    public CellId Cell = new(Id(40), Id(41)), OtherCell = new(Id(42), Id(43));
    public ExGuid Root = new(2, StorageManifestBuilder.RootExtendedGuid);
    public List<DataElement> Elements { get; }
    public byte[] Bytes { get; }

    public GraphFixture(byte[] bytes, bool blob = false)
    {
        Bytes = bytes;
        Elements = [
            Element(DataElementType.StorageIndexDataElementData, Index, w =>
            {
                Record(w, StreamObjectTypeHeaderStart.StorageIndexManifestMapping, b => { Manifest.Serialize(b); Mapping(Manifest).Serialize(b); });
                foreach (var (cell, target) in new[] { (Cell, CellManifest), (OtherCell, OtherCellManifest) })
                    Record(w, StreamObjectTypeHeaderStart.StorageIndexCellMapping, b => { cell.Serialize(b); target.Serialize(b); Mapping(target).Serialize(b); });
                foreach (var (revision, target) in new[] { (Revision, RevisionElement), (BaseRevision, BaseElement), (OtherRevision, OtherRevisionElement) })
                    Record(w, StreamObjectTypeHeaderStart.StorageIndexRevisionMapping, b => { revision.Serialize(b); target.Serialize(b); Mapping(target).Serialize(b); });
            }),
            Element(DataElementType.StorageManifestDataElementData, Manifest, w =>
            {
                Record(w, StreamObjectTypeHeaderStart.StorageManifestSchemaGUID, b => b.WriteBytes(StorageManifestBuilder.StorageManifestSchemaGuid.ToByteArray()));
                foreach (var (root, cell) in new[] { (Root, Cell), (OtherRoot, OtherCell) })
                    Record(w, StreamObjectTypeHeaderStart.StorageManifestRootDeclare, b => { root.Serialize(b); cell.Serialize(b); });
            }),
            Element(DataElementType.CellManifestDataElementData, CellManifest, w => Record(w, StreamObjectTypeHeaderStart.CellManifestCurrentRevision, Revision.Serialize)),
            Element(DataElementType.CellManifestDataElementData, OtherCellManifest, w => Record(w, StreamObjectTypeHeaderStart.CellManifestCurrentRevision, OtherRevision.Serialize)),
            RevisionRecord(RevisionElement, Revision, BaseRevision, Root, RootObject, CurrentGroup),
            RevisionRecord(BaseElement, BaseRevision, ExGuid.Null, Root, RootObject, BaseGroup),
            RevisionRecord(OtherRevisionElement, OtherRevision, ExGuid.Null, OtherRoot, Child, OtherGroup),
            Group(CurrentGroup, new Item(RootObject, 1, [], [Child]), new Item(RootObject, 4, "metadata is not file content"u8.ToArray())),
            Group(BaseGroup, new Item(RootObject, 1, "superseded root"u8.ToArray()),
                new Item(Child, 1, bytes, Blob: blob ? Blob : null)),
            Group(OtherGroup, new Item(Child, 1, "other cell is not file content"u8.ToArray())),
        ];
        if (blob) Elements.Add(Element(DataElementType.ObjectDataBLOBDataElementData, Blob,
            w => Record(w, StreamObjectTypeHeaderStart.ObjectDataBLOB, b => b.WriteBytes(bytes))));
    }

    public GenericPartitionGraphSnapshot Generic() => GenericPartitionGraphSnapshot.Create(Elements, Index);
    public PartitionGraphSnapshot File() => PartitionGraphSnapshot.Create(Elements, Index);
    public void Replace(DataElement replacement) => Elements[Elements.FindIndex(e => e.DataElementExtendedGuid.Equals(replacement.DataElementExtendedGuid))] = replacement;
    public void SetBase(ExGuid parent) => Replace(RevisionRecord(BaseElement, BaseRevision, parent, Root, RootObject, BaseGroup));
    public void OverrideChild(byte[] bytes) => Replace(Group(CurrentGroup, new Item(RootObject, 1, [], [Child]),
        new Item(RootObject, 4, [255]), new Item(Child, 1, bytes)));
    public void ConnectCells(bool cycle)
    {
        Replace(Group(CurrentGroup, new Item(RootObject, 1, [], [Child], Cells: [OtherCell]),
            new Item(RootObject, 4, [255])));
        if (cycle) Replace(Group(OtherGroup, new Item(Child, 1, [7], Cells: [Cell])));
    }
    public void SetOverriddenAncestorReference(CellId cell) => Replace(Group(BaseGroup,
        new Item(RootObject, 1, [], Cells: [cell]), new Item(Child, 1, Bytes)));
    public void MakeSelfContainedWithUnmappedAncestor()
    {
        Replace(Group(CurrentGroup, new Item(RootObject, 1, [], [Child]), new Item(Child, 1, Bytes)));
        SetBase(Id(99));
    }
    public static ExGuid Id(uint value) => new(value, Namespace);
    public static SerialNumber Mapping(ExGuid id) => new(Namespace, id.Value + 1000UL);
    public static DataElement Element(DataElementType type, ExGuid id, Action<BinaryWriterEx> write)
    {
        var writer = new BinaryWriterEx(); write(writer);
        return new(type, id, new(Namespace, id.Value)) { Data = writer.ToArray() };
    }
    public static void Record(BinaryWriterEx writer, StreamObjectTypeHeaderStart type, Action<BinaryWriterEx> write)
    {
        var body = new BinaryWriterEx(); write(body);
        new StreamObjectHeaderStart32Bit(type, body.Length).Serialize(writer);
        writer.WriteBytes(body.ToArray());
    }
    private static DataElement RevisionRecord(ExGuid element, ExGuid revision, ExGuid parent, ExGuid root, ExGuid obj, ExGuid group) =>
        Element(DataElementType.RevisionManifestDataElementData, element, w =>
        {
            Record(w, StreamObjectTypeHeaderStart.RevisionManifest, b => { revision.Serialize(b); parent.Serialize(b); });
            Record(w, StreamObjectTypeHeaderStart.RevisionManifestRootDeclare, b => { root.Serialize(b); obj.Serialize(b); });
            Record(w, StreamObjectTypeHeaderStart.RevisionManifestObjectGroupReferences, group.Serialize);
        });
    private static DataElement Group(ExGuid element, params Item[] items) => Element(DataElementType.ObjectGroupDataElementData, element, w =>
    {
        new StreamObjectHeaderStart32Bit(StreamObjectTypeHeaderStart.ObjectGroupDeclarations, 0).Serialize(w);
        foreach (var item in items)
            Record(w, item.Blob is null ? StreamObjectTypeHeaderStart.ObjectGroupObjectDeclare : StreamObjectTypeHeaderStart.ObjectGroupObjectBLOBDataDeclaration, b =>
            {
                item.Object.Serialize(b); item.Blob?.Serialize(b);
                new Compact64bitInt(item.Partition).Serialize(b);
                if (item.Blob is null) new Compact64bitInt((ulong)item.Bytes.Length).Serialize(b);
                new Compact64bitInt((ulong)(item.Objects?.Length ?? 0)).Serialize(b);
                new Compact64bitInt((ulong)(item.Cells?.Length ?? 0)).Serialize(b);
            });
        new StreamObjectHeaderEnd8Bit(StreamObjectTypeHeaderEnd.ObjectGroupDeclarations).Serialize(w);
        new StreamObjectHeaderStart32Bit(StreamObjectTypeHeaderStart.ObjectGroupData, 0).Serialize(w);
        foreach (var item in items)
            Record(w, item.Blob is null ? StreamObjectTypeHeaderStart.ObjectGroupObjectData : StreamObjectTypeHeaderStart.ObjectGroupObjectDataBLOBReference, b =>
            {
                new Compact64bitInt((ulong)(item.Objects?.Length ?? 0)).Serialize(b);
                foreach (var obj in item.Objects ?? []) obj.Serialize(b);
                new Compact64bitInt((ulong)(item.Cells?.Length ?? 0)).Serialize(b);
                foreach (var cell in item.Cells ?? []) cell.Serialize(b);
                if (item.Blob is null) new BinaryItem(item.Bytes).Serialize(b); else item.Blob.Serialize(b);
            });
        new StreamObjectHeaderEnd8Bit(StreamObjectTypeHeaderEnd.ObjectGroupData).Serialize(w);
    });
    private sealed record Item(ExGuid Object, ulong Partition, byte[] Bytes, ExGuid[]? Objects = null, ExGuid? Blob = null,
        CellId[]? Cells = null);
}
