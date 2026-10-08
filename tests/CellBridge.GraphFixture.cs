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

    public GraphFixture(byte[] bytes, bool blob = false, Func<uint, ExGuid>? identity = null, bool swapCellHalves = false)
    {
        if (identity is not null)
        {
            Index = identity(1); Manifest = identity(2); CellManifest = identity(3); RevisionElement = identity(4);
            BaseElement = identity(5); CurrentGroup = identity(6); BaseGroup = identity(7); Blob = identity(8);
            OtherCellManifest = identity(9); OtherRevisionElement = identity(10); OtherGroup = identity(11);
            Revision = identity(20); BaseRevision = identity(21); OtherRevision = identity(22);
            RootObject = identity(30); Child = identity(31); OtherRoot = identity(32);
            Cell = new(identity(40), identity(41)); OtherCell = new(identity(42), identity(43));
        }
        // Root is a distinguished file-profile constant, not a generated identity.
        if (swapCellHalves)
        {
            Cell = new(Cell.ShortId, Cell.LongId); OtherCell = new(OtherCell.ShortId, OtherCell.LongId);
        }
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

    public FsshttpbCellRequest CreatePatch(DataElement expected, bool other)
    {
        var space = Guid.NewGuid(); ExGuid Id(uint value) => new(value, space);
        var cell = other ? this.OtherCell : this.Cell; var root = other ? this.OtherRoot : this.Root;
        var obj = other ? this.Child : this.RootObject; var parent = other ? this.OtherRevision : this.Revision;
        var group = this.Elements.Single(e => e.DataElementExtendedGuid.Equals(other ? this.OtherGroup : this.CurrentGroup));
        var index = GraphFixture.Element(DataElementType.StorageIndexDataElementData, Id(1), w =>
        {
            GraphFixture.Record(w, StreamObjectTypeHeaderStart.StorageIndexCellMapping, b => { cell.Serialize(b); Id(2).Serialize(b); new SerialNumber(space, 12).Serialize(b); });
            GraphFixture.Record(w, StreamObjectTypeHeaderStart.StorageIndexRevisionMapping, b => { Id(3).Serialize(b); Id(4).Serialize(b); new SerialNumber(space, 14).Serialize(b); });
        });
        var request = new FsshttpbCellRequest { DataElementPackage = new() { DataElements = { index } }, SubRequests = { new(RequestTypes.PutChanges) { RequestId = 1, Data = new PutChangesSubRequestData { StorageIndex = index.DataElementExtendedGuid, ExpectedStorageIndex = expected.DataElementExtendedGuid, HasAdditionalFlags = true, AdditionalFlagsBits = 1 } } } };
        request.DataElementPackage!.DataElements.AddRange([
            expected,
            GraphFixture.Element(DataElementType.CellManifestDataElementData, Id(2), w => GraphFixture.Record(w, StreamObjectTypeHeaderStart.CellManifestCurrentRevision, Id(3).Serialize)),
            GraphFixture.Element(DataElementType.RevisionManifestDataElementData, Id(4), w =>
            {
                GraphFixture.Record(w, StreamObjectTypeHeaderStart.RevisionManifest, b => { Id(3).Serialize(b); parent.Serialize(b); });
                GraphFixture.Record(w, StreamObjectTypeHeaderStart.RevisionManifestRootDeclare, b => { root.Serialize(b); obj.Serialize(b); });
                GraphFixture.Record(w, StreamObjectTypeHeaderStart.RevisionManifestObjectGroupReferences, Id(5).Serialize);
            }),
            new DataElement(group.DataElementType, Id(5), SerialNumber.Null) { Data = group.Data!.ToArray() },
        ]);
        return request;
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
    public void SetRootReferences(ExGuid[] objects, CellId[] cells) => Replace(Group(CurrentGroup,
        new Item(RootObject, 1, [], objects, Cells: cells), new Item(RootObject, 4, [255])));

    /// <summary>Permutes records within their grammar positions, pairing each declaration with its data.</summary>
    public void PermuteRecords(Random random)
    {
        foreach (var element in Elements)
        {
            if (element.DataElementType == DataElementType.StorageIndexDataElementData)
            {
                var records = ReadRecords(new(element.Data!));
                element.Data = records.GroupBy(r => r.Type).SelectMany(g => g.OrderBy(_ => random.Next()))
                    .SelectMany(r => r.Bytes).ToArray();
            }
            else if (element.DataElementType == DataElementType.ObjectGroupDataElementData)
            {
                var reader = new BinaryReaderEx(element.Data!);
                int start = reader.Position; StreamObjectHeaderStart.Parse(reader);
                byte[] declarationStart = element.Data![start..reader.Position];
                var declarations = ReadRecords(reader);
                start = reader.Position; StreamObjectHeaderEnd.Parse(reader);
                byte[] declarationEnd = element.Data![start..reader.Position];
                start = reader.Position; StreamObjectHeaderStart.Parse(reader);
                byte[] dataStart = element.Data![start..reader.Position];
                var data = ReadRecords(reader);
                byte[] dataEnd = element.Data![reader.Position..];
                int[] order = Enumerable.Range(0, declarations.Count).OrderBy(_ => random.Next()).ToArray();
                element.Data = [..declarationStart, ..order.SelectMany(i => declarations[i].Bytes), ..declarationEnd,
                    ..dataStart, ..order.SelectMany(i => data[i].Bytes), ..dataEnd];
            }
        }
    }

    private static List<(StreamObjectTypeHeaderStart Type, byte[] Bytes)> ReadRecords(BinaryReaderEx reader)
    {
        var result = new List<(StreamObjectTypeHeaderStart, byte[])>();
        while (reader.Remaining > 0)
        {
            int position = reader.Position;
            byte first = reader.ReadByte(); reader.Position = position;
            if ((first & 3) is 1 or 3) break;
            var header = StreamObjectHeaderStart.Parse(reader);
            reader.Skip(header.Length);
            int end = reader.Position; reader.Position = position;
            result.Add((header.Type, reader.ReadBytes(end - position)));
        }
        return result;
    }
    public void MakeSelfContainedWithUnmappedAncestor()
    {
        Replace(Group(CurrentGroup, new Item(RootObject, 1, [], [Child]), new Item(Child, 1, Bytes)));
        SetBase(Id(99));
    }
    public static ExGuid Id(uint value) => new(value, Namespace);
    public static Func<uint, ExGuid> IdentityMap(int mode) => value => mode switch
    {
        0 => new(value, Namespace),
        1 => new(31, new Guid((int)value, 1, 2, [3, 4, 5, 6, 7, 8, 9, 10])),
        2 => new(value * 4096, Namespace),
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };
    public static DataElement GroupWithForeignObject(ExGuid group, ExGuid root, ExGuid foreign) =>
        Group(group, new Item(root, 1, [7]), new Item(foreign, 1, [9]));
    public static SerialNumber Mapping(ExGuid id) => new(id.Guid, id.Value + 1000UL);
    public static DataElement Element(DataElementType type, ExGuid id, Action<BinaryWriterEx> write)
    {
        var writer = new BinaryWriterEx(); write(writer);
        return new(type, id, new(id.Guid, id.Value)) { Data = writer.ToArray() };
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
