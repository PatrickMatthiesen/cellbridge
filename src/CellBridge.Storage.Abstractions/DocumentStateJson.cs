using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace CellBridge.Storage.Abstractions;

/// <summary>
/// Storage encoding 1 shares immutable graph descriptors within a state. Partition
/// membership is an ordered sequence of table ranges; no graph identity is removed.
/// The detached public records and legacy expanded JSON remain unchanged.
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
            ValidatePartitionIdentities(state);
            if (ranges is not null && ranges.Used.Any(used => !used))
                throw new JsonException("Unreferenced shared graph descriptor.");
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
            foreach (var partition in state.Partitions.Concat(state.Revisions.SelectMany(r => r.Partitions)))
                foreach (var element in partition.Elements)
                {
                    if (element is null) throw new StorageCorruptionException("Null immutable graph descriptor.");
                    if (indices.TryAdd(element, elements.Count)) elements.Add(element);
                }
            ValidatePartitionIdentities(state);
            _metadata = new(1, elements.ToArray());
            Ranges.Initialize(_metadata.Elements, indices);
        }

        public void Clear()
        {
            _metadata = null;
            Ranges.Clear();
            Active = false;
        }
    }

    private sealed class ElementRanges : JsonConverter<ImmutableArray<GraphElementState>>
    {
        private GraphElementState[] table = [];
        private Dictionary<GraphElementState, int>? indices;
        private int maxElements;
        public bool[] Used { get; private set; } = [];
        public void Initialize(GraphElementState[] elements, Dictionary<GraphElementState, int>? elementIndices = null,
            int maxElements = int.MaxValue)
        {
            table = elements;
            indices = elementIndices;
            this.maxElements = maxElements;
            Used = elementIndices is null ? new bool[elements.Length] : [];
        }
        public void Clear()
        {
            table = [];
            indices = null;
            Used = [];
        }
        public override ImmutableArray<GraphElementState> Read(ref Utf8JsonReader reader, Type typeToConvert,
            JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException("Missing graph ranges.");
            var elements = ImmutableArray.CreateBuilder<GraphElementState>();
            var identities = new HashSet<ExtendedId>();
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                if (reader.TokenType != JsonTokenType.Number || !reader.TryGetInt32(out var start) ||
                    !reader.Read() || reader.TokenType != JsonTokenType.Number || !reader.TryGetInt32(out var count) ||
                    start < 0 || count <= 0 || start > table.Length - count)
                    throw new JsonException("Invalid graph table range.");
                // A graph cannot contain an identity twice. Bound expansion before allocating.
                if (count > table.Length - elements.Count) throw new JsonException("Graph membership exceeds the table.");
                StorageLimits.Check("graph elements", checked((long)elements.Count + count), maxElements);
                for (var i = start; i < start + count; i++)
                {
                    if (!identities.Add(table[i].Id)) throw new JsonException("Duplicate graph member identity.");
                    Used[i] = true;
                    elements.Add(table[i]);
                }
            }
            if (reader.TokenType != JsonTokenType.EndArray) throw new JsonException("Incomplete graph ranges.");
            return elements.ToImmutable();
        }

        public override void Write(Utf8JsonWriter writer, ImmutableArray<GraphElementState> value,
            JsonSerializerOptions options)
        {
            writer.WriteStartArray();
            for (var i = 0; i < value.Length;)
            {
                var start = indices![value[i++]];
                var count = 1;
                while (i < value.Length && indices[value[i]] == start + count) { count++; i++; }
                writer.WriteNumberValue(start);
                writer.WriteNumberValue(count);
            }
            writer.WriteEndArray();
        }
    }

    private static void ValidatePartitionIdentities(DocumentState state)
    {
        foreach (var partitions in state.Partitions.Concat(state.Revisions.SelectMany(r => r.Partitions)).GroupBy(p => p.Kind))
        {
            var identities = new Dictionary<ExtendedId, (GraphElementState Element, int Partition)>();
            var partitionNumber = 0;
            foreach (var partition in partitions)
            {
                foreach (var element in partition.Elements)
                {
                    if (element is null || element.Id is null || element.Serial is null || element.Payload is null || element.MappingSerials.IsDefault)
                        throw new StorageCorruptionException("Incomplete immutable graph descriptor.");
                    var canonical = element;
                    if (identities.TryGetValue(element.Id, out var previous))
                    {
                        if (previous.Partition == partitionNumber)
                            throw new StorageCorruptionException("Duplicate graph member identity.");
                        var prior = previous.Element;
                        // Protocol null serials may acquire a watermark. Preserve
                        // the populated watermark while checking later partitions.
                        if (prior.Type != element.Type || prior.Payload.Length != element.Payload.Length ||
                            prior.Payload.Sha256 != element.Payload.Sha256 ||
                            prior.Serial.Guid != Guid.Empty && element.Serial.Guid != Guid.Empty && prior.Serial != element.Serial ||
                            !prior.MappingSerials.AsSpan().SequenceEqual(element.MappingSerials.AsSpan()))
                            throw new StorageCorruptionException("Conflicting immutable graph descriptor identity.");
                        if (prior.Serial.Guid != Guid.Empty) canonical = prior;
                    }
                    identities[element.Id] = (canonical, partitionNumber);
                }
                partitionNumber = checked(partitionNumber + 1);
            }
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
