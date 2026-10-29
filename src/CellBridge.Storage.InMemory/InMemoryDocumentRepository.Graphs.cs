using System.Collections.Immutable;
using System.Text.Json;
using CellBridge.FssHttpB;
using CellBridge.Storage.Abstractions;

namespace CellBridge.Storage.InMemory;

public sealed partial class InMemoryDocumentRepository
{
    private void ValidatePartition(Partition stored, PreparedPartitionPublication prepared, IndexedGraphScope scope)
    {
        StorageLimits.Check("inline partition bytes", prepared.After.InlineContent.Length, _limits.MaxObjectBytes);
        if (prepared.Before.Kind != scope.TargetPartition || prepared.After.Kind != scope.TargetPartition ||
            prepared.After.Identity != prepared.Before.Identity || prepared.EffectiveChanges.Scope != scope ||
            prepared.Before.PublicationSequence != prepared.EffectiveChanges.PreviousSequence ||
            prepared.After.PublicationSequence != prepared.EffectiveChanges.Sequence ||
            prepared.After.PublicationSequence <= prepared.Before.PublicationSequence ||
            prepared.AddedElements.Select(e => e.Id).Distinct().Count() != prepared.AddedElements.Length ||
            prepared.RetiredElements.Distinct().Count() != prepared.RetiredElements.Length ||
            prepared.Mappings.Any(m => m.Scope != scope) || prepared.Revisions.Any(r => r.Scope != scope) ||
            prepared.Cells.Any(c => c.Scope != scope) || prepared.StorageManifests.Any(s => s.Scope != scope) ||
            prepared.Objects.Any(o => o.Scope != scope))
            throw new InvalidDataException("Invalid partition publication identity or scope.");
        var activeCount = stored.Head.ElementCount;
        var activeBytes = stored.Head.ElementBytes;
        var addedIds = prepared.AddedElements.Select(e => e.Id).ToHashSet();
        foreach (var element in prepared.AddedElements)
        {
            if (element.Type == (uint)DataElementType.StorageIndexDataElementData)
                throw new InvalidDataException("Storage-index transmission envelopes are not immutable canonical elements.");
            if (element.MappingSerials.IsDefault) throw new InvalidDataException("An element has no mapping serial collection.");
            if (stored.Elements.TryGetValue(element.Id, out var existing)) IndexedDocumentGraph.ValidateIdentity(existing, element);
            if (!Visible(stored, element.Id, stored.Head.PublicationSequence))
            { activeCount = checked(activeCount + 1); activeBytes = checked(activeBytes + element.Payload.Length); }
        }
        foreach (var retired in prepared.RetiredElements)
        {
            if (!Visible(stored, retired, stored.Head.PublicationSequence) || addedIds.Contains(retired))
                throw new InvalidDataException("A retirement must name a currently visible, unchanged element.");
            activeCount--; activeBytes -= stored.Elements[retired].Payload.Length;
        }
        if (prepared.After.ElementCount != activeCount || prepared.After.ElementBytes != activeBytes)
            throw new InvalidDataException("The partition element counters do not match its changes.");
        foreach (var mapping in prepared.Mappings)
            if (!NullSerial(mapping.Serial) && stored.MappingClaims.TryGetValue(mapping.Serial, out var claims) &&
                claims.Values.Any(existing => existing.Scope != mapping.Scope || existing.Key != mapping.Key || existing.Target != mapping.Target))
                throw new InvalidDataException("A mapping serial was reused with a different key or target.");
        foreach (var claims in prepared.Mappings.Where(m => !NullSerial(m.Serial)).GroupBy(m => m.Serial))
            if (claims.Select(m => (m.Key, m.Target)).Distinct().Skip(1).Any())
                throw new InvalidDataException("An incoming mapping serial names different keys or targets.");
        if (prepared.MappingChanges.IsDefault || prepared.MappingChanges.Select(c => c.Key).Distinct().Count() != prepared.MappingChanges.Length ||
            prepared.After.StorageIndex is not null && prepared.After.StorageIndexSerial is null)
            throw new InvalidDataException("Invalid applied mapping root or changes.");
        if (prepared.MappingChanges.Length > 0 && prepared.After.StorageIndex is { } rootId && stored.IndexSequences.ContainsKey(rootId))
            throw new InvalidDataException("An applied mapping change requires a fresh logical storage-index root identity.");
        var incomingById = prepared.AddedElements.ToDictionary(element => element.Id);
        foreach (var change in prepared.MappingChanges)
        {
            if (change.Value is not { } value) continue;
            if (value.Target.Guid == Guid.Empty && value.Target.Value == 0) continue;
            var element = incomingById.GetValueOrDefault(value.Target) ?? stored.Elements.GetValueOrDefault(value.Target)
                ?? throw new InvalidDataException("A mapping target has no immutable element.");
            var expectedType = change.Key.Kind switch
            {
                IndexedMappingKind.Manifest => DataElementType.StorageManifestDataElementData,
                IndexedMappingKind.Cell => DataElementType.CellManifestDataElementData,
                IndexedMappingKind.Revision => DataElementType.RevisionManifestDataElementData,
                _ => throw new InvalidDataException("Unknown mapping kind."),
            };
            if (element.Type != (uint)expectedType) throw new InvalidDataException("A mapping target has the wrong immutable element type.");
        }
        foreach (var revision in prepared.Revisions) CheckImmutable(stored.Revisions, revision.ElementId, revision);
        foreach (var cell in prepared.Cells) CheckImmutable(stored.Cells, cell.ElementId, cell);
        foreach (var storage in prepared.StorageManifests) CheckImmutable(stored.StorageManifests, storage.ElementId, storage);
        foreach (var definition in prepared.Objects)
        {
            if (definition.Ordinal < 0 || string.IsNullOrEmpty(definition.DefinitionDigest))
                throw new InvalidDataException("Invalid object metadata.");
            CheckImmutable(stored.Objects, (definition.GroupElement, definition.Ordinal), definition);
        }
        if (prepared.Mappings.GroupBy(m => (m.StorageIndex, m.Key)).Any(g => g.Count() != 1) ||
            prepared.Revisions.GroupBy(r => r.ElementId).Any(g => g.Count() != 1) ||
            prepared.Objects.GroupBy(o => (o.GroupElement, o.Ordinal)).Any(g => g.Count() != 1))
            throw new InvalidDataException("Duplicate immutable metadata key.");
        foreach (var change in prepared.EffectiveChanges.Changes)
            if (change.Key.Scope != scope || change.Value is { } value && (value.Key != change.Key || value.ObjectOrdinal < 0) ||
                change.Value is null && !stored.Bindings.ContainsKey(change.Key))
                throw new InvalidDataException("Invalid effective binding change.");
        if (prepared.EffectiveChanges.Changes.Select(c => c.Key).Distinct().Count() != prepared.EffectiveChanges.Changes.Length)
            throw new InvalidDataException("Duplicate effective binding change.");
        var checkpointRequired = IndexedDocumentGraph.RequiresCheckpoint(stored.ReplayPosition, prepared.EffectiveChanges, _checkpointPolicy);
        if (checkpointRequired && prepared.Checkpoint is null)
            throw new InvalidDataException("The bounded history requires a synchronously prepared checkpoint.");
        if (prepared.Checkpoint is { } checkpoint)
        {
            var next = new Dictionary<IndexedBindingKey, IndexedObjectBinding>(stored.Bindings);
            ApplyBindingChanges(next, prepared.EffectiveChanges);
            var expected = IndexedDocumentGraph.Checkpoint(scope, prepared.After.PublicationSequence, next.Values);
            if (!EqualMetadata(checkpoint, expected))
                throw new InvalidDataException("Prepared checkpoint does not match the effective publication changes.");
        }
        // Check the encoded delta and its declared sequence even when a checkpoint replaces the tail.
        var validationBase = IndexedDocumentGraph.Checkpoint(scope, stored.Head.PublicationSequence,
            prepared.EffectiveChanges.Changes.Where(c => stored.Bindings.ContainsKey(c.Key)).Select(c => stored.Bindings[c.Key]));
        _ = IndexedDocumentGraph.Replay(validationBase, [prepared.EffectiveChanges], prepared.After.PublicationSequence,
            new IndexedCheckpointPolicy(int.MaxValue, long.MaxValue));
    }

    private static bool EqualMetadata<T>(T left, T right) =>
        JsonSerializer.SerializeToUtf8Bytes(left).AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(right));
    private static bool NullSerial(SerialId serial) => serial.Guid == Guid.Empty && serial.Value == 0;

    private static void CheckImmutable<TKey, TValue>(Dictionary<TKey, TValue> values, TKey key, TValue value) where TKey : notnull
    {
        if (values.TryGetValue(key, out var existing) && !EqualMetadata(existing, value))
            throw new InvalidDataException("Immutable indexed metadata was reused with a different definition.");
    }

    private static void ApplyBindingChanges(Dictionary<IndexedBindingKey, IndexedObjectBinding> values, IndexedBindingDelta delta)
    {
        foreach (var change in delta.Changes)
            if (change.Value is { } value) values[change.Key] = value;
            else values.Remove(change.Key);
    }

    private static void ApplyPartition(Partition stored, PreparedPartitionPublication prepared)
    {
        foreach (var element in prepared.AddedElements)
        {
            if (stored.Elements.TryAdd(element.Id, element))
            {
                IndexSerial(stored, element.Serial, element.Id);
                foreach (var serial in element.MappingSerials) IndexSerial(stored, serial, element.Id);
            }
            if (!Visible(stored, element.Id, stored.Head.PublicationSequence))
            {
                if (!stored.Visibility.TryGetValue(element.Id, out var intervals)) stored.Visibility[element.Id] = intervals = [];
                intervals.Add(new(prepared.After.PublicationSequence, null));
            }
        }
        foreach (var id in prepared.RetiredElements)
        {
            var intervals = stored.Visibility[id];
            intervals[^1] = intervals[^1] with { End = prepared.After.PublicationSequence };
        }
        foreach (var mapping in prepared.Mappings)
        {
            if (NullSerial(mapping.Serial)) continue;
            if (!stored.MappingClaims.TryGetValue(mapping.Serial, out var claims)) stored.MappingClaims[mapping.Serial] = claims = [];
            claims.TryAdd((mapping.Key, mapping.Target), mapping);
        }
        var nextRoot = stored.CurrentMappings;
        foreach (var change in prepared.MappingChanges)
        {
            if (stored.CurrentMappings.GetValueOrDefault(change.Key) == change.Value) continue;
            if (stored.MappingIntervals.TryGetValue(change.Key, out var intervals) && intervals.Count > 0 && intervals[^1].EndSequence is null)
                intervals[^1] = intervals[^1] with { EndSequence = prepared.After.PublicationSequence };
            if (change.Value is { } changed)
            {
                if (intervals is null) stored.MappingIntervals[change.Key] = intervals = [];
                intervals.Add(new(new(prepared.EffectiveChanges.Scope, prepared.After.StorageIndex!, change.Key, changed.Target, changed.Serial),
                    prepared.After.PublicationSequence, null));
            }
            nextRoot = change.Value is { } value ? nextRoot.SetItem(change.Key, value) : nextRoot.Remove(change.Key);
        }
        stored.MappingRoots.Add(prepared.After.PublicationSequence, nextRoot);
        stored.Heads.Add(prepared.After.PublicationSequence, prepared.After);
        if (prepared.After.StorageIndex is { } index) stored.IndexSequences.TryAdd(index, prepared.After.PublicationSequence);
        stored.CurrentMappings = nextRoot;
        foreach (var revision in prepared.Revisions) stored.Revisions.TryAdd(revision.ElementId, revision);
        foreach (var cell in prepared.Cells) stored.Cells.TryAdd(cell.ElementId, cell);
        foreach (var storage in prepared.StorageManifests) stored.StorageManifests.TryAdd(storage.ElementId, storage);
        foreach (var definition in prepared.Objects) stored.Objects.TryAdd((definition.GroupElement, definition.Ordinal), definition);
        foreach (var group in prepared.Objects.GroupBy(o => o.GroupElement))
            stored.Groups.TryAdd(group.Key, group.OrderBy(o => o.Ordinal).ToImmutableArray());
        foreach (var element in prepared.AddedElements.Where(element => element.Type == (uint)DataElementType.ObjectGroupDataElementData))
            stored.Groups.TryAdd(element.Id, []);
        ApplyBindingChanges(stored.Bindings, prepared.EffectiveChanges);
        stored.Changes.Add(prepared.EffectiveChanges.Sequence, prepared.EffectiveChanges);
        if (prepared.Checkpoint is { } checkpoint)
        {
            stored.Checkpoints.Add(checkpoint.Sequence, checkpoint);
            stored.ReplayPosition = new(checkpoint.Sequence, 0, 0);
        }
        else stored.ReplayPosition = stored.ReplayPosition with
        { Changes = checked(stored.ReplayPosition.Changes + 1), Bytes = checked(stored.ReplayPosition.Bytes + prepared.EffectiveChanges.EncodedBytes) };
        stored.Head = prepared.After;
    }

    private static void IndexSerial(Partition stored, SerialId serial, ExtendedId id)
    {
        if (!stored.Serials.TryGetValue(serial.Guid, out var entries)) stored.Serials[serial.Guid] = entries = new();
        if (!entries.Entries.TryGetValue(serial.Value, out var ids))
        { entries.Keys.Add(serial.Value); entries.Entries[serial.Value] = ids = []; }
        ids.Add(id);
    }
}
