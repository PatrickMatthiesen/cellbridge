using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace CellBridge.Storage.Abstractions;

/// <summary>
/// Storage encoding 1 shares immutable graph descriptors within a state. Partition
/// membership is an ordered sequence of table ranges; no graph identity is removed.
/// The public membership view and legacy expanded JSON preserve every descriptor.
/// </summary>
public static partial class DocumentStateJson
{
    private static readonly ConditionalWeakTable<DocumentState, Size> Sizes = new();
    private sealed record Size(long Bytes);
    private sealed record GraphMetadata(int Version, GraphElementState[] Elements);

    // Serialization is synchronous. Reuse type metadata on each worker thread,
    // but clear every operation's graph references in finally, including failures.
    [ThreadStatic] private static CodecContext? _threadContext;
    private static readonly JsonSerializerOptions StrictOptions = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
    };

    private static CodecContext AcquireContext()
    {
        var context = _threadContext ??= new CodecContext();
        if (context.Active) context = new CodecContext();
        context.Active = true;
        return context;
    }

    public static string Serialize(DocumentState state)
    {
        var context = AcquireContext();
        try
        {
            context.PrepareEncoding(state);
            var json = JsonSerializer.Serialize(state, context.WriteOptions);
            Sizes.GetValue(state, _ => new(System.Text.Encoding.UTF8.GetByteCount(json)));
            return json;
        }
        finally { context.Clear(); }
    }

    /// <summary>Counts encoded UTF-8 bytes without allocating the complete JSON buffer.</summary>
    public static long GetSerializedSize(DocumentState state) => Sizes.GetValue(state, value =>
    {
        var context = AcquireContext();
        try
        {
            context.PrepareEncoding(value);
            using var counter = new CountingStream();
            JsonSerializer.Serialize(counter, value, context.WriteOptions);
            return new(counter.Length);
        }
        finally { context.Clear(); }
    }).Bytes;

    /// <summary>Reads encoding 1 or the original expanded state JSON.</summary>
    public static DocumentState Deserialize(string json, bool strict = false, StorageLimits? limits = null)
    {
        CodecContext? context = null;
        try
        {
            limits ??= new StorageLimits();
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new JsonException("Expected a document state.");
            PreflightGraphMemberships(root, limits);
            if (strict) CheckDuplicates(document.RootElement);
            var options = strict ? StrictOptions : JsonSerializerOptions.Default;
            ElementRanges? ranges = null;
            if (document.RootElement.TryGetProperty("GraphMetadata", out var metadata))
            {
                var table = metadata.Deserialize<GraphMetadata>(options) ?? throw new JsonException("Missing graph table.");
                if (table.Version != 1 || table.Elements is null || table.Elements.Any(e => e is null || e.MappingSerials.IsDefault))
                    throw new JsonException("Unsupported or incomplete graph metadata.");
                context = AcquireContext();
                ranges = context.Ranges;
                ranges.Initialize(table.Elements, maxElements: limits.MaxGraphElements);
                options = strict ? context.StrictReadOptions : context.ReadOptions;
            }
            var state = document.RootElement.Deserialize<DocumentState>(options) ?? throw new JsonException("Empty state.");
            if (state.FormatVersion != DocumentState.CurrentFormat || state.Security is null || state.Security.Grants is null)
                throw new JsonException("Unsupported document format or missing security state.");
            if (ranges is not null)
            {
                ranges.Validate(state);
                ranges.CheckAllUsed();
            }
            else
            {
                context = AcquireContext();
                context.PrepareEncoding(state);
            }
            return state;
        }
        catch (JsonException ex) { throw new StorageCorruptionException("Invalid persisted document state.", ex); }
        finally { context?.Clear(); }
    }

    private static void CheckDuplicates(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in node.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new JsonException("Duplicate state property.");
                CheckDuplicates(property.Value);
            }
        }
        else if (node.ValueKind == JsonValueKind.Array)
            foreach (var item in node.EnumerateArray()) CheckDuplicates(item);
    }

    private sealed class CodecContext
    {
        public bool Active { get; set; }
        private GraphMetadata? _metadata;
        public ElementRanges Ranges { get; } = new();
        public JsonSerializerOptions WriteOptions { get; }
        public JsonSerializerOptions ReadOptions { get; }
        public JsonSerializerOptions StrictReadOptions { get; }

        public CodecContext()
        {
            WriteOptions = CreateOptions(write: true);
            ReadOptions = CreateOptions(write: false);
            StrictReadOptions = CreateOptions(write: false, strict: true);
        }

        private JsonSerializerOptions CreateOptions(bool write, bool strict = false)
        {
            var resolver = new DefaultJsonTypeInfoResolver();
            resolver.Modifiers.Add(info =>
            {
                if (info.Type != typeof(DocumentState)) return;
                var property = info.CreateJsonPropertyInfo(write ? typeof(GraphMetadata) : typeof(JsonElement), "GraphMetadata");
                if (write) property.Get = _ => _metadata;
                property.Order = -1;
                info.Properties.Add(property);
            });
            var options = new JsonSerializerOptions
            {
                TypeInfoResolver = resolver,
                UnmappedMemberHandling = strict ? JsonUnmappedMemberHandling.Disallow : JsonUnmappedMemberHandling.Skip,
                RespectRequiredConstructorParameters = strict,
            };
            options.Converters.Add(Ranges);
            return options;
        }

        public void PrepareEncoding(DocumentState state)
        {
            var indices = new Dictionary<GraphElementState, int>(ElementComparer.Instance);
            var elements = new List<GraphElementState>();
            foreach (var element in GraphElementCollection.UniqueElements(AllPartitions(state).Select(p => p.Elements)))
                {
                    if (element is null) throw new StorageCorruptionException("Null immutable graph descriptor.");
                    if (indices.TryAdd(element, elements.Count)) elements.Add(element);
                }
            _metadata = new(1, elements.ToArray());
            Ranges.Initialize(_metadata.Elements, indices);
            Ranges.Validate(state);
        }

        public void Clear()
        {
            _metadata = null;
            Ranges.Clear();
            Active = false;
        }
    }

    private static IEnumerable<PartitionState> AllPartitions(DocumentState state) =>
        state.Partitions.Concat(state.Revisions.SelectMany(r => r.Partitions));

    private readonly record struct TableRange(int Start, int Count);

    private sealed class ElementRanges : JsonConverter<GraphElementCollection>
    {
        private ImmutableArray<GraphElementState> table = [];
        private Dictionary<GraphElementState, int>? indices;
        private readonly Dictionary<GraphElementCollection.Node, TableRange[]> nodeRanges = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<ImmutableArray<GraphElementState>, TableRange[]> tableRuns = new();
        private int[] uniqueWindowStart = [];
        private int[] usage = [];
        private int maxElements;

        public void Initialize(GraphElementState[] elements, Dictionary<GraphElementState, int>? elementIndices = null,
            int maxElements = int.MaxValue)
        {
            table = ImmutableCollectionsMarshal.AsImmutableArray(elements);
            indices = elementIndices;
            this.maxElements = maxElements;
            usage = new int[elements.Length + 1];
            uniqueWindowStart = new int[elements.Length];
            var last = new Dictionary<ExtendedId, int>();
            var window = 0;
            for (var i = 0; i < elements.Length; i++)
            {
                var element = elements[i];
                if (element is null || element.Id is null || element.Serial is null || element.Payload is null ||
                    element.MappingSerials.IsDefault)
                    throw new StorageCorruptionException("Incomplete immutable graph descriptor.");
                if (last.TryGetValue(element.Id, out var previous)) window = Math.Max(window, previous + 1);
                uniqueWindowStart[i] = window;
                last[element.Id] = i;
            }
        }

        public void Clear()
        {
            table = [];
            indices = null;
            usage = [];
            uniqueWindowStart = [];
            nodeRanges.Clear();
            tableRuns.Clear();
        }

        public override GraphElementCollection Read(ref Utf8JsonReader reader, Type typeToConvert,
            JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException("Missing graph ranges.");
            var elements = GraphElementCollection.Empty;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                if (reader.TokenType != JsonTokenType.Number || !reader.TryGetInt32(out var start) ||
                    !reader.Read() || reader.TokenType != JsonTokenType.Number || !reader.TryGetInt32(out var count) ||
                    start < 0 || count <= 0 || start > table.Length - count)
                    throw new JsonException("Invalid graph table range.");
                if (count > table.Length - elements.Length) throw new JsonException("Graph membership exceeds the table.");
                StorageLimits.Check("graph elements", checked((long)elements.Length + count), maxElements);
                if (uniqueWindowStart[start + count - 1] > start) throw new JsonException("Duplicate graph member identity.");
                usage[start] = checked(usage[start] + 1);
                usage[start + count] = checked(usage[start + count] - 1);
                elements = elements.Concat(GraphElementCollection.FromRange(table, start, count));
            }
            if (reader.TokenType != JsonTokenType.EndArray) throw new JsonException("Incomplete graph ranges.");
            return elements;
        }

        public void CheckAllUsed()
        {
            var used = 0;
            for (var i = 0; i < table.Length; i++)
            {
                used = checked(used + usage[i]);
                if (used == 0) throw new JsonException("Unreferenced shared graph descriptor.");
            }
        }

        private TableRange[] GetRanges(GraphElementCollection elements)
        {
            if (elements.Root is null) return [];
            var stack = new Stack<(GraphElementCollection.Node Node, bool Visited)>();
            stack.Push((elements.Root, false));
            while (stack.TryPop(out var item))
            {
                var node = item.Node;
                if (nodeRanges.ContainsKey(node)) continue;
                if (node.Left is not null)
                {
                    if (!item.Visited)
                    {
                        stack.Push((node, true));
                        stack.Push((node.Right!, false));
                        stack.Push((node.Left, false));
                        continue;
                    }
                    var left = nodeRanges[node.Left];
                    var right = nodeRanges[node.Right!];
                    var merged = new List<TableRange>(left.Length + right.Length);
                    foreach (var range in left) AddRange(merged, range);
                    foreach (var range in right) AddRange(merged, range);
                    nodeRanges.Add(node, merged.ToArray());
                }
                else if (node.Table == table)
                    nodeRanges.Add(node, [new(node.Start, node.Length)]);
                else
                {
                    if (!tableRuns.TryGetValue(node.Table, out var runs))
                    {
                        var mapped = new List<TableRange>();
                        for (var i = 0; i < node.Table.Length; i++)
                        {
                            var index = node.Table[i] is { } descriptor && indices!.TryGetValue(descriptor, out var value) ? value : -1;
                            // An unused backing slot can occur in a sliced collection.
                            // Keep it separate so that a referenced slice cannot cross it.
                            if (index < 0) mapped.Add(new(int.MinValue, 1));
                            else AddRange(mapped, new(index, 1));
                        }
                        tableRuns.Add(node.Table, runs = mapped.ToArray());
                    }
                    var ranges = new List<TableRange>();
                    var offset = 0;
                    foreach (var run in runs)
                    {
                        var start = Math.Max(node.Start, offset);
                        var end = Math.Min(node.Start + node.Length, offset + run.Count);
                        if (start < end) AddRange(ranges, new(run.Start + start - offset, end - start));
                        offset += run.Count;
                        if (offset >= node.Start + node.Length) break;
                    }
                    nodeRanges.Add(node, ranges.ToArray());
                }
            }
            return nodeRanges[elements.Root];
        }

        private static void AddRange(List<TableRange> ranges, TableRange range)
        {
            if (ranges.Count > 0 && ranges[^1].Start + ranges[^1].Count == range.Start)
                ranges[^1] = ranges[^1] with { Count = checked(ranges[^1].Count + range.Count) };
            else ranges.Add(range);
        }

        public void Validate(DocumentState state)
        {
            // A single contiguous membership is checked in constant time after
            // preprocessing the table. Arbitrary reordered memberships use a set.
            foreach (var partition in AllPartitions(state))
            {
                if (partition.Elements.IsDefault) throw new StorageCorruptionException("Missing graph membership.");
                var ranges = GetRanges(partition.Elements);
                foreach (var range in ranges)
                    if (uniqueWindowStart[range.Start + range.Count - 1] > range.Start)
                        throw new StorageCorruptionException("Duplicate graph member identity.");
                if (ranges.Length > 1)
                {
                    var ids = new HashSet<ExtendedId>();
                    foreach (var element in partition.Elements)
                        if (!ids.Add(element.Id)) throw new StorageCorruptionException("Duplicate graph member identity.");
                }
            }
            foreach (var partitions in AllPartitions(state).GroupBy(p => p.Kind))
            {
                var identities = new Dictionary<ExtendedId, GraphElementState>();
                foreach (var element in GraphElementCollection.UniqueElements(partitions.Select(p => p.Elements)))
                {
                    if (element is null || element.Id is null || element.Serial is null || element.Payload is null || element.MappingSerials.IsDefault)
                        throw new StorageCorruptionException("Incomplete immutable graph descriptor.");
                    var canonical = element;
                    if (identities.TryGetValue(element.Id, out var prior))
                    {
                        if (prior.Type != element.Type || prior.Payload.Length != element.Payload.Length ||
                            prior.Payload.Sha256 != element.Payload.Sha256 ||
                            prior.Serial.Guid != Guid.Empty && element.Serial.Guid != Guid.Empty && prior.Serial != element.Serial ||
                            !prior.MappingSerials.AsSpan().SequenceEqual(element.MappingSerials.AsSpan()))
                            throw new StorageCorruptionException("Conflicting immutable graph descriptor identity.");
                        if (prior.Serial.Guid != Guid.Empty) canonical = prior;
                    }
                    identities[element.Id] = canonical;
                }
            }
        }

        public override void Write(Utf8JsonWriter writer, GraphElementCollection value, JsonSerializerOptions options)
        {
            writer.WriteStartArray();
            foreach (var range in GetRanges(value))
            {
                writer.WriteNumberValue(range.Start);
                writer.WriteNumberValue(range.Count);
            }
            writer.WriteEndArray();
        }
    }

    private sealed class ElementComparer : IEqualityComparer<GraphElementState>
    {
        public static ElementComparer Instance { get; } = new();
        public bool Equals(GraphElementState? x, GraphElementState? y) => ReferenceEquals(x, y) ||
            x is not null && y is not null && x.Id == y.Id && x.Type == y.Type && x.Serial == y.Serial &&
            x.Payload == y.Payload && x.MappingSerials.AsSpan().SequenceEqual(y.MappingSerials.AsSpan());
        public int GetHashCode(GraphElementState element) => HashCode.Combine(element.Id, element.Type, element.Serial, element.Payload);
    }

    private sealed class CountingStream : Stream
    {
        private long _length;
        public override long Length => _length;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Position { get => _length; set => throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) => _length = checked(_length + count);
        public override void Write(ReadOnlySpan<byte> buffer) => _length = checked(_length + buffer.Length);
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
