using System.IO.Compression;
using System.Text;
using System.Security.Cryptography;
using System.Xml;

namespace CellBridge.FssHttpB;

/// <summary>Values sent in one binary editors-table partition.</summary>
public sealed record EditorsTableEditor(
    string CacheId,
    long Timeout,
    string? FriendlyName = null,
    string? LoginName = null,
    string? SipAddress = null,
    string? EmailAddress = null,
    bool? HasEditorPermission = null,
    IReadOnlyDictionary<string, byte[]>? Metadata = null)
{
    public EditorsTableEditor(string cacheId, long timeout)
        : this(cacheId, timeout, null, null, null, null, null, null)
    {
    }
}

/// <summary>
/// Builds the special Query Changes response for the editors-table partition.
/// The UTF-8 XML is DEFLATE-compressed and prefixed with the eight-byte
/// editors header. The v13 response represents that complete stream as a
/// simple-chunking FSSHTTPD graph.
/// </summary>
public static class EditorsTablePartitionBuilder
{
    public static readonly Guid PartitionId =
        new("7808F4DD-2385-49D6-B7CE-37ACA5E43602");

    public static readonly byte[] ZipStreamHeader =
        { 0x1A, 0x5A, 0x3A, 0x30, 0, 0, 0, 0 };

    /// <summary>Builds a deterministic XML editors-table fragment.</summary>
    public static byte[] SerializeEditorsTable(
        IEnumerable<EditorsTableEditor>? editors)
    {
        var writerSettings = new XmlWriterSettings
        {
            OmitXmlDeclaration = true,
            NewLineHandling = NewLineHandling.None,
            Indent = false,
            Encoding = new UTF8Encoding(false),
        };

        using var stream = new MemoryStream();
        using (var xml = XmlWriter.Create(stream, writerSettings))
        {
            xml.WriteStartElement("EditorsTable");
            if (editors is not null)
            {
                foreach (var editor in editors)
                {
                    ArgumentNullException.ThrowIfNull(editor);
                    if (string.IsNullOrWhiteSpace(editor.CacheId))
                    {
                        throw new ArgumentException("CacheID is required.", nameof(editors));
                    }

                    if (editor.Timeout <= 0)
                    {
                        throw new ArgumentOutOfRangeException(nameof(editors),
                            "EditorsTable Timeout must be a positive tick count.");
                    }

                    xml.WriteStartElement("Editor");
                    WriteElement(xml, "CacheID", editor.CacheId);
                    WriteOptional(xml, "FriendlyName", editor.FriendlyName);
                    WriteOptional(xml, "LoginName", editor.LoginName);
                    WriteOptional(xml, "SIPAddress", editor.SipAddress);
                    WriteOptional(xml, "EmailAddress", editor.EmailAddress);
                    if (editor.HasEditorPermission.HasValue)
                    {
                        WriteElement(xml, "HasEditorPermission",
                            editor.HasEditorPermission.Value ? "true" : "false");
                    }

                    WriteElement(xml, "Timeout", editor.Timeout.ToString(
                        System.Globalization.CultureInfo.InvariantCulture));

                    if (editor.Metadata is not null)
                    {
                        xml.WriteStartElement("Metadata");
                        foreach (var pair in editor.Metadata.OrderBy(p => p.Key, StringComparer.Ordinal))
                        {
                            if (string.IsNullOrEmpty(pair.Key))
                            {
                                throw new ArgumentException("Metadata keys cannot be empty.", nameof(editors));
                            }

                            xml.WriteStartElement(pair.Key);
                            xml.WriteBase64(pair.Value ?? Array.Empty<byte>(), 0,
                                (pair.Value ?? Array.Empty<byte>()).Length);
                            xml.WriteEndElement();
                        }

                        xml.WriteEndElement();
                    }

                    xml.WriteEndElement();
                }
            }

            xml.WriteEndElement();
        }

        return stream.ToArray();
    }

    /// <summary>
    /// Returns the exact stream that is carried by the editors object data:
    /// the fixed header followed by raw DEFLATE bytes.
    /// </summary>
    public static byte[] BuildEditorsTableStream(IEnumerable<EditorsTableEditor>? editors)
    {
        byte[] xml = SerializeEditorsTable(editors);
        using var output = new MemoryStream();
        output.Write(ZipStreamHeader);
        using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            deflate.Write(xml);
        }

        return output.ToArray();
    }

    /// <summary>Builds the editors partition using generated stable identities.</summary>
    public static FsshttpbResponse BuildQueryChangesResponse(
        ulong requestId,
        IEnumerable<EditorsTableEditor>? editors,
        ulong knowledgeSequence = 73507)
    {
        var ids = CreateIdentity();
        return BuildQueryChangesResponse(requestId, editors, ids, knowledgeSequence);
    }

    /// <summary>Builds the editors partition using caller-owned stable identities.</summary>
    public static FsshttpbResponse BuildQueryChangesResponse(
        ulong requestId,
        IEnumerable<EditorsTableEditor>? editors,
        EditorsTableIdentity identity,
        ulong knowledgeSequence = 73507)
    {
        ArgumentNullException.ThrowIfNull(identity);
        byte[] stream = BuildEditorsTableStream(editors);
        byte[] compressed = stream[ZipStreamHeader.Length..];
        IReadOnlyList<FsshttpdChunk> chunks = FsshttpdChunker.Chunk(stream);

        var storageSerial = new SerialNumber(identity.SerialGuid, 1);
        var cellSerial = new SerialNumber(identity.SerialGuid, 2);
        var revisionSerial = new SerialNumber(identity.SerialGuid, 3);
        var groupSerial = new SerialNumber(identity.SerialGuid, 4);

        var storage = StorageManifestBuilder.BuildStorageManifestDataElement(
            identity.StorageManifestGuid, storageSerial,
            new ExGuid(2, StorageManifestBuilder.RootExtendedGuid), identity.CellId);
        var cell = StorageManifestBuilder.BuildCellManifestDataElement(
            identity.CellManifestGuid, cellSerial, identity.RevisionManifestGuid);
        var revision = StorageManifestBuilder.BuildRevisionManifestDataElement(
            identity.RevisionManifestGuid, revisionSerial,
            identity.RevisionId, ExGuid.Null,
            new ExGuid(2, StorageManifestBuilder.RootExtendedGuid), identity.HeaderObjectGuid,
            identity.ObjectGroupGuid);

        // The editors stream is deliberately represented as two consecutive
        // ObjectGroupObjectData values.  This is the representation required
        // by the EditorsTable extraction algorithm in the Microsoft test
        // suite: header first, then compressed XML.
        var group = BuildEditorsObjectGroup(
            identity.ObjectGroupGuid, groupSerial,
            identity.HeaderObjectGuid, identity.CompressedObjectGuid,
            ZipStreamHeader, compressed, chunks);

        var package = new DataElementPackage
        {
            DataElements = { storage, cell, revision, group },
        };

        var cellId = identity.CellId;
        return new FsshttpbResponse
        {
            Status = false,
            DataElementPackage = package,
            SubResponses =
            {
                new FsshttpbSubResponse
                {
                    RequestId = requestId,
                    RequestType = RequestTypes.QueryChanges,
                    Status = false,
                    Data = new QueryChangesSubResponseData
                    {
                        StorageIndexExtendedGuid = cellId.LongId,
                        CellKnowledgeCellGuid = cellId.LongId.Guid,
                        CellKnowledgeTo = knowledgeSequence,
                        Waterline = knowledgeSequence + 2000,
                    },
                },
            },
        };
    }

    /// <summary>
    /// Builds the six data elements in the v13 EditorsTable response. The
    /// graph uses one root node and simple-chunking leaf nodes over the full
    /// header-plus-deflate stream.
    /// </summary>
    public static IReadOnlyList<DataElement> BuildSharePointV13DataElements(
        IEnumerable<EditorsTableEditor>? editors,
        CellId cellId,
        Guid identityGuid)
    {
        ArgumentNullException.ThrowIfNull(cellId);
        var stream = BuildEditorsTableStream(editors);
        return BuildSharePointV13DataElements(stream, cellId, identityGuid);
    }

    private static IReadOnlyList<DataElement> BuildSharePointV13DataElements(byte[] stream, CellId cellId, Guid identityGuid)
    {
        var guid = identityGuid == Guid.Empty ? Guid.NewGuid() : identityGuid;
        var revisionGuid = new ExGuid(21, guid);
        var revisionElementGuid = new ExGuid(22, guid);
        var rootGuid = new ExGuid(2, StorageManifestBuilder.RootExtendedGuid);
        var objectRootGuid = new ExGuid(13, guid);
        var groupRootGuid = new ExGuid(12, guid);
        var groupStreamGuid = new ExGuid(11, guid);
        var storageGuid = new ExGuid(18, guid);
        var cellGuid = new ExGuid(29, guid);
        var indexGuid = new ExGuid(43, guid);
        var serialGuid = identityGuid == Guid.Empty ? guid : identityGuid;

        const uint firstChunkObjectId = 1000;
        // Keep generated chunk IDs disjoint from the fixed manifest IDs in
        // this file (including the cell and storage-index elements).
        var chunks = FsshttpdChunker.Chunk(stream)
            .Select((chunk, index) => chunk with
            {
                ExtendedGuid = new ExGuid(
                    checked((uint)(firstChunkObjectId + (2L * index))), guid),
            })
            .ToArray();
        var leafGuids = chunks.Select(chunk => chunk.ExtendedGuid).ToArray();
        var dataGuids = chunks.Select((_, index) =>
            new ExGuid(checked((uint)(firstChunkObjectId + (2L * index) + 1)), guid)).ToArray();
        var rootNode = FsshttpdNodeSerializer.SerializeSimpleRoot((ulong)stream.Length);
        var leafNodes = chunks.Select(FsshttpdNodeSerializer.SerializeSimpleLeaf).ToArray();

        var rootGroup = BuildObjectGroupDataElement(
            groupRootGuid,
            new SerialNumber(serialGuid, 129),
            new[] { new ObjectDataSpec(objectRootGuid, rootNode, leafGuids) },
            new[] { new ObjectDeclarationSpec(objectRootGuid, (ulong)rootNode.Length, leafGuids.Length) });

        var streamObjects = new List<ObjectDataSpec>(chunks.Length * 2);
        var streamDeclarations = new List<ObjectDeclarationSpec>(chunks.Length * 2);
        for (var i = 0; i < chunks.Length; i++)
        {
            streamObjects.Add(new ObjectDataSpec(leafGuids[i], leafNodes[i], [dataGuids[i]]));
            streamObjects.Add(new ObjectDataSpec(dataGuids[i], chunks[i].Content, Array.Empty<ExGuid>()));
            streamDeclarations.Add(new ObjectDeclarationSpec(leafGuids[i], (ulong)leafNodes[i].Length, 1));
            streamDeclarations.Add(new ObjectDeclarationSpec(dataGuids[i], chunks[i].Length, 0));
        }

        var streamGroup = BuildObjectGroupDataElement(
            groupStreamGuid,
            new SerialNumber(serialGuid, 130),
            streamObjects,
            streamDeclarations);

        var revision = StorageManifestBuilder.BuildRevisionManifestDataElement(
            revisionElementGuid, new SerialNumber(serialGuid, 128), revisionGuid,
            ExGuid.Null, rootGuid, objectRootGuid, new[] { groupRootGuid, groupStreamGuid });
        var storage = StorageManifestBuilder.BuildStorageManifestDataElement(
            storageGuid, new SerialNumber(serialGuid, 124), rootGuid, cellId);
        var cell = StorageManifestBuilder.BuildCellManifestDataElement(
            cellGuid, new SerialNumber(serialGuid, 126), revisionGuid);
        var index = StorageManifestBuilder.BuildStorageIndexDataElement(
            indexGuid, new SerialNumber(serialGuid, 44),
            storageGuid, new SerialNumber(serialGuid, 125),
            cellId, cellGuid, new SerialNumber(serialGuid, 127),
            revisionGuid, revisionElementGuid, new SerialNumber(serialGuid, 131));

        return new[] { revision, rootGroup, storage, cell, streamGroup, index };
    }

    /// <summary>Builds a complete QueryChanges response using the SharePoint 13/11 graph.</summary>
    public static FsshttpbResponse BuildSharePointV13QueryChangesResponse(
        ulong requestId,
        IEnumerable<EditorsTableEditor>? editors,
        CellId cellId,
        Guid identityGuid,
        ulong knowledgeSequence)
    {
        ArgumentNullException.ThrowIfNull(cellId);
        var stream = BuildEditorsTableStream(editors);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        // The graph layout is part of the snapshot identity. Version this
        // domain when mappings change so Office does not reuse a cached
        // snapshot whose storage index has a different graph.
        hash.AppendData("CellBridge editors snapshot v3"u8);
        hash.AppendData((identityGuid == Guid.Empty ? Guid.NewGuid() : identityGuid).ToByteArray());
        hash.AppendData(stream);
        var snapshot = new Guid(hash.GetHashAndReset().AsSpan(0, 16));
        var elements = BuildSharePointV13DataElements(stream, cellId, snapshot);
        var storageIndex = new ExGuid(43, snapshot);
        return new FsshttpbResponse
        {
            ProtocolVersion = 13,
            MinimumVersion = 11,
            DataElementPackage = new DataElementPackage
            {
                DataElements = [..elements],
            },
            SubResponses =
            {
                new FsshttpbSubResponse
                {
                    RequestId = requestId,
                    RequestType = RequestTypes.QueryChanges,
                    Data = new QueryChangesSubResponseData
                    {
                        StorageIndexExtendedGuid = storageIndex,
                        CellKnowledgeCellGuid = cellId.LongId.Guid,
                        CellKnowledgeTo = knowledgeSequence,
                        WaterlineCellStorageExtendedGuid = cellId.ShortId,
                        Waterline = knowledgeSequence,
                        KnowledgeBytes = BinaryKnowledgeBuilder.FromElements(elements, cellId.ShortId, knowledgeSequence),
                    },
                },
            },
        };
    }

    private sealed record ObjectDataSpec(
        ExGuid Guid,
        byte[] Content,
        IReadOnlyList<ExGuid> References);

    private sealed record ObjectDeclarationSpec(ExGuid Guid, ulong Size, int References);

    private static DataElement BuildObjectGroupDataElement(
        ExGuid groupGuid,
        SerialNumber serial,
        IReadOnlyList<ObjectDataSpec> dataObjects,
        IReadOnlyList<ObjectDeclarationSpec> declarations)
    {
        var output = new BinaryWriterEx();
        var declarationBytes = new BinaryWriterEx();
        foreach (var declaration in declarations)
        {
            var body = new BinaryWriterEx();
            declaration.Guid.Serialize(body);
            new Compact64bitInt(1).Serialize(body);
            new Compact64bitInt(declaration.Size).Serialize(body);
            new Compact64bitInt((ulong)declaration.References).Serialize(body);
            new Compact64bitInt(0).Serialize(body);
            WriteStart(declarationBytes, StreamObjectTypeHeaderStart.ObjectGroupObjectDeclare, body.Length);
            declarationBytes.WriteBytes(body.ToArray());
        }

        WriteStart(output, StreamObjectTypeHeaderStart.ObjectGroupDeclarations, 0);
        output.WriteBytes(declarationBytes.ToArray());
        new StreamObjectHeaderEnd8Bit(StreamObjectTypeHeaderEnd.ObjectGroupDeclarations).Serialize(output);

        var dataBytes = new BinaryWriterEx();
        foreach (var item in dataObjects)
        {
            var body = new BinaryWriterEx();
            new Compact64bitInt((ulong)item.References.Count).Serialize(body);
            foreach (var reference in item.References) reference.Serialize(body);
            new Compact64bitInt(0).Serialize(body);
            new BinaryItem(item.Content).Serialize(body);
            WriteStart(dataBytes, StreamObjectTypeHeaderStart.ObjectGroupObjectData, body.Length);
            dataBytes.WriteBytes(body.ToArray());
        }

        WriteStart(output, StreamObjectTypeHeaderStart.ObjectGroupData, 0);
        output.WriteBytes(dataBytes.ToArray());
        new StreamObjectHeaderEnd8Bit(StreamObjectTypeHeaderEnd.ObjectGroupData).Serialize(output);
        return new DataElement(DataElementType.ObjectGroupDataElementData, groupGuid, serial)
        {
            Data = output.ToArray(),
        };
    }

    /// <summary>Stable identities for a partition across requests.</summary>
    public sealed record EditorsTableIdentity(
        ExGuid StorageManifestGuid,
        ExGuid CellManifestGuid,
        ExGuid RevisionManifestGuid,
        ExGuid ObjectGroupGuid,
        ExGuid HeaderObjectGuid,
        ExGuid CompressedObjectGuid,
        ExGuid RevisionId,
        CellId CellId,
        Guid SerialGuid);

    private static EditorsTableIdentity CreateIdentity()
    {
        return new EditorsTableIdentity(
            new ExGuid(1, Guid.NewGuid()),
            new ExGuid(2, Guid.NewGuid()),
            new ExGuid(3, Guid.NewGuid()),
            new ExGuid(4, Guid.NewGuid()),
            new ExGuid(5, Guid.NewGuid()),
            new ExGuid(6, Guid.NewGuid()),
            new ExGuid(7, Guid.NewGuid()),
            new CellId(
                new ExGuid(1, StorageManifestBuilder.RootExtendedGuid),
                new ExGuid(1, StorageManifestBuilder.CellSecondExtendedGuid)),
            Guid.NewGuid());
    }

    private static DataElement BuildEditorsObjectGroup(
        ExGuid groupGuid,
        SerialNumber serial,
        ExGuid headerGuid,
        ExGuid compressedGuid,
        byte[] header,
        byte[] compressed,
        IReadOnlyList<FsshttpdChunk> chunks)
    {
        var output = new BinaryWriterEx();
        var declarations = new BinaryWriterEx();
        WriteObjectDeclaration(declarations, headerGuid, (ulong)header.Length, 0);
        WriteObjectDeclaration(declarations, compressedGuid, (ulong)compressed.Length, 0);

        // The special editors stream is also materialized as a regular
        // FSSHTTPD tree.  The first object is the root node; every leaf points
        // to one raw data object.  Keeping this graph in the same object group
        // preserves the editor extractor's mandated header/compressed pair
        // while making the actual chunked representation available to a
        // protocol client.
        ExGuid rootGuid = new(groupGuid.Value, groupGuid.Guid);
        var leafGuids = chunks.Select((_, i) => new ExGuid(
            checked((uint)(100 + i)), Guid.NewGuid())).ToArray();
        var dataGuids = chunks.Select((_, i) => new ExGuid(
            checked((uint)(1000 + i)), Guid.NewGuid())).ToArray();
        byte[] rootNode = FsshttpdNodeSerializer.SerializeIntermediate(
            (ulong)(header.Length + compressed.Length),
            System.Security.Cryptography.SHA1.HashData(chunks.SelectMany(c => c.Content).ToArray()));
        WriteObjectDeclaration(declarations, rootGuid, (ulong)rootNode.Length, leafGuids.Length);
        for (int i = 0; i < chunks.Count; i++)
        {
            byte[] leafNode = FsshttpdNodeSerializer.SerializeLeaf(chunks[i]);
            WriteObjectDeclaration(declarations, leafGuids[i], (ulong)leafNode.Length, 1);
            WriteObjectDeclaration(declarations, dataGuids[i], chunks[i].Length, 0);
        }

        WriteStart(output, StreamObjectTypeHeaderStart.ObjectGroupDeclarations, 0);
        output.WriteBytes(declarations.ToArray());
        new StreamObjectHeaderEnd8Bit(StreamObjectTypeHeaderEnd.ObjectGroupDeclarations).Serialize(output);

        var data = new BinaryWriterEx();
        WriteObjectData(data, headerGuid, header, Array.Empty<ExGuid>());
        WriteObjectData(data, compressedGuid, compressed, Array.Empty<ExGuid>());
        WriteObjectData(data, rootGuid, rootNode, leafGuids);
        for (int i = 0; i < chunks.Count; i++)
        {
            byte[] leafNode = FsshttpdNodeSerializer.SerializeLeaf(chunks[i]);
            WriteObjectData(data, leafGuids[i], leafNode, new[] { dataGuids[i] });
            WriteObjectData(data, dataGuids[i], chunks[i].Content, Array.Empty<ExGuid>());
        }
        WriteStart(output, StreamObjectTypeHeaderStart.ObjectGroupData, 0);
        output.WriteBytes(data.ToArray());
        new StreamObjectHeaderEnd8Bit(StreamObjectTypeHeaderEnd.ObjectGroupData).Serialize(output);

        return new DataElement(DataElementType.ObjectGroupDataElementData, groupGuid, serial)
        {
            Data = output.ToArray(),
        };
    }

    private static void WriteObjectDeclaration(BinaryWriterEx output, ExGuid objectGuid, ulong size, int references)
    {
        var body = new BinaryWriterEx();
        objectGuid.Serialize(body);
        new Compact64bitInt(1).Serialize(body); // Object partition ID
        new Compact64bitInt(size).Serialize(body);
        new Compact64bitInt((ulong)references).Serialize(body);
        new Compact64bitInt(0).Serialize(body); // Cell references
        WriteStart(output, StreamObjectTypeHeaderStart.ObjectGroupObjectDeclare, body.Length);
        output.WriteBytes(body.ToArray());
    }

    private static void WriteObjectData(BinaryWriterEx output, ExGuid objectGuid, byte[] content, IReadOnlyList<ExGuid> references)
    {
        var body = new BinaryWriterEx();
        new Compact64bitInt((ulong)references.Count).Serialize(body);
        foreach (var reference in references)
        {
            reference.Serialize(body);
        }
        new Compact64bitInt(0).Serialize(body); // Cell ID array
        new BinaryItem(content).Serialize(body);
        WriteStart(output, StreamObjectTypeHeaderStart.ObjectGroupObjectData, body.Length);
        output.WriteBytes(body.ToArray());
    }

    private static void WriteStart(BinaryWriterEx output, StreamObjectTypeHeaderStart type, int length)
    {
        if (length <= StreamObjectHeaderStart.Max16BitLength && (int)type <= 0x3F)
        {
            new StreamObjectHeaderStart16Bit(type, length).Serialize(output);
        }
        else
        {
            new StreamObjectHeaderStart32Bit(type, length).Serialize(output);
        }
    }

    private static void WriteElement(XmlWriter writer, string name, string value)
    {
        writer.WriteStartElement(name);
        writer.WriteString(value);
        writer.WriteEndElement();
    }

    private static void WriteOptional(XmlWriter writer, string name, string? value)
    {
        if (value is not null)
        {
            WriteElement(writer, name, value);
        }
    }
}
