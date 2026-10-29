using CellBridge.FssHttpB;

internal static class BenchmarkClientGraph
{
    internal static DataElement CurrentIndex(PartitionGraphSnapshot graph) =>
        graph.StorageIndexes.Single(element => element.DataElementExtendedGuid.Equals(graph.StorageIndex));

    // PutChanges returns a mapping delta. The client retains untouched keys and uses the
    // server's applied token, never the transient proposed request index, as its next root.
    internal static PartitionGraphSnapshot ApplyAcceptedGraph(PartitionGraphSnapshot current,
        FsshttpbCellRequest request, FsshttpbResponse response, ExGuid accepted)
    {
        var envelope = response.DataElementPackage?.DataElements.SingleOrDefault(element =>
            element.DataElementType == DataElementType.StorageIndexDataElementData && element.DataElementExtendedGuid.Equals(accepted))
            ?? throw new InvalidDataException("Save omitted its applied storage index envelope.");
        var mappings = StorageIndexMappingSerials.ReadMappings(CurrentIndex(current).Data!).ToDictionary(Key);
        foreach (var mapping in StorageIndexMappingSerials.ReadMappings(envelope.Data!))
        {
            if (mapping.Target.IsNull) mappings.Remove(Key(mapping));
            else mappings[Key(mapping)] = mapping;
        }
        var writer = new BinaryWriterEx();
        foreach (var mapping in mappings.Values)
        {
            var body = new BinaryWriterEx();
            mapping.Cell?.Serialize(body);
            mapping.Revision?.Serialize(body);
            mapping.Target.Serialize(body);
            mapping.Serial.Serialize(body);
            var bytes = body.ToArray();
            new StreamObjectHeaderStart16Bit(mapping.Type, bytes.Length).Serialize(writer);
            writer.WriteBytes(bytes);
        }
        var index = new DataElement(DataElementType.StorageIndexDataElementData, accepted, envelope.SerialNumber) { Data = writer.ToArray() };
        var assignments = ReadAssignments(response);
        var admitted = request.DataElementPackage!.DataElements.Where(element => element.DataElementType != DataElementType.StorageIndexDataElementData)
            .Select(element => new DataElement(element.DataElementType,element.DataElementExtendedGuid,
                assignments.GetValueOrDefault(element.DataElementExtendedGuid) ?? throw new InvalidDataException("Save omitted an accepted serial assignment."))
                { Data=element.Data });
        return current.Merge(admitted.Append(index),accepted);

        static (StreamObjectTypeHeaderStart, ExGuid?, ExGuid?, ExGuid?) Key(StorageIndexMapping mapping) =>
            (mapping.Type, mapping.Revision, mapping.Cell?.LongId, mapping.Cell?.ShortId);
    }
    internal static PartitionGraphSnapshot ApplyQueryGraph(PartitionGraphSnapshot? current,FsshttpbResponse response,ExGuid selected)
    {
        var elements = response.DataElementPackage?.DataElements ?? [];
        if (current is null) return PartitionGraphSnapshot.Create(elements,selected);
        var index = elements.SingleOrDefault(element => element.DataElementType == DataElementType.StorageIndexDataElementData && element.DataElementExtendedGuid.Equals(selected));
        if (index is null)
        {
            if (!current.StorageIndex.Equals(selected)) throw new InvalidDataException("Query changed its index without supplying the graph.");
            return current.Merge(elements,selected);
        }
        if (current.StorageIndex.Equals(selected))
        {
            if (!CurrentIndex(current).SerialNumber.Equals(index.SerialNumber))
                throw new InvalidDataException("A query changed the serial under the same logical index.");
            var previous = StorageIndexPatch.Read(CurrentIndex(current)).ToDictionary(MappingKey);
            foreach (var mapping in StorageIndexPatch.Read(index))
                if (previous.TryGetValue(MappingKey(mapping),out var known) &&
                    (!known.Target.Equals(mapping.Target) || !known.Serial.Equals(mapping.Serial)))
                    throw new InvalidDataException("A query changed mappings under the same logical index.");
        }
        // The full query may serialize the same logical mapping set in a different order.
        var merged = current.Merge(elements.Where(element => element.DataElementType != DataElementType.StorageIndexDataElementData),current.StorageIndex);
        return PartitionGraphSnapshot.Create(merged.Elements.Where(element => !element.DataElementExtendedGuid.Equals(selected)).Append(index),selected);
    }

    private static (StreamObjectTypeHeaderStart,ExGuid?,ExGuid?,ExGuid?) MappingKey(StorageIndexMapping mapping) =>
        (mapping.Type,mapping.Revision,mapping.Cell?.LongId,mapping.Cell?.ShortId);

    private static Dictionary<ExGuid,SerialNumber> ReadAssignments(FsshttpbResponse response)
    {
        var result = new Dictionary<ExGuid,SerialNumber>();
        var bytes = response.SubResponses.Select(sub => sub.Data).OfType<PutChangesSubResponseData>().SingleOrDefault()?.SerialNumberReassignAllBytes;
        if (bytes is null) return result;
        var reader = new BinaryReaderEx(bytes);
        while (reader.Remaining > 0)
        {
            var header = StreamObjectHeaderStart.Parse(reader);
            var body = new BinaryReaderEx(reader.ReadMemory(header.Length));
            if (header.Type == StreamObjectTypeHeaderStart.PutChangesResponseSerialNumberReassignAll)
            {
                if (!SerialNumber.Deserialize(body).IsNull) throw new InvalidDataException("Unexpected shared serial assignment.");
            }
            else if (header.Type == StreamObjectTypeHeaderStart.PutChangesResponseSerialNumberReassign)
            {
                var id = ExGuid.Deserialize(body);
                if (!result.TryAdd(id,SerialNumber.Deserialize(body))) throw new InvalidDataException("Duplicate accepted serial assignment.");
            }
            else throw new InvalidDataException("Unknown serial assignment object.");
            if (body.Remaining != 0) throw new InvalidDataException("Trailing serial assignment bytes.");
        }
        return result;
    }

}
