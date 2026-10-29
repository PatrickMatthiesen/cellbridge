using System.Collections.Immutable;
using CellBridge.FssHttpB;

namespace CellBridge.Storage.Abstractions;

/// <summary>Offline graph access for a complete normalized recovery snapshot. Ordinary requests use repository indexes.</summary>
public sealed class NormalizedSnapshotGraphSource : IIndexedGraphSource
{
    private readonly NormalizedDocumentSnapshot snapshot;
    private readonly IContentStore content;
    private readonly StorageLimits limits;
    private readonly Dictionary<(IndexedGraphScope, ExtendedId), GraphElementState> elements;
    private readonly Dictionary<(IndexedGraphScope, ExtendedId), IndexedRevisionManifest> revisions;
    private readonly Dictionary<(IndexedGraphScope, ExtendedId), ImmutableArray<IndexedObjectDefinition>> groups;
    private readonly Dictionary<(IndexedGraphScope, ExtendedId), NormalizedMappingRoot> roots;
    private readonly Dictionary<(IndexedGraphScope, IndexedMappingKey), NormalizedMappingInterval[]> intervals;

    public NormalizedSnapshotGraphSource(NormalizedDocumentSnapshot snapshot, IContentStore content, StorageLimits limits)
    {
        this.snapshot = snapshot; this.content = content; this.limits = limits;
        elements = snapshot.Elements.ToDictionary(value => (value.Scope, value.Element.Id), value => value.Element);
        revisions = snapshot.GraphRevisions.ToDictionary(value => (value.Scope, value.ElementId));
        groups = snapshot.Objects.GroupBy(value => (value.Scope, value.GroupElement))
            .ToDictionary(group => group.Key, group => group.OrderBy(value => value.Ordinal).ToImmutableArray());
        roots = snapshot.MappingRoots.ToDictionary(value => (value.Scope, value.Id));
        intervals = snapshot.MappingIntervals.GroupBy(value => (value.Mapping.Scope, value.Mapping.Key))
            .ToDictionary(group => group.Key, group => group.OrderBy(value => value.StartSequence).ToArray());
    }

    public ImmutableArray<IndexedStorageMapping> Mappings(IndexedGraphScope scope, ExtendedId index)
    {
        var root = Root(scope, index);
        return intervals.Where(pair => pair.Key.Item1 == scope).Select(pair => Selected(pair.Value, root.Sequence))
            .Where(value => value is not null).Select(value => value!.Mapping with { StorageIndex = index }).ToImmutableArray();
    }

    public ImmutableArray<IndexedObjectBinding> Bindings(IndexedGraphScope scope, long sequence)
    {
        var checkpoint = snapshot.Checkpoints.Where(value => value.Scope == scope && value.Sequence <= sequence)
            .OrderByDescending(value => value.Sequence).FirstOrDefault()
            ?? throw new StorageCorruptionException("The selected recovery graph has no checkpoint.");
        return IndexedDocumentGraph.Replay(checkpoint, snapshot.BindingDeltas.Where(value => value.Scope == scope &&
            value.Sequence > checkpoint.Sequence && value.Sequence <= sequence).OrderBy(value => value.Sequence), sequence, IndexedCheckpointPolicy.Default);
    }

    public IDocumentPackageReader PackageReader() => new GraphDocumentPackageReader((package, ct) =>
    {
        ct.ThrowIfCancellationRequested();
        var revision = snapshot.ApplicationRevisions.SingleOrDefault(value => value.RevisionNumber == package.RevisionNumber);
        if (revision?.Package != package || revision.Partitions.SingleOrDefault(value => value.Kind == 0)?.StorageIndex != package.Root.StorageIndex)
            throw new StorageCorruptionException("The package does not match its immutable recovery revision.");
        return ValueTask.FromResult<IReadOnlyList<IndexedObjectBinding>>(Bindings(package.Root.Scope, Root(package.Root.Scope, package.Root.StorageIndex).Sequence));
    }, ReadElementAsync, limits.MaxDocumentBytes);

    public async ValueTask<DataElement> ReadElementAsync(IndexedGraphScope scope, ExtendedId id, CancellationToken cancellationToken = default)
    {
        if (!elements.TryGetValue((scope, id), out var value)) throw new StorageCorruptionException("Missing immutable recovery element.");
        return new((DataElementType)value.Type, IndexedDocumentGraph.Restore(id), new(value.Serial.Guid, value.Serial.Value))
            { Data = await content.ReadVerifiedAsync(value.Payload, limits.MaxObjectBytes, cancellationToken) };
    }

    public ValueTask<IndexedStorageMapping?> FindMappingAsync(IndexedGraphScope scope, ExtendedId storageIndex,
        IndexedMappingKey key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var root = Root(scope, storageIndex);
        var selected = intervals.TryGetValue((scope, key), out var values) ? Selected(values, root.Sequence) : null;
        return ValueTask.FromResult<IndexedStorageMapping?>(selected is null ? null : selected.Mapping with { StorageIndex = storageIndex });
    }

    public ValueTask<IndexedRevisionManifest?> ReadRevisionAsync(IndexedGraphScope scope, ExtendedId manifest,
        CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(revisions.GetValueOrDefault((scope, manifest))); }

    public ValueTask<ImmutableArray<IndexedObjectDefinition>> ReadGroupAsync(IndexedGraphScope scope, ExtendedId group,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(elements.TryGetValue((scope, group), out var element) &&
            element.Type == (uint)DataElementType.ObjectGroupDataElementData ? groups.GetValueOrDefault((scope, group), []) : default);
    }

    private NormalizedMappingRoot Root(IndexedGraphScope scope, ExtendedId index) => roots.GetValueOrDefault((scope, index))
        ?? throw new StorageCorruptionException("Missing logical recovery mapping root.");

    private static NormalizedMappingInterval? Selected(NormalizedMappingInterval[] values, long sequence)
    {
        int low = 0, high = values.Length;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (values[middle].StartSequence <= sequence) low = middle + 1; else high = middle;
        }
        return low > 0 && (values[low - 1].EndSequence is not { } end || sequence < end) ? values[low - 1] : null;
    }
}
