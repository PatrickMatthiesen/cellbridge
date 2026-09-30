namespace CellBridge.FssHttpB;

/// <summary>Builds the typed Knowledge object used by Query Changes and Put Changes.</summary>
public static class BinaryKnowledgeBuilder
{
    private static readonly Guid CellKnowledgeGuid = new("327A35F6-0761-4414-9686-51E900667A4D");
    private static readonly Guid WaterlineKnowledgeGuid = new("3A76E90E-8032-4D0C-B9DD-F3C65029433E");
    private static readonly Guid VersionTokenKnowledgeGuid = new("BF12E2C1-E64F-4959-8282-73B9A24A7C44");

    /// <summary>
    /// Creates a complete Knowledge stream object from data-element serials.
    /// Null serials are omitted; ranges contain serial values, not element counts.
    /// </summary>
    public static byte[] FromElements(IEnumerable<DataElement> elements, ExGuid cellStorageId,
        ulong waterline, byte[]? versionToken = null)
    {
        ArgumentNullException.ThrowIfNull(elements);
        ArgumentNullException.ThrowIfNull(cellStorageId);

        var ranges = elements
            .Where(element => element is not null && !element.SerialNumber.IsNull)
            .GroupBy(element => element.SerialNumber.Guid)
            .Select(group =>
            {
                var values = group.Select(element => element.SerialNumber.Value).Distinct().OrderBy(value => value).ToArray();
                var runs = new List<(Guid Guid, ulong From, ulong To)>();
                ulong from = values[0];
                ulong previous = from;
                foreach (ulong value in values.Skip(1))
                {
                    if (value == 0 || previous == ulong.MaxValue || value - 1 != previous)
                    {
                        runs.Add((group.Key, from, previous));
                        from = value;
                    }
                    previous = value;
                }
                runs.Add((group.Key, from, previous));
                return runs;
            })
            .SelectMany(runs => runs)
            .OrderBy(range => range.Guid)
            .ThenBy(range => range.From)
            .ToArray();

        var knowledge = new BinaryWriterEx();
        new StreamObjectHeaderStart16Bit(StreamObjectTypeHeaderStart.Knowledge, 0).Serialize(knowledge);

        var cell = new BinaryWriterEx();
        ExGuid.WriteGuid(cell, CellKnowledgeGuid);
        new StreamObjectHeaderStart16Bit(StreamObjectTypeHeaderStart.CellKnowledge, 0).Serialize(cell);
        foreach (var range in ranges)
        {
            var payload = new BinaryWriterEx();
            ExGuid.WriteGuid(payload, range.Guid);
            new Compact64bitInt(range.From).Serialize(payload);
            new Compact64bitInt(range.To).Serialize(payload);
            var bytes = payload.ToArray();
            new StreamObjectHeaderStart16Bit(StreamObjectTypeHeaderStart.CellKnowledgeRange, bytes.Length).Serialize(cell);
            cell.WriteBytes(bytes);
        }
        new StreamObjectHeaderEnd8Bit(StreamObjectTypeHeaderEnd.CellKnowledge).Serialize(cell);
        WriteSpecialized(knowledge, cell.ToArray());

        var waterlineData = new BinaryWriterEx();
        ExGuid.WriteGuid(waterlineData, WaterlineKnowledgeGuid);
        new StreamObjectHeaderStart16Bit(StreamObjectTypeHeaderStart.WaterlineKnowledge, 0).Serialize(waterlineData);
        var entry = new BinaryWriterEx();
        cellStorageId.Serialize(entry);
        new Compact64bitInt(waterline).Serialize(entry);
        new Compact64bitInt(0).Serialize(entry);
        var entryBytes = entry.ToArray();
        new StreamObjectHeaderStart16Bit(StreamObjectTypeHeaderStart.WaterlineKnowledgeEntry, entryBytes.Length).Serialize(waterlineData);
        waterlineData.WriteBytes(entryBytes);
        new StreamObjectHeaderEnd8Bit(StreamObjectTypeHeaderEnd.WaterlineKnowledge).Serialize(waterlineData);
        WriteSpecialized(knowledge, waterlineData.ToArray());

        if (versionToken is { Length: > 0 })
        {
            var token = new BinaryWriterEx();
            ExGuid.WriteGuid(token, VersionTokenKnowledgeGuid);
            var tokenData = new BinaryWriterEx();
            new BinaryItem(versionToken).Serialize(tokenData);
            var tokenBytes = tokenData.ToArray();
            new StreamObjectHeaderStart32Bit(StreamObjectTypeHeaderStart.VersionTokenKnowledge, tokenBytes.Length)
                .Serialize(token);
            token.WriteBytes(tokenBytes);
            WriteSpecialized(knowledge, token.ToArray());
        }

        new StreamObjectHeaderEnd8Bit(StreamObjectTypeHeaderEnd.Knowledge).Serialize(knowledge);
        return knowledge.ToArray();
    }

    private static void WriteSpecialized(BinaryWriterEx writer, byte[] payload)
    {
        new StreamObjectHeaderStart32Bit(StreamObjectTypeHeaderStart.SpecializedKnowledge, 16).Serialize(writer);
        writer.WriteBytes(payload);
        new StreamObjectHeaderEnd16Bit(StreamObjectTypeHeaderEnd.SpecializedKnowledge).Serialize(writer);
    }
}
