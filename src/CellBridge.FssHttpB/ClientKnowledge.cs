namespace CellBridge.FssHttpB;

public sealed record SerialRange(Guid Guid, ulong From, ulong To);

/// <summary>Supported client serial knowledge. Waterlines never imply element possession.</summary>
public sealed class ClientKnowledge
{
    private static readonly Guid CellGuid = new("327A35F6-0761-4414-9686-51E900667A4D");
    private static readonly Guid WaterlineGuid = new("3A76E90E-8032-4D0C-B9DD-F3C65029433E");
    private readonly SerialRange[] _ranges;
    private readonly byte[] _wire;
    private readonly Dictionary<Guid, SerialRange[]> _byGuid;
    public bool HasUnsupportedSpecialization { get; }
    public bool RangeLimitExceeded { get; }
    public bool RequiresFullResponse => RangeLimitExceeded || HasUnsupportedSpecialization;
    public IReadOnlyList<SerialRange> Ranges => Array.AsReadOnly(_ranges);

    private ClientKnowledge(IEnumerable<SerialRange> ranges, byte[] wire, bool unsupported, bool rangeLimitExceeded)
    {
        var normalized = new List<SerialRange>();
        foreach (var group in ranges.GroupBy(r => r.Guid).OrderBy(g => g.Key))
        {
            SerialRange? current = null;
            foreach (var range in group.OrderBy(r => r.From))
            {
                if (current is not null && (range.From <= current.To ||
                    (current.To != ulong.MaxValue && range.From == current.To + 1)))
                    current = current with { To = Math.Max(current.To, range.To) };
                else
                {
                    if (current is not null) normalized.Add(current);
                    current = range;
                }
            }
            if (current is not null) normalized.Add(current);
        }
        _ranges = normalized.ToArray();
        _byGuid = _ranges.GroupBy(r => r.Guid).ToDictionary(g => g.Key, g => g.ToArray());
        _wire = wire;
        HasUnsupportedSpecialization = unsupported;
        RangeLimitExceeded = rangeLimitExceeded;
    }

    public bool Contains(SerialNumber serial)
    {
        if (RequiresFullResponse || serial.IsNull || !_byGuid.TryGetValue(serial.Guid, out var ranges)) return false;
        int low = 0, high = ranges.Length - 1;
        while (low <= high)
        {
            int middle = low + (high - low) / 2;
            if (serial.Value < ranges[middle].From) high = middle - 1;
            else if (serial.Value > ranges[middle].To) low = middle + 1;
            else return true;
        }
        return false;
    }

    public void Serialize(BinaryWriterEx writer) => writer.WriteBytes(_wire);

    public static ClientKnowledge FromElements(IEnumerable<DataElement> elements) =>
        Deserialize(new BinaryReaderEx(BinaryKnowledgeBuilder.FromElements(elements, ExGuid.Null, 0)));

    /// <summary>
    /// Validates complete knowledge, retaining at most 10,000 decoded entries.
    /// Larger valid exchanges require a full response, including when a client
    /// echoes knowledge advertised by an admitted graph with many serial ranges.
    /// </summary>
    public static ClientKnowledge Deserialize(BinaryReaderEx reader) =>
        DeserializeCore(reader, 10_000, allowFullResponseFallback: true);

    /// <summary>Validates knowledge with an explicit strict entry limit.</summary>
    public static ClientKnowledge Deserialize(BinaryReaderEx reader, int maxEntries) =>
        DeserializeCore(reader, maxEntries, allowFullResponseFallback: false);

    private static ClientKnowledge DeserializeCore(BinaryReaderEx reader, int maxEntries, bool allowFullResponseFallback)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxEntries);
        int start = reader.Position;
        RequireStart(reader, StreamObjectTypeHeaderStart.Knowledge, 0);
        var ranges = new List<SerialRange>();
        bool unsupported = false;
        bool rangeLimitExceeded = false;
        int entries = 0;
        void CountEntry()
        {
            if (++entries <= maxEntries) return;
            if (!allowFullResponseFallback) throw new InvalidDataException("Knowledge entry limit exceeded.");
            rangeLimitExceeded = true;
            ranges.Clear();
        }
        var specialized = new HashSet<Guid>();
        while (!AtEnd(reader))
        {
            RequireStart(reader, StreamObjectTypeHeaderStart.SpecializedKnowledge, 16);
            var guid = ExGuid.ReadGuid(reader);
            // Range fallback must not remove the bound on duplicate-detection state.
            if (specialized.Count >= maxEntries) throw new InvalidDataException("Knowledge specialization limit exceeded.");
            if (!specialized.Add(guid)) throw new InvalidDataException("Duplicate knowledge specialization.");
            var header = StreamObjectHeaderStart.Parse(reader);
            if (guid == CellGuid)
            {
                if (header.Type != StreamObjectTypeHeaderStart.CellKnowledge || header.Length != 0)
                    throw new InvalidDataException("Invalid cell knowledge header.");
                while (!AtEnd(reader))
                {
                    CountEntry();
                    var entry = StreamObjectHeaderStart.Parse(reader);
                    var body = new BinaryReaderEx(reader.ReadMemory(entry.Length));
                    SerialRange range;
                    if (entry.Type == StreamObjectTypeHeaderStart.CellKnowledgeRange)
                        range = new(ExGuid.ReadGuid(body), Compact64bitInt.Deserialize(body).Value,
                            Compact64bitInt.Deserialize(body).Value);
                    else if (entry.Type == StreamObjectTypeHeaderStart.CellKnowledgeEntry)
                    {
                        var serial = SerialNumber.Deserialize(body);
                        range = new(serial.Guid, serial.Value, serial.Value);
                    }
                    else throw new InvalidDataException("Unsupported cell knowledge entry.");
                    if (body.Remaining != 0 || range.From > range.To)
                        throw new InvalidDataException("Invalid serial knowledge range.");
                    if (!rangeLimitExceeded) ranges.Add(range);
                }
                RequireEnd(reader, StreamObjectTypeHeaderEnd.CellKnowledge);
            }
            else if (guid == WaterlineGuid)
            {
                if (header.Type != StreamObjectTypeHeaderStart.WaterlineKnowledge || header.Length != 0)
                    throw new InvalidDataException("Invalid waterline knowledge header.");
                while (!AtEnd(reader))
                {
                    CountEntry();
                    var entry = StreamObjectHeaderStart.Parse(reader);
                    if (entry.Type != StreamObjectTypeHeaderStart.WaterlineKnowledgeEntry)
                        throw new InvalidDataException("Invalid waterline entry.");
                    var body = new BinaryReaderEx(reader.ReadMemory(entry.Length));
                    _ = ExGuid.Deserialize(body);
                    _ = Compact64bitInt.Deserialize(body);
                    _ = Compact64bitInt.Deserialize(body); // Reserved field must be ignored.
                    if (body.Remaining != 0) throw new InvalidDataException("Trailing waterline entry data.");
                }
                RequireEnd(reader, StreamObjectTypeHeaderEnd.WaterlineKnowledge);
            }
            else
            {
                unsupported = true;
                Skip(reader, header, 0, CountEntry);
            }
            RequireEnd(reader, StreamObjectTypeHeaderEnd.SpecializedKnowledge);
        }
        RequireEnd(reader, StreamObjectTypeHeaderEnd.Knowledge);
        int end = reader.Position;
        reader.Position = start;
        return new(ranges, reader.ReadBytes(end - start), unsupported, rangeLimitExceeded);
    }

    private static void Skip(BinaryReaderEx reader, StreamObjectHeaderStart header, int depth, Action countEntry)
    {
        if (depth > 32) throw new InvalidDataException("Knowledge framing limit exceeded.");
        countEntry();
        reader.Skip(header.Length);
        if (header.Compound == 0) return;
        while (!AtEnd(reader)) Skip(reader, StreamObjectHeaderStart.Parse(reader), depth + 1, countEntry);
        RequireEnd(reader, (StreamObjectTypeHeaderEnd)(int)header.Type);
    }

    private static bool AtEnd(BinaryReaderEx reader)
    {
        int position = reader.Position;
        int kind = reader.ReadByte() & 3;
        reader.Position = position;
        return kind is 1 or 3;
    }

    private static void RequireStart(BinaryReaderEx reader, StreamObjectTypeHeaderStart type, int length)
    {
        var header = StreamObjectHeaderStart.Parse(reader);
        if (header.Type != type || header.Length != length) throw new InvalidDataException($"Invalid {type} knowledge header.");
    }

    private static void RequireEnd(BinaryReaderEx reader, StreamObjectTypeHeaderEnd type)
    {
        if (StreamObjectHeaderEnd.Parse(reader).Type != type) throw new InvalidDataException($"Invalid {type} knowledge end.");
    }
}
