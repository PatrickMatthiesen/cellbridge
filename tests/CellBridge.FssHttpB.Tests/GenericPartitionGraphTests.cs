using CellBridge.FssHttpB;

public sealed class GenericPartitionGraphTests
{
    [Fact]
    public void PreservesSchemaRootsCellsAndOpaquePartitionsWithNearestOverrides()
    {
        var f = new Fixture();
        var graph = f.Create();
        Assert.Equal(f.Schema, graph.SchemaGuid);
        Assert.Equal(f.CellA, graph.Roots[0].Cell);
        Assert.Equal(f.CellB, graph.Roots[1].Cell);
        var partitions = graph.GetObjectPartitions(f.CellA, f.Object);
        Assert.Equal("new", Text(Assert.Single(partitions, p => p.Declaration.PartitionId == 1)));
        Assert.Equal("metadata", Text(Assert.Single(partitions, p => p.Declaration.PartitionId == 4)));
        Assert.Equal("other-cell", Text(Assert.Single(graph.GetObjectPartitions(f.CellB, f.Object))));
        Assert.Equal(f.RevisionA, graph.GetCurrentRevision(f.CellA));
        Assert.Equal(f.Object, Assert.Single(graph.GetRevisionRoots(f.CellA)).Object);
        Assert.Equal(f.Elements.Count, graph.RequiredElements.Count);
    }

    [Fact]
    public void ResolvesBlobIdentityAndPreservesAllowedOpaqueCycles()
    {
        var f = new Fixture();
        f.Replace(f.GroupNew, Group(f.GroupNew,
            new Item(f.Object, 1, [0xFF], [f.Object], [f.CellB], f.Blob)));
        f.Elements.Add(Element(DataElementType.ObjectDataBLOBDataElementData, f.Blob,
            w => Record(w, StreamObjectTypeHeaderStart.ObjectDataBLOB, b => b.WriteBytes("attachment"u8))));
        var graph = f.Create();
        var obj = Assert.Single(graph.GetObjectPartitions(f.CellA, f.Object), o => o.Declaration.PartitionId == 1);
        Assert.Equal("attachment", Text(obj));
        Assert.Equal(f.Blob, obj.Declaration.BlobReference);
        Assert.Equal(f.Object, Assert.Single(obj.ObjectReferences));
        Assert.Equal(f.CellB, Assert.Single(obj.CellReferences));
        Assert.Contains(f.Blob, graph.RequiredElements);
    }

    [Fact]
    public void OpaqueBytesResemblingFileNodesAreNotInterpreted()
    {
        var f = new Fixture();
        var writer = new BinaryWriterEx();
        new StreamObjectHeaderStart32Bit(StreamObjectTypeHeaderStart.LeafNodeObject, 0).Serialize(writer);
        var group = Group(f.GroupNew, new Item(f.Object, 1, writer.ToArray()));
        Assert.ThrowsAny<Exception>(() => ObjectGroupDataElement.Parse(group));
        f.Replace(f.GroupNew, group);
        Assert.Equal(writer.ToArray(), Assert.Single(f.Create().GetObjectPartitions(f.CellA, f.Object),
            o => o.Declaration.PartitionId == 1).Content);
    }

    [Fact]
    public void InputAndReturnedValuesCannotMutateTheSnapshot()
    {
        var f = new Fixture();
        var graph = f.Create();
        var cell = new CellId(new(f.CellA.LongId.Value, f.CellA.LongId.Guid), new(f.CellA.ShortId.Value, f.CellA.ShortId.Guid));
        foreach (var element in f.Elements) { element.Data!.AsSpan().Clear(); element.DataElementExtendedGuid.Guid = Guid.Empty; }
        graph.StorageIndex.Guid = Guid.Empty;
        graph.Roots[0].Cell.LongId.Guid = Guid.Empty;
        graph.Roots[0].Root.Guid = Guid.Empty;
        graph.GetCurrentRevision(cell).Guid = Guid.Empty;
        graph.GetRevisionRoots(cell)[0].Object.Guid = Guid.Empty;
        var first = graph.GetObjects(cell).First();
        first.ObjectGuid.Guid = Guid.Empty;
        first.Content.AsSpan().Clear();
        graph.Elements.First().Data!.AsSpan().Clear();
        var partitions = graph.GetObjectPartitions(cell, f.Object);
        Assert.Equal("new", Text(Assert.Single(partitions, p => p.Declaration.PartitionId == 1)));
        Assert.Equal("metadata", Text(Assert.Single(partitions, p => p.Declaration.PartitionId == 4)));
    }

    [Fact]
    public void AnObjectInAnotherCellCannotSatisfyALocalReference()
    {
        var f = new Fixture();
        var foreign = Id(90);
        f.Replace(f.GroupB, Group(f.GroupB, new Item(f.Object, 1, "other-cell"u8.ToArray()), new Item(foreign, 1, [1])));
        f.Replace(f.GroupNew, Group(f.GroupNew, new Item(f.Object, 1, [1], [foreign])));
        Assert.Throws<InvalidDataException>(() => f.Create());
    }

    [Fact]
    public void ConflictingDefinitionsInOneRevisionAreRejected()
    {
        var f = new Fixture();
        f.Replace(f.GroupNew, Group(f.GroupNew, new Item(f.Object, 1, [1]), new Item(f.Object, 1, [2])));
        Assert.Throws<InvalidDataException>(() => f.Create());
    }

    [Theory]
    [InlineData("missing-base")]
    [InlineData("base-cycle")]
    [InlineData("revision-mismatch")]
    [InlineData("wrong-kind")]
    [InlineData("missing-blob")]
    [InlineData("detached-self-cycle")]
    [InlineData("detached-missing-base")]
    public void RejectsIncompleteOrInconsistentMappedGraph(string fault)
    {
        var f = new Fixture();
        switch (fault)
        {
            case "missing-base": f.Replace(f.RevisionElementA, Revision(f.RevisionElementA, f.RevisionA, Id(99), f.RootA, f.Object, f.GroupNew)); break;
            case "base-cycle": f.Replace(f.RevisionElementOld, Revision(f.RevisionElementOld, f.RevisionOld, f.RevisionA, f.RootA, f.Object, f.GroupOld)); break;
            case "revision-mismatch": f.Replace(f.RevisionElementA, Revision(f.RevisionElementA, Id(99), ExGuid.Null, f.RootA, f.Object, f.GroupNew)); break;
            case "wrong-kind": f.Elements.Single(e => e.DataElementExtendedGuid.Equals(f.GroupNew)).DataElementType = DataElementType.ObjectDataBLOBDataElementData; break;
            case "missing-blob": f.Replace(f.GroupNew, Group(f.GroupNew, new Item(f.Object, 1, [], Blob: f.Blob))); break;
            case "detached-self-cycle":
            case "detached-missing-base":
                f.Replace(f.RevisionElementA, Revision(f.RevisionElementA, f.RevisionA, ExGuid.Null, f.RootA, f.Object, f.GroupNew));
                f.Replace(f.RevisionElementOld, Revision(f.RevisionElementOld, f.RevisionOld,
                    fault == "detached-self-cycle" ? f.RevisionOld : Id(99), f.RootA, f.Object, f.GroupOld));
                break;
        }
        Assert.Throws<InvalidDataException>(() => f.Create());
    }

    [Fact]
    public void RejectsStorageManifestWithoutRoots()
    {
        var f = new Fixture();
        f.Replace(f.Manifest, Element(DataElementType.StorageManifestDataElementData, f.Manifest,
            w => Record(w, StreamObjectTypeHeaderStart.StorageManifestSchemaGUID, b => b.WriteBytes(f.Schema.ToByteArray()))));
        Assert.Throws<InvalidDataException>(() => f.Create());
    }

    [Fact]
    public void OpaqueParserRejectsMalformedContainerAndRecordHeaders()
    {
        foreach (bool container in new[] { true, false })
        {
            var f = new Fixture();
            var group = f.Elements.Single(e => e.DataElementExtendedGuid.Equals(f.GroupNew));
            group.Data![container ? 0 : 4] ^= 4; // Compound bit of container or first declaration.
            Assert.Throws<InvalidDataException>(() => f.Create());
        }
    }

    [Fact]
    public void CountsReferencesInDetachedHistoryBeforeAllocatingThem()
    {
        var f = new Fixture();
        f.Replace(f.RevisionElementA, Revision(f.RevisionElementA, f.RevisionA, ExGuid.Null, f.RootA, f.Object, f.GroupNew));
        f.Replace(f.GroupOld, Group(f.GroupOld, new Item(f.Object, 1, [1], Enumerable.Repeat(f.Object, 51).ToArray())));
        var error = Assert.Throws<InvalidDataException>(() => GenericPartitionGraphSnapshot.Create(f.Elements, f.Index,
            new GenericGraphLimits(MaxReferenceVisits: 50)));
        Assert.Contains("parsing budget", error.Message);
    }

    [Fact]
    public void EnforcesElementPayloadRevisionAndTraversalBudgets()
    {
        var f = new Fixture();
        foreach (var limits in new[]
        {
            new GenericGraphLimits(MaxElements: 1), new GenericGraphLimits(MaxPayloadBytes: 1),
            new GenericGraphLimits(MaxRevisionDepth: 1), new GenericGraphLimits(MaxObjectVisits: 1),
            new GenericGraphLimits(MaxReferenceVisits: 1),
        }) Assert.Throws<InvalidDataException>(() => GenericPartitionGraphSnapshot.Create(f.Elements, f.Index, limits));
    }

    [Fact]
    public void ExistingFileGraphCanBeReadOpaqueWithoutChangingFileMaterialization()
    {
        byte[] bytes = "file adapter regression"u8.ToArray();
        var response = StorageManifestBuilder.BuildQueryChangesResponse(1, bytes);
        var query = Assert.IsType<QueryChangesSubResponseData>(Assert.Single(response.SubResponses).Data);
        var graph = GenericPartitionGraphSnapshot.Create(response.DataElementPackage!.DataElements, query.StorageIndexExtendedGuid);
        Assert.Equal(StorageManifestBuilder.StorageManifestSchemaGuid, graph.SchemaGuid);
        Assert.NotEmpty(graph.GetObjects(Assert.Single(graph.Cells)));
        Assert.Equal(bytes, PartitionGraphSnapshot.Create(graph.Elements, graph.StorageIndex).Materialize());
    }

    private static string Text(ObjectGroupObject obj) => System.Text.Encoding.UTF8.GetString(obj.Content);
    private static ExGuid Id(uint value) => new(value, new Guid("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"));
    private sealed record Item(ExGuid Object, ulong Partition, byte[] Bytes, ExGuid[]? Objects = null, CellId[]? Cells = null, ExGuid? Blob = null);

    private sealed class Fixture
    {
        public Guid Schema = new("1F937CB4-B26F-445F-B9F8-17E20160E461");
        public ExGuid Index = Id(1), Manifest = Id(2), RootA = Id(3), RootB = Id(4), Object = Id(5),
            RevisionA = Id(6), RevisionB = Id(7), RevisionOld = Id(8), RevisionElementA = Id(9),
            RevisionElementB = Id(10), RevisionElementOld = Id(11), GroupNew = Id(12), GroupB = Id(13),
            GroupOld = Id(14), CellElementA = Id(15), CellElementB = Id(16), Blob = Id(17);
        public CellId CellA = new(Id(20), Id(21)), CellB = new(Id(22), Id(23));
        public List<DataElement> Elements;
        public Fixture()
        {
            Elements = [
                Element(DataElementType.StorageIndexDataElementData, Index, w =>
                {
                    Record(w, StreamObjectTypeHeaderStart.StorageIndexManifestMapping, b => { Manifest.Serialize(b); SerialNumber.Null.Serialize(b); });
                    foreach (var (cell, element) in new[] { (CellA, CellElementA), (CellB, CellElementB) })
                        Record(w, StreamObjectTypeHeaderStart.StorageIndexCellMapping, b => { cell.Serialize(b); element.Serialize(b); SerialNumber.Null.Serialize(b); });
                    foreach (var (revision, element) in new[] { (RevisionA, RevisionElementA), (RevisionB, RevisionElementB), (RevisionOld, RevisionElementOld) })
                        Record(w, StreamObjectTypeHeaderStart.StorageIndexRevisionMapping, b => { revision.Serialize(b); element.Serialize(b); SerialNumber.Null.Serialize(b); });
                }),
                Element(DataElementType.StorageManifestDataElementData, Manifest, w =>
                {
                    Record(w, StreamObjectTypeHeaderStart.StorageManifestSchemaGUID, b => b.WriteBytes(Schema.ToByteArray()));
                    foreach (var (root, cell) in new[] { (RootA, CellA), (RootB, CellB) })
                        Record(w, StreamObjectTypeHeaderStart.StorageManifestRootDeclare, b => { root.Serialize(b); cell.Serialize(b); });
                }),
                Element(DataElementType.CellManifestDataElementData, CellElementA, w => Record(w, StreamObjectTypeHeaderStart.CellManifestCurrentRevision, RevisionA.Serialize)),
                Element(DataElementType.CellManifestDataElementData, CellElementB, w => Record(w, StreamObjectTypeHeaderStart.CellManifestCurrentRevision, RevisionB.Serialize)),
                Revision(RevisionElementA, RevisionA, RevisionOld, RootA, Object, GroupNew),
                Revision(RevisionElementB, RevisionB, ExGuid.Null, RootB, Object, GroupB),
                Revision(RevisionElementOld, RevisionOld, ExGuid.Null, RootA, Object, GroupOld),
                Group(GroupNew, new Item(Object, 1, "new"u8.ToArray())),
                Group(GroupOld, new Item(Object, 1, "old"u8.ToArray()), new Item(Object, 4, "metadata"u8.ToArray())),
                Group(GroupB, new Item(Object, 1, "other-cell"u8.ToArray(), [Object], [CellA])),
            ];
        }
        public GenericPartitionGraphSnapshot Create() => GenericPartitionGraphSnapshot.Create(Elements, Index);
        public void Replace(ExGuid id, DataElement replacement) => Elements[Elements.FindIndex(e => e.DataElementExtendedGuid.Equals(id))] = replacement;
    }

    private static DataElement Element(DataElementType type, ExGuid id, Action<BinaryWriterEx> write)
    {
        var writer = new BinaryWriterEx(); write(writer);
        return new(type, id, SerialNumber.Null) { Data = writer.ToArray() };
    }
    private static void Record(BinaryWriterEx writer, StreamObjectTypeHeaderStart type, Action<BinaryWriterEx> write)
    {
        var body = new BinaryWriterEx(); write(body);
        new StreamObjectHeaderStart32Bit(type, body.Length).Serialize(writer);
        writer.WriteBytes(body.ToArray());
    }
    private static DataElement Revision(ExGuid element, ExGuid revision, ExGuid parent, ExGuid root, ExGuid obj, ExGuid group) =>
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
}
