using System.Collections.Immutable;

namespace CellBridge.Storage.Abstractions;

public static partial class IndexedDocumentGraph
{
    public static ImmutableArray<GraphSerialRange> Knowledge(IEnumerable<GraphSerialRange> previous,
        IEnumerable<GraphElementState> added) => NormalizeRanges(previous.Concat(added.SelectMany(element =>
            element.MappingSerials.Prepend(element.Serial)).Where(serial => serial.Guid != Guid.Empty || serial.Value != 0)
            .Select(serial => new GraphSerialRange(serial.Guid, serial.Value, serial.Value))));

    public static ImmutableArray<GraphSerialRange> NormalizeRanges(IEnumerable<GraphSerialRange> ranges)
    {
        var result = ImmutableArray.CreateBuilder<GraphSerialRange>();
        foreach (var range in ranges.OrderBy(range => range.Namespace).ThenBy(range => range.First))
        {
            if (range.First > range.Last) throw new InvalidDataException("Invalid graph knowledge range.");
            if (result.Count > 0 && result[^1] is var previous && previous.Namespace == range.Namespace &&
                (range.First <= previous.Last || previous.Last != ulong.MaxValue && range.First == previous.Last + 1))
                result[^1] = previous with { Last = Math.Max(previous.Last, range.Last) };
            else result.Add(range);
        }
        return result.ToImmutable();
    }
}
