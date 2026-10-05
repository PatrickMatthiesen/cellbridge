namespace CellBridge.FssHttpB;

public sealed record GenericGraphLimits(int MaxElements = 100_000, long MaxPayloadBytes = 512L * 1024 * 1024,
    int MaxRevisionDepth = 256, int MaxObjectVisits = 1_000_000, int MaxReferenceVisits = 1_000_000);

public sealed record StorageRootReference(ExGuid Root, CellId Cell);
public sealed record RevisionRootReference(ExGuid Root, ExGuid Object);

/// <summary>
/// Immutable, opaque MS-FSSHTTPB graph reader. It preserves application schemas,
/// roots and object partitions; it does not reconstruct or publish document files.
/// Returned identifiers, arrays and objects are detached from the snapshot.
/// </summary>
public sealed class GenericPartitionGraphSnapshot
{
    private readonly Dictionary<ExGuid, DataElement> _elements = new();
    private readonly Dictionary<CellId, ExGuid> _cellMappings = new();
    private readonly Dictionary<ExGuid, ExGuid> _revisionMappings = new();
    private readonly Dictionary<ExGuid, RevisionInfo> _revisions = new();
    private readonly Dictionary<ExGuid, IReadOnlyList<ObjectGroupObject>> _groups = new();
    private readonly Dictionary<CellId, CellInfo> _cells = new();
    private readonly Dictionary<ExGuid, byte[]> _blobs = new();
    private readonly HashSet<ExGuid> _required = new();
    private readonly List<StorageRootReference> _roots = new();
    private readonly ExGuid _storageIndex;
    private readonly GenericGraphLimits _limits;
    private readonly ObjectGroupParsingBudget _parsingBudget;
    private int _objectVisits;
    private int _referenceVisits;

    private GenericPartitionGraphSnapshot(IEnumerable<DataElement> elements, ExGuid storageIndex, GenericGraphLimits limits)
    {
        if (limits.MaxElements <= 0 || limits.MaxPayloadBytes < 0 || limits.MaxRevisionDepth <= 0 ||
            limits.MaxObjectVisits <= 0 || limits.MaxReferenceVisits <= 0)
            throw new ArgumentOutOfRangeException(nameof(limits));
        _limits = limits;
        _parsingBudget = new(limits.MaxObjectVisits, limits.MaxReferenceVisits);
        _storageIndex = Copy(storageIndex);
        long payloadBytes = 0;
        foreach (var element in elements)
        {
            ArgumentNullException.ThrowIfNull(element);
            if (_elements.Count >= limits.MaxElements)
                throw new InvalidDataException("Generic graph element budget exceeded.");
            payloadBytes = checked(payloadBytes + (element.Data?.LongLength ?? 0));
            if (payloadBytes > limits.MaxPayloadBytes)
                throw new InvalidDataException("Generic graph payload budget exceeded.");
            var copy = Copy(element);
            if (!_elements.TryAdd(copy.DataElementExtendedGuid, copy))
                throw new InvalidDataException("Generic graph contains duplicate data-element identifiers.");
        }
        PayloadBytes = payloadBytes;
        ReadIndex(Require(_storageIndex, DataElementType.StorageIndexDataElementData));
        foreach (var (cell, elementId) in _cellMappings)
        {
            var reader = Reader(Require(elementId, DataElementType.CellManifestDataElementData));
            var body = Body(reader, StreamObjectTypeHeaderStart.CellManifestCurrentRevision);
            var current = ExGuid.Deserialize(body);
            Empty(body);
            Empty(reader);
            _cells.Add(cell, ResolveCell(current));
        }
        foreach (var root in _roots)
            if (!_cells.ContainsKey(root.Cell)) throw new InvalidDataException("Unresolved storage root cell.");
        // Protect every mapping in this selected index, including historical
        // revisions. Objects are interpreted only in their selected cell scope.
        foreach (var revision in _revisionMappings.Keys) ReadRevision(revision);
        ValidateRevisionChains();
        ValidateReferences();
    }

    public static GenericPartitionGraphSnapshot Create(IEnumerable<DataElement> elements, ExGuid storageIndex,
        GenericGraphLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(elements);
        ArgumentNullException.ThrowIfNull(storageIndex);
        return new(elements, storageIndex, limits ?? new());
    }

    public Guid SchemaGuid { get; private set; }
    public ExGuid StorageIndex => Copy(_storageIndex);
    public int ElementCount => _elements.Count;
    public long PayloadBytes { get; }
    public IReadOnlyList<StorageRootReference> Roots => _roots.Select(r => new StorageRootReference(Copy(r.Root), Copy(r.Cell))).ToArray();
    public IReadOnlyCollection<CellId> Cells => _cells.Keys.Select(Copy).ToArray();
    public IReadOnlyCollection<DataElement> Elements => _elements.Values.Select(Copy).ToArray();
    /// <summary>Conservative reference closure of all mappings in the selected index.</summary>
    public IReadOnlyCollection<ExGuid> RequiredElements => _required.Select(Copy).ToArray();

    public ExGuid GetCurrentRevision(CellId cell) => Copy(Cell(cell).Revision);
    public IReadOnlyList<RevisionRootReference> GetRevisionRoots(CellId cell) => Cell(cell).Roots
        .Select(r => new RevisionRootReference(Copy(r.Root), Copy(r.Object))).ToArray();

    /// <summary>
    /// Returns the nearest definition of each object partition in this cell's
    /// current revision chain. References identify objects, not their partitions.
    /// BLOB-backed contents are resolved by their declared data-element ID.
    /// </summary>
    public IReadOnlyCollection<ObjectGroupObject> GetObjects(CellId cell) => Cell(cell).Objects.Values.Select(Detach).ToArray();
    public IReadOnlyCollection<ObjectGroupObject> GetObjectPartitions(CellId cell, ExGuid objectId) =>
        Cell(cell).Objects.Where(p => p.Key.Value == objectId.Value && p.Key.Guid == objectId.Guid)
            .Select(p => Detach(p.Value)).ToArray();

    private CellInfo Cell(CellId cell) => _cells.TryGetValue(cell, out var result) ? result
        : throw new InvalidDataException("Cell is not mapped by the selected storage index.");

    private void ReadIndex(DataElement element)
    {
        var reader = Reader(element);
        ExGuid? manifest = null;
        while (reader.Remaining > 0)
        {
            var header = StreamObjectHeaderStart.Parse(reader);
            var body = Body(reader, header);
            switch (header.Type)
            {
                case StreamObjectTypeHeaderStart.StorageIndexManifestMapping:
                    if (manifest is not null) throw new InvalidDataException("Duplicate storage manifest mapping.");
                    manifest = ExGuid.Deserialize(body);
                    _ = SerialNumber.Deserialize(body);
                    break;
                case StreamObjectTypeHeaderStart.StorageIndexCellMapping:
                    var cell = CellId.Deserialize(body);
                    var cellElement = ExGuid.Deserialize(body);
                    _ = SerialNumber.Deserialize(body);
                    if (!_cellMappings.TryAdd(cell, cellElement)) throw new InvalidDataException("Duplicate cell mapping.");
                    break;
                case StreamObjectTypeHeaderStart.StorageIndexRevisionMapping:
                    var revision = ExGuid.Deserialize(body);
                    var revisionElement = ExGuid.Deserialize(body);
                    _ = SerialNumber.Deserialize(body);
                    if (revision.IsNull || !_revisionMappings.TryAdd(revision, revisionElement))
                        throw new InvalidDataException("Null or duplicate revision mapping.");
                    break;
                default: throw new InvalidDataException($"Unexpected index record {header.Type}.");
            }
            Empty(body);
            VisitReference();
        }
        if (manifest is null || manifest.IsNull) throw new InvalidDataException("Missing storage manifest mapping.");
        var manifestReader = Reader(Require(manifest, DataElementType.StorageManifestDataElementData));
        var schema = Body(manifestReader, StreamObjectTypeHeaderStart.StorageManifestSchemaGUID);
        SchemaGuid = ExGuid.ReadGuid(schema);
        Empty(schema);
        var roots = new HashSet<ExGuid>();
        while (manifestReader.Remaining > 0)
        {
            var body = Body(manifestReader, StreamObjectTypeHeaderStart.StorageManifestRootDeclare);
            var root = ExGuid.Deserialize(body);
            var cell = CellId.Deserialize(body);
            Empty(body);
            if (!roots.Add(root)) throw new InvalidDataException("Duplicate storage root identifier.");
            _roots.Add(new(root, cell));
            VisitReference();
        }
        if (_roots.Count == 0) throw new InvalidDataException("Storage manifest must declare at least one root.");
    }

    private RevisionInfo ReadRevision(ExGuid id)
    {
        if (_revisions.TryGetValue(id, out var cached)) return cached;
        if (!_revisionMappings.TryGetValue(id, out var elementId)) throw new InvalidDataException("Unmapped revision reference.");
        var reader = Reader(Require(elementId, DataElementType.RevisionManifestDataElementData));
        var body = Body(reader, StreamObjectTypeHeaderStart.RevisionManifest);
        var actual = ExGuid.Deserialize(body);
        var baseRevision = ExGuid.Deserialize(body);
        Empty(body);
        if (!actual.Equals(id)) throw new InvalidDataException("Revision mapping does not match its target revision.");
        var roots = new List<RevisionRootReference>();
        var rootIds = new HashSet<ExGuid>();
        var objects = new Dictionary<ObjectKey, ObjectGroupObject>();
        while (reader.Remaining > 0)
        {
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
                    var groupId = ExGuid.Deserialize(body);
                    if (!_groups.TryGetValue(groupId, out var group))
                    {
                        group = ObjectGroupDataElement.ParseOpaque(Require(groupId, DataElementType.ObjectGroupDataElementData), _parsingBudget).Objects;
                        _groups.Add(groupId, group);
                    }
                    foreach (var item in group)
                    {
                        VisitObject();
                        var key = Key(item);
                        if (objects.TryGetValue(key, out var previous) && !Same(previous, item))
                            throw new InvalidDataException("Conflicting object-partition definitions in one revision.");
                        objects[key] = item;
                        if (item.Declaration.BlobReference is { } blob) ReadBlob(blob);
                    }
                    break;
                default: throw new InvalidDataException($"Unexpected revision record {header.Type}.");
            }
            Empty(body);
            VisitReference();
        }
        var result = new RevisionInfo(actual, baseRevision, roots, objects);
        _revisions.Add(id, result);
        return result;
    }

    private CellInfo ResolveCell(ExGuid current)
    {
        var first = ReadRevision(current);
        var objects = new Dictionary<ObjectKey, ObjectGroupObject>();
        var seen = new HashSet<ExGuid>();
        var id = current;
        while (!id.IsNull)
        {
            if (!seen.Add(id)) throw new InvalidDataException("Revision-base cycle.");
            if (seen.Count > _limits.MaxRevisionDepth) throw new InvalidDataException("Revision depth budget exceeded.");
            var revision = ReadRevision(id);
            foreach (var (key, value) in revision.Objects)
            {
                VisitObject();
                objects.TryAdd(key, value); // Nearest revision wins per partition.
            }
            id = revision.BaseRevision;
        }
        return new(current, first.Roots, objects);
    }

    private void ValidateRevisionChains()
    {
        var depths = new Dictionary<ExGuid, int>();
        foreach (var start in _revisionMappings.Keys)
        {
            var path = new List<ExGuid>();
            var seen = new HashSet<ExGuid>();
            var current = start;
            while (!current.IsNull && !depths.ContainsKey(current))
            {
                VisitReference();
                if (!seen.Add(current)) throw new InvalidDataException("Mapped historical revision-base cycle.");
                if (path.Count >= _limits.MaxRevisionDepth) throw new InvalidDataException("Historical revision depth budget exceeded.");
                path.Add(current);
                current = ReadRevision(current).BaseRevision;
            }
            int depth = current.IsNull ? 0 : depths[current];
            for (int i = path.Count - 1; i >= 0; i--)
            {
                if (++depth > _limits.MaxRevisionDepth) throw new InvalidDataException("Historical revision depth budget exceeded.");
                depths.Add(path[i], depth);
            }
        }
    }

    private void ValidateReferences()
    {
        foreach (var (cell, info) in _cells)
        {
            var ids = info.Objects.Keys.Select(k => (k.Value, k.Guid)).ToHashSet();
            foreach (var root in info.Roots)
            {
                VisitReference();
                if (!ids.Contains((root.Object.Value, root.Object.Guid))) throw new InvalidDataException("Unresolved revision root object.");
            }
            foreach (var obj in info.Objects.Values)
            {
                foreach (var reference in obj.ObjectReferences)
                {
                    VisitReference();
                    if (!ids.Contains((reference.Value, reference.Guid)))
                        throw new InvalidDataException("Unresolved object reference in its cell/revision scope.");
                }
                foreach (var reference in obj.CellReferences)
                {
                    VisitReference();
                    if (!_cells.ContainsKey(reference)) throw new InvalidDataException("Unresolved object cell reference.");
                }
            }
            // Opaque object/cell cycles are permitted. Validation visits each
            // stored edge once rather than recursively expanding references.
        }
    }

    private void ReadBlob(ExGuid id)
    {
        if (_blobs.ContainsKey(id)) return;
        var reader = Reader(Require(id, DataElementType.ObjectDataBLOBDataElementData));
        var body = Body(reader, StreamObjectTypeHeaderStart.ObjectDataBLOB);
        _blobs.Add(id, body.ReadBytes(body.Remaining));
        Empty(reader);
    }

    private DataElement Require(ExGuid id, DataElementType type)
    {
        if (!_elements.TryGetValue(id, out var element) || element.DataElementType != type)
            throw new InvalidDataException($"Missing or wrong-kind graph target {id}, expected {type}.");
        _required.Add(id);
        return element;
    }

    private void VisitObject()
    {
        if (++_objectVisits > _limits.MaxObjectVisits) throw new InvalidDataException("Object traversal budget exceeded.");
    }
    private void VisitReference()
    {
        if (++_referenceVisits > _limits.MaxReferenceVisits) throw new InvalidDataException("Reference traversal budget exceeded.");
    }
    private static BinaryReaderEx Reader(DataElement element) => new(element.Data ?? throw new InvalidDataException("Missing graph payload."));
    private static BinaryReaderEx Body(BinaryReaderEx reader, StreamObjectTypeHeaderStart type)
    {
        var header = StreamObjectHeaderStart.Parse(reader);
        if (header.Type != type) throw new InvalidDataException($"Expected {type}, got {header.Type}.");
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
    private static ObjectKey Key(ObjectGroupObject obj) => new(obj.ObjectGuid.Value, obj.ObjectGuid.Guid, obj.Declaration.PartitionId);
    private static bool Same(ObjectGroupObject a, ObjectGroupObject b) => a.Declaration == b.Declaration &&
        a.ObjectReferences.SequenceEqual(b.ObjectReferences) && a.CellReferences.SequenceEqual(b.CellReferences) && a.Content.SequenceEqual(b.Content);
    private ObjectGroupObject Detach(ObjectGroupObject obj) => obj with
    {
        Declaration = obj.Declaration with { ObjectGuid = Copy(obj.ObjectGuid),
            BlobReference = obj.Declaration.BlobReference is { } blob ? Copy(blob) : null },
        ObjectReferences = obj.ObjectReferences.Select(Copy).ToArray(),
        CellReferences = obj.CellReferences.Select(Copy).ToArray(),
        Content = (obj.Declaration.BlobReference is { } id ? _blobs[id] : obj.Content).ToArray(),
    };
    private static ExGuid Copy(ExGuid id) => new(id.Value, id.Guid);
    private static CellId Copy(CellId id) => new(Copy(id.LongId), Copy(id.ShortId));
    private static DataElement Copy(DataElement element) => new(element.DataElementType, Copy(element.DataElementExtendedGuid),
        new SerialNumber(element.SerialNumber.Guid, element.SerialNumber.Value)) { Data = element.Data?.ToArray() };
    private readonly record struct ObjectKey(uint Value, Guid Guid, ulong Partition);
    private sealed record RevisionInfo(ExGuid Revision, ExGuid BaseRevision, IReadOnlyList<RevisionRootReference> Roots,
        Dictionary<ObjectKey, ObjectGroupObject> Objects);
    private sealed record CellInfo(ExGuid Revision, IReadOnlyList<RevisionRootReference> Roots,
        Dictionary<ObjectKey, ObjectGroupObject> Objects);
}
