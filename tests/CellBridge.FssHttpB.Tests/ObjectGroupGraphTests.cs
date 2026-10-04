using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using CellBridge.FssHttp;

namespace CellBridge.FssHttpB.Tests;

public sealed class ObjectGroupGraphTests
{
    [Fact]
    public void SharePointEditorsGraph_ParsesNodesAndFollowsReferencesInOrder()
    {
        var identity = Guid.Parse("aa9843aa-4fa5-464b-bafc-9196a4225025");
        var cell = new CellId(new ExGuid(1, identity),
            new ExGuid(1, Guid.Parse("6f2a4665-42c8-46c7-bab4-e28fdce1e32b")));
        var elements = EditorsTablePartitionBuilder.BuildSharePointV13DataElements(
            Array.Empty<EditorsTableEditor>(), cell, identity);

        var graph = ObjectGroupGraph.FromDataElements(elements);
        var root = new ExGuid(13, identity);
        var materialized = graph.Materialize(root);

        Assert.Equal(EditorsTablePartitionBuilder.BuildEditorsTableStream(
            Array.Empty<EditorsTableEditor>()), materialized);
        Assert.Equal(ObjectGroupPayloadKind.IntermediateNode,
            graph.Objects[root].PayloadKind);
        Assert.Contains(graph.Objects.Values,
            x => x.PayloadKind == ObjectGroupPayloadKind.LeafNode);
    }

    [Fact]
    public void GeneratedFileContentGraph_MaterializesOriginalBytes()
    {
        var guid = Guid.Parse("15c7b2a2-822a-4a04-8bb0-46c7de4bc901");
        var identity = new StorageManifestBuilder.StableIdentity(
            new ExGuid(1, guid), new ExGuid(2, guid), new ExGuid(3, guid),
            new ExGuid(4, guid), new ExGuid(5, guid), new ExGuid(6, guid),
            new ExGuid(7, guid),
            new CellId(new ExGuid(8, guid), new ExGuid(9,
                StorageManifestBuilder.CellSecondExtendedGuid)), guid);
        var content = Enumerable.Range(0, 4097).Select(i => (byte)(i % 251)).ToArray();

        var response = FileContentPartitionBuilder.BuildQueryChangesResponse(
            1, content, identity, knowledgeSequence: 73507);
        var graph = ObjectGroupGraph.FromDataElements(response.DataElementPackage!.DataElements);

        Assert.Equal(content, graph.Materialize(identity.ObjectGuid));
        Assert.Equal(ObjectGroupPayloadKind.IntermediateNode,
            graph.Objects[identity.ObjectGuid].PayloadKind);
        Assert.Contains(graph.Objects.Values,
            x => x.PayloadKind == ObjectGroupPayloadKind.LeafNode);
    }

    [Fact]
    public void CapturedSaveFirst_FileGraphMaterializesZipFromReferencedChunks()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "save-first.json")));
        var side = fixture.RootElement.GetProperty("request");
        var bytes = Convert.FromBase64String(side.GetProperty("bodyBase64").GetString()!);
        var parts = MtomMessageParser.Parse(bytes, side.GetProperty("contentType").GetString()!);
        var binaryPart = Assert.Single(parts, x => x.ContentType.Contains(
            "application/octet-stream", StringComparison.OrdinalIgnoreCase));
        var requestReader = new BinaryReaderEx(binaryPart.Content);
        var request = FsshttpbCellRequest.Deserialize(requestReader);
        Assert.Equal(0, requestReader.Remaining);

        var graph = ObjectGroupGraph.FromDataElements(request.DataElementPackage!.DataElements);
        var root = new ExGuid(1, Guid.Parse("e02970a9-c167-433f-a6e1-a7ae72f79cc3"));
        var materialized = graph.Materialize(root);

        Assert.Equal("PK", System.Text.Encoding.ASCII.GetString(materialized, 0, 2));
        using var archive = new ZipArchive(new MemoryStream(materialized), ZipArchiveMode.Read);
        Assert.NotEmpty(archive.Entries);
        Assert.Contains(archive.Entries, x => x.FullName == "word/document.xml");
    }

    [Fact]
    public void CapturedSaveSecond_ParsesDeltaButRequiresPriorGraphObjects()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "save-second.json")));
        var side = fixture.RootElement.GetProperty("request");
        var bytes = Convert.FromBase64String(side.GetProperty("bodyBase64").GetString()!);
        var parts = MtomMessageParser.Parse(bytes, side.GetProperty("contentType").GetString()!);
        var binaryPart = Assert.Single(parts, x => x.ContentType.Contains(
            "application/octet-stream", StringComparison.OrdinalIgnoreCase));
        var requestReader = new BinaryReaderEx(binaryPart.Content);
        var request = FsshttpbCellRequest.Deserialize(requestReader);
        Assert.Equal(0, requestReader.Remaining);

        var graph = ObjectGroupGraph.FromDataElements(request.DataElementPackage!.DataElements);
        var root = new ExGuid(1, Guid.Parse("e02970a9-c167-433f-a6e1-a7ae72f79cc3"));
        var exception = Assert.Throws<InvalidDataException>(() => graph.Materialize(root));
        Assert.Contains("unresolved", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MissingReference_IsRejectedBeforeReturningPartialContent()
    {
        var group = TestGroup(
            new ExGuid(1, Guid.NewGuid()),
            references: [new ExGuid(99, Guid.NewGuid())],
            content: [1, 2, 3]);
        var graph = ObjectGroupGraph.FromDataElements([group]);

        var root = Assert.Single(graph.Objects).Key;
        var exception = Assert.Throws<InvalidDataException>(() => graph.Materialize(root));
        Assert.Contains("unresolved", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CyclicReferences_AreRejected()
    {
        var a = new ExGuid(1, Guid.NewGuid());
        var b = new ExGuid(2, a.Guid);
        var first = TestGroup(a, [b], [1]);
        var second = TestGroup(b, [a], [2]);
        var graph = ObjectGroupGraph.FromDataElements([first, second]);

        var exception = Assert.Throws<InvalidDataException>(() => graph.Materialize(a));
        Assert.Contains("cycle", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StreamOutputSupportsNonSeekableDestinationsAndRepeatedChildren(bool asynchronous)
    {
        var root = new ExGuid(1, Guid.NewGuid());
        var child = new ExGuid(2, root.Guid);
        var graph = ObjectGroupGraph.FromDataElements([
            TestGroup(root, [child, child], []), TestGroup(child, [], [1, 2, 3])]);
        using var bytes = new MemoryStream();
        bytes.WriteByte(99);
        using var destination = new NonSeekableWriteStream(bytes);

        var length = asynchronous
            ? await graph.MaterializeToAsync(destination, root, maxBytes: 6)
            : graph.MaterializeTo(destination, root, maxBytes: 6);

        Assert.Equal(6, length);
        Assert.Equal(new byte[] { 99, 1, 2, 3, 1, 2, 3 }, bytes.ToArray());
        Assert.True(destination.CanWrite);
        Assert.Equal(new byte[] { 1, 2, 3, 1, 2, 3 }, graph.Materialize(root, maxBytes: 6));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("cycle")]
    [InlineData("quota")]
    [InlineData("size")]
    public async Task InvalidGraphsLeaveStreamDestinationsUntouched(string failure)
    {
        var root = new ExGuid(1, Guid.NewGuid());
        var child = new ExGuid(2, root.Guid);
        var other = new ExGuid(3, root.Guid);
        var graph = ObjectGroupGraph.FromDataElements([
            TestGroup(root, [child, other], []), TestGroup(child, [], [7, 8])]);
        if (failure == "cycle") graph.Add(ObjectGroupDataElement.Parse(TestGroup(other, [root], [])));
        if (failure is "quota" or "size")
            graph.Add(ObjectGroupDataElement.Parse(TestGroup(other, [], [9])));
        if (failure == "size")
        {
            // Use a genuine FSSHTTPD node with an incorrect represented size.
            graph = ObjectGroupGraph.FromDataElements([
                TestGroup(root, [child], FsshttpdNodeSerializer.SerializeIntermediate(3, new byte[] { 1 })),
                TestGroup(child, [], [7, 8])]);
        }
        using var destination = new MemoryStream();
        destination.WriteByte(99);
        long limit = failure == "quota" ? 2 : 10;
        var sync = Record.Exception(() => graph.MaterializeTo(destination, root, limit));
        if (failure == "quota") Assert.IsType<GraphMaterializationLimitException>(sync);
        else Assert.IsType<InvalidDataException>(sync);
        Assert.Equal(new byte[] { 99 }, destination.ToArray());
        var asyncError = await Record.ExceptionAsync(async () =>
            await graph.MaterializeToAsync(destination, root, limit));
        if (failure == "quota") Assert.IsType<GraphMaterializationLimitException>(asyncError);
        else Assert.IsType<InvalidDataException>(asyncError);
        Assert.Equal(new byte[] { 99 }, destination.ToArray());
    }

    [Fact]
    public async Task CancellationBeforeMaterializationDoesNotWrite()
    {
        var root = new ExGuid(1, Guid.NewGuid());
        var graph = ObjectGroupGraph.FromDataElements([TestGroup(root, [], [1, 2])]);
        using var destination = new MemoryStream();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await graph.MaterializeToAsync(destination, root, cancellationToken: new CancellationToken(true)));
        Assert.Empty(destination.ToArray());
    }

    [Fact]
    public void ExponentialZeroByteGraphIsBoundedBeforeWriting()
    {
        var guid = Guid.NewGuid();
        var elements = Enumerable.Range(1, 21).Select(i => TestGroup(new ExGuid((uint)i, guid),
            i == 21 ? [] : [new ExGuid((uint)i + 1, guid), new ExGuid((uint)i + 1, guid)], [])).ToArray();
        var graph = ObjectGroupGraph.FromDataElements(elements);
        using var destination = new MemoryStream();
        var error = Assert.Throws<InvalidDataException>(() => graph.MaterializeTo(destination, new(1, guid)));
        Assert.Contains("traversal limit", error.Message);
        Assert.Empty(destination.ToArray());
    }

    [Theory]
    [InlineData(256)]
    [InlineData(257)]
    public void ActiveObjectPathLimitIsPreservedForStreamOutput(int depth)
    {
        var guid = Guid.NewGuid();
        var graph = ObjectGroupGraph.FromDataElements(Enumerable.Range(1, depth).Select(i =>
            TestGroup(new((uint)i, guid), i == depth ? [] : [new((uint)i + 1, guid)],
                i == depth ? [1] : [])));
        using var destination = new MemoryStream();
        if (depth == 256)
        {
            Assert.Equal(1, graph.MaterializeTo(destination, new(1, guid)));
            Assert.Equal(new byte[] { 1 }, destination.ToArray());
        }
        else
        {
            var error = Assert.Throws<InvalidDataException>(() => graph.MaterializeTo(destination, new(1, guid)));
            Assert.Contains("nesting limit", error.Message);
            Assert.Empty(destination.ToArray());
        }
    }

    [Fact]
    public void LargeObjectParsingAndMaterializationAvoidExtraPayloadSizedBuffers()
    {
        var root = new ExGuid(1, Guid.NewGuid());
        var content = new byte[4 * 1024 * 1024];
        var element = TestGroup(root, [], content);
        _ = ObjectGroupDataElement.Parse(element); // Warm the parser before measuring allocations.
        long before = GC.GetAllocatedBytesForCurrentThread();
        var parsed = ObjectGroupDataElement.Parse(element);
        long parseBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(parseBytes < content.LongLength * 2, $"Parse allocated {parseBytes} bytes.");
        var graph = new ObjectGroupGraph();
        graph.Add(parsed);
        _ = graph.Materialize(root);
        before = GC.GetAllocatedBytesForCurrentThread();
        var result = graph.Materialize(root);
        long materializeBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(materializeBytes < content.LongLength * 2, $"Materialization allocated {materializeBytes} bytes.");
        Assert.Equal(content, result);
    }

    [Theory]
    [InlineData(1UL, 0UL)]
    [InlineData(0UL, 1UL)]
    public void ReferenceCountsMustMatchTheirDeclaration(ulong objectCount, ulong cellCount)
    {
        var group = TestGroup(new(1, Guid.NewGuid()), [], [1], objectCount, cellCount);
        var error = Assert.Throws<InvalidDataException>(() => ObjectGroupDataElement.Parse(group));
        Assert.Contains("reference counts", error.Message);
    }

    [Fact]
    public void ParsedContentDoesNotBorrowMutableInputBuffers()
    {
        var group = TestGroup(new(1, Guid.NewGuid()), [], [1, 2, 3]);
        var parsed = ObjectGroupDataElement.Parse(group);
        Array.Fill(group.Data!, (byte)255);
        Assert.Equal(new byte[] { 1, 2, 3 }, Assert.Single(parsed.Objects).Content);
    }

    [Fact]
    public void DeclarationCountIsBoundedBeforeReadingObjectData()
    {
        var one = TestGroup(new(1, Guid.NewGuid()), [], []);
        var reader = new BinaryReaderEx(one.Data!);
        _ = StreamObjectHeaderStart.Parse(reader);
        int begin = reader.Position;
        var header = StreamObjectHeaderStart.Parse(reader);
        reader.Skip(header.Length);
        var declaration = one.Data.AsSpan(begin, reader.Position - begin).ToArray();
        var payload = new BinaryWriterEx();
        new StreamObjectHeaderStart16Bit(StreamObjectTypeHeaderStart.ObjectGroupDeclarations, 0).Serialize(payload);
        for (int i = 0; i < 100_001; i++) payload.WriteBytes(declaration);
        one.Data = payload.ToArray();
        var error = Assert.Throws<InvalidDataException>(() => ObjectGroupDataElement.Parse(one));
        Assert.Contains("declaration count limit", error.Message);
    }

    [Fact]
    public void DeclarationDataSizeRemainsAdvisory()
    {
        // MS-FSSHTTPB specifies SHOULD, rather than MUST, for this size equality.
        var group = TestGroup(new(1, Guid.NewGuid()), [], [1, 2, 3], declaredDataSize: 99);
        Assert.Equal(new byte[] { 1, 2, 3 }, Assert.Single(ObjectGroupDataElement.Parse(group).Objects).Content);
    }

    private static DataElement TestGroup(
        ExGuid owner,
        IReadOnlyList<ExGuid> references,
        byte[] content,
        ulong? declaredObjectCount = null,
        ulong declaredCellCount = 0,
        ulong? declaredDataSize = null)
    {
        var declarationBody = new BinaryWriterEx();
        owner.Serialize(declarationBody);
        new Compact64bitInt(1).Serialize(declarationBody);
        new Compact64bitInt(declaredDataSize ?? (ulong)content.Length).Serialize(declarationBody);
        new Compact64bitInt(declaredObjectCount ?? (ulong)references.Count).Serialize(declarationBody);
        new Compact64bitInt(declaredCellCount).Serialize(declarationBody);

        var payload = new BinaryWriterEx();
        new StreamObjectHeaderStart16Bit(StreamObjectTypeHeaderStart.ObjectGroupDeclarations, 0)
            .Serialize(payload);
        new StreamObjectHeaderStart16Bit(StreamObjectTypeHeaderStart.ObjectGroupObjectDeclare,
            declarationBody.Length).Serialize(payload);
        payload.WriteBytes(declarationBody.ToArray());
        new StreamObjectHeaderEnd8Bit(StreamObjectTypeHeaderEnd.ObjectGroupDeclarations)
            .Serialize(payload);

        var objectBody = new BinaryWriterEx();
        new Compact64bitInt((ulong)references.Count).Serialize(objectBody);
        foreach (var reference in references)
            reference.Serialize(objectBody);
        new Compact64bitInt(0).Serialize(objectBody);
        new BinaryItem(content).Serialize(objectBody);
        new StreamObjectHeaderStart16Bit(StreamObjectTypeHeaderStart.ObjectGroupData, 0)
            .Serialize(payload);
        new StreamObjectHeaderStart32Bit(StreamObjectTypeHeaderStart.ObjectGroupObjectData,
            objectBody.Length).Serialize(payload);
        payload.WriteBytes(objectBody.ToArray());
        new StreamObjectHeaderEnd8Bit(StreamObjectTypeHeaderEnd.ObjectGroupData)
            .Serialize(payload);

        return new DataElement(DataElementType.ObjectGroupDataElementData,
            new ExGuid(1, owner.Guid), SerialNumber.Null)
        {
            Data = payload.ToArray(),
        };
    }

    private sealed class NonSeekableWriteStream(Stream destination) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => destination.CanWrite;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => destination.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => destination.Write(buffer, offset, count);
        public override void Write(ReadOnlySpan<byte> buffer) => destination.Write(buffer);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => destination.WriteAsync(buffer, cancellationToken);
    }
}
