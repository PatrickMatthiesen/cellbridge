using System.Collections.Immutable;
using CellBridge.Storage.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace CellBridge.Storage.Shared;

internal sealed partial class RelationalDocumentRepository<TContext> where TContext : DbContext
{
    public ValueTask<ImmutableArray<ExtendedId>> ReadVisibleElementIdsAsync(IndexedGraphScope scope, long publicationSequence,
        IReadOnlyCollection<ExtendedId> elementIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(elementIds);
        ArgumentOutOfRangeException.ThrowIfNegative(publicationSequence);
        cancellationToken.ThrowIfCancellationRequested();
        if (elementIds.Count == 0) return ValueTask.FromResult(ImmutableArray<ExtendedId>.Empty);
        return ReadAsync(async db =>
        {
            var current = await db.Set<RepositoryPartitionRow>().AsNoTracking().Where(row => row.ResourceId == scope.ResourceId &&
                row.Lifecycle == scope.LifecycleGeneration && row.Partition == scope.TargetPartition).Select(row => (long?)row.Sequence).SingleOrDefaultAsync(cancellationToken);
            if (current is null || publicationSequence > current) return ImmutableArray<ExtendedId>.Empty;
            var result = ImmutableArray.CreateBuilder<ExtendedId>();
            foreach (var batch in elementIds.Distinct().Chunk(256))
            {
                var requested = batch.ToDictionary(ElementKey, StringComparer.Ordinal);
                if (current == publicationSequence)
                {
                    var keys = requested.Keys.ToArray();
                    var visible = await db.Set<RepositoryVisibilityRow>().AsNoTracking().Where(row => row.ResourceId == scope.ResourceId &&
                        row.Lifecycle == scope.LifecycleGeneration && row.Partition == scope.TargetPartition && keys.Contains(row.ElementKey) &&
                        row.StartSequence <= publicationSequence && row.EndSequence == null)
                        .Select(row => row.ElementKey).ToArrayAsync(cancellationToken);
                    if (visible.Distinct(StringComparer.Ordinal).Count() != visible.Length)
                        throw new StorageCorruptionException("An element has overlapping current visibility intervals.");
                    result.AddRange(visible.Select(key => requested[key]));
                }
                else
                    foreach (var pair in requested)
                    {
                        var interval = await db.Set<RepositoryVisibilityRow>().AsNoTracking().Where(row => row.ResourceId == scope.ResourceId &&
                            row.Lifecycle == scope.LifecycleGeneration && row.Partition == scope.TargetPartition && row.ElementKey == pair.Key &&
                            row.StartSequence <= publicationSequence).OrderByDescending(row => row.StartSequence)
                            .Select(row => new { row.EndSequence }).FirstOrDefaultAsync(cancellationToken);
                        if (interval is not null && (interval.EndSequence is null || interval.EndSequence > publicationSequence)) result.Add(pair.Value);
                    }
            }
            return result.ToImmutable();
        }, cancellationToken);
    }
    public ValueTask<ImmutableArray<GraphElementState>> ReadElementsAsync(IndexedGraphScope scope, IReadOnlyCollection<ExtendedId> ids,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        cancellationToken.ThrowIfCancellationRequested();
        if (ids.Count == 0) return ValueTask.FromResult(ImmutableArray<GraphElementState>.Empty);
        return ReadWithoutTransactionAsync(async db =>
        {
            var result = ImmutableArray.CreateBuilder<GraphElementState>();
            foreach (var batch in ids.Distinct().Chunk(256))
            {
                var keys = batch.Select(ElementKey).ToArray();
                var json = await db.Set<RepositoryElementRow>().AsNoTracking().Where(row => row.ResourceId == scope.ResourceId &&
                    row.Lifecycle == scope.LifecycleGeneration && row.Partition == scope.TargetPartition && keys.Contains(row.ElementKey))
                    .Select(row => row.Json).ToArrayAsync(cancellationToken);
                result.AddRange(json.Select(Decode<GraphElementState>));
            }
            return result.ToImmutable();
        }, cancellationToken);
    }
    public ValueTask<ImmutableArray<IndexedStorageMapping>> ReadMappingClaimsAsync(IndexedGraphScope scope, IReadOnlyCollection<SerialId> serials,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serials);
        cancellationToken.ThrowIfCancellationRequested();
        if (serials.Count == 0) return ValueTask.FromResult(ImmutableArray<IndexedStorageMapping>.Empty);
        return ReadWithoutTransactionAsync(async db =>
        {
            var result = ImmutableArray.CreateBuilder<IndexedStorageMapping>();
            foreach (var group in serials.Distinct().GroupBy(serial => serial.Guid))
                foreach (var batch in group.Chunk(256))
                {
                    var values = batch.Select(serial => (decimal?)serial.Value).ToArray();
                    var json = await db.Set<RepositoryMappingClaimRow>().AsNoTracking().Where(row => row.ResourceId == scope.ResourceId &&
                        row.Lifecycle == scope.LifecycleGeneration && row.Partition == scope.TargetPartition &&
                        row.SerialGuid == group.Key && values.Contains(row.SerialValue)).Select(row => row.Json).ToArrayAsync(cancellationToken);
                    result.AddRange(json.Select(Decode<IndexedStorageMapping>));
                }
            return result.ToImmutable();
        }, cancellationToken);
    }
}
