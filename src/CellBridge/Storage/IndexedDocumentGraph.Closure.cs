using System.Collections.Immutable;
using CellBridge.FssHttpB;

namespace CellBridge.Storage.Abstractions;

public static partial class IndexedDocumentGraph
{
    /// <summary>
    /// Query-time cell closure, matching GenericPartitionGraphSnapshot.GetRequiredElements.
    /// Reads wrapper payloads and indexed object metadata; never loads group or BLOB payloads.
    /// The returned logical index ID is synthesized by the query caller.
    /// </summary>
    public static async ValueTask<ImmutableArray<ExtendedId>> RequiredElementsAsync(IndexedGraphScope scope,
        ExtendedId storageIndex, IndexedResolvedCell selected, IIndexedGraphSource source,
        Func<IndexedGraphScope, ExtendedId, CancellationToken, ValueTask<DataElement>> readElement,
        GenericGraphLimits? limits = null, CancellationToken cancellationToken = default)
    {
        limits ??= new();
        if (selected.Scope != scope) throw new InvalidDataException("The selected cell belongs to another graph.");
        var required = new HashSet<ExtendedId> { storageIndex };
        var manifest = await Mapping(new(IndexedMappingKind.Manifest));
        required.Add(manifest.Target);
        var cells = new Dictionary<IndexedCellId, IndexedResolvedCell> { [selected.Cell] = selected };
        var visited = new HashSet<IndexedCellId>();
        var pending = new Queue<IndexedCellId>();
        pending.Enqueue(selected.Cell);
        var groups = new Dictionary<ExtendedId, ImmutableArray<IndexedObjectDefinition>>();
        int visits = 0;
        while (pending.TryDequeue(out var cell))
        {
            Visit();
            if (!visited.Add(cell)) continue;
            var mappedCell = await Mapping(new(IndexedMappingKind.Cell, Cell: cell));
            required.Add(mappedCell.Target);
            var revisionId = Capture(IndexedGraphRecords.ReadCellRevision(await readElement(scope, mappedCell.Target, cancellationToken)));
            if (!cells.TryGetValue(cell, out var resolved))
                cells.Add(cell, resolved = scope.TargetPartition == 0
                    ? await ResolveFileCellAsync(scope, storageIndex, cell, revisionId, source, limits, cancellationToken)
                    : await ResolveCellAsync(scope, storageIndex, cell, revisionId, source, limits, cancellationToken));
            if (resolved.Revision != revisionId) throw new InvalidDataException("The selected cell belongs to another publication.");
            var seen = new HashSet<ExtendedId>();
            var revision = revisionId;
            while (!IsNull(revision))
            {
                Visit();
                if (!seen.Add(revision) || seen.Count > limits.MaxRevisionDepth)
                    throw new InvalidDataException("Invalid revision chain in scoped query.");
                var mapped = await source.FindMappingAsync(scope, storageIndex, new(IndexedMappingKind.Revision, Revision: revision), cancellationToken);
                // Existing file profile allows a self-contained revision with an unused unmapped base.
                if (mapped is null && scope.TargetPartition == 0 && seen.Count > 1) break;
                if (mapped is null || mapped.Scope != scope || mapped.StorageIndex != storageIndex || IsNull(mapped.Target))
                    throw new InvalidDataException("Unmapped query revision.");
                var record = await source.ReadRevisionAsync(scope, mapped.Target, cancellationToken);
                if (record is null || record.Scope != scope || record.ElementId != mapped.Target || record.RevisionId != revision)
                    throw new InvalidDataException("Invalid query revision metadata.");
                required.Add(record.ElementId);
                foreach (var group in record.ObjectGroups)
                {
                    Visit();
                    required.Add(group);
                    foreach (var obj in await Group(group))
                    {
                        Visit();
                        if (obj.BlobElement is { } blob) required.Add(blob);
                    }
                }
                revision = record.BaseRevisionId;
            }
            foreach (var binding in resolved.Bindings)
            {
                Visit();
                var objects = await Group(binding.GroupElement);
                if (binding.Key.Scope != scope || binding.Key.Cell != cell || binding.ObjectOrdinal < 0 || binding.ObjectOrdinal >= objects.Length)
                    throw new InvalidDataException("Invalid query binding.");
                var obj = objects[binding.ObjectOrdinal];
                if (obj.ObjectId != binding.Key.ObjectId || obj.ObjectPartitionId != binding.Key.ObjectPartitionId)
                    throw new InvalidDataException("Query binding disagrees with its declaration.");
                foreach (var reference in obj.CellReferences) { Visit(); pending.Enqueue(reference); }
            }
        }
        return required.ToImmutableArray();

        void Visit()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++visits > limits.MaxReferenceVisits) throw new InvalidDataException("Query closure budget exceeded.");
        }
        async ValueTask<IndexedStorageMapping> Mapping(IndexedMappingKey key)
        {
            var mapping = await source.FindMappingAsync(scope, storageIndex, key, cancellationToken);
            if (mapping is null || mapping.Scope != scope || mapping.StorageIndex != storageIndex || mapping.Key != key || IsNull(mapping.Target))
                throw new InvalidDataException("Missing query mapping.");
            return mapping;
        }
        async ValueTask<ImmutableArray<IndexedObjectDefinition>> Group(ExtendedId group)
        {
            if (!groups.TryGetValue(group, out var objects))
                groups.Add(group, objects = await source.ReadGroupAsync(scope, group, cancellationToken));
            if (objects.IsDefault || objects.Any(obj => obj.Scope != scope || obj.GroupElement != group))
                throw new InvalidDataException("Missing query group metadata.");
            return objects;
        }
    }
}
