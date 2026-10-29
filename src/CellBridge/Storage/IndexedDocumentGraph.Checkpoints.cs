using System.Collections.Immutable;
using System.Text.Json;

namespace CellBridge.Storage.Abstractions;

public static partial class IndexedDocumentGraph
{
    public static IndexedBindingDelta Difference(IndexedGraphScope scope, long previousSequence, long sequence,
        IEnumerable<IndexedObjectBinding> previous, IEnumerable<IndexedObjectBinding> next)
    {
        if (previousSequence < 0 || sequence <= previousSequence) throw new ArgumentOutOfRangeException(nameof(sequence));
        var before = BindingMap(scope, previous);
        var after = BindingMap(scope, next);
        var changes = new List<IndexedBindingChange>();
        foreach (var (key, value) in after)
            if (!before.TryGetValue(key, out var old) || old != value) changes.Add(new(key, value));
        foreach (var key in before.Keys)
            if (!after.ContainsKey(key)) changes.Add(new(key, null));
        var ordered = Order(changes, change => change.Key).ToImmutableArray();
        return new(scope, previousSequence, sequence, ordered, EncodedSize(ordered));
    }

    public static IndexedGraphCheckpoint Checkpoint(IndexedGraphScope scope, long sequence,
        IEnumerable<IndexedObjectBinding> bindings)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sequence);
        var ordered = Order(BindingMap(scope, bindings).Values, binding => binding.Key).ToImmutableArray();
        return new(scope, sequence, ordered, EncodedSize(ordered));
    }

    /// <summary>When true, the publication must synchronously stage a checkpoint before accepting its head.</summary>
    public static bool RequiresCheckpoint(IndexedReplayPosition position, IndexedBindingDelta next,
        IndexedCheckpointPolicy policy)
    {
        policy.Validate();
        if (position.CheckpointSequence < 0 || position.Changes < 0 || position.Bytes < 0)
            throw new InvalidDataException("Invalid checkpoint replay position.");
        ValidateDelta(next);
        return checked(position.Changes + 1) >= policy.MaxReplayChanges ||
            checked(position.Bytes + next.EncodedBytes) >= policy.MaxReplayBytes;
    }

    /// <summary>Replays effective publication differences, never raw protocol uploads or unrelated branches.</summary>
    public static ImmutableArray<IndexedObjectBinding> Replay(IndexedGraphCheckpoint checkpoint,
        IEnumerable<IndexedBindingDelta> tail, long selectedSequence, IndexedCheckpointPolicy policy)
    {
        policy.Validate();
        if (checkpoint.Sequence < 0 || selectedSequence < checkpoint.Sequence ||
            checkpoint.EncodedBytes != EncodedSize(checkpoint.Bindings))
            throw new InvalidDataException("Invalid metadata checkpoint.");
        var map = BindingMap(checkpoint.Scope, checkpoint.Bindings);
        var sequence = checkpoint.Sequence;
        int changes = 0;
        long bytes = 0;
        foreach (var delta in tail)
        {
            ValidateDelta(delta);
            if (delta.Scope != checkpoint.Scope || delta.PreviousSequence != sequence || delta.Sequence > selectedSequence)
                throw new InvalidDataException("Checkpoint tail is not the selected publication chain.");
            if (++changes > policy.MaxReplayChanges || (bytes = checked(bytes + delta.EncodedBytes)) > policy.MaxReplayBytes)
                throw new InvalidDataException("Checkpoint replay bound exceeded.");
            foreach (var change in delta.Changes)
            {
                if (change.Value is { } value) map[change.Key] = value;
                else if (!map.Remove(change.Key)) throw new InvalidDataException("Checkpoint tail removes an absent binding.");
            }
            sequence = delta.Sequence;
        }
        if (sequence != selectedSequence) throw new InvalidDataException("Checkpoint tail does not reach the selected publication.");
        return Order(map.Values, binding => binding.Key).ToImmutableArray();
    }

    private static void ValidateDelta(IndexedBindingDelta delta)
    {
        if (delta.PreviousSequence < 0 || delta.Sequence <= delta.PreviousSequence || delta.Changes.IsDefault ||
            delta.EncodedBytes != EncodedSize(delta.Changes))
            throw new InvalidDataException("Invalid effective binding delta.");
        var seen = new HashSet<IndexedBindingKey>();
        foreach (var change in delta.Changes)
        {
            if (change.Key.Scope != delta.Scope || !seen.Add(change.Key) ||
                change.Value is { } value && (value.Key != change.Key || value.ObjectOrdinal < 0))
                throw new InvalidDataException("Conflicting or cross-scope effective binding change.");
        }
    }

    private static Dictionary<IndexedBindingKey, IndexedObjectBinding> BindingMap(IndexedGraphScope scope,
        IEnumerable<IndexedObjectBinding> bindings)
    {
        var map = new Dictionary<IndexedBindingKey, IndexedObjectBinding>();
        foreach (var binding in bindings)
            if (binding.Key.Scope != scope || binding.ObjectOrdinal < 0 || !map.TryAdd(binding.Key, binding))
                throw new InvalidDataException("Duplicate, invalid or cross-scope resolved object binding.");
        return map;
    }

    private static IOrderedEnumerable<T> Order<T>(IEnumerable<T> items, Func<T, IndexedBindingKey> key) => items
        .OrderBy(value => key(value).Cell.LongId.Guid).ThenBy(value => key(value).Cell.LongId.Value)
        .ThenBy(value => key(value).Cell.ShortId.Guid).ThenBy(value => key(value).Cell.ShortId.Value)
        .ThenBy(value => key(value).ObjectId.Guid).ThenBy(value => key(value).ObjectId.Value)
        .ThenBy(value => key(value).ObjectPartitionId);

    private static long EncodedSize<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value).LongLength;
}
