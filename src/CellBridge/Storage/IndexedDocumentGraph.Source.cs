using System.Collections.Immutable;

namespace CellBridge.Storage.Abstractions;

/// <summary>New immutable metadata over an indexed source; no aggregate or payload catalog.</summary>
public sealed class IndexedGraphOverlaySource : IIndexedGraphSource
{
    private readonly IIndexedGraphSource? fallback;
    private readonly Dictionary<(IndexedGraphScope, ExtendedId, IndexedMappingKey), IndexedStorageMapping> mappings = [];
    private readonly Dictionary<(IndexedGraphScope, ExtendedId), IndexedRevisionManifest> revisions = [];
    private readonly Dictionary<(IndexedGraphScope, ExtendedId), ImmutableArray<IndexedObjectDefinition>> groups = [];

    public IndexedGraphOverlaySource(IndexedGraphScope scope, IEnumerable<IndexedElementMetadata> records,
        IIndexedGraphSource? fallback = null)
    {
        this.fallback = fallback;
        foreach (var record in records)
        {
            foreach (var mapping in record.Mappings) mappings.Add((scope, mapping.StorageIndex, mapping.Key), mapping);
            if (record.Revision is { } revision) revisions.Add((scope, revision.ElementId), revision);
            if (record.ObjectGroupElement is { } group) groups.Add((scope, group), record.Objects);
        }
    }

    public ValueTask<IndexedStorageMapping?> FindMappingAsync(IndexedGraphScope scope, ExtendedId storageIndex,
        IndexedMappingKey key, CancellationToken cancellationToken = default) => mappings.TryGetValue((scope, storageIndex, key), out var mapping)
        ? ValueTask.FromResult<IndexedStorageMapping?>(mapping)
        : fallback?.FindMappingAsync(scope, storageIndex, key, cancellationToken) ?? ValueTask.FromResult<IndexedStorageMapping?>(null);
    public ValueTask<IndexedRevisionManifest?> ReadRevisionAsync(IndexedGraphScope scope, ExtendedId manifest,
        CancellationToken cancellationToken = default) => revisions.TryGetValue((scope, manifest), out var revision)
        ? ValueTask.FromResult<IndexedRevisionManifest?>(revision)
        : fallback?.ReadRevisionAsync(scope, manifest, cancellationToken) ?? ValueTask.FromResult<IndexedRevisionManifest?>(null);
    public ValueTask<ImmutableArray<IndexedObjectDefinition>> ReadGroupAsync(IndexedGraphScope scope, ExtendedId group,
        CancellationToken cancellationToken = default) => groups.TryGetValue((scope, group), out var objects)
        ? ValueTask.FromResult(objects)
        : fallback?.ReadGroupAsync(scope, group, cancellationToken) ?? ValueTask.FromResult(default(ImmutableArray<IndexedObjectDefinition>));
}
