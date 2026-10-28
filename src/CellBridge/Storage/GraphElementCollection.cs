using System.Collections;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CellBridge.Storage.Abstractions;

/// <summary>An immutable ordered graph membership. Appends share their retained prefix.</summary>
[CollectionBuilder(typeof(GraphElementCollection), nameof(Create))]
[JsonConverter(typeof(GraphElementCollectionJsonConverter))]
public readonly struct GraphElementCollection : IReadOnlyList<GraphElementState>, IEquatable<GraphElementCollection>
{
    internal sealed class Node
    {
        internal readonly ImmutableArray<GraphElementState> Table;
        internal readonly int Start;
        internal readonly int Length;
        internal readonly Node? Left;
        internal readonly Node? Right;
        internal Node(ImmutableArray<GraphElementState> table, int start, int length)
        { Table = table; Start = start; Length = length; }
        internal Node(Node left, Node right)
        { Left = left; Right = right; Length = checked(left.Length + right.Length); }
    }

    internal Node? Root { get; }
    private readonly bool _initialized;
    private GraphElementCollection(Node? root) { Root = root; _initialized = true; }
    public int Length => Root?.Length ?? 0;
    public int Count => Length;
    public bool Equals(GraphElementCollection other) => ReferenceEquals(Root, other.Root) && _initialized == other._initialized;
    public override bool Equals(object? obj) => obj is GraphElementCollection other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(Root, _initialized);
    public static bool operator ==(GraphElementCollection left, GraphElementCollection right) => left.Equals(right);
    public static bool operator !=(GraphElementCollection left, GraphElementCollection right) => !left.Equals(right);
    public bool IsEmpty => Length == 0;
    public bool IsDefault => !_initialized;
    public static GraphElementCollection Empty { get; } = new(null);
    public static GraphElementCollection Create(ReadOnlySpan<GraphElementState> elements) =>
        (GraphElementCollection)ImmutableArray.Create(elements);
    public static implicit operator GraphElementCollection(ImmutableArray<GraphElementState> elements) =>
        elements.IsDefault ? default : FromRange(elements, 0, elements.Length);
    internal static GraphElementCollection FromRange(ImmutableArray<GraphElementState> table, int start, int length) =>
        new(length == 0 ? null : new Node(table, start, length));
    public GraphElementCollection Add(GraphElementState element) => Concat(Create([element]));
    public GraphElementCollection Concat(GraphElementCollection tail) => Root is null ? tail : tail.Root is null ? this :
        new(new Node(Root, tail.Root));
    public GraphElementCollection SetItem(int index, GraphElementState element)
    {
        var copy = this.ToImmutableArray();
        return copy.SetItem(index, element);
    }
    public GraphElementState this[int index]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            if (index >= Length) throw new ArgumentOutOfRangeException(nameof(index));
            var node = Root!;
            while (node.Left is not null)
            {
                if (index < node.Left.Length) node = node.Left;
                else { index -= node.Left.Length; node = node.Right!; }
            }
            return node.Table[node.Start + index];
        }
    }
    public IEnumerator<GraphElementState> GetEnumerator()
    {
        if (Root is null) yield break;
        var stack = new Stack<Node>();
        stack.Push(Root);
        while (stack.TryPop(out var node))
        {
            if (node.Left is not null) { stack.Push(node.Right!); stack.Push(node.Left); }
            else for (var i = node.Start; i < node.Start + node.Length; i++) yield return node.Table[i];
        }
    }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    internal readonly record struct Segment(ImmutableArray<GraphElementState> Table, int Start, int Length);

    // Visit backing slots once, even when many historical memberships share
    // concat nodes or overlapping views of a decoded descriptor table.
    internal static IEnumerable<Segment> UniqueSegments(IEnumerable<GraphElementCollection> collections)
    {
        var nodes = new HashSet<Node>(ReferenceEqualityComparer.Instance);
        var covered = new Dictionary<ImmutableArray<GraphElementState>, List<(int Start, int End)>>();
        var stack = new Stack<Node>();
        foreach (var collection in collections)
        {
            if (collection.Root is null) continue;
            stack.Push(collection.Root);
            while (stack.TryPop(out var node))
            {
                if (!nodes.Add(node)) continue;
                if (node.Left is not null) { stack.Push(node.Right!); stack.Push(node.Left); continue; }
                if (!covered.TryGetValue(node.Table, out var intervals))
                    covered.Add(node.Table, intervals = []);
                var start = node.Start;
                var end = checked(start + node.Length);
                var lo = 0;
                var hi = intervals.Count;
                while (lo < hi)
                {
                    var mid = lo + (hi - lo) / 2;
                    if (intervals[mid].End < start) lo = mid + 1; else hi = mid;
                }
                var first = lo;
                var cursor = start;
                while (lo < intervals.Count && intervals[lo].Start <= end)
                {
                    var old = intervals[lo];
                    if (cursor < old.Start) yield return new(node.Table, cursor, Math.Min(end, old.Start) - cursor);
                    cursor = Math.Max(cursor, old.End);
                    start = Math.Min(start, old.Start);
                    end = Math.Max(end, old.End);
                    lo++;
                }
                // Only the original requested interval contains newly visited slots.
                var requestedEnd = node.Start + node.Length;
                if (cursor < requestedEnd) yield return new(node.Table, cursor, requestedEnd - cursor);
                if (lo > first) intervals.RemoveRange(first, lo - first);
                intervals.Insert(first, (start, end));
            }
        }
    }

    internal static IEnumerable<GraphElementState> UniqueElements(IEnumerable<GraphElementCollection> collections)
    {
        foreach (var segment in UniqueSegments(collections))
            for (var i = segment.Start; i < segment.Start + segment.Length; i++) yield return segment.Table[i];
    }
}

/// <summary>The public JSON representation remains the expanded ordered array.</summary>
public sealed class GraphElementCollectionJsonConverter : JsonConverter<GraphElementCollection>
{
    public override GraphElementCollection Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        JsonSerializer.Deserialize<ImmutableArray<GraphElementState>>(ref reader, options);
    public override void Write(Utf8JsonWriter writer, GraphElementCollection value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var element in value) JsonSerializer.Serialize(writer, element, options);
        writer.WriteEndArray();
    }
}
