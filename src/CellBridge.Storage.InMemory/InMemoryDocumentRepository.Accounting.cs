using System.Text;
using CellBridge.Storage.Abstractions;

namespace CellBridge.Storage.InMemory;

public sealed partial class InMemoryDocumentRepository
{
    private static long Bytes<T>(T value) => NormalizedDocumentRecovery.SerializedBytes(value);
    private static DocumentHead AccountedHead(Entry entry, DocumentHead head, long metadataDelta = 0, long physicalDelta = 0) =>
        head with { Counters = head.Counters with { StoredBytes = checked(entry.MetadataBytes + metadataDelta + entry.PhysicalBytes +
            physicalDelta + NormalizedDocumentRecovery.CanonicalHeadBytes(head)) } };

    private static long ControlMetadataBytes(DocumentControlRead control) => checked(Bytes(control.Coordination) +
        control.Editors.Sum(editor => Bytes(editor)) + control.Partitions.Sum(partition => Bytes(partition)));

    private static long PublicationMetadataDelta(Entry entry, PreparedDocumentPublication publication, DocumentDelivery? delivery)
    {
        long bytes = Bytes(publication.Receipt);
        if (publication.Revision is { } revision) bytes = checked(bytes + Bytes(revision));
        if (delivery is not null) bytes = checked(bytes + Bytes(delivery));
        foreach (var prepared in publication.Partitions)
        {
            var stored = entry.Partitions[prepared.After.Kind];
            bytes = checked(bytes + Bytes(prepared.After) - Bytes(stored.Head));
            if (prepared.After.StorageIndex is { } logicalIndex && !stored.IndexSequences.ContainsKey(logicalIndex))
                bytes = checked(bytes + Bytes(prepared.After));
            foreach (var element in prepared.AddedElements)
                if (!stored.Elements.ContainsKey(element.Id)) bytes = checked(bytes + Bytes(element));
            foreach (var graphRevision in prepared.Revisions)
                if (!stored.Revisions.ContainsKey(graphRevision.ElementId)) bytes = checked(bytes + Bytes(graphRevision));
            foreach (var obj in prepared.Objects)
                if (!stored.Objects.ContainsKey((obj.GroupElement, obj.Ordinal))) bytes = checked(bytes + Bytes(obj));
            foreach (var cell in prepared.Cells)
                if (!stored.Cells.ContainsKey(cell.ElementId)) bytes = checked(bytes + Bytes(cell));
            foreach (var manifest in prepared.StorageManifests)
                if (!stored.StorageManifests.ContainsKey(manifest.ElementId)) bytes = checked(bytes + Bytes(manifest));
            foreach (var change in prepared.EffectiveChanges.Changes)
            {
                bytes = checked(bytes + Bytes(change));
                if (stored.Bindings.TryGetValue(change.Key, out var before)) bytes = checked(bytes - Bytes(before));
                if (change.Value is { } after) bytes = checked(bytes + Bytes(after));
            }
            if (prepared.Checkpoint is { } checkpoint) bytes = checked(bytes + 4 + checkpoint.Bindings.Sum(binding => Bytes(binding)));
            foreach (var claim in prepared.Mappings.Where(mapping => !NullSerial(mapping.Serial)).DistinctBy(mapping => mapping.Serial))
                if (!stored.MappingClaims.ContainsKey(claim.Serial)) bytes = checked(bytes + Bytes(claim));
            foreach (var change in prepared.MappingChanges)
            {
                var old = stored.CurrentMappings.GetValueOrDefault(change.Key);
                if (old == change.Value) continue;
                if (old is not null) bytes = checked(bytes - Bytes(stored.MappingIntervals[change.Key][^1].Mapping));
                if (change.Value is { } value)
                    bytes = checked(bytes + 2 * Bytes(new IndexedStorageMapping(prepared.EffectiveChanges.Scope,
                        prepared.After.StorageIndex!, change.Key, value.Target, value.Serial)));
            }
        }
        return bytes;
    }
}
