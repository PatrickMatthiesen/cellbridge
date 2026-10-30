using System.Collections.Immutable;
using System.Text;
using CellBridge.Storage.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace CellBridge.Storage.Shared;

internal sealed partial class RelationalDocumentRepository<TContext> where TContext : DbContext
{
    private sealed record PartitionWrite(long MetadataBytes, int AddedElements, long AddedElementBytes);
    private static long GraphRecord<T>(TContext db, IndexedGraphScope scope, PartitionWriteRows rows, string kind, string owner, string key, T record)
    {
        var json = Encode(record);
        var lookup = new GraphWriteKey(kind, owner, key);
        var existing = rows.Graph[lookup];
        if (existing is not null)
        {
            if (existing.Json != json) throw new StorageCorruptionException("An immutable indexed graph record changed.");
            return 0;
        }
        var added = new RepositoryGraphRow { ResourceId = scope.ResourceId, Lifecycle = scope.LifecycleGeneration,
            Partition = scope.TargetPartition, Kind = kind, OwnerKey = owner, RecordKey = key, Json = json };
        db.Set<RepositoryGraphRow>().Add(added);
        rows.Graph[lookup] = added;
        return Encoding.UTF8.GetByteCount(json);
    }
    private async Task<PartitionWrite> WritePartitionAsync(TContext db, DocumentHead head, PreparedPartitionPublication publication, bool initial, CancellationToken ct)
    {
        var scope = Scope(head, publication.After.Kind);
        var sequence = publication.After.PublicationSequence;
        if (publication.EffectiveChanges.Scope != scope || publication.After.Kind != publication.Before.Kind || sequence < 0 ||
            !initial && (sequence <= publication.Before.PublicationSequence || publication.EffectiveChanges.PreviousSequence != publication.Before.PublicationSequence) ||
            publication.EffectiveChanges.Sequence != sequence)
            throw new InvalidOperationException("Invalid partition publication sequence or scope.");
        if (publication.AddedElements.Select(element => element.Id).Distinct().Count() != publication.AddedElements.Length ||
            publication.RetiredElements.Distinct().Count() != publication.RetiredElements.Length ||
            publication.RetiredElements.Any(id => publication.AddedElements.Any(element => element.Id == id)))
            throw new InvalidDataException("A partition publication repeats or both adds and retires an element identity.");
        var activeCount = initial ? 0 : publication.Before.ElementCount;
        long activeBytes = initial ? 0 : publication.Before.ElementBytes;
        var partition = await db.Set<RepositoryPartitionRow>().FindAsync([scope.ResourceId, scope.LifecycleGeneration, scope.TargetPartition], ct);
        if (!initial && (partition is null || partition.Sequence != publication.Before.PublicationSequence))
            throw new InvalidOperationException("Partition publication lost its guarded current root.");
        if (partition is null)
        {
            partition = new() { ResourceId = scope.ResourceId, Lifecycle = scope.LifecycleGeneration, Partition = scope.TargetPartition, Json = "" };
            db.Set<RepositoryPartitionRow>().Add(partition);
        }
        var rows = await PrefetchPartitionAsync(db, scope, publication, ct);
        long metadata = Encoding.UTF8.GetByteCount(Encode(publication.After)) - Encoding.UTF8.GetByteCount(partition.Json);
        int addedCount = 0;
        long addedBytes = 0;
        foreach (var element in publication.AddedElements)
        {
            if (element.MappingSerials.IsDefault) throw new InvalidDataException("Graph element mapping serials are absent.");
            var key = ElementKey(element.Id);
            var existing = rows.Elements[key];
            if (existing is null)
            {
                var json = Encode(element);
                var added = new RepositoryElementRow { ResourceId = scope.ResourceId, Lifecycle = scope.LifecycleGeneration,
                    Partition = scope.TargetPartition, ElementKey = key, SerialGuid = element.Serial.Guid, SerialValue = element.Serial.Value, Json = json };
                db.Set<RepositoryElementRow>().Add(added);
                rows.Elements[key] = added;
                metadata = checked(metadata + Encoding.UTF8.GetByteCount(json));
                addedCount = checked(addedCount + 1); addedBytes = checked(addedBytes + element.Payload.Length);
            }
            else IndexedDocumentGraph.ValidateIdentity(Decode<GraphElementState>(existing.Json), element);
            if (rows.Visibility[key] is null)
            {
                activeCount = checked(activeCount + 1); activeBytes = checked(activeBytes + element.Payload.Length);
                var visibility = new RepositoryVisibilityRow { ResourceId = scope.ResourceId, Lifecycle = scope.LifecycleGeneration, Partition = scope.TargetPartition,
                    ElementKey = key, StartSequence = sequence };
                db.Set<RepositoryVisibilityRow>().Add(visibility);
                rows.Visibility[key] = visibility;
            }
        }
        foreach (var id in publication.RetiredElements)
        {
            var key = ElementKey(id);
            var visibility = rows.Visibility[key] ?? throw new InvalidDataException("A retired element is not in the current visible catalog.");
            visibility.EndSequence = sequence;
            rows.Visibility[key] = null;
            var retired = rows.Elements[key]
                ?? throw new StorageCorruptionException("A visible element has no immutable metadata.");
            activeCount--; activeBytes = checked(activeBytes - Decode<GraphElementState>(retired.Json).Payload.Length);
        }
        if (publication.After.ElementCount != activeCount || publication.After.ElementBytes != activeBytes)
            throw new InvalidDataException("Current catalog counters disagree with the prepared element changes.");
        metadata = checked(metadata + RegisterMappingClaims(db, scope, publication.Mappings, rows));
        metadata = checked(metadata + await WriteMappingChangesAsync(db, scope, publication, rows, ct));
        foreach (var revision in publication.Revisions)
        {
            if (revision.Scope != scope) throw new InvalidDataException("Cross-document revision metadata.");
            metadata = checked(metadata + GraphRecord(db, scope, rows, "revision", ElementKey(revision.ElementId), "", revision));
        }
        foreach (var definition in publication.Objects)
        {
            if (definition.Scope != scope || definition.Ordinal < 0) throw new InvalidDataException("Invalid object definition scope or ordinal.");
            metadata = checked(metadata + GraphRecord(db, scope, rows, "object", ElementKey(definition.GroupElement), definition.Ordinal.ToString("D10"), definition));
        }
        foreach (var cell in publication.Cells)
        {
            if (cell.Scope != scope) throw new InvalidDataException("Cross-document cell metadata.");
            metadata = checked(metadata + GraphRecord(db, scope, rows, "cell", ElementKey(cell.ElementId), "", cell));
        }
        foreach (var manifest in publication.StorageManifests)
        {
            if (manifest.Scope != scope) throw new InvalidDataException("Cross-document storage manifest metadata.");
            metadata = checked(metadata + GraphRecord(db, scope, rows, "manifest", ElementKey(manifest.ElementId), "", manifest));
        }
        long replayBytes = 0;
        foreach (var change in publication.EffectiveChanges.Changes)
        {
            if (change.Key.Scope != scope || change.Value is { } binding && binding.Key != change.Key)
                throw new InvalidDataException("Invalid effective object-binding change.");
            var key = RecordKey(change.Key);
            var existing = rows.Bindings[key];
            if (existing is not null && Decode<IndexedObjectBinding>(existing.Json).Key != change.Key)
                throw new StorageCorruptionException("A binding key hash collision was detected.");
            if (change.Value is { } value)
            {
                var json = Encode(value);
                metadata = checked(metadata + Encoding.UTF8.GetByteCount(json) - (existing is null ? 0 : Encoding.UTF8.GetByteCount(existing.Json)));
                if (existing is null)
                {
                    var added = new RepositoryBindingRow { ResourceId = scope.ResourceId, Lifecycle = scope.LifecycleGeneration,
                        Partition = scope.TargetPartition, BindingKey = key, Json = json };
                    db.Set<RepositoryBindingRow>().Add(added);
                    rows.Bindings[key] = added;
                }
                else existing.Json = json;
            }
            else if (existing is null) throw new InvalidDataException("An effective change removes an absent object binding.");
            else
            {
                metadata = checked(metadata - Encoding.UTF8.GetByteCount(existing.Json));
                db.Set<RepositoryBindingRow>().Remove(existing);
                rows.Bindings[key] = null;
            }
            var changeJson = Encode(change);
            replayBytes = checked(replayBytes + Encoding.UTF8.GetByteCount(changeJson));
            db.Set<RepositoryBindingChangeRow>().Add(new() { ResourceId = scope.ResourceId, Lifecycle = scope.LifecycleGeneration,
                Partition = scope.TargetPartition, Sequence = sequence, BindingKey = key, Json = changeJson });
        }
        metadata = checked(metadata + replayBytes);
        var needsCheckpoint = IndexedDocumentGraph.RequiresCheckpoint(new(partition.CheckpointSequence, partition.ReplayChanges, partition.ReplayBytes),
            publication.EffectiveChanges, checkpointPolicy);
        if (!initial && needsCheckpoint && publication.Checkpoint is null)
            throw new InvalidDataException("The replay bound requires a synchronously prepared checkpoint.");
        db.Set<RepositoryDeltaRow>().Add(new() { ResourceId = scope.ResourceId, Lifecycle = scope.LifecycleGeneration, Partition = scope.TargetPartition,
            Sequence = sequence, PreviousSequence = publication.EffectiveChanges.PreviousSequence, EncodedBytes = publication.EffectiveChanges.EncodedBytes,
            ChangeCount = publication.EffectiveChanges.Changes.Length });
        partition.Sequence = sequence; partition.Json = Encode(publication.After);
        partition.ReplayChanges = checked(partition.ReplayChanges + 1);
        partition.ReplayBytes = checked(partition.ReplayBytes + publication.EffectiveChanges.EncodedBytes);
        if (publication.Checkpoint is { } checkpoint)
        {
            if (checkpoint.Scope != scope || checkpoint.Sequence != sequence)
                throw new InvalidDataException("A prepared checkpoint belongs to another scope or publication.");
            db.Set<RepositoryCheckpointRow>().Add(new() { ResourceId = scope.ResourceId, Lifecycle = scope.LifecycleGeneration,
                Partition = scope.TargetPartition, Sequence = sequence, BindingKey = "", Json = "null" });
            metadata = checked(metadata + 4);
            foreach (var binding in checkpoint.Bindings)
            {
                var json = Encode(binding);
                db.Set<RepositoryCheckpointRow>().Add(new() { ResourceId = scope.ResourceId, Lifecycle = scope.LifecycleGeneration,
                    Partition = scope.TargetPartition, Sequence = sequence, BindingKey = RecordKey(binding.Key), Json = json });
                metadata = checked(metadata + Encoding.UTF8.GetByteCount(json));
            }
            partition.CheckpointSequence = sequence; partition.ReplayBytes = 0; partition.ReplayChanges = 0;
        }
        else if (initial) throw new InvalidDataException("An initial partition requires a prepared checkpoint.");
        return new(metadata, addedCount, addedBytes);
    }
}
