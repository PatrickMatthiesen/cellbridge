namespace CellBridge.FssHttpB;

public sealed record GraphRetentionAnalysis(int RetainedElements, long RetainedBytes,
    int RequiredElements, long RequiredBytes, IReadOnlyList<string> Blockers)
{
    public bool Complete => Blockers.Count == 0;
}

public sealed partial class PartitionGraphSnapshot
{
    /// <summary>
    /// Conservatively follows every mapping and revision-base link. Unresolved
    /// references block destructive compaction, even if the file materializes.
    /// </summary>
    public GraphRetentionAnalysis AnalyzeRetention(IEnumerable<ExGuid>? protectedIndexes = null)
    {
        var required = TraceReferences(protectedIndexes, out var blockers);
        return new(ElementCount, PayloadBytes, required.Count,
            required.Sum(id => (long)(_elements[id].Data?.Length ?? 0)), blockers.ToArray());
    }

    /// <summary>Pure compaction; hosts must separately establish base/receipt admission policy.</summary>
    public PartitionGraphSnapshot Compact(IEnumerable<ExGuid>? protectedIndexes = null)
    {
        var required = TraceReferences(protectedIndexes, out var blockers);
        if (blockers.Count != 0)
            throw new InvalidDataException("Graph retention cannot be established: " + string.Join("; ", blockers));
        return Build(required.ToDictionary(id => id, id => _elements[id]), _storageIndex);
    }

    private HashSet<ExGuid> TraceReferences(IEnumerable<ExGuid>? protectedIndexes, out List<string> blockers)
    {
        var issues = new List<string>();
        var required = new HashSet<ExGuid>();
        foreach (var root in (protectedIndexes ?? []).Append(_storageIndex))
        {
            try
            {
                var index = ParseStorageIndex(RequireElement(_elements, root,
                    DataElementType.StorageIndexDataElementData, "protected index"));
                var pending = new Queue<ExGuid>();
                var revisions = index.RevisionMappings.ToDictionary(x => x.Revision, x => x.Mapping.Guid);
                foreach (var mapping in index.RevisionMappings)
                    if (!ParseRevisionManifest(RequireElement(_elements, mapping.Mapping.Guid,
                        DataElementType.RevisionManifestDataElementData, "mapped revision")).Revision.Equals(mapping.Revision))
                        throw new InvalidDataException("Revision mapping does not match its target revision.");
                var revisionObjects = new Dictionary<ExGuid, (ExGuid Base, IReadOnlyList<ObjectGroupObject> Objects)>();
                pending.Enqueue(root);
                if (index.ManifestMapping is not null)
                {
                    _ = RequireElement(_elements, index.ManifestMapping.Guid,
                        DataElementType.StorageManifestDataElementData, "mapped storage manifest");
                    pending.Enqueue(index.ManifestMapping.Guid);
                }
                foreach (var cell in index.CellMappings)
                {
                    _ = RequireElement(_elements, cell.Mapping.Guid,
                        DataElementType.CellManifestDataElementData, "mapped cell manifest");
                    pending.Enqueue(cell.Mapping.Guid);
                }
                foreach (var revision in index.RevisionMappings) pending.Enqueue(revision.Mapping.Guid);
                // Track per index, as the same cell/revision may be resolved through different mappings.
                var visited = new HashSet<ExGuid>();
                while (pending.TryDequeue(out var id))
                {
                    if (!visited.Add(id)) continue;
                    if (!_elements.TryGetValue(id, out var element))
                    { issues.Add($"Missing referenced data element {id}."); continue; }
                    required.Add(id);
                    var reader = new BinaryReaderEx(element.Data ?? []);
                    switch (element.DataElementType)
                    {
                        case DataElementType.StorageIndexDataElementData: break;
                        case DataElementType.StorageManifestDataElementData:
                            _ = ParseStorageManifest(element);
                            var schema = StreamObjectHeaderStart.Parse(reader);
                            reader.Skip(schema.Length);
                            while (reader.Remaining > 0)
                            {
                                var header = StreamObjectHeaderStart.Parse(reader);
                                var body = new BinaryReaderEx(ReadBody(reader, header, "manifest root"));
                                _ = ExGuid.Deserialize(body);
                                var cell = CellId.Deserialize(body);
                                var mapping = index.CellMappings.SingleOrDefault(x => x.CellId.Equals(cell));
                                if (mapping is null) issues.Add($"Unresolved root cell {cell}.");
                                else pending.Enqueue(mapping.Mapping.Guid);
                            }
                            break;
                        case DataElementType.CellManifestDataElementData:
                            EnqueueRevision(ParseCellManifest(element));
                            break;
                        case DataElementType.RevisionManifestDataElementData:
                            var start = StreamObjectHeaderStart.Parse(reader);
                            var fixedBody = new BinaryReaderEx(ReadBody(reader, start, "revision"));
                            _ = ExGuid.Deserialize(fixedBody);
                            var baseRevision = ExGuid.Deserialize(fixedBody);
                            if (!baseRevision.IsNull) EnqueueRevision(baseRevision);
                            var manifest = ParseRevisionManifest(element);
                            var declaredObjects = RevisionObjects(manifest.Revision);
                            foreach (var group in manifest.ObjectGroups) pending.Enqueue(group);
                            while (reader.Remaining > 0)
                            {
                                var child = StreamObjectHeaderStart.Parse(reader);
                                var body = new BinaryReaderEx(ReadBody(reader, child, "revision child"));
                                if (child.Type == StreamObjectTypeHeaderStart.RevisionManifestRootDeclare)
                                {
                                    _ = ExGuid.Deserialize(body);
                                    var declaredRoot = ExGuid.Deserialize(body);
                                    if (!declaredObjects.Contains(declaredRoot)) issues.Add($"Unresolved declared root {declaredRoot}.");
                                }
                            }
                            break;
                        case DataElementType.ObjectGroupDataElementData:
                            var objects = ObjectGroupDataElement.Parse(element).Objects;
                            foreach (var item in objects)
                            {
                                foreach (var cell in item.CellReferences)
                                {
                                    var mapping = index.CellMappings.SingleOrDefault(x => x.CellId.Equals(cell));
                                    if (mapping is null) issues.Add($"Unresolved object cell {cell}.");
                                    else pending.Enqueue(mapping.Mapping.Guid);
                                }
                            }
                            break;
                        default: issues.Add($"Unsupported reference-bearing element {id}, type {element.DataElementType}."); break;
                    }
                }

                void EnqueueRevision(ExGuid revision)
                {
                    if (revisions.TryGetValue(revision, out var target)) pending.Enqueue(target);
                    else issues.Add($"Unresolved base/current revision {revision}.");
                }
                HashSet<ExGuid> RevisionObjects(ExGuid revision)
                {
                    var declared = new HashSet<ExGuid>();
                    var chain = new HashSet<ExGuid>();
                    var ownObjects = new List<ObjectGroupObject>();
                    bool current = true;
                    while (!revision.IsNull)
                    {
                        if (!chain.Add(revision))
                        { issues.Add($"Revision base cycle at {revision}."); break; }
                        if (!revisions.TryGetValue(revision, out var target))
                        { issues.Add($"Unresolved base/current revision {revision}."); break; }
                        if (!revisionObjects.TryGetValue(revision, out var info))
                        {
                            var element = RequireElement(_elements, target, DataElementType.RevisionManifestDataElementData, "revision");
                            var manifest = ParseRevisionManifest(element);
                            if (!manifest.Revision.Equals(revision))
                                throw new InvalidDataException("Revision mapping does not match its target revision.");
                            var objects = manifest.ObjectGroups.SelectMany(group => ObjectGroupDataElement.Parse(
                                RequireElement(_elements, group, DataElementType.ObjectGroupDataElementData, "object group")).Objects).ToArray();
                            var reader = new BinaryReaderEx(element.Data!);
                            var header = StreamObjectHeaderStart.Parse(reader);
                            var body = new BinaryReaderEx(ReadBody(reader, header, "revision"));
                            _ = ExGuid.Deserialize(body);
                            info = (ExGuid.Deserialize(body), objects);
                            revisionObjects.Add(revision, info);
                        }
                        foreach (var item in info.Objects) declared.Add(item.ObjectGuid);
                        if (current) ownObjects.AddRange(info.Objects);
                        revision = info.Base;
                        current = false;
                    }
                    foreach (var item in ownObjects)
                        foreach (var reference in item.ObjectReferences)
                            if (!declared.Contains(reference)) issues.Add($"Unresolved object {reference} in revision scope.");
                    return declared;
                }

            }
            catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or ArgumentException)
            { issues.Add(ex.Message); }
        }
        blockers = issues.Distinct().ToList();
        return required;
    }
}
