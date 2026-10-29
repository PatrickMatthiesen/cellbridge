using System.Collections.Concurrent;
using System.Collections.Immutable;
using CellBridge.Storage.Abstractions;

namespace CellBridge.Storage.InMemory;

/// <summary>Normalized volatile repository. Each document has its own publication guard.</summary>
public sealed partial class InMemoryDocumentRepository : IDocumentRepository, IIndexedGraphSource
{
    private readonly ConcurrentDictionary<Guid, Entry> _documents = new();
    private readonly ConcurrentDictionary<string, Guid> _paths = new(StringComparer.Ordinal);
    private readonly IContentStore _content;
    private readonly StorageLimits _limits;
    private readonly TimeProvider _clock;
    private readonly IndexedCheckpointPolicy _checkpointPolicy;

    public InMemoryDocumentRepository(IContentStore content, StorageLimits? limits = null,
        TimeProvider? clock = null)
    {
        _content = content;
        _limits = limits ?? new();
        _limits.Validate();
        _clock = clock ?? TimeProvider.System;
        _checkpointPolicy = IndexedCheckpointPolicy.Default;
        _checkpointPolicy.Validate();
    }

    public bool Durable => false;
    public bool Shared => false;

    private sealed class Entry(DocumentHead head, CoordinationState coordination, ImmutableArray<EditorState> editors)
    {
        public readonly SemaphoreSlim Guard = new(1);
        public DocumentHead Head = head;
        public CoordinationState Coordination = coordination;
        public ImmutableArray<EditorState> Editors = editors;
        public readonly Dictionary<int, Partition> Partitions = [];
        public readonly SortedList<ulong, DocumentRevisionRecord> Revisions = [];
        public readonly Dictionary<SaveOperationIdentity, DocumentOperationReceipt> Receipts = [];
        public readonly Dictionary<string, ContentHandle> References = new(StringComparer.Ordinal);
        public readonly Dictionary<string, long> ReferenceCounts = new(StringComparer.Ordinal);
        public readonly Dictionary<string, long> AdmissionBytes = new(StringComparer.Ordinal);
        public readonly HashSet<string> PendingReferences = new(StringComparer.Ordinal);
        public readonly HashSet<string> RetiredPaths = new(StringComparer.Ordinal);
        public readonly SortedList<long, DocumentDelivery> Deliveries = [];
        public readonly HashSet<string> OwnedPaths = new(StringComparer.Ordinal) { head.PathKey };
        public long MetadataBytes;
        public long PhysicalBytes;
    }

    private sealed class Partition(PartitionHead head)
    {
        public PartitionHead Head = head;
        public readonly Dictionary<ExtendedId, GraphElementState> Elements = [];
        public readonly Dictionary<ExtendedId, List<Visibility>> Visibility = [];
        public readonly Dictionary<Guid, SerialIndex> Serials = [];
        public readonly Dictionary<ExtendedId, long> IndexSequences = [];
        public readonly Dictionary<long, PartitionHead> Heads = [];
        public readonly Dictionary<long, ImmutableDictionary<IndexedMappingKey, IndexedMappingValue>> MappingRoots = [];
        public ImmutableDictionary<IndexedMappingKey, IndexedMappingValue> CurrentMappings = ImmutableDictionary<IndexedMappingKey, IndexedMappingValue>.Empty;
        public readonly Dictionary<SerialId, Dictionary<(IndexedMappingKey Key, ExtendedId Target), IndexedStorageMapping>> MappingClaims = [];
        public readonly Dictionary<IndexedMappingKey, List<NormalizedMappingInterval>> MappingIntervals = [];
        public readonly Dictionary<ExtendedId, IndexedRevisionManifest> Revisions = [];
        public readonly Dictionary<ExtendedId, IndexedCellManifest> Cells = [];
        public readonly Dictionary<ExtendedId, IndexedStorageManifest> StorageManifests = [];
        public readonly Dictionary<(ExtendedId Group, int Ordinal), IndexedObjectDefinition> Objects = [];
        public readonly Dictionary<ExtendedId, ImmutableArray<IndexedObjectDefinition>> Groups = [];
        public readonly Dictionary<IndexedBindingKey, IndexedObjectBinding> Bindings = [];
        public readonly SortedList<long, IndexedBindingDelta> Changes = [];
        public readonly SortedList<long, IndexedGraphCheckpoint> Checkpoints = [];
        public IndexedReplayPosition ReplayPosition = new(0, 0, 0);
    }
    private sealed record Visibility(long Start, long? End);
    private sealed class SerialIndex
    {
        public readonly SortedSet<ulong> Keys = [];
        public readonly Dictionary<ulong, HashSet<ExtendedId>> Entries = [];
    }

    private DateTime UtcNow => _clock.GetUtcNow().UtcDateTime;
    private DocumentControlRead Control(Entry entry) => new(entry.Head, UtcNow, entry.Coordination,
        entry.Editors, entry.Partitions.Values.OrderBy(p => p.Head.Kind).Select(p => p.Head).ToImmutableArray());

    public async ValueTask<DocumentHead?> FindAsync(DocumentIdentity identity, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var id = identity switch
        {
            DocumentIdentity.Resource resource => resource.ResourceId,
            DocumentIdentity.Path path => _paths.GetValueOrDefault(path.PathKey),
            _ => throw new ArgumentOutOfRangeException(nameof(identity)),
        };
        if (!TryDocument(id, cancellationToken, out var entry)) return null;
        await entry.Guard.WaitAsync(cancellationToken);
        try
        {
            var head = entry.Head;
            if (identity is DocumentIdentity.Path path && head.PathKey != path.PathKey) return null;
            return !head.IsDeleted || identity is DocumentIdentity.Resource { IncludeDeleted: true } ? head : null;
        }
        finally { entry.Guard.Release(); }
    }

    public ValueTask<IReadOnlyList<DocumentHead>> ListAsync(int offset, int limit, CancellationToken cancellationToken = default)
    {
        EnsureOnline(cancellationToken);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        IReadOnlyList<DocumentHead> result = _documents.Values.Select(e => Volatile.Read(ref e.Head))
            .Where(h => !h.IsDeleted).OrderBy(h => h.PathKey, StringComparer.Ordinal).Skip(offset).Take(limit).ToArray();
        return ValueTask.FromResult(result);
    }

    public async ValueTask<DocumentControlRead?> ReadControlAsync(Guid id, CancellationToken cancellationToken = default)
    {
        if (!TryDocument(id, cancellationToken, out var entry)) return null;
        await entry.Guard.WaitAsync(cancellationToken);
        try { return entry.Head.IsDeleted ? null : Control(entry); }
        finally { entry.Guard.Release(); }
    }

    public async ValueTask<DocumentRevisionPage?> ReadRevisionsAsync(Guid id, int offset, int limit,
        CancellationToken cancellationToken = default)
    {
        DocumentReadModels.ValidatePage(offset, limit);
        if (!TryDocument(id, cancellationToken, out var entry)) return null;
        await entry.Guard.WaitAsync(cancellationToken);
        try
        {
            if (entry.Head.IsDeleted) return null;
            var count = Math.Min(limit, Math.Max(0, entry.Revisions.Count - offset));
            var values = ImmutableArray.CreateBuilder<DocumentRevisionRecord>(count);
            for (var i = 0; i < count; i++) values.Add(entry.Revisions.Values[entry.Revisions.Count - 1 - offset - i]);
            return new(entry.Head, values.MoveToImmutable(), offset + count < entry.Revisions.Count ? offset + count : null);
        }
        finally { entry.Guard.Release(); }
    }

    public async ValueTask<DocumentRevisionSelection?> ReadRevisionAsync(Guid id, ulong number,
        CancellationToken cancellationToken = default)
    {
        if (!TryDocument(id, cancellationToken, out var entry)) return null;
        await entry.Guard.WaitAsync(cancellationToken);
        try { return new(entry.Head, entry.Revisions.GetValueOrDefault(number)); }
        finally { entry.Guard.Release(); }
    }

    public async ValueTask<SaveReceiptRead?> ReadReceiptAsync(Guid id, SaveOperationIdentity operation,
        CancellationToken cancellationToken = default)
    {
        if (!TryDocument(id, cancellationToken, out var entry)) return null;
        await entry.Guard.WaitAsync(cancellationToken);
        try { return entry.Head.IsDeleted ? null : new(entry.Head, entry.Receipts.GetValueOrDefault(operation)); }
        finally { entry.Guard.Release(); }
    }

    public async ValueTask<PartitionRead?> ReadPartitionAsync(Guid id, PartitionReadRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!TryDocument(id, cancellationToken, out var entry)) return null;
        await entry.Guard.WaitAsync(cancellationToken);
        try { return entry.Head.IsDeleted && request.RevisionNumber is null ? null : SelectPartition(entry, request); }
        finally { entry.Guard.Release(); }
    }

    public async ValueTask<SavePreparationRead?> PrepareSaveAsync(Guid id, SavePreparationRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!TryDocument(id, cancellationToken, out var entry)) return null;
        await entry.Guard.WaitAsync(cancellationToken);
        try
        {
            if (entry.Head.IsDeleted) return null;
            var read = SelectPartition(entry, new(request.PartitionKind)
                { IncludeCatalog = request.IncludeCatalog, IncludeBindings = request.IncludeBindings }, request.AffectedMappings);
            return read is null ? null : new(Control(entry), read, entry.Receipts.GetValueOrDefault(request.Operation))
                { ReplayPosition = entry.Partitions[request.PartitionKind].ReplayPosition };
        }
        finally { entry.Guard.Release(); }
    }

    private PartitionRead? SelectPartition(Entry entry, PartitionReadRequest request, IEnumerable<IndexedMappingKey>? mappingKeys = null)
    {
        if (!entry.Partitions.TryGetValue(request.PartitionKind, out var partition)) return null;
        var selected = partition.Head;
        if (request.RevisionNumber is { } revision)
        {
            if (!entry.Revisions.TryGetValue(revision, out var record)) return null;
            selected = record.Partitions.FirstOrDefault(p => p.Kind == request.PartitionKind);
            if (selected is null) return null;
        }
        if (request.StorageIndex is { } storageIndex && selected.StorageIndex != storageIndex)
        {
            // An explicit old protocol root is addressable through the immutable catalog.
            if (!partition.IndexSequences.TryGetValue(storageIndex, out var sequence)) return null;
            selected = partition.Heads[sequence];
        }
        var ids = new HashSet<ExtendedId>(request.ElementIds);
        if (request.IncludeCatalog) ids.UnionWith(partition.Elements.Keys);
        foreach (var range in request.SerialRanges)
        {
            if (range.First > range.Last) throw new ArgumentException("A serial range is reversed.", nameof(request));
            if (!partition.Serials.TryGetValue(range.Namespace, out var serials)) continue;
            foreach (var serial in serials.Keys.GetViewBetween(range.First, range.Last)) ids.UnionWith(serials.Entries[serial]);
        }
        var elements = ids.Where(id => Visible(partition, id, selected.PublicationSequence))
            .Select(id => partition.Elements[id]).ToImmutableArray();
        var bindings = request.IncludeBindings ? BindingsAt(partition, selected.PublicationSequence) : [];
        return new(entry.Head, selected, elements, bindings)
        {
            Mappings = mappingKeys is not null && selected.StorageIndex is { } root && partition.MappingRoots.TryGetValue(selected.PublicationSequence, out var indexMappings)
                ? mappingKeys.Distinct().Where(indexMappings.ContainsKey).Select(key =>
                    new IndexedStorageMapping(new(entry.Head.ResourceId, entry.Head.LifecycleGeneration, selected.Kind),
                        root, key, indexMappings[key].Target, indexMappings[key].Serial)).ToImmutableArray() : [],
            Revisions = elements.Where(e => partition.Revisions.ContainsKey(e.Id)).Select(e => partition.Revisions[e.Id]).ToImmutableArray(),
        };
    }

    private ImmutableArray<IndexedObjectBinding> BindingsAt(Partition partition, long sequence)
    {
        if (sequence == partition.Head.PublicationSequence) return partition.Bindings.Values.ToImmutableArray();
        var index = LowerBound(partition.Checkpoints.Keys, sequence);
        if (index == partition.Checkpoints.Count || partition.Checkpoints.Keys[index] > sequence) index--;
        if (index < 0) throw new StorageCorruptionException("Historical graph checkpoint is missing.");
        var checkpoint = partition.Checkpoints.Values[index];
        var start = LowerBound(partition.Changes.Keys, checkpoint.Sequence + 1);
        var tail = new List<IndexedBindingDelta>();
        for (var i = start; i < partition.Changes.Count && partition.Changes.Keys[i] <= sequence; i++) tail.Add(partition.Changes.Values[i]);
        return IndexedDocumentGraph.Replay(checkpoint, tail, sequence, _checkpointPolicy);
    }

    private static bool Visible(Partition partition, ExtendedId id, long sequence)
    {
        if (!partition.Visibility.TryGetValue(id, out var intervals)) return false;
        var low = 0; var high = intervals.Count;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (intervals[middle].Start <= sequence) low = middle + 1; else high = middle;
        }
        return low > 0 && (intervals[low - 1].End is not { } end || sequence < end);
    }

    private static int LowerBound<T>(IList<T> values, T value) where T : IComparable<T>
    {
        var low = 0; var high = values.Count;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (values[middle].CompareTo(value) < 0) low = middle + 1; else high = middle;
        }
        return low;
    }

    public async ValueTask<IndexedStorageMapping?> FindMappingAsync(IndexedGraphScope scope, ExtendedId storageIndex,
        IndexedMappingKey key, CancellationToken cancellationToken = default)
    {
        if (!TryDocument(scope.ResourceId, cancellationToken, out var entry)) return null;
        await entry.Guard.WaitAsync(cancellationToken);
        try { return entry.Head.LifecycleGeneration == scope.LifecycleGeneration && entry.Partitions.TryGetValue(scope.TargetPartition, out var part)
            ? Mapping(part, scope, storageIndex, key) : null; }
        finally { entry.Guard.Release(); }
    }

    public async ValueTask<IndexedRevisionManifest?> ReadRevisionAsync(IndexedGraphScope scope, ExtendedId manifest,
        CancellationToken cancellationToken = default)
    {
        if (!TryDocument(scope.ResourceId, cancellationToken, out var entry)) return null;
        await entry.Guard.WaitAsync(cancellationToken);
        try { return entry.Head.LifecycleGeneration == scope.LifecycleGeneration && entry.Partitions.TryGetValue(scope.TargetPartition, out var part)
            ? part.Revisions.GetValueOrDefault(manifest) : null; }
        finally { entry.Guard.Release(); }
    }

    public async ValueTask<ImmutableArray<IndexedObjectDefinition>> ReadGroupAsync(IndexedGraphScope scope, ExtendedId group,
        CancellationToken cancellationToken = default)
    {
        if (!TryDocument(scope.ResourceId, cancellationToken, out var entry)) return default;
        await entry.Guard.WaitAsync(cancellationToken);
        try { return entry.Head.LifecycleGeneration == scope.LifecycleGeneration && entry.Partitions.TryGetValue(scope.TargetPartition, out var part)
            ? part.Groups.GetValueOrDefault(group) : default; }
        finally { entry.Guard.Release(); }
    }

    public async ValueTask<ImmutableArray<GraphElementState>> ReadElementsAsync(IndexedGraphScope scope,
        IReadOnlyCollection<ExtendedId> ids, CancellationToken cancellationToken = default)
    {
        if (!TryDocument(scope.ResourceId, cancellationToken, out var entry)) return [];
        await entry.Guard.WaitAsync(cancellationToken);
        try { return entry.Head.LifecycleGeneration == scope.LifecycleGeneration && entry.Partitions.TryGetValue(scope.TargetPartition, out var part)
            ? ids.Distinct().Where(part.Elements.ContainsKey).Select(id => part.Elements[id]).ToImmutableArray() : []; }
        finally { entry.Guard.Release(); }
    }

    public async ValueTask<ImmutableArray<ExtendedId>> ReadVisibleElementIdsAsync(IndexedGraphScope scope,
        long publicationSequence, IReadOnlyCollection<ExtendedId> elementIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(elementIds);
        if (!TryDocument(scope.ResourceId, cancellationToken, out var entry)) return [];
        await entry.Guard.WaitAsync(cancellationToken);
        try
        {
            if (entry.Head.LifecycleGeneration != scope.LifecycleGeneration ||
                !entry.Partitions.TryGetValue(scope.TargetPartition, out var part) ||
                publicationSequence < 0 || publicationSequence > part.Head.PublicationSequence) return [];
            return elementIds.Distinct().Where(id => Visible(part, id, publicationSequence)).ToImmutableArray();
        }
        finally { entry.Guard.Release(); }
    }

    public async ValueTask<ImmutableArray<IndexedStorageMapping>> ReadMappingClaimsAsync(IndexedGraphScope scope,
        IReadOnlyCollection<SerialId> serials, CancellationToken cancellationToken = default)
    {
        if (!TryDocument(scope.ResourceId, cancellationToken, out var entry)) return [];
        await entry.Guard.WaitAsync(cancellationToken);
        try { return entry.Head.LifecycleGeneration == scope.LifecycleGeneration && entry.Partitions.TryGetValue(scope.TargetPartition, out var part)
            ? serials.Distinct().Where(part.MappingClaims.ContainsKey).SelectMany(s => part.MappingClaims[s].Values).ToImmutableArray() : []; }
        finally { entry.Guard.Release(); }
    }

    public async ValueTask<ImmutableArray<IndexedStorageMapping>> ReadMappingsAsync(IndexedGraphScope scope,
        long publicationSequence, CancellationToken cancellationToken = default)
    {
        if (!TryDocument(scope.ResourceId, cancellationToken, out var entry)) return [];
        await entry.Guard.WaitAsync(cancellationToken);
        try
        {
            if (entry.Head.LifecycleGeneration != scope.LifecycleGeneration || !entry.Partitions.TryGetValue(scope.TargetPartition, out var part) ||
                !part.MappingRoots.TryGetValue(publicationSequence, out var root) || part.Heads[publicationSequence].StorageIndex is not { } index) return [];
            return root.Select(pair => new IndexedStorageMapping(scope, index, pair.Key, pair.Value.Target, pair.Value.Serial)).ToImmutableArray();
        }
        finally { entry.Guard.Release(); }
    }

    private static IndexedStorageMapping? Mapping(Partition partition, IndexedGraphScope scope, ExtendedId index, IndexedMappingKey key) =>
        partition.IndexSequences.TryGetValue(index, out var sequence) && partition.MappingRoots[sequence].TryGetValue(key, out var value)
            ? new(scope, index, key, value.Target, value.Serial) : null;

    public ValueTask CheckHealthAsync(CancellationToken cancellationToken = default)
    { EnsureOnline(cancellationToken); return ValueTask.CompletedTask; }
}
