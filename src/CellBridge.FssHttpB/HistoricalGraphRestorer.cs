namespace CellBridge.FssHttpB;

public sealed record HistoricalGraphPublication(GenericPartitionGraphSnapshot Graph, ulong Knowledge);

/// <summary>Publishes historical cells through fresh immutable wrappers without changing their objects or ancestors.</summary>
public static class HistoricalGraphRestorer
{
    public static HistoricalGraphPublication Rebase(GenericPartitionGraphSnapshot current,
        GenericPartitionGraphSnapshot selected, Guid serialNamespace, ulong knowledge, GenericGraphLimits? limits = null)
    {
        var elements = current.Elements.ToDictionary(e => e.DataElementExtendedGuid);
        foreach (var element in selected.Elements)
        {
            if (elements.TryGetValue(element.DataElementExtendedGuid, out var prior))
            {
                if (prior.DataElementType != element.DataElementType || !prior.SerialNumber.Equals(element.SerialNumber) ||
                    !(prior.Data ?? []).SequenceEqual(element.Data ?? []))
                    throw new InvalidDataException("Conflicting immutable historical data-element identity.");
            }
            else elements.Add(element.DataElementExtendedGuid, element);
        }
        var selectedIndex = selected.Elements.Single(e => e.DataElementExtendedGuid.Equals(selected.StorageIndex));
        var selectedMappings = StorageIndexMappingSerials.ReadMappings(selectedIndex.Data ?? []);
        var revisions = new Dictionary<ExGuid, StorageIndexMapping>();
        foreach (var index in elements.Values.Where(e => e.DataElementType == DataElementType.StorageIndexDataElementData))
            foreach (var mapping in StorageIndexMappingSerials.ReadMappings(index.Data ?? []))
            {
                if (mapping.Serial.Guid == serialNamespace) knowledge = Math.Max(knowledge, mapping.Serial.Value);
                if (mapping.Revision is { } revision)
                {
                    if (revisions.TryGetValue(revision, out var prior) && !prior.Target.Equals(mapping.Target))
                        throw new InvalidDataException("Conflicting immutable revision mapping.");
                    revisions[revision] = mapping;
                }
            }
        foreach (var element in elements.Values)
            if (element.SerialNumber.Guid == serialNamespace) knowledge = Math.Max(knowledge, element.SerialNumber.Value);

        SerialNumber NextSerial() => new(serialNamespace, checked(++knowledge));
        ExGuid NextId() => new(1, Guid.NewGuid());
        var mappings = new List<StorageIndexMapping>();
        var manifest = selectedMappings.SingleOrDefault(m => m.Type == StreamObjectTypeHeaderStart.StorageIndexManifestMapping);
        if (manifest is not null) mappings.Add(manifest);
        foreach (var cell in selected.Cells)
        {
            var revision = NextId();
            var revisionElement = NextId();
            var serial = NextSerial();
            var body = new BinaryWriterEx();
            var record = new BinaryWriterEx();
            revision.Serialize(record); selected.GetCurrentRevision(cell).Serialize(record);
            Write(body, StreamObjectTypeHeaderStart.RevisionManifest, record);
            foreach (var root in selected.GetRevisionRoots(cell))
            {
                record = new(); root.Root.Serialize(record); root.Object.Serialize(record);
                Write(body, StreamObjectTypeHeaderStart.RevisionManifestRootDeclare, record);
            }
            elements.Add(revisionElement, new(DataElementType.RevisionManifestDataElementData, revisionElement, serial) { Data = body.ToArray() });
            revisions.Add(revision, new(StreamObjectTypeHeaderStart.StorageIndexRevisionMapping, revision, null, revisionElement, NextSerial()));
            var cellManifest = StorageManifestBuilder.BuildCellManifestDataElement(NextId(), NextSerial(), revision);
            elements.Add(cellManifest.DataElementExtendedGuid, cellManifest);
            mappings.Add(new(StreamObjectTypeHeaderStart.StorageIndexCellMapping, null, cell, cellManifest.DataElementExtendedGuid, NextSerial()));
        }
        // An empty index selects no graph. Retained elements remain immutable,
        // but selecting old revision mappings would fabricate a nonempty graph.
        if (manifest is not null) mappings.AddRange(revisions.Values);
        var payload = new BinaryWriterEx();
        foreach (var mapping in mappings)
        {
            var body = new BinaryWriterEx();
            mapping.Cell?.Serialize(body); mapping.Revision?.Serialize(body);
            mapping.Target.Serialize(body); mapping.Serial.Serialize(body);
            Write(payload, mapping.Type, body);
        }
        var indexId = NextId();
        elements.Add(indexId, new(DataElementType.StorageIndexDataElementData, indexId, NextSerial()) { Data = payload.ToArray() });
        return new(GenericPartitionGraphSnapshot.Create(elements.Values, indexId, limits), knowledge);
    }

    private static void Write(BinaryWriterEx writer, StreamObjectTypeHeaderStart type, BinaryWriterEx body)
    {
        new StreamObjectHeaderStart32Bit(type, body.Length).Serialize(writer);
        writer.WriteBytes(body.ToArray());
    }
}
