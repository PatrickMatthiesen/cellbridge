namespace CellBridge.FssHttpB;

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
        IReadOnlyList<ObjectGroupObject> objects, IEnumerable<ulong> changeFrequencies)
    {
        DataElementGuid = dataElementGuid;
        SerialNumber = serialNumber;
        Objects = objects;
        ChangeFrequencies = Array.AsReadOnly(changeFrequencies.ToArray());
    }

    public ExGuid DataElementGuid { get; }
    public SerialNumber SerialNumber { get; }
    public IReadOnlyList<ObjectGroupObject> Objects { get; }
    /// <summary>Optional metadata in wire order. Values at least 4 are custom frequencies.</summary>
    public IReadOnlyList<ulong> ChangeFrequencies { get; }

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

            if (declarations.Count >= 100_000)
                throw new InvalidDataException("Object declaration count limit exceeded.");
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

        // MS-FSSHTTPB 2.2.1.12.6.3.1 defines a change-frequency integer, with no references.
        IReadOnlyList<ulong> frequencies = [];
        if (IsStart(reader) && PeekStartType(reader) == StreamObjectTypeHeaderStart.ObjectGroupMetadataDeclarations)
            frequencies = ReadMetadataDeclarations(reader);

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
            var declaration = declarations[objects.Count];
            if (declaration.ObjectReferencesCount != (ulong)objectReferences.Count ||
                declaration.CellReferencesCount != (ulong)cellReferences.Count)
                throw new InvalidDataException("Object reference counts do not match the declaration.");
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

        return new ObjectGroupDataElement(element.DataElementExtendedGuid, element.SerialNumber, objects, frequencies);
    }

    private static IReadOnlyList<ulong> ReadMetadataDeclarations(BinaryReaderEx reader)
    {
        var start = StreamObjectHeaderStart.Parse(reader);
        if (start.Type != StreamObjectTypeHeaderStart.ObjectGroupMetadataDeclarations || start.Compound != 1 || start.Length != 0)
            throw new InvalidDataException("Invalid object metadata declarations header.");
        var frequencies = new List<ulong>();
        while (IsStart(reader))
        {
            var header = StreamObjectHeaderStart.Parse(reader);
            if (header.Type != (StreamObjectTypeHeaderStart)0x78 || header.Compound != 0)
                throw new InvalidDataException($"Unexpected {header.Type} in object-group metadata declarations.");
            if (frequencies.Count >= 100_000) throw new InvalidDataException("Object metadata count limit exceeded.");
            var body = new BinaryReaderEx(reader.ReadMemory(header.Length));
            frequencies.Add(Compact64bitInt.Deserialize(body).Value);
            RequireEmpty(body, "object metadata");
        }
        RequireEnd(reader, StreamObjectTypeHeaderEnd.ObjectGroupMetadataDeclarations);
        return frequencies;
    }

    private static IReadOnlyList<ExGuid> ReadExtendedGuids(BinaryReaderEx reader, string field)
    {
        var count = Compact64bitInt.Deserialize(reader).Value;
        if (count > 100_000 || count > (ulong)reader.Remaining)
            throw new InvalidDataException($"{field} count {count} exceeds the supported buffer size.");
        var result = new List<ExGuid>();
        for (var i = 0; i < (int)count; i++)
            result.Add(ExGuid.Deserialize(reader));
        return result;
    }

    private static IReadOnlyList<CellId> ReadCellIds(BinaryReaderEx reader)
    {
        var count = Compact64bitInt.Deserialize(reader).Value;
        if (count > 100_000 || count > (ulong)reader.Remaining)
            throw new InvalidDataException($"Cell reference count {count} exceeds the supported buffer size.");
        var result = new List<CellId>();
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

    private static ReadOnlyMemory<byte> ReadBody(BinaryReaderEx reader, StreamObjectHeaderStart header, string description)
    {
        if (header.Length < 0 || header.Length > reader.Remaining)
            throw new InvalidDataException($"{description} header length {header.Length} exceeds the remaining payload.");
        return reader.ReadMemory(header.Length);
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
                graph.AddObject(obj);
        }
        return graph;
    }

    public void Add(ObjectGroupDataElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        foreach (var obj in element.Objects)
            AddObject(obj);
    }

    private void AddObject(ObjectGroupObject obj)
    {
        if (_objects.TryGetValue(obj.ObjectGuid, out var previous) &&
            (previous.Declaration != obj.Declaration ||
             !previous.ObjectReferences.SequenceEqual(obj.ObjectReferences) ||
             !previous.CellReferences.SequenceEqual(obj.CellReferences) ||
             !previous.Content.SequenceEqual(obj.Content)))
            throw new InvalidDataException("The selected revision declares an object identifier with conflicting contents.");
        _objects[obj.ObjectGuid] = obj;
    }

    public byte[] Materialize(ExGuid rootObjectGuid, long maxBytes = int.MaxValue)
    {
        var plan = PlanMaterialization(rootObjectGuid, Math.Min(maxBytes, Array.MaxLength));
        var output = GC.AllocateUninitializedArray<byte>((int)plan.Length);
        int position = 0;
        foreach (var chunk in plan.Chunks)
        {
            chunk.CopyTo(output, position);
            position += chunk.Length;
        }
        return output;
    }

    /// <summary>
    /// Validates the complete selected graph before writing bytes to a writable stream.
    /// The destination need not support seeking and remains open. Destination I/O failures
    /// can leave partial output; structural and byte-limit failures leave it untouched.
    /// Callers must not mutate this graph during materialization.
    /// </summary>
    public long MaterializeTo(Stream destination, ExGuid rootObjectGuid, long maxBytes = int.MaxValue)
    {
        RequireWritable(destination);
        var plan = PlanMaterialization(rootObjectGuid, maxBytes);
        foreach (var chunk in plan.Chunks) destination.Write(chunk);
        return plan.Length;
    }

    /// <summary>
    /// Asynchronously writes a validated graph without seeking or closing the destination.
    /// Cancellation and destination I/O failures can leave partial output.
    /// Callers must not mutate this graph during materialization.
    /// </summary>
    public async ValueTask<long> MaterializeToAsync(Stream destination, ExGuid rootObjectGuid,
        long maxBytes = int.MaxValue, CancellationToken cancellationToken = default)
    {
        RequireWritable(destination);
        var plan = PlanMaterialization(rootObjectGuid, maxBytes, cancellationToken);
        foreach (var chunk in plan.Chunks)
            await destination.WriteAsync(chunk, cancellationToken);
        return plan.Length;
    }

    private static void RequireWritable(Stream destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite)
            throw new ArgumentException("The destination stream must be writable.", nameof(destination));
    }

    private MaterializationPlan PlanMaterialization(ExGuid rootObjectGuid, long maxBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rootObjectGuid);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        var chunks = new List<byte[]>();
        var active = new HashSet<ExGuid>();
        long length = 0;
        int visits = 0;
        Visit(rootObjectGuid);
        return new(length, chunks);

        void Visit(ExGuid objectGuid)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Repeated child references can expand a small DAG exponentially, including
            // zero-byte leaves that never reach the byte budget.
            if (++visits > 1_000_000)
                throw new InvalidDataException("Object graph traversal limit exceeded.");
            if (!_objects.TryGetValue(objectGuid, out var obj))
                throw new InvalidDataException($"Object reference {objectGuid} is unresolved.");
            if (!active.Add(objectGuid))
                throw new InvalidDataException($"Object graph contains a cycle at {objectGuid}.");
            if (active.Count > 256) throw new InvalidDataException("Object graph nesting limit exceeded.");
            if (obj.Node is not null && obj.Node.RepresentedDataSize > (ulong)maxBytes)
                throw new GraphMaterializationLimitException(maxBytes);

            var before = length;
            if (obj.ObjectReferences.Count == 0)
            {
                if (obj.Content.LongLength > maxBytes - length)
                    throw new GraphMaterializationLimitException(maxBytes);
                if (obj.Content.Length != 0) chunks.Add(obj.Content);
                length += obj.Content.LongLength;
            }
            else
            {
                foreach (var reference in obj.ObjectReferences) Visit(reference);
            }

            var materialized = (ulong)(length - before);
            if (obj.Node is not null && obj.Node.RepresentedDataSize != materialized)
                throw new InvalidDataException(
                    $"Object {objectGuid} represents {obj.Node.RepresentedDataSize} bytes but its references materialize {materialized} bytes.");
            active.Remove(objectGuid);
        }
    }

    private sealed record MaterializationPlan(long Length, IReadOnlyList<byte[]> Chunks);
}

public sealed class GraphMaterializationLimitException(long limit)
    : IOException($"Materialized graph exceeds the {limit}-byte limit.");
