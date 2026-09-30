using System.IO.Compression;
using System.Text;
using CellBridge.FssHttpB;

namespace CellBridge.FssHttpB.Tests;

public sealed class EditorsTablePartitionTests
{
    private static int PeekDiscriminator(BinaryReaderEx reader)
    {
        int position = reader.Position;
        int value = reader.ReadByte() & 3;
        reader.Position = position;
        return value;
    }

    [Fact]
    public void Stream_HasRequiredHeaderAndRoundTripsDeflateXml()
    {
        var stream = EditorsTablePartitionBuilder.BuildEditorsTableStream(
            new[] { new EditorsTableEditor("client-a", 638923456000000000, "A User") });

        Assert.Equal(new byte[] { 0x1A, 0x5A, 0x3A, 0x30, 0, 0, 0, 0 }, stream[..8]);

        using var source = new MemoryStream(stream, 8, stream.Length - 8);
        using var deflate = new DeflateStream(source, CompressionMode.Decompress);
        using var xml = new MemoryStream();
        deflate.CopyTo(xml);
        string text = Encoding.UTF8.GetString(xml.ToArray());

        Assert.Contains("<EditorsTable><Editor>", text);
        Assert.Contains("<CacheID>client-a</CacheID>", text);
        Assert.Contains("<Timeout>638923456000000000</Timeout>", text);
    }

    [Fact]
    public void QueryChanges_UsesHeaderThenCompressedObjectData()
    {
        var identity = new EditorsTablePartitionBuilder.EditorsTableIdentity(
            new ExGuid(1, Guid.NewGuid()), new ExGuid(2, Guid.NewGuid()),
            new ExGuid(3, Guid.NewGuid()), new ExGuid(4, Guid.NewGuid()),
            new ExGuid(5, Guid.NewGuid()), new ExGuid(6, Guid.NewGuid()),
            new ExGuid(7, Guid.NewGuid()),
            new CellId(new ExGuid(8, Guid.NewGuid()), new ExGuid(9, Guid.NewGuid())),
            Guid.NewGuid());

        var response = EditorsTablePartitionBuilder.BuildQueryChangesResponse(
            10, Array.Empty<EditorsTableEditor>(), identity);
        var group = Assert.Single(response.DataElementPackage!.DataElements,
            e => e.DataElementType == DataElementType.ObjectGroupDataElementData);
        Assert.NotNull(group.Data);

        var reader = new BinaryReaderEx(group.Data!);
        var declarations = StreamObjectHeaderStart.Parse(reader);
        Assert.Equal(StreamObjectTypeHeaderStart.ObjectGroupDeclarations, declarations.Type);
        var declaredGuids = new List<ExGuid>();
        while ((PeekDiscriminator(reader) is 0 or 2))
        {
            var declaration = StreamObjectHeaderStart.Parse(reader);
            Assert.Equal(StreamObjectTypeHeaderStart.ObjectGroupObjectDeclare, declaration.Type);
            declaredGuids.Add(ExGuid.Deserialize(reader));
            Compact64bitInt.Deserialize(reader); // partition
            Compact64bitInt.Deserialize(reader); // data size
            Compact64bitInt.Deserialize(reader); // object references
            Compact64bitInt.Deserialize(reader); // cell references
        }
        SkipObjectEnd(reader, StreamObjectTypeHeaderEnd.ObjectGroupDeclarations);
        var data = StreamObjectHeaderStart.Parse(reader);
        Assert.Equal(StreamObjectTypeHeaderStart.ObjectGroupData, data.Type);

        // The first object data's BinaryItem is the fixed header, and the
        // immediate next object data is compressed XML.
        var first = ReadObjectData(reader, declaredGuids[0], 0);
        var second = ReadObjectData(reader, declaredGuids[1], 0);
        Assert.Equal(EditorsTablePartitionBuilder.ZipStreamHeader, first.Content);
        Assert.NotEqual(first.Content, second.Content);
        Assert.True(second.Content.Length > 0);

        // The remaining object data is the FSSHTTPD root, leaves, and raw
        // data nodes.  Their references must reconstruct the full stream.
        var root = ReadObjectData(reader, declaredGuids[2], 1);
        Assert.Equal(StreamObjectTypeHeaderStart.IntermediateNodeObject,
            StreamObjectHeaderStart.Parse(new BinaryReaderEx(root.Content)).Type);
        var reconstructed = new List<byte>();
        for (int i = 0; i < root.References.Count; i++)
        {
            var leaf = ReadObjectData(reader, declaredGuids[3 + (i * 2)], 1);
            Assert.Equal(root.References[i], leaf.Guid);
            Assert.Equal(StreamObjectTypeHeaderStart.LeafNodeObject,
                StreamObjectHeaderStart.Parse(new BinaryReaderEx(leaf.Content)).Type);
            var chunkData = ReadObjectData(reader, declaredGuids[4 + (i * 2)], 0);
            Assert.Equal(leaf.References[0], chunkData.Guid);
            reconstructed.AddRange(chunkData.Content);
        }

        Assert.Equal(EditorsTablePartitionBuilder.BuildEditorsTableStream(
            Array.Empty<EditorsTableEditor>()), reconstructed.ToArray());
    }

    [Fact]
    public void Chunker_ProducesContiguousSha1SignedChunks()
    {
        byte[] content = Enumerable.Range(0, 25).Select(i => (byte)i).ToArray();
        var chunks = FsshttpdChunker.Chunk(content, i => new ExGuid((uint)i, Guid.NewGuid()), 8);

        Assert.Equal(4, chunks.Count);
        Assert.Equal(new[] { 0UL, 8UL, 16UL, 24UL }, chunks.Select(c => c.Offset));
        Assert.Equal(content, chunks.SelectMany(c => c.Content).ToArray());
        foreach (var chunk in chunks)
        {
            Assert.Equal(System.Security.Cryptography.SHA1.HashData(chunk.Content), chunk.Signature);
            Assert.True(chunk.Length <= 8);
        }
    }

    [Fact]
    public void SharePointV13_ProfileUsesLegacyEditorsGraphAndStorageIndex()
    {
        var guid = Guid.Parse("aa9843aa-4fa5-464b-bafc-9196a4225025");
        var cell = new CellId(new ExGuid(1, guid), new ExGuid(1, Guid.Parse("6f2a4665-42c8-46c7-bab4-e28fdce1e32b")));
        var elements = EditorsTablePartitionBuilder.BuildSharePointV13DataElements(
            new[] { new EditorsTableEditor("client-a", 638923456000000000, "A User") },
            cell,
            guid);

        Assert.Equal(
            new[]
            {
                DataElementType.RevisionManifestDataElementData,
                DataElementType.ObjectGroupDataElementData,
                DataElementType.StorageManifestDataElementData,
                DataElementType.CellManifestDataElementData,
                DataElementType.ObjectGroupDataElementData,
                DataElementType.StorageIndexDataElementData,
            },
            elements.Select(x => x.DataElementType));

        var revision = elements[0].Data!;
        var revisionReader = new BinaryReaderEx(revision);
        Assert.Equal(StreamObjectTypeHeaderStart.RevisionManifest, StreamObjectHeaderStart.Parse(revisionReader).Type);
        revisionReader.Skip(18);
        Assert.Equal(StreamObjectTypeHeaderStart.RevisionManifestRootDeclare, StreamObjectHeaderStart.Parse(revisionReader).Type);

        var rootGroup = elements[1].Data!;
        var rootReader = new BinaryReaderEx(rootGroup);
        Assert.Equal(StreamObjectTypeHeaderStart.ObjectGroupDeclarations, StreamObjectHeaderStart.Parse(rootReader).Type);
        var declaration = StreamObjectHeaderStart.Parse(rootReader);
        Assert.Equal(StreamObjectTypeHeaderStart.ObjectGroupObjectDeclare, declaration.Type);
        _ = ExGuid.Deserialize(rootReader);
        Assert.Equal(1UL, Compact64bitInt.Deserialize(rootReader).Value);
        Assert.Equal(28UL, Compact64bitInt.Deserialize(rootReader).Value);
        Assert.Equal(2UL, Compact64bitInt.Deserialize(rootReader).Value);

        var streamGroup = elements[4].Data!;
        var streamReader = new BinaryReaderEx(streamGroup);
        Assert.Equal(StreamObjectTypeHeaderStart.ObjectGroupDeclarations, StreamObjectHeaderStart.Parse(streamReader).Type);
        var declarations = new List<(ulong Size, ulong References)>();
        while (PeekDiscriminator(streamReader) is 0 or 2)
        {
            _ = StreamObjectHeaderStart.Parse(streamReader);
            _ = ExGuid.Deserialize(streamReader);
            _ = Compact64bitInt.Deserialize(streamReader);
            declarations.Add((Compact64bitInt.Deserialize(streamReader).Value,
                Compact64bitInt.Deserialize(streamReader).Value));
            _ = Compact64bitInt.Deserialize(streamReader);
        }
        Assert.Equal(4, declarations.Count);
        Assert.Equal(8UL, declarations[0].Size);
        Assert.True(declarations[1].Size > 0);
        Assert.Equal(new[] { 0UL, 0UL, 1UL, 1UL }, declarations.Select(x => x.References));

        var index = elements[5];
        var indexReader = new BinaryReaderEx(index.Data!);
        var indexHeader = StreamObjectHeaderStart.Parse(indexReader);
        Assert.Equal(StreamObjectTypeHeaderStart.StorageIndexManifestMapping, indexHeader.Type);
        Assert.Equal(2, indexHeader.Length);
        Assert.True(ExGuid.Deserialize(indexReader).IsNull);
        Assert.True(SerialNumber.Deserialize(indexReader).IsNull);
    }

    private static (byte[] Content, ExGuid Guid, List<ExGuid> References) ReadObjectData(
        BinaryReaderEx reader, ExGuid ownGuid, int expectedReferenceCount)
    {
        var objectData = StreamObjectHeaderStart.Parse(reader);
        Assert.Equal(StreamObjectTypeHeaderStart.ObjectGroupObjectData, objectData.Type);
        var guidCount = Compact64bitInt.Deserialize(reader).Value;
        Assert.Equal((ulong)expectedReferenceCount, guidCount);
        var references = Enumerable.Range(0, checked((int)guidCount))
            .Select(_ => ExGuid.Deserialize(reader)).ToList();
        Assert.Equal(0UL, Compact64bitInt.Deserialize(reader).Value);
        var item = BinaryItem.Deserialize(reader);
        return (item.Content, ownGuid, references);
    }

    private static void SkipObjectEnd(BinaryReaderEx reader, StreamObjectTypeHeaderEnd expected)
    {
        var end = StreamObjectHeaderEnd.Parse(reader);
        Assert.Equal(expected, end.Type);
    }
}
