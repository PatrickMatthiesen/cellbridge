namespace OfficeCollabServer.FssHttpB;

/// <summary>
/// An immutable view of one FSSHTTPB partition data-element graph.
/// It follows the manifest and storage-index mappings before materializing the
/// object graph selected by the revision manifest.
/// </summary>
public sealed class PartitionGraphSnapshot
{
    private readonly Dictionary<ExGuid, DataElement> _elements;

    private PartitionGraphSnapshot(
        Dictionary<ExGuid, DataElement> elements,
        ExGuid storageIndex,
        ExGuid? rootObject,
        ExGuid? revision)
    {
        _elements = elements;
        StorageIndex = Clone(storageIndex);
        RootObject = rootObject is null ? null : Clone(rootObject);
        Revision = revision is null ? null : Clone(revision);
    }

    /// <summary>The storage-index data-element identifier selected by this snapshot.</summary>
    public ExGuid StorageIndex { get; }

    /// <summary>The retained data elements, keyed by extended GUID.</summary>
    public IReadOnlyCollection<DataElement> Elements => _elements.Values.ToArray();

    /// <summary>The object selected by the revision manifest root declare.</summary>
    public ExGuid? RootObject { get; }

    /// <summary>The revision selected by the cell manifest.</summary>
    public ExGuid? Revision { get; }

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
        var merged = CloneElements(_elements.Values);
        foreach (var element in delta)
        {
            ArgumentNullException.ThrowIfNull(element);
            merged[element.DataElementExtendedGuid] = Clone(element);
        }

        return Build(merged, storageIndex);
    }

    /// <summary>Materializes the object graph selected by the manifest chain.</summary>
    public byte[] Materialize()
    {
        if (RootObject is null)
            throw new InvalidDataException("The partition has no revision-manifest root object.");

        var objectGraph = ObjectGroupGraph.FromDataElements(_elements.Values);
        return objectGraph.Materialize(RootObject);
    }

    /// <summary>Checks the expected values for the keys updated by a Put Changes request.</summary>
    public bool MatchesPutChanges(IEnumerable<DataElement> elements, ExGuid proposedIndex,
        ExGuid expectedIndex, bool implyNullExpected)
    {
        var candidates = CloneElements(elements);
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
        ValidateMappingSerial(index.ManifestMapping.SerialNumber, manifestElement, "storage manifest");
        var manifest = ParseStorageManifest(manifestElement);
        var cellMapping = index.CellMappings.SingleOrDefault(x => x.CellId.Equals(manifest.CellId));
        if (cellMapping is null)
            throw new InvalidDataException("The storage-index cell mappings do not contain the manifest root CellID.");

        var cellElement = RequireElement(elements, cellMapping.Mapping.Guid,
            DataElementType.CellManifestDataElementData, "cell manifest");
        ValidateMappingSerial(cellMapping.Mapping.SerialNumber, cellElement, "cell manifest");
        var currentRevision = ParseCellManifest(cellElement);
        var revisionMapping = index.RevisionMappings.SingleOrDefault(
            x => x.Revision.Equals(currentRevision));
        if (revisionMapping is null)
            throw new InvalidDataException("The storage-index revision mappings do not contain the cell's current revision.");

        var revisionElement = RequireElement(elements, revisionMapping.Mapping.Guid,
            DataElementType.RevisionManifestDataElementData, "revision manifest");
        ValidateMappingSerial(revisionMapping.Mapping.SerialNumber, revisionElement, "revision manifest");
        var revision = ParseRevisionManifest(revisionElement);

        if (!revision.RootExtendedGuid.Equals(manifest.RootExtendedGuid))
            throw new InvalidDataException("The revision and storage manifests use different root extended GUIDs.");
        foreach (var group in revision.ObjectGroups)
        {
            _ = RequireElement(elements, group,
                DataElementType.ObjectGroupDataElementData, "revision object group");
        }

        return new PartitionGraphSnapshot(elements, storageIndex,
            revision.ObjectGuid, currentRevision);
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

    private static void ValidateMappingSerial(
        SerialNumber mappingSerial,
        DataElement element,
        string description)
    {
        // Captured SharePoint save requests have null outer DataElement serial
        // numbers while their index mappings retain serial values. Generated
        // responses carry both. Validate when both sides are available.
        if (!mappingSerial.IsNull && !element.SerialNumber.IsNull &&
            !mappingSerial.Equals(element.SerialNumber))
        {
            throw new InvalidDataException($"The {description} serial number does not match its storage-index mapping.");
        }
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
        _ = ExGuid.Deserialize(manifestBody); // Base revision is not needed for current materialization.
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
                        result = new RevisionManifestInfo(revision, root, objectGuid, groups);
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
            result[element.DataElementExtendedGuid] = Clone(element);
        }
        return result;
    }

    private static DataElement Clone(DataElement element) =>
        new(element.DataElementType, Clone(element.DataElementExtendedGuid),
            new SerialNumber(element.SerialNumber.Guid, element.SerialNumber.Value))
        {
            Data = element.Data?.ToArray(),
        };

    private static ExGuid Clone(ExGuid value) => new(value.Value, value.Guid);

    private static byte[] ReadBody(BinaryReaderEx reader, StreamObjectHeaderStart header, string description)
    {
        if (header.Length < 0 || header.Length > reader.Remaining)
            throw new InvalidDataException($"{description} header length {header.Length} exceeds the remaining payload.");
        return reader.ReadBytes(header.Length);
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
        ExGuid RootExtendedGuid,
        ExGuid ObjectGuid,
        IReadOnlyList<ExGuid> ObjectGroups);

}
