namespace OfficeCollabServer.FssHttpB;

/// <summary>Builds the root, leaf and byte objects used by Word's file stream.</summary>
public static class FileContentPartitionBuilder
{
    public static FsshttpbResponse BuildQueryChangesResponse(ulong requestId, byte[] content,
        StorageManifestBuilder.StableIdentity identity, ulong knowledgeSequence)
    {
        var response = StorageManifestBuilder.BuildQueryChangesResponse(requestId, content, identity, knowledgeSequence);
        var group = response.DataElementPackage!.DataElements.Single(
            x => x.DataElementType == DataElementType.ObjectGroupDataElementData);
        var root = identity.ObjectGuid;
        var leaf = new ExGuid(root.Value ^ 0x80000000U, root.Guid);
        var bytes = new ExGuid(root.Value ^ 0x40000000U, root.Guid);
        var chunk = new FsshttpdChunk(0, content, leaf, []);
        var objects = new[]
        {
            new StreamObject(root, FsshttpdNodeSerializer.SerializeLegacyIntermediate((ulong)content.Length, content), [leaf]),
            new StreamObject(leaf, FsshttpdNodeSerializer.SerializeLegacyLeaf(chunk), [bytes]),
            new StreamObject(bytes, content, []),
        };
        var writer = new BinaryWriterEx();
        new StreamObjectHeaderStart32Bit(StreamObjectTypeHeaderStart.ObjectGroupDeclarations, 0).Serialize(writer);
        foreach (var item in objects)
        {
            var body = new BinaryWriterEx();
            item.Id.Serialize(body);
            new Compact64bitInt(1).Serialize(body);
            new Compact64bitInt((ulong)item.Content.Length).Serialize(body);
            new Compact64bitInt((ulong)item.References.Length).Serialize(body);
            new Compact64bitInt(0).Serialize(body);
            WriteObject(writer, StreamObjectTypeHeaderStart.ObjectGroupObjectDeclare, body);
        }
        new StreamObjectHeaderEnd8Bit(StreamObjectTypeHeaderEnd.ObjectGroupDeclarations).Serialize(writer);
        new StreamObjectHeaderStart32Bit(StreamObjectTypeHeaderStart.ObjectGroupData, 0).Serialize(writer);
        foreach (var item in objects)
        {
            var body = new BinaryWriterEx();
            new Compact64bitInt((ulong)item.References.Length).Serialize(body);
            foreach (var reference in item.References) reference.Serialize(body);
            new Compact64bitInt(0).Serialize(body);
            new BinaryItem(item.Content).Serialize(body);
            WriteObject(writer, StreamObjectTypeHeaderStart.ObjectGroupObjectData, body);
        }
        new StreamObjectHeaderEnd8Bit(StreamObjectTypeHeaderEnd.ObjectGroupData).Serialize(writer);
        group.Data = writer.ToArray();
        return response;
    }

    private static void WriteObject(BinaryWriterEx writer, StreamObjectTypeHeaderStart type, BinaryWriterEx body)
    {
        new StreamObjectHeaderStart32Bit(type, body.Length).Serialize(writer);
        writer.WriteBytes(body.ToArray());
    }

    private sealed record StreamObject(ExGuid Id, byte[] Content, ExGuid[] References);
}
