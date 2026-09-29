using System.IO.Compression;
using System.Text;
using System.Xml;

namespace OfficeCollabServer.FssHttpB;

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
/// MS-FSSHTTPB 2.2.3.1.2.1 requires the UTF-8 XML to be DEFLATE-compressed,
/// prefixed with the eight-byte editors header, and then represented as the
/// first two object data values in the object group.  The object group itself
/// remains a normal FSSHTTPB graph, while the stream is available to the
/// FSSHTTPD chunker for callers that need to materialize a node tree.
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
    /// Builds the six data elements observed in SharePoint's 13/11
    /// EditorsTable response.  The graph is intentionally split into two
    /// object groups: one root node group and one group containing the fixed
    /// stream header, compressed XML, and its two leaf nodes.  This is the
    /// legacy representation consumed by the Word client in the reference
    /// capture.
    /// </summary>
    public static IReadOnlyList<DataElement> BuildSharePointV13DataElements(
        IEnumerable<EditorsTableEditor>? editors,
        CellId cellId,
        Guid identityGuid)
    {
        ArgumentNullException.ThrowIfNull(cellId);
        var guid = identityGuid == Guid.Empty ? Guid.NewGuid() : identityGuid;
        var stream = BuildEditorsTableStream(editors);
        var header = ZipStreamHeader.ToArray();
        var compressed = stream[ZipStreamHeader.Length..];

        var revisionGuid = new ExGuid(21, guid);
        var revisionElementGuid = new ExGuid(22, guid);
        var rootGuid = new ExGuid(2, StorageManifestBuilder.RootExtendedGuid);
        var objectRootGuid = new ExGuid(13, guid);
        var groupRootGuid = new ExGuid(24, guid);
        var groupStreamGuid = new ExGuid(26, guid);
        var headerGuid = new ExGuid(15, guid);
        var compressedGuid = new ExGuid(17, guid);
        var leafHeaderGuid = new ExGuid(14, guid);
        var leafCompressedGuid = new ExGuid(16, guid);
        var storageGuid = new ExGuid(18, guid);
        var cellGuid = new ExGuid(29, guid);
        var indexGuid = new ExGuid(43, guid);
        var serialGuid = identityGuid == Guid.Empty ? guid : identityGuid;

        var rootChunk = FsshttpdChunker.Chunk(header,
            _ => leafHeaderGuid).Single();
        var compressedChunk = FsshttpdChunker.Chunk(compressed,
            _ => leafCompressedGuid).Single();
        var rootNode = FsshttpdNodeSerializer.SerializeLegacyIntermediate(
            (ulong)stream.Length, stream);
        var leafHeader = FsshttpdNodeSerializer.SerializeLegacyLeaf(rootChunk);
        var leafCompressed = FsshttpdNodeSerializer.SerializeLegacyLeaf(compressedChunk);

        var rootGroup = BuildObjectGroupDataElement(
            groupRootGuid,
            new SerialNumber(serialGuid, 129),
            new[] { new ObjectDataSpec(objectRootGuid, rootNode, new[] { leafHeaderGuid, leafCompressedGuid }) },
            new[] { new ObjectDeclarationSpec(objectRootGuid, (ulong)rootNode.Length, 2) });
        var streamGroup = BuildObjectGroupDataElement(
            groupStreamGuid,
            new SerialNumber(serialGuid, 130),
            new[]
            {
                new ObjectDataSpec(headerGuid, header, Array.Empty<ExGuid>()),
                new ObjectDataSpec(compressedGuid, compressed, Array.Empty<ExGuid>()),
                new ObjectDataSpec(leafHeaderGuid, leafHeader, new[] { headerGuid }),
                new ObjectDataSpec(leafCompressedGuid, leafCompressed, new[] { compressedGuid }),
            },
            new[]
            {
                new ObjectDeclarationSpec(headerGuid, (ulong)header.Length, 0),
                new ObjectDeclarationSpec(compressedGuid, (ulong)compressed.Length, 0),
                new ObjectDeclarationSpec(leafHeaderGuid, (ulong)leafHeader.Length, 1),
                new ObjectDeclarationSpec(leafCompressedGuid, (ulong)leafCompressed.Length, 1),
            });

        var revision = StorageManifestBuilder.BuildRevisionManifestDataElement(
            revisionElementGuid, new SerialNumber(serialGuid, 128), revisionGuid,
            ExGuid.Null, rootGuid, objectRootGuid, new[] { groupRootGuid, groupStreamGuid });
        var storage = StorageManifestBuilder.BuildStorageManifestDataElement(
            storageGuid, new SerialNumber(serialGuid, 124), rootGuid, cellId);
        var cell = StorageManifestBuilder.BuildCellManifestDataElement(
            cellGuid, new SerialNumber(serialGuid, 126), revisionGuid);
        var index = StorageManifestBuilder.BuildStorageIndexDataElement(
            indexGuid, new SerialNumber(serialGuid, 44));

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
        var storageIndex = new ExGuid(43, identityGuid);
        return new FsshttpbResponse
        {
            ProtocolVersion = 13,
            MinimumVersion = 11,
            DataElementPackage = new DataElementPackage
            {
                DataElements = [..BuildSharePointV13DataElements(editors, cellId, identityGuid)],
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
                        Waterline = knowledgeSequence,
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
