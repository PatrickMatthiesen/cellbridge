namespace CellBridge.FssHttpB;

/// <summary>Mapping serials describe mappings, independently of their target data-element serials.</summary>
public static class StorageIndexMappingSerials
{
    public static IReadOnlyList<SerialNumber> Read(ReadOnlyMemory<byte> payload) =>
        ReadMappings(payload).Where(m => !m.Serial.IsNull).Select(m => m.Serial).ToArray();

    public static IReadOnlyList<StorageIndexMapping> ReadMappings(ReadOnlyMemory<byte> payload)
    {
        var reader = new BinaryReaderEx(payload);
        var result = new List<StorageIndexMapping>();
        while (reader.Remaining > 0)
        {
            if (result.Count >= 100_000) throw new InvalidDataException("Storage-index mapping count limit exceeded.");
            var header = StreamObjectHeaderStart.Parse(reader);
            var body = new BinaryReaderEx(reader.ReadMemory(header.Length));
            if (header.Compound != 0) throw new InvalidDataException("Compound storage-index mapping.");
            ExGuid? revision = null;
            CellId? cell = null;
            switch (header.Type)
            {
                case StreamObjectTypeHeaderStart.StorageIndexManifestMapping: break;
                case StreamObjectTypeHeaderStart.StorageIndexCellMapping: cell = CellId.Deserialize(body); break;
                case StreamObjectTypeHeaderStart.StorageIndexRevisionMapping: revision = ExGuid.Deserialize(body); break;
                default: throw new InvalidDataException($"Unexpected storage-index mapping {header.Type}.");
            }
            var target = ExGuid.Deserialize(body);
            var serial = SerialNumber.Deserialize(body);
            if (body.Remaining != 0) throw new InvalidDataException("Trailing storage-index mapping bytes.");
            result.Add(new(header.Type, revision, cell, target, serial));
        }
        return result;
    }
}

public sealed record StorageIndexMapping(StreamObjectTypeHeaderStart Type, ExGuid? Revision,
    CellId? Cell, ExGuid Target, SerialNumber Serial);
