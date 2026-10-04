namespace CellBridge.FssHttpB;

/// <summary>
/// An immutable view of one FSSHTTPB partition data-element graph.
/// It follows the manifest and storage-index mappings before materializing the
/// object graph selected by the revision manifest.
/// </summary>
public sealed partial class PartitionGraphSnapshot
{
    private readonly Dictionary<ExGuid, DataElement> _elements;
    private readonly ExGuid _storageIndex;
    private readonly ExGuid? _rootObject;
    private readonly ExGuid? _revision;
    private readonly ExGuid[] _objectGroups;

    private PartitionGraphSnapshot(
        Dictionary<ExGuid, DataElement> elements,
        ExGuid storageIndex,
        ExGuid? rootObject,
        ExGuid? revision, IEnumerable<ExGuid> objectGroups)
    {
        _elements = elements;
        _storageIndex = Clone(storageIndex);
        _rootObject = rootObject is null ? null : Clone(rootObject);
        _revision = revision is null ? null : Clone(revision);
        _objectGroups = objectGroups.Select(Clone).ToArray();
    }

    /// <summary>The storage-index data-element identifier selected by this snapshot.</summary>
    public ExGuid StorageIndex => Clone(_storageIndex);

    /// <summary>The retained data elements, keyed by extended GUID.</summary>
    public IReadOnlyCollection<DataElement> Elements => _elements.Values.Select(Clone).ToArray();

    public int ElementCount => _elements.Count;
    public long PayloadBytes => _elements.Values.Sum(e => (long)(e.Data?.Length ?? 0));
    public IReadOnlyDictionary<ExGuid, IReadOnlyList<SerialNumber>> MappingSerials => _elements.Values
        .Where(e => e.DataElementType == DataElementType.StorageIndexDataElementData)
        .ToDictionary(e => Clone(e.DataElementExtendedGuid), e => StorageIndexMappingSerials.Read(e.Data ?? []));

    /// <summary>Rejects serial reuse for a different mapping while permitting exact mapping repetition.</summary>
    public void ValidateMappingSerials(IEnumerable<StorageIndexMapping> incoming)
    {
        var meanings = new Dictionary<SerialNumber, StorageIndexMapping>();
        foreach (var mapping in incoming.Where(m => !m.Serial.IsNull))
        {
            if (meanings.TryGetValue(mapping.Serial, out var prior) && !prior.Equals(mapping))
                throw new InvalidDataException("A storage-index mapping serial was reused for a different mapping.");
            meanings[mapping.Serial] = mapping;
        }
        // Inspect private immutable buffers directly, without copying index history.
        foreach (var element in _elements.Values.Where(e => e.DataElementType == DataElementType.StorageIndexDataElementData))
            foreach (var mapping in StorageIndexMappingSerials.ReadMappings(element.Data ?? []))
                if (!mapping.Serial.IsNull && meanings.TryGetValue(mapping.Serial, out var proposed) && !proposed.Equals(mapping))
                    throw new InvalidDataException("A storage-index mapping serial was reused for a different mapping.");
    }

    /// <summary>Copies only selected payloads, leaving retained history untouched.</summary>
    public IReadOnlyCollection<DataElement> SelectElements(Func<DataElement, bool> select) =>
        _elements.Values.Where(e => select(new DataElement(e.DataElementType,
            Clone(e.DataElementExtendedGuid), new SerialNumber(e.SerialNumber.Guid, e.SerialNumber.Value))))
        .Select(Clone).ToArray();

    /// <summary>Detached identifiers and serials, without copying retained payload bytes.</summary>
    public IReadOnlyCollection<DataElement> ElementMetadata => _elements.Values.Select(e =>
        new DataElement(e.DataElementType, Clone(e.DataElementExtendedGuid),
            new SerialNumber(e.SerialNumber.Guid, e.SerialNumber.Value))).ToArray();

    /// <summary>Detached storage indexes used for coherency checks.</summary>
    public IReadOnlyCollection<DataElement> StorageIndexes => _elements.Values
        .Where(e => e.DataElementType == DataElementType.StorageIndexDataElementData).Select(Clone).ToArray();

    /// <summary>Compares an existing immutable payload without exposing or copying it.</summary>
    public bool ConflictsWith(DataElement element) => _elements.TryGetValue(element.DataElementExtendedGuid, out var prior) &&
        (prior.DataElementType != element.DataElementType || !(prior.Data ?? []).SequenceEqual(element.Data ?? []));

    /// <summary>Stages retained immutable payloads directly, with detached identifiers.</summary>
    public async ValueTask VisitElementsAsync(Func<DataElement, Stream, ValueTask> visit)
    {
        foreach (var element in _elements.Values)
        {
            var metadata = new DataElement(element.DataElementType, Clone(element.DataElementExtendedGuid),
                new SerialNumber(element.SerialNumber.Guid, element.SerialNumber.Value));
            // MemoryStream does not expose this private buffer, and is read-only.
            using var payload = new MemoryStream(element.Data ?? [], writable: false);
            await visit(metadata, payload);
        }
    }

    /// <summary>The object selected by the revision manifest root declare.</summary>
    public ExGuid? RootObject => _rootObject is null ? null : Clone(_rootObject);

    /// <summary>The revision selected by the cell manifest.</summary>
    public ExGuid? Revision => _revision is null ? null : Clone(_revision);

    /// <summary>
    /// Creates and validates a snapshot from a complete data-element graph.
    /// </summary>
    public static PartitionGraphSnapshot Create(
        IEnumerable<DataElement> elements,
        ExGuid storageIndex)
    {
        ArgumentNullException.ThrowIfNull(elements);
        ArgumentNullException.ThrowIfNull(storageIndex);
        return Build(CloneElements(elements), storageIndex);
    }

    /// <summary>
    /// Applies a data-element delta and returns a new validated snapshot.
    /// Existing elements are retained unless the delta supplies the same
    /// extended GUID. The previous snapshot is never mutated.
    /// </summary>
    public PartitionGraphSnapshot Merge(
        IEnumerable<DataElement> delta,
        ExGuid storageIndex)
    {
        ArgumentNullException.ThrowIfNull(delta);
        ArgumentNullException.ThrowIfNull(storageIndex);
        // These elements belong to immutable snapshots; share their private
        // buffers between revisions and clone only incoming mutable elements.
        var merged = new Dictionary<ExGuid, DataElement>(_elements);
        foreach (var element in CloneElements(delta).Values)
        {
            ArgumentNullException.ThrowIfNull(element);
            AddElement(merged, element, copyPayload: false);
        }

        return Build(merged, storageIndex);
    }

    /// <summary>Materializes the object graph selected by the manifest chain.</summary>
    public byte[] Materialize(long maxBytes = int.MaxValue)
        => SelectedObjectGraph().Materialize(_rootObject!, maxBytes);

    /// <summary>Validates and writes the selected file graph, leaving the destination open.</summary>
    public long MaterializeTo(Stream destination, long maxBytes = int.MaxValue)
        => SelectedObjectGraph().MaterializeTo(destination, _rootObject!, maxBytes);

    /// <summary>Validates and asynchronously writes the selected file graph, leaving the destination open.</summary>
    public ValueTask<long> MaterializeToAsync(Stream destination, long maxBytes = int.MaxValue,
        CancellationToken cancellationToken = default)
        => SelectedObjectGraph().MaterializeToAsync(destination, _rootObject!, maxBytes, cancellationToken);

    private ObjectGroupGraph SelectedObjectGraph()
    {
        if (_rootObject is null)
            throw new InvalidDataException("The partition has no revision-manifest root object.");

        // Historical groups remain available for delta saves, but only the
        // selected revision's groups define the objects of this revision.
        return ObjectGroupGraph.FromDataElements(
            _objectGroups.Select(id => _elements[id]));
    }

    /// <summary>Checks the expected values for the keys updated by a Put Changes request.</summary>
    public bool MatchesPutChanges(IEnumerable<DataElement> elements, ExGuid proposedIndex,
        ExGuid expectedIndex, bool implyNullExpected)
    {
        var candidates = CloneElements(elements.Where(e => e.DataElementType == DataElementType.StorageIndexDataElementData));
        var proposed = ParseStorageIndex(RequireElement(candidates, proposedIndex,
            DataElementType.StorageIndexDataElementData, "proposed storage index"));
        var expected = expectedIndex.IsNull ? new StorageIndexInfo(null, [], []) :
            ParseStorageIndex(RequireElement(candidates, expectedIndex,
                DataElementType.StorageIndexDataElementData, "expected storage index"));
        var current = ParseStorageIndex(_elements[StorageIndex]);
        bool Matches(StorageMapping? actual, StorageMapping? anticipated)
        {
            if (anticipated is null)
                return !implyNullExpected || actual is null || actual.Guid.IsNull;
            if (anticipated.Guid.IsNull)
                return actual is null || actual.Guid.IsNull;
            // Serial numbers describe the sender's knowledge, not the mapped value.
            return actual is not null && actual.Guid.Equals(anticipated.Guid);
        }
        if (proposed.ManifestMapping is not null && !Matches(current.ManifestMapping, expected.ManifestMapping))
            return false;
        foreach (var update in proposed.CellMappings)
            if (!Matches(current.CellMappings.SingleOrDefault(x => x.CellId.Equals(update.CellId))?.Mapping,
                expected.CellMappings.SingleOrDefault(x => x.CellId.Equals(update.CellId))?.Mapping))
                return false;
        foreach (var update in proposed.RevisionMappings)
            if (!Matches(current.RevisionMappings.SingleOrDefault(x => x.Revision.Equals(update.Revision))?.Mapping,
                expected.RevisionMappings.SingleOrDefault(x => x.Revision.Equals(update.Revision))?.Mapping))
                return false;
        return true;
    }

    private static PartitionGraphSnapshot Build(
        Dictionary<ExGuid, DataElement> elements,
        ExGuid storageIndex)
    {
        if (!elements.TryGetValue(storageIndex, out var indexElement) ||
            indexElement.DataElementType != DataElementType.StorageIndexDataElementData)
        {
            throw new InvalidDataException($"Storage index {storageIndex} is not present in the data-element graph.");
        }

        var index = ParseStorageIndex(indexElement);
        if (index.ManifestMapping is null)
            throw new InvalidDataException($"Storage index {storageIndex} has no manifest mapping.");
        if (index.CellMappings.Count == 0)
            throw new InvalidDataException($"Storage index {storageIndex} has no cell mapping.");
        if (index.RevisionMappings.Count == 0)
            throw new InvalidDataException($"Storage index {storageIndex} has no revision mapping.");

        var manifestElement = RequireElement(elements, index.ManifestMapping.Guid,
            DataElementType.StorageManifestDataElementData, "storage manifest");
        var manifest = ParseStorageManifest(manifestElement);
        var cellMapping = index.CellMappings.SingleOrDefault(x => x.CellId.Equals(manifest.CellId));
        if (cellMapping is null)
            throw new InvalidDataException("The storage-index cell mappings do not contain the manifest root CellID.");

        var cellElement = RequireElement(elements, cellMapping.Mapping.Guid,
            DataElementType.CellManifestDataElementData, "cell manifest");
        var currentRevision = ParseCellManifest(cellElement);
        var revisionMapping = index.RevisionMappings.SingleOrDefault(
            x => x.Revision.Equals(currentRevision));
        if (revisionMapping is null)
            throw new InvalidDataException("The storage-index revision mappings do not contain the cell's current revision.");

        var revisionElement = RequireElement(elements, revisionMapping.Mapping.Guid,
            DataElementType.RevisionManifestDataElementData, "revision manifest");
        var revision = ParseRevisionManifest(revisionElement);
        if (!revision.Revision.Equals(currentRevision))
            throw new InvalidDataException("Revision mapping does not match its target revision.");

        if (!revision.RootExtendedGuid.Equals(manifest.RootExtendedGuid))
            throw new InvalidDataException("The revision and storage manifests use different root extended GUIDs.");
        foreach (var group in revision.ObjectGroups)
        {
            _ = RequireElement(elements, group,
                DataElementType.ObjectGroupDataElementData, "revision object group");
        }

        return new PartitionGraphSnapshot(elements, storageIndex,
            revision.ObjectGuid, currentRevision, revision.ObjectGroups);
    }

    private static DataElement RequireElement(
        IReadOnlyDictionary<ExGuid, DataElement> elements,
        ExGuid id,
        DataElementType type,
        string description)
    {
        if (!elements.TryGetValue(id, out var element))
            throw new InvalidDataException($"{description} {id} is not present in the data-element graph.");
        if (element.DataElementType != type)
            throw new InvalidDataException($"Data element {id} is {element.DataElementType}, expected {type} for {description}.");
        return element;
    }

    private static StorageIndexInfo ParseStorageIndex(DataElement element)
    {
        var reader = new BinaryReaderEx(element.Data ?? throw new InvalidDataException("Storage index has no payload."));
        StorageMapping? manifest = null;
        var cells = new List<CellMapping>();
        var revisions = new List<RevisionMapping>();
        while (reader.Remaining > 0)
        {
            var header = StreamObjectHeaderStart.Parse(reader);
            var body = ReadBody(reader, header, "storage-index mapping");
            var bodyReader = new BinaryReaderEx(body);
            switch (header.Type)
            {
                case StreamObjectTypeHeaderStart.StorageIndexManifestMapping:
                    if (manifest is not null)
                        throw new InvalidDataException("A storage index contains duplicate manifest mappings.");
                    manifest = new StorageMapping(
                        ExGuid.Deserialize(bodyReader),
                        SerialNumber.Deserialize(bodyReader));
                    RequireEmpty(bodyReader, "storage-index manifest mapping");
                    break;
                case StreamObjectTypeHeaderStart.StorageIndexCellMapping:
                    cells.Add(new CellMapping(
                        CellId.Deserialize(bodyReader),
                        new StorageMapping(ExGuid.Deserialize(bodyReader), SerialNumber.Deserialize(bodyReader))));
                    RequireEmpty(bodyReader, "storage-index cell mapping");
                    break;
                case StreamObjectTypeHeaderStart.StorageIndexRevisionMapping:
                    revisions.Add(new RevisionMapping(
                        ExGuid.Deserialize(bodyReader),
                        new StorageMapping(ExGuid.Deserialize(bodyReader), SerialNumber.Deserialize(bodyReader))));
                    RequireEmpty(bodyReader, "storage-index revision mapping");
                    break;
                default:
                    throw new InvalidDataException($"Unexpected {header.Type} in storage-index data.");
            }
        }

        if (cells.Select(c => c.CellId).Distinct().Count() != cells.Count ||
            revisions.Select(r => r.Revision).Distinct().Count() != revisions.Count)
            throw new InvalidDataException("A storage index contains duplicate cell or revision mappings.");
        return new StorageIndexInfo(manifest, cells, revisions);
    }

    private static StorageManifestInfo ParseStorageManifest(DataElement element)
    {
        var reader = new BinaryReaderEx(element.Data ?? throw new InvalidDataException("Storage manifest has no payload."));
        var schema = StreamObjectHeaderStart.Parse(reader);
        if (schema.Type != StreamObjectTypeHeaderStart.StorageManifestSchemaGUID)
            throw new InvalidDataException($"Expected storage-manifest schema GUID, got {schema.Type}.");
        var schemaBody = new BinaryReaderEx(ReadBody(reader, schema, "storage-manifest schema GUID"));
        var schemaGuid = ExGuid.ReadGuid(schemaBody);
        if (schemaGuid != StorageManifestBuilder.StorageManifestSchemaGuid)
            throw new InvalidDataException($"Unexpected storage-manifest schema GUID {schemaGuid}.");
        RequireEmpty(schemaBody, "storage-manifest schema GUID");

        StorageManifestInfo? result = null;
        while (reader.Remaining > 0)
        {
            var header = StreamObjectHeaderStart.Parse(reader);
            if (header.Type != StreamObjectTypeHeaderStart.StorageManifestRootDeclare)
                throw new InvalidDataException($"Unexpected {header.Type} in storage manifest.");
            var body = new BinaryReaderEx(ReadBody(reader, header, "storage-manifest root"));
            var root = ExGuid.Deserialize(body);
            var cell = CellId.Deserialize(body);
            RequireEmpty(body, "storage-manifest root");
            if (root.Value == 2 && root.Guid == StorageManifestBuilder.RootExtendedGuid)
            {
                if (result is not null)
                    throw new InvalidDataException("Storage manifest contains duplicate fixed roots.");
                result = new StorageManifestInfo(root, cell);
            }
        }

        return result ?? throw new InvalidDataException(
            "Storage manifest does not contain the fixed root ExGuid 2:84DEFAB9-AAA3-4A0D-A3A8-520C77AC7073.");
    }

    private static ExGuid ParseCellManifest(DataElement element)
    {
        var reader = new BinaryReaderEx(element.Data ?? throw new InvalidDataException("Cell manifest has no payload."));
        var header = StreamObjectHeaderStart.Parse(reader);
        if (header.Type != StreamObjectTypeHeaderStart.CellManifestCurrentRevision)
            throw new InvalidDataException($"Expected cell-manifest current revision, got {header.Type}.");
        var body = new BinaryReaderEx(ReadBody(reader, header, "cell-manifest current revision"));
        var revision = ExGuid.Deserialize(body);
        RequireEmpty(body, "cell-manifest current revision");
        RequireEmpty(reader, "cell manifest");
        return revision;
    }

    private static RevisionManifestInfo ParseRevisionManifest(DataElement element)
    {
        var reader = new BinaryReaderEx(element.Data ?? throw new InvalidDataException("Revision manifest has no payload."));
        var manifestHeader = StreamObjectHeaderStart.Parse(reader);
        if (manifestHeader.Type != StreamObjectTypeHeaderStart.RevisionManifest)
            throw new InvalidDataException($"Expected revision manifest, got {manifestHeader.Type}.");
        var manifestBody = new BinaryReaderEx(ReadBody(reader, manifestHeader, "revision manifest"));
        var revision = ExGuid.Deserialize(manifestBody);
        var baseRevision = ExGuid.Deserialize(manifestBody);
        RequireEmpty(manifestBody, "revision manifest");

        RevisionManifestInfo? result = null;
        var groups = new List<ExGuid>();
        while (reader.Remaining > 0)
        {
            var header = StreamObjectHeaderStart.Parse(reader);
            var body = new BinaryReaderEx(ReadBody(reader, header, "revision manifest child"));
            switch (header.Type)
            {
                case StreamObjectTypeHeaderStart.RevisionManifestRootDeclare:
                    var root = ExGuid.Deserialize(body);
                    var objectGuid = ExGuid.Deserialize(body);
                    RequireEmpty(body, "revision manifest root");
                    // SharePoint can include another root for protocol
                    // metadata in the same revision. The file partition is
                    // selected by the fixed root used by the storage
                    // manifest; only that root participates in materialization.
                    if (root.Value == 2 && root.Guid == StorageManifestBuilder.RootExtendedGuid)
                    {
                        if (result is not null)
                            throw new InvalidDataException("Revision manifest contains duplicate fixed roots.");
                        result = new RevisionManifestInfo(revision, baseRevision, root, objectGuid, groups);
                    }
                    break;
                case StreamObjectTypeHeaderStart.RevisionManifestObjectGroupReferences:
                    groups.Add(ExGuid.Deserialize(body));
                    RequireEmpty(body, "revision manifest object-group reference");
                    break;
                default:
                    throw new InvalidDataException($"Unexpected {header.Type} in revision manifest.");
            }
        }

        if (result is null)
            throw new InvalidDataException("Revision manifest has no root declare.");
        return result with { ObjectGroups = groups.ToArray() };
    }

    private static Dictionary<ExGuid, DataElement> CloneElements(IEnumerable<DataElement> elements)
    {
        var result = new Dictionary<ExGuid, DataElement>();
        foreach (var element in elements)
        {
            ArgumentNullException.ThrowIfNull(element);
            AddElement(result, element);
        }
        return result;
    }

    private static void AddElement(Dictionary<ExGuid, DataElement> elements, DataElement element, bool copyPayload = true)
    {
        if (elements.TryGetValue(element.DataElementExtendedGuid, out var previous))
        {
            if (previous.DataElementType != element.DataElementType ||
                !(previous.Data ?? []).SequenceEqual(element.Data ?? []) ||
                (!previous.SerialNumber.IsNull && !element.SerialNumber.IsNull &&
                 !previous.SerialNumber.Equals(element.SerialNumber)))
                throw new InvalidDataException("A data element identifier was reused for different content or serial numbers.");
            if (!previous.SerialNumber.IsNull || element.SerialNumber.IsNull) return;
        }
        var copy = copyPayload ? Clone(element) : element;
        elements[copy.DataElementExtendedGuid] = copy;
    }

    private static DataElement Clone(DataElement element) =>
        new(element.DataElementType, Clone(element.DataElementExtendedGuid),
            new SerialNumber(element.SerialNumber.Guid, element.SerialNumber.Value))
        {
            Data = element.Data?.ToArray(),
        };

    private static ExGuid Clone(ExGuid value) => new(value.Value, value.Guid);

    private static ReadOnlyMemory<byte> ReadBody(BinaryReaderEx reader, StreamObjectHeaderStart header, string description)
    {
        if (header.Length < 0 || header.Length > reader.Remaining)
            throw new InvalidDataException($"{description} header length {header.Length} exceeds the remaining payload.");
        return reader.ReadMemory(header.Length);
    }

    private static void RequireEmpty(BinaryReaderEx reader, string description)
    {
        if (reader.Remaining != 0)
            throw new InvalidDataException($"{description} has {reader.Remaining} trailing bytes.");
    }

    private sealed record StorageIndexInfo(
        StorageMapping? ManifestMapping,
        IReadOnlyList<CellMapping> CellMappings,
        IReadOnlyList<RevisionMapping> RevisionMappings);

    private sealed record StorageMapping(ExGuid Guid, SerialNumber SerialNumber);

    private sealed record CellMapping(CellId CellId, StorageMapping Mapping);

    private sealed record RevisionMapping(ExGuid Revision, StorageMapping Mapping);

    private sealed record StorageManifestInfo(ExGuid RootExtendedGuid, CellId CellId);

    private sealed record RevisionManifestInfo(
        ExGuid Revision,
        ExGuid BaseRevision,
        ExGuid RootExtendedGuid,
        ExGuid ObjectGuid,
        IReadOnlyList<ExGuid> ObjectGroups);

}
