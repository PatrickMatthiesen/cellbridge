using System.Collections.Immutable;
using System.Text;
using CellBridge.Storage.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace CellBridge.Storage.Shared;

internal sealed partial class RelationalDocumentRepository<TContext> where TContext : DbContext
{
    private static PartitionHead ReadMappingRoot(IndexedGraphScope scope, ExtendedId index, PartitionIdentity identity, RepositoryMappingRootRow row)
    {
        var root = Decode<PartitionHead>(row.Json);
        if (row.ResourceId != scope.ResourceId || row.Lifecycle != scope.LifecycleGeneration || row.Partition != scope.TargetPartition ||
            row.IndexKey != ElementKey(index) || row.SerialValue < 0 || row.SerialValue > ulong.MaxValue ||
            root.Kind != scope.TargetPartition || root.StorageIndex != index || root.StorageIndexSerial != new SerialId(row.SerialGuid, (ulong)row.SerialValue) ||
            root.PublicationSequence != row.Sequence || root.Identity != identity)
            throw new StorageCorruptionException("An immutable logical root header disagrees with its indexed identity.");
        return root;
    }
    private static async Task<Dictionary<IndexedMappingKey, IndexedStorageMapping?>> CurrentMappingsAsync(TContext db, IndexedGraphScope scope,
        PartitionHead partition, IEnumerable<IndexedMappingKey> requested, CancellationToken ct)
    {
        var result = requested.Distinct().ToDictionary(key => key, _ => (IndexedStorageMapping?)null);
        if (result.Count == 0 || partition.StorageIndex is not { } index) return result;
        var indexKey = ElementKey(index);
        var row = await db.Set<RepositoryMappingRootRow>().AsNoTracking().SingleOrDefaultAsync(row => row.ResourceId == scope.ResourceId &&
            row.Lifecycle == scope.LifecycleGeneration && row.Partition == scope.TargetPartition && row.IndexKey == indexKey, ct)
            ?? throw new StorageCorruptionException("The current logical mapping root is absent.");
        var root = ReadMappingRoot(scope, index, partition.Identity, row);
        if (root.PublicationSequence != partition.PublicationSequence || root.StorageIndexSerial != partition.StorageIndexSerial)
            throw new StorageCorruptionException("The current partition disagrees with its logical mapping root.");
        var keys = new Dictionary<string, IndexedMappingKey>(StringComparer.Ordinal);
        foreach (var key in result.Keys)
            if (!keys.TryAdd(RecordKey(key), key)) throw new StorageCorruptionException("A mapping key hash collision was detected.");
        // Current rows are the authoritative hot projection, maintained atomically
        // with their intervals under the document guard. Offline recovery audits
        // projection completeness against history; ordinary reads do not scan it.
        foreach (var batch in keys.Keys.Chunk(256))
        {
            var mappings = await db.Set<RepositoryMappingRow>().AsNoTracking().Where(row => row.ResourceId == scope.ResourceId &&
                row.Lifecycle == scope.LifecycleGeneration && row.Partition == scope.TargetPartition && batch.Contains(row.MappingKey)).ToArrayAsync(ct);
            foreach (var mappingRow in mappings)
            {
                var mapping = Decode<IndexedStorageMapping>(mappingRow.Json);
                if (mappingRow.ResourceId != scope.ResourceId || mappingRow.Lifecycle != scope.LifecycleGeneration || mappingRow.Partition != scope.TargetPartition ||
                    !keys.TryGetValue(mappingRow.MappingKey, out var key) || mapping.Key != key || mapping.Scope != scope ||
                    RecordKey(mapping.Key) != mappingRow.MappingKey || mappingRow.StartSequence < 0 || mappingRow.StartSequence > partition.PublicationSequence)
                    throw new StorageCorruptionException("A current mapping disagrees with its indexed key or scope.");
                result[key] = mapping with { StorageIndex = index };
            }
        }
        return result;
    }
    private static async Task<IndexedStorageMapping?> MappingAsync(TContext db, IndexedGraphScope scope, ExtendedId index, IndexedMappingKey key, CancellationToken ct)
    {
        var root = await db.Set<RepositoryMappingRootRow>().AsNoTracking().SingleOrDefaultAsync(row => row.ResourceId == scope.ResourceId &&
            row.Lifecycle == scope.LifecycleGeneration && row.Partition == scope.TargetPartition && row.IndexKey == ElementKey(index), ct);
        if (root is null) return null;
        var hash = RecordKey(key);
        var interval = await db.Set<RepositoryMappingIntervalRow>().AsNoTracking().Where(row => row.ResourceId == scope.ResourceId &&
            row.Lifecycle == scope.LifecycleGeneration && row.Partition == scope.TargetPartition && row.MappingKey == hash && row.StartSequence <= root.Sequence)
            .OrderByDescending(row => row.StartSequence).FirstOrDefaultAsync(ct);
        if (interval is null || interval.EndSequence is { } end && end <= root.Sequence) return null;
        var mapping = Decode<IndexedStorageMapping>(interval.Json);
        if (mapping.Key != key || mapping.Scope != scope) throw new StorageCorruptionException("A mapping key hash collision was detected.");
        return mapping with { StorageIndex = index };
    }
    private static async Task<ImmutableArray<IndexedStorageMapping>> MappingsAsync(TContext db, IndexedGraphScope scope, long sequence,
        CancellationToken ct, long? knownCurrentSequence = null)
    {
        var current = knownCurrentSequence ?? await db.Set<RepositoryPartitionRow>().AsNoTracking().Where(row => row.ResourceId == scope.ResourceId &&
            row.Lifecycle == scope.LifecycleGeneration && row.Partition == scope.TargetPartition).Select(row => (long?)row.Sequence).SingleOrDefaultAsync(ct);
        var json = current == sequence
            ? await db.Set<RepositoryMappingRow>().AsNoTracking().Where(row => row.ResourceId == scope.ResourceId && row.Lifecycle == scope.LifecycleGeneration &&
                row.Partition == scope.TargetPartition).Select(row => row.Json).ToArrayAsync(ct)
            : await db.Set<RepositoryMappingIntervalRow>().AsNoTracking().Where(row => row.ResourceId == scope.ResourceId && row.Lifecycle == scope.LifecycleGeneration &&
                row.Partition == scope.TargetPartition && row.StartSequence <= sequence && (row.EndSequence == null || row.EndSequence > sequence)).Select(row => row.Json).ToArrayAsync(ct);
        return json.Select(Decode<IndexedStorageMapping>).ToImmutableArray();
    }
    public ValueTask<ImmutableArray<IndexedStorageMapping>> ReadMappingsAsync(IndexedGraphScope scope, long sequence, CancellationToken cancellationToken = default) =>
        ReadAsync(db => MappingsAsync(db, scope, sequence, cancellationToken), cancellationToken);
    private static long RegisterMappingClaims(TContext db, IndexedGraphScope scope, IEnumerable<IndexedStorageMapping> mappings, PartitionWriteRows rows)
    {
        long metadata = 0;
        foreach (var value in mappings)
        {
            if (value.Scope != scope) throw new InvalidDataException("A serial claim crosses its graph scope.");
            if (value.Serial.Guid == Guid.Empty && value.Serial.Value == 0) continue;
            var key = new ClaimWriteKey(value.Serial.Guid, value.Serial.Value);
            var claim = rows.Claims[key];
            if (claim is not null)
            {
                var accepted = Decode<IndexedStorageMapping>(claim.Json);
                if (accepted.Key != value.Key || accepted.Target != value.Target)
                    throw new InvalidDataException("A mapping serial was reused for a different key or target.");
                continue;
            }
            var json = Encode(value);
            var added = new RepositoryMappingClaimRow { ResourceId = scope.ResourceId, Lifecycle = scope.LifecycleGeneration,
                Partition = scope.TargetPartition, SerialGuid = value.Serial.Guid, SerialValue = value.Serial.Value, Json = json };
            db.Set<RepositoryMappingClaimRow>().Add(added);
            rows.Claims[key] = added;
            metadata = checked(metadata + Encoding.UTF8.GetByteCount(json));
        }
        return metadata;
    }
    private static async Task<long> WriteMappingChangesAsync(TContext db, IndexedGraphScope scope, PreparedPartitionPublication publication, PartitionWriteRows rows, CancellationToken ct)
    {
        long metadata = 0;
        var sequence = publication.After.PublicationSequence;
        foreach (var change in publication.MappingChanges)
        {
            var key = RecordKey(change.Key);
            var current = await db.Set<RepositoryMappingRow>().FindAsync([scope.ResourceId, scope.LifecycleGeneration, scope.TargetPartition, key], ct);
            var interval = current is null ? null : await db.Set<RepositoryMappingIntervalRow>().FindAsync(
                [scope.ResourceId, scope.LifecycleGeneration, scope.TargetPartition, key, current.StartSequence], ct);
            if ((current is null) != (interval is null)) throw new StorageCorruptionException("Current mapping and visibility interval disagree.");
            if (change.Value is { } changed)
            {
                var value = new IndexedStorageMapping(scope, publication.After.StorageIndex
                    ?? throw new InvalidDataException("A mapping publication requires a logical root."), change.Key, changed.Target, changed.Serial);
                var expectedType = change.Key.Kind switch
                {
                    IndexedMappingKind.Manifest => CellBridge.FssHttpB.DataElementType.StorageManifestDataElementData,
                    IndexedMappingKind.Cell => CellBridge.FssHttpB.DataElementType.CellManifestDataElementData,
                    IndexedMappingKind.Revision => CellBridge.FssHttpB.DataElementType.RevisionManifestDataElementData,
                    _ => throw new InvalidDataException("Unknown storage-index mapping kind."),
                };
                if (value.Target != new ExtendedId(0, Guid.Empty))
                {
                    var target = rows.Elements[ElementKey(value.Target)]
                        ?? throw new InvalidDataException("A mapping update references an absent immutable target.");
                    if (Decode<GraphElementState>(target.Json).Type != (uint)expectedType)
                        throw new InvalidDataException("A storage-index mapping target has the wrong immutable element type.");
                }
                var json = Encode(value);
                if (current is null)
                {
                    db.Set<RepositoryMappingRow>().Add(new() { ResourceId = scope.ResourceId, Lifecycle = scope.LifecycleGeneration,
                        Partition = scope.TargetPartition, MappingKey = key, StartSequence = sequence, Json = json });
                    metadata = checked(metadata + Encoding.UTF8.GetByteCount(json));
                }
                else
                {
                    var before = Decode<IndexedStorageMapping>(current.Json);
                    if (before.Key != change.Key) throw new StorageCorruptionException("A mapping key hash collision was detected.");
                    if (before.Target == value.Target && before.Serial == value.Serial) continue;
                    metadata = checked(metadata + Encoding.UTF8.GetByteCount(json) - Encoding.UTF8.GetByteCount(current.Json));
                    current.Json = json; current.StartSequence = sequence;
                }
                if (interval is not null) interval.EndSequence = sequence;
                db.Set<RepositoryMappingIntervalRow>().Add(new() { ResourceId = scope.ResourceId, Lifecycle = scope.LifecycleGeneration,
                    Partition = scope.TargetPartition, MappingKey = key, StartSequence = sequence, Json = json });
                metadata = checked(metadata + Encoding.UTF8.GetByteCount(json));
            }
            else
            {
                if (current is null && interval is null) continue;
                if (current is null || interval is null) throw new StorageCorruptionException("Current mapping and interval disagree.");
                metadata = checked(metadata - Encoding.UTF8.GetByteCount(current.Json));
                db.Set<RepositoryMappingRow>().Remove(current); interval.EndSequence = sequence;
            }
        }
        if (publication.After.StorageIndex is { } rootId)
        {
            var key = ElementKey(rootId);
            var root = await db.Set<RepositoryMappingRootRow>().FindAsync([scope.ResourceId, scope.LifecycleGeneration, scope.TargetPartition, key], ct);
            if (root is null)
            {
                var headerJson = Encode(publication.After);
                metadata = checked(metadata + System.Text.Encoding.UTF8.GetByteCount(headerJson));
                db.Set<RepositoryMappingRootRow>().Add(new() { ResourceId = scope.ResourceId, Lifecycle = scope.LifecycleGeneration,
                Partition = scope.TargetPartition, IndexKey = key, Sequence = sequence,
                SerialGuid = publication.After.StorageIndexSerial?.Guid ?? throw new InvalidDataException("A logical index requires its serial."),
                SerialValue = publication.After.StorageIndexSerial.Value, Json = headerJson });
            }
            else if (root.Sequence != sequence)
                throw new InvalidDataException("A logical mapping root was reused for another publication sequence.");
        }
        return metadata;
    }
}
