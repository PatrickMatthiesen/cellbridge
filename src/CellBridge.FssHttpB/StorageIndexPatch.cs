namespace CellBridge.FssHttpB;

/// <summary>Checks and merges wire mapping updates while preserving keys absent from the update.</summary>
public static class StorageIndexPatch
{
    private static (StreamObjectTypeHeaderStart, ExGuid?, CellId?) Key(StorageIndexMapping mapping)
        => (mapping.Type, mapping.Revision, mapping.Cell);

    public static IReadOnlyList<StorageIndexMapping> Read(DataElement index)
    {
        if (index.DataElementType != DataElementType.StorageIndexDataElementData)
            throw new InvalidDataException("Expected a storage-index data element.");
        var mappings = StorageIndexMappingSerials.ReadMappings(index.Data ?? throw new InvalidDataException("Missing index payload."));
        var keys = new HashSet<(StreamObjectTypeHeaderStart, ExGuid?, CellId?)>();
        foreach (var mapping in mappings)
            if (!keys.Add(Key(mapping)) || mapping.Revision?.IsNull == true)
                throw new InvalidDataException("Duplicate or invalid storage-index mapping key.");
        return mappings;
    }

    public static bool IsCoherent(IReadOnlyList<StorageIndexMapping> current,
        IReadOnlyList<StorageIndexMapping> updates, IReadOnlyList<StorageIndexMapping> expected, bool implyNullExpected)
    {
        var actual = current.ToDictionary(Key);
        var anticipated = expected.ToDictionary(Key);
        foreach (var update in updates)
        {
            actual.TryGetValue(Key(update), out var value);
            if (anticipated.TryGetValue(Key(update), out var expectation))
            {
                if (expectation.Target.IsNull ? value is not null && !value.Target.IsNull
                    : value is null || !value.Target.Equals(expectation.Target)) return false;
            }
            else if (implyNullExpected && value is not null && !value.Target.IsNull) return false;
        }
        return true;
    }

    public static byte[] Merge(IReadOnlyList<StorageIndexMapping> current, IReadOnlyList<StorageIndexMapping> updates)
    {
        var merged = current.ToDictionary(Key);
        foreach (var update in updates) merged[Key(update)] = update;
        return Serialize(merged.Values);
    }

    public static byte[] Serialize(IEnumerable<StorageIndexMapping> mappings)
    {
        var writer = new BinaryWriterEx();
        foreach (var mapping in mappings)
        {
            var body = new BinaryWriterEx();
            mapping.Cell?.Serialize(body);
            mapping.Revision?.Serialize(body);
            mapping.Target.Serialize(body);
            mapping.Serial.Serialize(body);
            new StreamObjectHeaderStart32Bit(mapping.Type, body.Length).Serialize(writer);
            writer.WriteBytes(body.ToArray());
        }
        return writer.ToArray();
    }
}
