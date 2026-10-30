using System.Collections.Immutable;
using System.Text.Json;
using CellBridge.Storage.Abstractions;

namespace CellBridge.Storage.Tests;

public sealed class GraphElementCollectionTests
{
    [Fact]
    public void AppendSharesPrefixAndPublicJsonPreservesExpandedOrder()
    {
        GraphElementCollection prefix = [Element(1), Element(2)];
        var appended = prefix.Add(Element(3));
        Assert.Same(prefix.Root, appended.Root!.Left);
        Assert.Equal(2, prefix.Length);
        Assert.Equal(new uint[] { 1, 2, 3 }, appended.Select(e => e.Id.Value));
        Assert.Same(prefix[1], appended[1]);
        Assert.True(default(GraphElementCollection).IsDefault);
        GraphElementCollection empty = [];
        Assert.True(empty.IsEmpty);
        Assert.False(empty.IsDefault);
        var json = JsonSerializer.Serialize(appended);
        var decoded = JsonSerializer.Deserialize<GraphElementCollection>(json);
        Assert.Equal(json, JsonSerializer.Serialize(decoded));
        Assert.Equal(appended.Select(e => e.Id), decoded.Select(e => e.Id));
    }

    [Fact]
    public void DeepAppendTraversalIsIterativeAndVisitsSharedSlotsOnce()
    {
        const int count = 20_000;
        var prefix = GraphElementCollection.Empty;
        var histories = new List<GraphElementCollection>();
        for (var i = 1; i <= count; i++)
        {
            prefix = prefix.Add(Element((uint)i));
            histories.Add(prefix);
        }
        Assert.Equal(count, prefix.Count());
        Assert.Equal(1u, prefix[0].Id.Value);
        Assert.Equal((uint)count, prefix[count - 1].Id.Value);
        Assert.Equal(count, GraphElementCollection.UniqueSegments(histories.AsEnumerable().Reverse()).Sum(s => s.Length));
        Assert.Equal(count, GraphElementCollection.UniqueSegments(histories).Sum(s => s.Length));
    }

    [Fact]
    public void IntervalUnionKeepsEveryReferencedVariantAndFirstEncounterOrder()
    {
        var table = Enumerable.Range(1, 10).Select(i => Element((uint)i)).ToImmutableArray();
        var views = new[]
        {
            GraphElementCollection.FromRange(table, 4, 3),
            GraphElementCollection.FromRange(table, 1, 5),
            GraphElementCollection.FromRange(table, 0, 10),
        };
        Assert.Equal(new uint[] { 5, 6, 7, 2, 3, 4, 1, 8, 9, 10 },
            GraphElementCollection.UniqueElements(views).Select(e => e.Id.Value));
        Assert.Equal(10, GraphElementCollection.UniqueSegments(views).Sum(s => s.Length));
    }

    internal static GraphElementState Element(uint id) => new(new(id, Guid.Empty), 1,
        new(Guid.Empty, 0), new("graph-" + id, 1, new string('0', 64))) { MappingSerials = [] };
}
