namespace OfficeCollabServer.FssHttpB;

/// <summary>
/// The payload kind carried by an object in an object-group data element.
/// </summary>
public enum ObjectGroupPayloadKind
{
    Raw,
    IntermediateNode,
    LeafNode,
}

/// <summary>Metadata declared for one object in an object group.</summary>
public sealed record ObjectGroupObjectDeclaration(
    ExGuid ObjectGuid,
    ulong PartitionId,
    ulong DataSize,
    ulong ObjectReferencesCount,
    ulong CellReferencesCount);

/// <summary>The parsed FSSHTTPD node envelope, when an object contains one.</summary>
public sealed record ObjectGroupNode(
    ObjectGroupPayloadKind Kind,
    byte[] Signature,
    ulong RepresentedDataSize,
    byte[]? DataHash);

/// <summary>
/// One object declaration and its corresponding object data record.
/// Object data records are associated with declarations by wire order. The
/// extended GUID array in ObjectGroupObjectData is the child-reference array;
/// it does not identify the object which owns the record.
/// </summary>
public sealed record ObjectGroupObject(
    ObjectGroupObjectDeclaration Declaration,
    IReadOnlyList<ExGuid> ObjectReferences,
    IReadOnlyList<CellId> CellReferences,
    byte[] Content,
    ObjectGroupPayloadKind PayloadKind,
    ObjectGroupNode? Node)
{
    public ExGuid ObjectGuid => Declaration.ObjectGuid;
}

/// <summary>One parsed ObjectGroupDataElementData payload.</summary>
public sealed class ObjectGroupDataElement
{
    private ObjectGroupDataElement(
        ExGuid dataElementGuid,
        SerialNumber serialNumber,
        IReadOnlyList<ObjectGroupObject> objects)
    {
        DataElementGuid = dataElementGuid;
        SerialNumber = serialNumber;
        Objects = objects;
    }

    public ExGuid DataElementGuid { get; }
    public SerialNumber SerialNumber { get; }
    public IReadOnlyList<ObjectGroupObject> Objects { get; }

    /// <summary>
    /// Parses the object-group payload retained by <see cref="DataElement"/>.
    /// Object-data BLOB references are rejected because resolving them needs
    /// the surrounding response's BLOB data elements.
    /// </summary>
    public static ObjectGroupDataElement Parse(DataElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (element.DataElementType != DataElementType.ObjectGroupDataElementData)
            throw new ArgumentException("The data element is not an object group.", nameof(element));
        if (element.Data is null)
            throw new InvalidDataException("The object group has no payload.");

        var reader = new BinaryReaderEx(element.Data);
        RequireStart(reader, StreamObjectTypeHeaderStart.ObjectGroupDeclarations);

        var declarations = new List<ObjectGroupObjectDeclaration>();
        while (IsStart(reader))
        {
            var header = StreamObjectHeaderStart.Parse(reader);
            if (header.Type != StreamObjectTypeHeaderStart.ObjectGroupObjectDeclare)
            {
                if (header.Type == StreamObjectTypeHeaderStart.ObjectGroupObjectBLOBDataDeclaration)
                    throw new InvalidDataException("Object-data BLOB declarations are not supported by this reader.");
                throw new InvalidDataException($"Unexpected {header.Type} in object-group declarations.");
            }

            var body = ReadBody(reader, header, "object declaration");
            var bodyReader = new BinaryReaderEx(body);
            var declaration = new ObjectGroupObjectDeclaration(
                ExGuid.Deserialize(bodyReader),
                Compact64bitInt.Deserialize(bodyReader).Value,
                Compact64bitInt.Deserialize(bodyReader).Value,
                Compact64bitInt.Deserialize(bodyReader).Value,
                Compact64bitInt.Deserialize(bodyReader).Value);
            RequireEmpty(bodyReader, "object declaration");
            declarations.Add(declaration);
        }

        RequireEnd(reader, StreamObjectTypeHeaderEnd.ObjectGroupDeclarations);

        // SharePoint includes a compact metadata-declarations compound block
        // in the captured save graphs. Its records are orthogonal to content;
        // consume their framed bodies so the following data block is aligned.
        if (IsStart(reader) && PeekStartType(reader) == StreamObjectTypeHeaderStart.ObjectGroupMetadataDeclarations)
            SkipMetadataDeclarations(reader);

        RequireStart(reader, StreamObjectTypeHeaderStart.ObjectGroupData);
        var objects = new List<ObjectGroupObject>(declarations.Count);
        while (IsStart(reader))
        {
            var header = StreamObjectHeaderStart.Parse(reader);
            if (header.Type == StreamObjectTypeHeaderStart.ObjectGroupObjectDataBLOBReference)
                throw new InvalidDataException("Object-data BLOB references are not supported by this reader.");
            if (header.Type != StreamObjectTypeHeaderStart.ObjectGroupObjectData)
                throw new InvalidDataException($"Unexpected {header.Type} in object-group data.");

            if (objects.Count == declarations.Count)
                throw new InvalidDataException("Object group contains more data records than declarations.");

            var body = ReadBody(reader, header, "object data");
            var bodyReader = new BinaryReaderEx(body);
            var objectReferences = ReadExtendedGuids(bodyReader, "object references");
            var cellReferences = ReadCellIds(bodyReader);
            var dataSize = Compact64bitInt.Deserialize(bodyReader).Value;
            if (dataSize > int.MaxValue)
                throw new InvalidDataException($"Object data size {dataSize} exceeds the supported buffer size.");
            var content = bodyReader.ReadBytes((int)dataSize);
            RequireEmpty(bodyReader, "object data");

            var (payloadKind, node) = ParseNode(content);
            objects.Add(new ObjectGroupObject(
                declarations[objects.Count],
                objectReferences,
                cellReferences,
                content,
                payloadKind,
                node));
        }

        RequireEnd(reader, StreamObjectTypeHeaderEnd.ObjectGroupData);
        RequireEmpty(reader, "object-group data element");
        if (objects.Count != declarations.Count)
            throw new InvalidDataException($"Object group declares {declarations.Count} objects but contains {objects.Count} data records.");

        return new ObjectGroupDataElement(element.DataElementExtendedGuid, element.SerialNumber, objects);
    }

    private static void SkipMetadataDeclarations(BinaryReaderEx reader)
    {
        RequireStart(reader, StreamObjectTypeHeaderStart.ObjectGroupMetadataDeclarations);
        while (IsStart(reader))
        {
            var header = StreamObjectHeaderStart.Parse(reader);
            if (header.Type != (StreamObjectTypeHeaderStart)0x78)
                throw new InvalidDataException($"Unexpected {header.Type} in object-group metadata declarations.");
            _ = ReadBody(reader, header, "object metadata");
        }
        RequireEnd(reader, StreamObjectTypeHeaderEnd.ObjectGroupMetadataDeclarations);
    }

    private static IReadOnlyList<ExGuid> ReadExtendedGuids(BinaryReaderEx reader, string field)
    {
        var count = Compact64bitInt.Deserialize(reader).Value;
        if (count > int.MaxValue)
            throw new InvalidDataException($"{field} count {count} exceeds the supported buffer size.");
        var result = new List<ExGuid>((int)count);
        for (var i = 0; i < (int)count; i++)
            result.Add(ExGuid.Deserialize(reader));
        return result;
    }

    private static IReadOnlyList<CellId> ReadCellIds(BinaryReaderEx reader)
    {
        var count = Compact64bitInt.Deserialize(reader).Value;
        if (count > int.MaxValue)
            throw new InvalidDataException($"Cell reference count {count} exceeds the supported buffer size.");
        var result = new List<CellId>((int)count);
        for (var i = 0; i < (int)count; i++)
            result.Add(CellId.Deserialize(reader));
        return result;
    }

    private static (ObjectGroupPayloadKind Kind, ObjectGroupNode? Node) ParseNode(byte[] content)
    {
        if (content.Length == 0)
            return (ObjectGroupPayloadKind.Raw, null);

        var reader = new BinaryReaderEx(content);
        StreamObjectHeaderStart start;
        try
        {
            start = StreamObjectHeaderStart.Parse(reader);
        }
        catch (InvalidDataException)
        {
            return (ObjectGroupPayloadKind.Raw, null);
        }
        catch (EndOfStreamException)
        {
            return (ObjectGroupPayloadKind.Raw, null);
        }

        if (start.Type is not (StreamObjectTypeHeaderStart.IntermediateNodeObject or StreamObjectTypeHeaderStart.LeafNodeObject))
            return (ObjectGroupPayloadKind.Raw, null);

        // Older SharePoint node streams use a zero-length compound start and
        // terminate the node with its end header. Newer streams put the
        // nested body length in the start header. Keep the outer reader for
        // the legacy form so its nested records remain visible.
        var boundedBody = start.Length > 0;
        var bodyReader = boundedBody
            ? new BinaryReaderEx(ReadBody(reader, start, "FSSHTTPD node"))
            : reader;
        var signatureHeader = StreamObjectHeaderStart.Parse(bodyReader);
        if (signatureHeader.Type != StreamObjectTypeHeaderStart.SignatureObject)
            throw new InvalidDataException($"Expected SignatureObject, got {signatureHeader.Type}.");
        var signature = BinaryItem.Deserialize(new BinaryReaderEx(ReadBody(bodyReader, signatureHeader, "node signature")));
        var sizeHeader = StreamObjectHeaderStart.Parse(bodyReader);
        if (sizeHeader.Type != StreamObjectTypeHeaderStart.DataSizeObject)
            throw new InvalidDataException($"Expected DataSizeObject, got {sizeHeader.Type}.");
        var sizeBody = ReadBody(bodyReader, sizeHeader, "node data size");
        var sizeReader = new BinaryReaderEx(sizeBody);
        var representedSize = sizeReader.ReadUInt64();
        RequireEmpty(sizeReader, "node data size");
        byte[]? hash = null;
        if (bodyReader.Remaining > 0 && IsStart(bodyReader))
        {
            // MS-FSSHTTPD permits an optional leaf hash. The current captures
            // and generated file graph do not use it; preserve it when its
            // standard header is present and reject other trailing structures.
            var hashHeader = StreamObjectHeaderStart.Parse(bodyReader);
            if (hashHeader.Type != (StreamObjectTypeHeaderStart)0x2F)
                throw new InvalidDataException($"Unsupported trailing {hashHeader.Type} in FSSHTTPD node.");
            hash = BinaryItem.Deserialize(new BinaryReaderEx(ReadBody(bodyReader, hashHeader, "leaf data hash"))).Content;
        }
        if (boundedBody)
            RequireEmpty(bodyReader, "FSSHTTPD node body");

        var end = StreamObjectHeaderEnd.Parse(reader);
        var expectedEnd = start.Type == StreamObjectTypeHeaderStart.LeafNodeObject
            ? StreamObjectTypeHeaderEnd.IntermediateNodeEnd
            : StreamObjectTypeHeaderEnd.RootNodeEnd;
        if (end.Type != expectedEnd)
            throw new InvalidDataException($"Expected {expectedEnd} for {start.Type}, got {end.Type}.");
        RequireEmpty(reader, "FSSHTTPD node");

        var kind = start.Type == StreamObjectTypeHeaderStart.LeafNodeObject
            ? ObjectGroupPayloadKind.LeafNode
            : ObjectGroupPayloadKind.IntermediateNode;
        return (kind, new ObjectGroupNode(kind, signature.Content, representedSize, hash));
    }

    private static byte[] ReadBody(BinaryReaderEx reader, StreamObjectHeaderStart header, string description)
    {
        if (header.Length < 0 || header.Length > reader.Remaining)
            throw new InvalidDataException($"{description} header length {header.Length} exceeds the remaining payload.");
        return reader.ReadBytes(header.Length);
    }

    private static void RequireStart(BinaryReaderEx reader, StreamObjectTypeHeaderStart expected)
    {
        var header = StreamObjectHeaderStart.Parse(reader);
        if (header.Type != expected)
            throw new InvalidDataException($"Expected {expected}, got {header.Type}.");
    }

    private static void RequireEnd(BinaryReaderEx reader, StreamObjectTypeHeaderEnd expected)
    {
        var header = StreamObjectHeaderEnd.Parse(reader);
        if (header.Type != expected)
            throw new InvalidDataException($"Expected {expected}, got {header.Type}.");
    }

    private static void RequireEmpty(BinaryReaderEx reader, string description)
    {
        if (reader.Remaining != 0)
            throw new InvalidDataException($"{description} has {reader.Remaining} trailing bytes.");
    }

    private static bool IsStart(BinaryReaderEx reader) => reader.Remaining > 0 && (PeekDiscriminator(reader) is 0 or 2);

    private static StreamObjectTypeHeaderStart PeekStartType(BinaryReaderEx reader)
    {
        var position = reader.Position;
        var header = StreamObjectHeaderStart.Parse(reader);
        reader.Position = position;
        return header.Type;
    }

    private static int PeekDiscriminator(BinaryReaderEx reader)
    {
        var position = reader.Position;
        var value = reader.ReadByte() & 3;
        reader.Position = position;
        return value;
    }
}

/// <summary>
/// Merges object-group records and materializes a root by following its child
/// references in order. This is intentionally independent of document storage
/// so PutChanges callers can validate a graph before committing it.
/// </summary>
public sealed class ObjectGroupGraph
{
    private readonly Dictionary<ExGuid, ObjectGroupObject> _objects = new();

    public IReadOnlyDictionary<ExGuid, ObjectGroupObject> Objects => _objects;

    public static ObjectGroupGraph FromDataElements(IEnumerable<DataElement> elements)
    {
        ArgumentNullException.ThrowIfNull(elements);
        var graph = new ObjectGroupGraph();
        foreach (var element in elements.Where(x => x.DataElementType == DataElementType.ObjectGroupDataElementData))
        {
            foreach (var obj in ObjectGroupDataElement.Parse(element).Objects)
                graph._objects[obj.ObjectGuid] = obj;
        }
        return graph;
    }

    public void Add(ObjectGroupDataElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        foreach (var obj in element.Objects)
            _objects[obj.ObjectGuid] = obj;
    }

    public byte[] Materialize(ExGuid rootObjectGuid)
    {
        var output = new MemoryStream();
        var active = new HashSet<ExGuid>();
        Visit(rootObjectGuid, output, active);
        return output.ToArray();
    }

    private void Visit(ExGuid objectGuid, MemoryStream output, HashSet<ExGuid> active)
    {
        if (!_objects.TryGetValue(objectGuid, out var obj))
            throw new InvalidDataException($"Object reference {objectGuid} is unresolved.");
        if (!active.Add(objectGuid))
            throw new InvalidDataException($"Object graph contains a cycle at {objectGuid}.");

        var before = output.Length;
        if (obj.ObjectReferences.Count == 0)
        {
            output.Write(obj.Content);
        }
        else
        {
            foreach (var reference in obj.ObjectReferences)
                Visit(reference, output, active);
        }

        var materialized = checked((ulong)(output.Length - before));
        if (obj.Node is not null && obj.Node.RepresentedDataSize != materialized)
        {
            throw new InvalidDataException(
                $"Object {objectGuid} represents {obj.Node.RepresentedDataSize} bytes but its references materialize {materialized} bytes.");
        }

        active.Remove(objectGuid);
    }
}
