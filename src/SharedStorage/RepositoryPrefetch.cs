using System.Linq.Expressions;
using CellBridge.Storage.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace CellBridge.Storage.Shared;

internal sealed partial class RelationalDocumentRepository<TContext> where TContext : DbContext
{
    private const int PublicationReadBatchSize = 256;
    private readonly record struct GraphWriteKey(string Kind, string Owner, string Record);
    private readonly record struct ClaimWriteKey(Guid Guid, decimal Value);

    // Owned by one guarded transaction. Null entries are known misses; EF's FindAsync
    // does not cache misses. Insertions must update these dictionaries before later checks.
    private sealed record PartitionWriteRows(
        Dictionary<string, RepositoryElementRow?> Elements,
        Dictionary<string, RepositoryVisibilityRow?> Visibility,
        Dictionary<GraphWriteKey, RepositoryGraphRow?> Graph,
        Dictionary<string, RepositoryBindingRow?> Bindings,
        Dictionary<ClaimWriteKey, RepositoryMappingClaimRow?> Claims);

    private static async Task<Dictionary<TKey, TRow?>> PrefetchAsync<TRow, TKey>(TContext db,
        IQueryable<TRow> scoped, IEnumerable<TKey> requested, Func<TKey, Expression<Func<TRow, bool>>> predicate,
        Func<TRow, TKey> keyOf, Func<TRow, bool> inScope, string category, CancellationToken ct, bool includeTracked = true)
        where TRow : class where TKey : notnull
    {
        var keys = requested.Distinct().ToArray();
        var result = keys.ToDictionary(key => key, _ => (TRow?)null);
        foreach (var batch in keys.Chunk(PublicationReadBatchSize))
        {
            ct.ThrowIfCancellationRequested();
            var rows = await scoped.Where(AnyKey(batch.Select(predicate).ToArray()))
                .TagWith("CellBridge publication prefetch: " + category).ToArrayAsync(ct);
            foreach (var row in rows)
            {
                if (!inScope(row)) continue;
                var key = keyOf(row);
                if (result[key] is not null) throw new StorageCorruptionException("Duplicate current publication lookup key.");
                result[key] = row;
            }
        }
        if (keys.Length == 0 || !includeTracked) return result;
        // Earlier helpers in this transaction may already have staged an insert/update.
        // Local excludes deleted rows; the normal publication never reuses a deleted key.
        foreach (var row in db.Set<TRow>().Local)
            if (inScope(row) && result.ContainsKey(keyOf(row))) result[keyOf(row)] = row;
        return result;
    }

    private static Expression<Func<TRow, bool>> AnyKey<TRow>(Expression<Func<TRow, bool>>[] predicates)
    {
        var parameter = Expression.Parameter(typeof(TRow), "row");
        var bodies = predicates.Select(predicate => new ReplaceParameter(predicate.Parameters[0], parameter).Visit(predicate.Body)!).ToArray();
        return Expression.Lambda<Func<TRow, bool>>(Join(0, bodies.Length), parameter);
        Expression Join(int offset, int count) => count == 1 ? bodies[offset]
            : Expression.OrElse(Join(offset, count / 2), Join(offset + count / 2, count - count / 2));
    }

    private sealed class ReplaceParameter(ParameterExpression original, ParameterExpression replacement) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == original ? replacement : base.VisitParameter(node);
    }

    private static async Task<PartitionWriteRows> PrefetchPartitionAsync(TContext db, IndexedGraphScope scope,
        PreparedPartitionPublication publication, CancellationToken ct)
    {
        var elementKeys = publication.AddedElements.Select(element => ElementKey(element.Id))
            .Concat(publication.RetiredElements.Select(ElementKey))
            .Concat(publication.MappingChanges.Where(change => change.Value is { Target: var target } && target != new ExtendedId(0, Guid.Empty))
                .Select(change => ElementKey(change.Value!.Target)));
        var elements = await PrefetchAsync(db, db.Set<RepositoryElementRow>().Where(row => row.ResourceId == scope.ResourceId &&
            row.Lifecycle == scope.LifecycleGeneration && row.Partition == scope.TargetPartition), elementKeys,
            key => row => row.ElementKey == key, row => row.ElementKey,
            row => row.ResourceId == scope.ResourceId && row.Lifecycle == scope.LifecycleGeneration && row.Partition == scope.TargetPartition,
            "elements", ct);
        var visibility = await PrefetchAsync(db, db.Set<RepositoryVisibilityRow>().Where(row => row.ResourceId == scope.ResourceId &&
            row.Lifecycle == scope.LifecycleGeneration && row.Partition == scope.TargetPartition && row.EndSequence == null),
            publication.AddedElements.Select(element => ElementKey(element.Id)).Concat(publication.RetiredElements.Select(ElementKey)),
            key => row => row.ElementKey == key, row => row.ElementKey,
            row => row.ResourceId == scope.ResourceId && row.Lifecycle == scope.LifecycleGeneration && row.Partition == scope.TargetPartition && row.EndSequence == null,
            "visibility", ct);
        var graphKeys = publication.Revisions.Select(value => new GraphWriteKey("revision", ElementKey(value.ElementId), ""))
            .Concat(publication.Objects.Select(value => new GraphWriteKey("object", ElementKey(value.GroupElement), value.Ordinal.ToString("D10"))))
            .Concat(publication.Cells.Select(value => new GraphWriteKey("cell", ElementKey(value.ElementId), "")))
            .Concat(publication.StorageManifests.Select(value => new GraphWriteKey("manifest", ElementKey(value.ElementId), "")));
        var graph = await PrefetchAsync(db, db.Set<RepositoryGraphRow>().Where(row => row.ResourceId == scope.ResourceId &&
            row.Lifecycle == scope.LifecycleGeneration && row.Partition == scope.TargetPartition), graphKeys,
            key => row => row.Kind == key.Kind && row.OwnerKey == key.Owner && row.RecordKey == key.Record,
            row => new GraphWriteKey(row.Kind, row.OwnerKey, row.RecordKey),
            row => row.ResourceId == scope.ResourceId && row.Lifecycle == scope.LifecycleGeneration && row.Partition == scope.TargetPartition,
            "graph", ct);
        var bindings = await PrefetchAsync(db, db.Set<RepositoryBindingRow>().Where(row => row.ResourceId == scope.ResourceId &&
            row.Lifecycle == scope.LifecycleGeneration && row.Partition == scope.TargetPartition),
            publication.EffectiveChanges.Changes.Select(change => RecordKey(change.Key)), key => row => row.BindingKey == key, row => row.BindingKey,
            row => row.ResourceId == scope.ResourceId && row.Lifecycle == scope.LifecycleGeneration && row.Partition == scope.TargetPartition,
            "bindings", ct);
        var claims = await PrefetchAsync(db, db.Set<RepositoryMappingClaimRow>().Where(row => row.ResourceId == scope.ResourceId &&
            row.Lifecycle == scope.LifecycleGeneration && row.Partition == scope.TargetPartition),
            publication.Mappings.Where(value => value.Serial.Guid != Guid.Empty || value.Serial.Value != 0)
                .Select(value => new ClaimWriteKey(value.Serial.Guid, value.Serial.Value)),
            key => row => row.SerialGuid == key.Guid && row.SerialValue == key.Value, row => new ClaimWriteKey(row.SerialGuid, row.SerialValue),
            row => row.ResourceId == scope.ResourceId && row.Lifecycle == scope.LifecycleGeneration && row.Partition == scope.TargetPartition,
            "claims", ct);
        return new(elements, visibility, graph, bindings, claims);
    }
}
