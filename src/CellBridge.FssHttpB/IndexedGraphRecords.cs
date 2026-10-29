namespace CellBridge.FssHttpB;

public sealed record ProtocolRevisionRecord(ExGuid Revision, ExGuid BaseRevision,
    IReadOnlyList<RevisionRootReference> Roots, IReadOnlyList<ExGuid> ObjectGroups);
public sealed record ProtocolStorageManifestRecord(Guid Schema, IReadOnlyList<StorageRootReference> Roots);

/// <summary>Reads immutable records independently so an indexer need only decode newly admitted elements.</summary>
public static class IndexedGraphRecords
{
    public static ProtocolRevisionRecord ReadRevision(DataElement element)
    {
        var reader = Reader(element, DataElementType.RevisionManifestDataElementData);
        var body = Body(reader, StreamObjectTypeHeaderStart.RevisionManifest);
        var revision = ExGuid.Deserialize(body);
        var baseRevision = ExGuid.Deserialize(body);
        Empty(body);
        if (revision.IsNull) throw new InvalidDataException("Null protocol revision identity.");
        var roots = new List<RevisionRootReference>();
        var rootIds = new HashSet<ExGuid>();
        var groups = new List<ExGuid>();
        while (reader.Remaining != 0)
        {
            if (roots.Count + groups.Count >= 1_000_000) throw new InvalidDataException("Revision reference budget exceeded.");
            var header = StreamObjectHeaderStart.Parse(reader);
            body = Body(reader, header);
            switch (header.Type)
            {
                case StreamObjectTypeHeaderStart.RevisionManifestRootDeclare:
                    var root = ExGuid.Deserialize(body);
                    var obj = ExGuid.Deserialize(body);
                    if (!rootIds.Add(root)) throw new InvalidDataException("Duplicate revision root identifier.");
                    roots.Add(new(root, obj));
                    break;
                case StreamObjectTypeHeaderStart.RevisionManifestObjectGroupReferences:
                    groups.Add(ExGuid.Deserialize(body));
                    break;
                default: throw new InvalidDataException($"Unexpected revision record {header.Type}.");
            }
            Empty(body);
        }
        return new(revision, baseRevision, roots, groups);
    }

    public static ExGuid ReadCellRevision(DataElement element)
    {
        var reader = Reader(element, DataElementType.CellManifestDataElementData);
        var body = Body(reader, StreamObjectTypeHeaderStart.CellManifestCurrentRevision);
        var revision = ExGuid.Deserialize(body);
        Empty(body);
        Empty(reader);
        return revision;
    }

    public static ProtocolStorageManifestRecord ReadStorageManifest(DataElement element)
    {
        var reader = Reader(element, DataElementType.StorageManifestDataElementData);
        var schema = Body(reader, StreamObjectTypeHeaderStart.StorageManifestSchemaGUID);
        var schemaId = ExGuid.ReadGuid(schema);
        Empty(schema);
        var roots = new List<StorageRootReference>();
        var rootIds = new HashSet<ExGuid>();
        while (reader.Remaining != 0)
        {
            if (roots.Count >= 1_000_000) throw new InvalidDataException("Storage root budget exceeded.");
            var body = Body(reader, StreamObjectTypeHeaderStart.StorageManifestRootDeclare);
            var root = ExGuid.Deserialize(body);
            var cell = CellId.Deserialize(body);
            Empty(body);
            if (!rootIds.Add(root)) throw new InvalidDataException("Duplicate storage root identifier.");
            roots.Add(new(root, cell));
        }
        if (roots.Count == 0) throw new InvalidDataException("Storage manifest must declare at least one root.");
        return new(schemaId, roots);
    }

    public static byte[] ReadBlob(DataElement element)
    {
        var reader = Reader(element, DataElementType.ObjectDataBLOBDataElementData);
        var body = Body(reader, StreamObjectTypeHeaderStart.ObjectDataBLOB);
        var bytes = body.ReadBytes(body.Remaining);
        Empty(reader);
        return bytes;
    }

    private static BinaryReaderEx Reader(DataElement element, DataElementType kind)
    {
        if (element.DataElementType != kind) throw new InvalidDataException($"Expected {kind}.");
        return new(element.Data ?? throw new InvalidDataException("Missing graph payload."));
    }
    private static BinaryReaderEx Body(BinaryReaderEx reader, StreamObjectTypeHeaderStart kind)
    {
        var header = StreamObjectHeaderStart.Parse(reader);
        if (header.Type != kind) throw new InvalidDataException($"Expected {kind}, got {header.Type}.");
        return Body(reader, header);
    }
    private static BinaryReaderEx Body(BinaryReaderEx reader, StreamObjectHeaderStart header)
    {
        if (header.Compound != 0) throw new InvalidDataException("Graph record must not be compound.");
        return new(reader.ReadMemory(header.Length));
    }
    private static void Empty(BinaryReaderEx reader)
    {
        if (reader.Remaining != 0) throw new InvalidDataException("Trailing graph record bytes.");
    }
}
