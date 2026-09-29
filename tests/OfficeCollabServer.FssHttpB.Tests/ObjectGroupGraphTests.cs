using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using OfficeCollabServer.FssHttp;

namespace OfficeCollabServer.FssHttpB.Tests;

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

    private static DataElement TestGroup(
        ExGuid owner,
        IReadOnlyList<ExGuid> references,
        byte[] content)
    {
        var declarationBody = new BinaryWriterEx();
        owner.Serialize(declarationBody);
        new Compact64bitInt(1).Serialize(declarationBody);
        new Compact64bitInt((ulong)content.Length).Serialize(declarationBody);
        new Compact64bitInt((ulong)references.Count).Serialize(declarationBody);
        new Compact64bitInt(0).Serialize(declarationBody);

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
        new StreamObjectHeaderStart16Bit(StreamObjectTypeHeaderStart.ObjectGroupObjectData,
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
}
