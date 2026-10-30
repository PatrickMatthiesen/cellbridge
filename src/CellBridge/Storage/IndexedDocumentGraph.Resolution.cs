using System.Collections.Immutable;
using CellBridge.FssHttpB;

namespace CellBridge.Storage.Abstractions;

public static partial class IndexedDocumentGraph
{
    /// <summary>
    /// Resolves an unpublished or historical protocol revision only through its explicit base chain
    /// in the selected index. Current application reads use persisted bindings instead.
    /// </summary>
    public static ValueTask<IndexedResolvedCell> ResolveCellAsync(IndexedGraphScope scope,
        ExtendedId storageIndex, IndexedCellId cell, ExtendedId revisionId, IIndexedGraphSource source,
        GenericGraphLimits? limits = null, CancellationToken cancellationToken = default) =>
        ResolveCellCoreAsync(scope, storageIndex, cell, revisionId, source, false, null, limits, cancellationToken);

    /// <summary>Preserves the existing file adapter's self-contained legacy revision behavior.</summary>
    public static ValueTask<IndexedResolvedCell> ResolveFileCellAsync(IndexedGraphScope scope,
        ExtendedId storageIndex, IndexedCellId cell, ExtendedId revisionId, IIndexedGraphSource source,
        GenericGraphLimits? limits = null, CancellationToken cancellationToken = default) =>
        ResolveCellCoreAsync(scope, storageIndex, cell, revisionId, source, true, null, limits, cancellationToken);

    /// <summary>Stops at the already resolved current base instead of traversing its retained ancestors again.</summary>
    public static ValueTask<IndexedResolvedCell> ResolveCellWithCurrentAsync(IndexedGraphScope scope,
        ExtendedId storageIndex, IndexedCellId cell, ExtendedId revisionId, IIndexedGraphSource source,
        IndexedResolvedCell currentBase, bool file, GenericGraphLimits? limits = null,
        CancellationToken cancellationToken = default) =>
        ResolveCellCoreAsync(scope, storageIndex, cell, revisionId, source, file, currentBase, limits, cancellationToken);

    private static async ValueTask<IndexedResolvedCell> ResolveCellCoreAsync(IndexedGraphScope scope,
        ExtendedId storageIndex, IndexedCellId cell, ExtendedId revisionId, IIndexedGraphSource source,
        bool allowSelfContainedFile, IndexedResolvedCell? currentBase, GenericGraphLimits? limits, CancellationToken cancellationToken)
    {
        limits ??= new();
        if (limits.MaxRevisionDepth <= 0 || limits.MaxObjectVisits <= 0 || limits.MaxReferenceVisits <= 0)
            throw new ArgumentOutOfRangeException(nameof(limits));
        var seen = new HashSet<ExtendedId>();
        var resolved = new Dictionary<IndexedBindingKey, IndexedObjectDefinition>();
        var groups = new Dictionary<ExtendedId, ImmutableArray<IndexedObjectDefinition>>();
        var roots = ImmutableArray<IndexedRevisionRoot>.Empty;
        var current = revisionId;
        int objectVisits = 0, referenceVisits = 0;
        while (!IsNull(current))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!seen.Add(current)) throw new InvalidDataException("Revision-base cycle.");
            if (seen.Count > limits.MaxRevisionDepth) throw new InvalidDataException("Revision depth budget exceeded.");
            VisitReference();
            var mapping = await source.FindMappingAsync(scope, storageIndex,
                new(IndexedMappingKind.Revision, Revision: current), cancellationToken);
            if (mapping is null || mapping.Scope != scope || mapping.StorageIndex != storageIndex ||
                mapping.Key != new IndexedMappingKey(IndexedMappingKind.Revision, Revision: current) || IsNull(mapping.Target))
                throw new InvalidDataException("Unmapped revision reference in the selected index.");
            var revision = await source.ReadRevisionAsync(scope, mapping.Target, cancellationToken);
            if (revision is null || revision.Scope != scope || revision.ElementId != mapping.Target || revision.RevisionId != current)
                throw new InvalidDataException("Revision mapping does not match its target revision.");
            if (seen.Count == 1) roots = revision.Roots;
            if (currentBase is not null && current == currentBase.Revision)
            {
                if (currentBase.Scope != scope || currentBase.Cell != cell)
                    throw new InvalidDataException("The resolved current base belongs to another scope.");
                foreach (var binding in currentBase.Bindings)
                {
                    if (++objectVisits > limits.MaxObjectVisits) throw new InvalidDataException("Object traversal budget exceeded.");
                    if (binding.Key.Scope != scope || binding.Key.Cell != cell)
                        throw new InvalidDataException("The resolved current binding belongs to another scope.");
                    if (!groups.TryGetValue(binding.GroupElement, out var objects))
                        groups.Add(binding.GroupElement, objects = await source.ReadGroupAsync(scope, binding.GroupElement, cancellationToken));
                    if (objects.IsDefault || binding.ObjectOrdinal < 0 || binding.ObjectOrdinal >= objects.Length)
                        throw new InvalidDataException("The resolved current binding has no declaring object.");
                    var definition = objects[binding.ObjectOrdinal];
                    if (definition.Scope != scope || definition.GroupElement != binding.GroupElement || definition.Ordinal != binding.ObjectOrdinal ||
                        definition.ObjectId != binding.Key.ObjectId || definition.ObjectPartitionId != binding.Key.ObjectPartitionId)
                        throw new InvalidDataException("The resolved current binding disagrees with its declaring object.");
                    resolved.TryAdd(binding.Key, definition);
                }
                break;
            }
            var local = new Dictionary<IndexedBindingKey, IndexedObjectDefinition>();
            foreach (var group in revision.ObjectGroups)
            {
                VisitReference();
                if (!groups.TryGetValue(group, out var objects))
                    groups.Add(group, objects = await source.ReadGroupAsync(scope, group, cancellationToken));
                if (objects.IsDefault) throw new InvalidDataException("Missing revision object group.");
                foreach (var obj in objects)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (++objectVisits > limits.MaxObjectVisits) throw new InvalidDataException("Object traversal budget exceeded.");
                    if (obj.Scope != scope || obj.GroupElement != group || obj.Ordinal < 0 || string.IsNullOrEmpty(obj.DefinitionDigest))
                        throw new InvalidDataException("Object metadata disagrees with its graph scope.");
                    var key = new IndexedBindingKey(scope, cell, obj.ObjectId, obj.ObjectPartitionId);
                    if (local.TryGetValue(key, out var prior) && !SameDefinition(prior, obj))
                        throw new InvalidDataException("Conflicting object-partition definitions in one revision.");
                    local[key] = obj;
                }
            }
            foreach (var (key, obj) in local) resolved.TryAdd(key, obj);
            // A fresh restore wrapper can inherit a complete legacy file from an ancestor.
            // Once every selected root/reference resolves from self-contained partition-1 data,
            // an unused older base is unnecessary, just as it is for a self-contained selected head.
            if (allowSelfContainedFile && resolved.Count != 0 &&
                resolved.Values.All(obj => obj.ObjectPartitionId == 1 && obj.BlobElement is null) &&
                roots.All(root => resolved.ContainsKey(new(scope, cell, root.Object, 1))) &&
                resolved.Values.All(obj => obj.ObjectReferences.All(reference => resolved.ContainsKey(new(scope, cell, reference, 1)))))
                break;
            current = revision.BaseRevisionId;
        }
        if (seen.Count == 0) throw new InvalidDataException("A cell requires a non-null revision.");
        var objectIds = resolved.Keys.Select(key => key.ObjectId).ToHashSet();
        foreach (var root in roots)
        {
            VisitReference();
            if (!objectIds.Contains(root.Object)) throw new InvalidDataException("Unresolved revision root object.");
        }
        var checkedCells = new HashSet<IndexedCellId>();
        foreach (var obj in resolved.Values)
        {
            foreach (var reference in obj.ObjectReferences)
            {
                VisitReference();
                if (!objectIds.Contains(reference)) throw new InvalidDataException("Unresolved object reference in its cell/revision scope.");
            }
            foreach (var reference in obj.CellReferences)
            {
                VisitReference();
                if (!checkedCells.Add(reference)) continue;
                var mapping = await source.FindMappingAsync(scope, storageIndex,
                    new(IndexedMappingKind.Cell, Cell: reference), cancellationToken);
                if (mapping is null || mapping.Scope != scope || mapping.StorageIndex != storageIndex ||
                    mapping.Key != new IndexedMappingKey(IndexedMappingKind.Cell, Cell: reference) || IsNull(mapping.Target))
                    throw new InvalidDataException("Unresolved object cell reference.");
            }
        }
        return new(scope, cell, revisionId, roots, resolved.Select(pair =>
            new IndexedObjectBinding(pair.Key, pair.Value.GroupElement, pair.Value.Ordinal)).ToImmutableArray());

        void VisitReference()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++referenceVisits > limits.MaxReferenceVisits) throw new InvalidDataException("Reference traversal budget exceeded.");
        }
    }

    private static bool SameDefinition(IndexedObjectDefinition left, IndexedObjectDefinition right) =>
        left.ObjectId == right.ObjectId && left.ObjectPartitionId == right.ObjectPartitionId &&
        left.BlobElement == right.BlobElement && left.DefinitionDigest == right.DefinitionDigest &&
        left.ObjectReferences.AsSpan().SequenceEqual(right.ObjectReferences.AsSpan()) &&
        left.CellReferences.AsSpan().SequenceEqual(right.CellReferences.AsSpan());
}
