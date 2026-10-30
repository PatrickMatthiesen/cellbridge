using System.Collections.Immutable;
using CellBridge.Storage.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace CellBridge.Storage.Shared;

internal sealed partial class RelationalDocumentRepository<TContext> where TContext : DbContext
{
    private async Task<SaveGraphRead> ReadSaveGraphAsync(TContext db, IndexedGraphScope scope,
        ImmutableArray<IndexedObjectBinding> bindings, ImmutableArray<IndexedStorageMapping> mappings,
        SaveGraphReadRequest request, CancellationToken cancellationToken)
    {
        ExtendedId[] submittedIds = request.SubmittedElementIds.IsDefaultOrEmpty ? [] : request.SubmittedElementIds.Distinct().ToArray();
        SerialId[] mappingSerials = request.MappingSerials.IsDefaultOrEmpty ? [] : request.MappingSerials.Distinct().ToArray();
        var groupIds = bindings.Select(binding => binding.GroupElement).Distinct().ToArray();
        var currentIds = groupIds.Concat(mappings.Where(mapping => !IsNull(mapping.Target)).Select(mapping => mapping.Target))
            .Distinct().ToArray();
        var allIds = submittedIds.Concat(currentIds).Distinct().ToArray();
        var elements = new Dictionary<ExtendedId, GraphElementState>();
        foreach (var batch in allIds.Chunk(256))
        {
            var keys = batch.Select(ElementKey).ToArray();
            var rows = await db.Set<RepositoryElementRow>().AsNoTracking().Where(row => row.ResourceId == scope.ResourceId &&
                row.Lifecycle == scope.LifecycleGeneration && row.Partition == scope.TargetPartition && keys.Contains(row.ElementKey))
                .Select(row => new { row.ElementKey, row.Json }).ToArrayAsync(cancellationToken);
            foreach (var row in rows)
            {
                var element = Decode<GraphElementState>(row.Json);
                if (ElementKey(element.Id) != row.ElementKey || !elements.TryAdd(element.Id, element))
                    throw new StorageCorruptionException("An indexed graph element disagrees with its identity or is duplicated.");
            }
        }

        var claims = mappingSerials.Length == 0
            ? ImmutableArray<IndexedStorageMapping>.Empty
            : await MappingClaimsAsync(db, scope, mappingSerials, cancellationToken);
        var objects = ImmutableArray.CreateBuilder<IndexedObjectDefinition>();
        foreach (var batch in groupIds.Select(ElementKey).Distinct(StringComparer.Ordinal).Chunk(256))
        {
            var rows = await db.Set<RepositoryGraphRow>().AsNoTracking().Where(row => row.ResourceId == scope.ResourceId &&
                row.Lifecycle == scope.LifecycleGeneration && row.Partition == scope.TargetPartition && row.Kind == "object" &&
                batch.Contains(row.OwnerKey)).OrderBy(row => row.OwnerKey).ThenBy(row => row.RecordKey).ToArrayAsync(cancellationToken);
            foreach (var row in rows)
            {
                var definition = Decode<IndexedObjectDefinition>(row.Json);
                if (definition.Scope != scope || row.OwnerKey != ElementKey(definition.GroupElement) ||
                    row.RecordKey != definition.Ordinal.ToString("D10"))
                    throw new StorageCorruptionException("An indexed object definition disagrees with its owner or key.");
                objects.Add(definition);
            }
        }

        return new(
            submittedIds.Where(elements.ContainsKey).Select(id => elements[id]).ToImmutableArray(),
            claims,
            currentIds.Where(elements.ContainsKey).Select(id => elements[id]).ToImmutableArray(),
            objects.ToImmutable());
    }

    private static bool IsNull(ExtendedId id) => id.Guid == Guid.Empty && id.Value == 0;
}
